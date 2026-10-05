namespace AgentLang.Conventional.Tests

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang.Conventional

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private text (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode

    let private propertyString (node: JsonNode) (name: string) (fallback: string) =
        let mutable value = Unchecked.defaultof<JsonNode>
        if not (isNull node) && (node :? JsonObject) && (node.AsObject().TryGetPropertyValue(name, &value)) then
            try value.GetValue<string>() with _ -> fallback
        else fallback

    let private args (fields: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in fields do result[name] <- value
        result

    let private call (dispatcher: ConventionalDispatcher) (operation: string) (fields: (string * JsonNode) list) =
        dispatcher.Dispatch(operation, args fields)

    let private isOk (response: JsonObject) = response["ok"].GetValue<bool>()

    let private errorCode (response: JsonObject) = propertyString response["error"] "code" ""

    let private expectError (expectedCode: string) (response: JsonObject) (label: string) =
        check (not (isOk response)) $"{label} should fail"
        equal expectedCode (errorCode response) $"{label} error code"

    let private projectText = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"""

    let private makeRoot () =
        let baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentlang-conventional-tests"))
        Directory.CreateDirectory(baseDirectory) |> ignore
        let root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        root

    let private makeProject root name =
        let path = Path.Combine(root, name)
        Directory.CreateDirectory(path) |> ignore
        File.WriteAllText(Path.Combine(path, "Synthetic.fsproj"), projectText)
        path

    let private makeDispatcher root action timeoutMs outputLimit searchResults searchEntries searchDepth =
        let command =
            { Action = action
              ProjectFile = "Synthetic.fsproj"
              TimeoutMilliseconds = timeoutMs
              MaximumOutputCharactersPerStream = outputLimit }
        new ConventionalDispatcher(
            root,
            command,
            maximumSearchResults = searchResults,
            maximumSearchEntries = searchEntries,
            maximumSearchDepth = searchDepth)

    let private defaultDispatcher root action timeoutMs outputLimit =
        makeDispatcher root action timeoutMs outputLimit 200 20_000 64

    let private testPathConfinementAndWindowsAliases root =
        let project = makeProject root "paths"
        let source = Path.Combine(project, "safe.fs")
        File.WriteAllText(source, "module Safe\nlet answer = 42\n")
        let outside = Path.Combine(root, "outside.fs")
        File.WriteAllText(outside, "module Outside\nlet marker = 1\n")
        let dispatcher = defaultDispatcher project ValidationAction.Build 10_000 2048

        expectError "PATH_TRAVERSAL" (call dispatcher "read" [ "path", text "../outside.fs" ]) "parent traversal"
        expectError "PATH_INVALID" (call dispatcher "read" [ "path", text "src/.. /outside.fs" ]) "Windows traversal alias"
        expectError "PATH_ABSOLUTE" (call dispatcher "read" [ "path", text outside ]) "absolute path"
        expectError "PATH_COMPONENT_INVALID" (call dispatcher "read" [ "path", text "CON.txt" ]) "reserved device name"
        expectError "PATH_COMPONENT_INVALID" (call dispatcher "read" [ "path", text "safe.fs:secret" ]) "alternate data stream path"
        expectError "PATH_COMPONENT_INVALID" (call dispatcher "read" [ "path", text "safe.fs." ]) "trailing-dot alias"
        expectError "PATH_COMPONENT_INVALID" (call dispatcher "read" [ "path", text "safe.fs " ]) "trailing-space alias"

        let outsideBin = Path.Combine(root, "outside-bin")
        Directory.CreateDirectory(outsideBin) |> ignore
        let linkedDirectory = Path.Combine(project, "linked")
        let linkCreated =
            try
                Directory.CreateSymbolicLink(linkedDirectory, outsideBin) |> ignore
                true
            with
            | :? UnauthorizedAccessException
            | :? PlatformNotSupportedException
            | :? IOException -> false
        if linkCreated then
            expectError "PATH_REPARSE_POINT" (call dispatcher "read" [ "path", text "linked/outside.fs" ]) "symbolic-link traversal"
            expectError "PATH_REPARSE_POINT" (call dispatcher "search" [ "query", text "marker" ]) "search over symbolic links"

            let movedRoot = project + ".physical"
            Directory.Move(project, movedRoot)
            try
                Directory.CreateSymbolicLink(project, movedRoot) |> ignore
                expectError "PATH_REPARSE_POINT" (call dispatcher "search" [ "query", text "marker" ]) "root replacement by symbolic link"
            finally
                if Directory.Exists project && (File.GetAttributes(project) &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
                    Directory.Delete project
                if Directory.Exists movedRoot then Directory.Move(movedRoot, project)
        else
            printfn "SKIP symbolic-link fixture: this host does not permit creating links"

        File.WriteAllText(Path.Combine(project, "unsupported.bin"), "data")
        expectError "FILE_FORMAT_UNSUPPORTED" (call dispatcher "read" [ "path", text "unsupported.bin" ]) "unsupported file format"
        expectError "ARGUMENTS_INVALID" (call dispatcher "read" [ "path", text "safe.fs"; "unexpected", text "extra" ]) "extra read argument"

    let private testDeterministicBoundedSearch root =
        let project = makeProject root "search"
        Directory.CreateDirectory(Path.Combine(project, "src")) |> ignore
        File.WriteAllText(Path.Combine(project, "alpha.fs"), "let first = \"needle\"\r\n// NEEDLE second\r\n")
        File.WriteAllText(Path.Combine(project, "src", "a.fs"), "// needle third\n")
        File.WriteAllText(Path.Combine(project, "src", "b.fs"), "// Needle fourth\n")
        File.WriteAllText(Path.Combine(project, "notes.md"), "needle fifth\n")
        let dispatcher = defaultDispatcher project ValidationAction.Build 10_000 2048
        let first = call dispatcher "search" [ "query", text "needle" ]
        let repeated = call dispatcher "search" [ "query", text "needle" ]
        check (isOk first && isOk repeated) "literal search succeeds"
        let firstMatchesNode = first["data"]["matches"]
        let repeatedMatchesNode = repeated["data"]["matches"]
        let firstMatches = firstMatchesNode.ToJsonString()
        equal firstMatches (repeatedMatchesNode.ToJsonString()) "repeated search results are deterministic"
        let results = firstMatchesNode.AsArray()
        equal 5 results.Count "search includes supported source and documentation formats"
        let paths = results |> Seq.map (fun item -> propertyString item "path" "") |> Seq.toList
        equal [ "alpha.fs"; "alpha.fs"; "notes.md"; "src/a.fs"; "src/b.fs" ] paths "search is sorted by normalized path and line"
        let firstLine = results[0]["line"]
        equal 1 (firstLine.GetValue<int>()) "search includes one-based line numbers"

        let resultLimited = makeDispatcher project ValidationAction.Build 10_000 2048 2 20_000 64
        let limited = call resultLimited "search" [ "query", text "needle" ]
        let limitedData = limited["data"]
        equal 2 (limitedData["matches"].AsArray().Count) "search result count is capped"
        check (limitedData["truncated"].GetValue<bool>()) "search reports truncated results"

        let entryLimited = makeDispatcher project ValidationAction.Build 10_000 2048 200 4 64
        let bounded = call entryLimited "search" [ "query", text "needle" ]
        let boundedData = bounded["data"]
        check (boundedData["truncated"].GetValue<bool>()) "filesystem traversal stops at its entry cap"
        check (boundedData["filesScanned"].GetValue<int>() <= 4) "entry cap bounds file reads"

    let private testCompareAndSwapAtomicReplacement root =
        let project = makeProject root "edit"
        Directory.CreateDirectory(Path.Combine(project, "src")) |> ignore
        let sourcePath = Path.Combine(project, "src", "answer.fs")
        let original = "module Answer\nlet value = 41\n"
        let replacement = "module Answer\nlet value = 42\n"
        File.WriteAllText(sourcePath, original)
        let dispatcher = defaultDispatcher project ValidationAction.Build 10_000 2048
        let readBefore = call dispatcher "read" [ "path", text "src/answer.fs" ]
        check (isOk readBefore) "source read succeeds"
        equal original (propertyString readBefore["data"] "content" "") "read returns exact UTF-8 contents"
        let expectedHash = propertyString readBefore["data"] "sha256" ""
        check (expectedHash.Length = 64) "read provides a SHA-256 content token"

        let updated = call dispatcher "replace" [ "path", text "src/answer.fs"; "expectedSha256", text expectedHash; "content", text replacement ]
        if not (isOk updated) then failwith $"replacement response: {updated.ToJsonString()}"
        check (isOk updated) "fresh content hash allows replacement"
        let readAfter = call dispatcher "read" [ "path", text "src/answer.fs" ]
        equal replacement (propertyString readAfter["data"] "content" "") "replacement is visible to a fresh read"
        let stale = call dispatcher "replace" [ "path", text "src/answer.fs"; "expectedSha256", text expectedHash; "content", text "module Answer\nlet value = 0\n" ]
        expectError "STALE_CONTENT" stale "stale compare-and-swap edit"
        let unchanged = call dispatcher "read" [ "path", text "src/answer.fs" ]
        equal replacement (propertyString unchanged["data"] "content" "") "stale edit leaves current contents unchanged"
        equal 0 (Directory.GetFiles(Path.Combine(project, "src"), ".agentlang-replace-*.tmp").Length) "atomic replacement leaves no temporary files"
        expectError "OPERATION_UNSUPPORTED" (call dispatcher "shell" []) "unsupported operation"

        let logs = dispatcher.OperationLog
        let sequence = logs |> Seq.map (fun item -> item["sequence"].GetValue<int64>()) |> Seq.toList
        equal [ 1L .. int64 logs.Count ] sequence "operation events are monotonic and ordered"
        check (logs.ToJsonString().Contains("replace")) "operation log records the definition-level file replacement"
        check (not (logs.ToJsonString().Contains(replacement))) "operation logs omit source contents"

    let private sourceForValidation body =
        $"""open System

[<EntryPoint>]
let main _ =
{body}
"""

    let private testFixedDotnetValidation root =
        let project = makeProject root "validation"
        let programPath = Path.Combine(project, "Program.fs")
        let successBody = "    let secret = Environment.GetEnvironmentVariable(\"AGENTLANG_CONVENTIONAL_TEST_SECRET\")\n    if String.IsNullOrEmpty secret then\n        Console.WriteLine(\"SECRET_ENV_REMOVED\")\n        Console.WriteLine(\"PASS\")\n        0\n    else\n        Console.WriteLine(\"SECRET_ENV_PRESENT\")\n        1"
        File.WriteAllText(programPath, sourceForValidation successBody)

        let originalSecret = Environment.GetEnvironmentVariable("AGENTLANG_CONVENTIONAL_TEST_SECRET")
        let testSecret = "do-not-copy-to-child-or-logs"
        Environment.SetEnvironmentVariable("AGENTLANG_CONVENTIONAL_TEST_SECRET", testSecret)
        try
            let builder = defaultDispatcher project ValidationAction.Build 120_000 2048
            let built = call builder "validate" []
            let buildText = propertyString built "text" ""
            check (isOk built) $"fixed dotnet build passes: {buildText}"
            let buildData = built["data"]
            equal 0 (buildData["exitCode"].GetValue<int>()) "build result contains the exit code"
            let runValidator = defaultDispatcher project ValidationAction.Run 120_000 2048
            let ran = call runValidator "validate" []
            let runText = propertyString ran "text" ""
            check (isOk ran) $"fixed dotnet run passes: {runText}"
            let output = propertyString ran["data"] "stdout" "" + propertyString ran["data"] "stderr" ""
            check (output.Contains("PASS", StringComparison.Ordinal)) "test executable output is captured"
            check (output.Contains("SECRET_ENV_REMOVED", StringComparison.Ordinal)) "credential-named process variables are removed before validation"
            check (not (output.Contains(testSecret, StringComparison.Ordinal))) "filtered environment secret is absent from validation output"
            check (not (runValidator.OperationLog.ToJsonString().Contains(testSecret))) "validation logs never contain environment values"

            let failureBody = "    for _ in 1 .. 10000 do Console.Write(\"OUT\")\n    Console.Error.WriteLine(\"INTENTIONAL_FAILURE\")\n    7"
            File.WriteAllText(programPath, sourceForValidation failureBody)
            let cappedRunner = defaultDispatcher project ValidationAction.Run 120_000 96
            let failed = call cappedRunner "validate" []
            expectError "VALIDATION_FAILED" failed "non-zero test executable"
            let failedData = failed["data"]
            equal 7 (failedData["exitCode"].GetValue<int>()) "validation records non-zero exit status"
            check (failedData["stdout"].GetValue<string>().Length <= 96) "captured stdout respects the configured character cap"
            check (failedData["stderr"].GetValue<string>().Length <= 96) "captured stderr respects the configured character cap"
            check (failedData["stdoutTruncated"].GetValue<bool>()) "validation reports when stdout was truncated"

            let timeoutBody = "    while true do System.Threading.Thread.Sleep(25)\n    0"
            File.WriteAllText(programPath, sourceForValidation timeoutBody)
            let timeoutRunner = defaultDispatcher project ValidationAction.Run 1 96
            let timedOut = call timeoutRunner "validate" []
            expectError "VALIDATION_TIMEOUT" timedOut "hung validation executable"
            let timedOutData = timedOut["data"]
            check (timedOutData["timedOut"].GetValue<bool>()) "validation timeout is reported structurally"
        finally
            Environment.SetEnvironmentVariable("AGENTLANG_CONVENTIONAL_TEST_SECRET", originalSecret)

    let private removeRoot root =
        let expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentlang-conventional-tests"))
        let resolved = Path.GetFullPath(root)
        if resolved.StartsWith(expectedParent + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists resolved then
            Directory.Delete(resolved, true)

    [<EntryPoint>]
    let main _ =
        let root = makeRoot ()
        try
            testPathConfinementAndWindowsAliases root
            testDeterministicBoundedSearch root
            testCompareAndSwapAtomicReplacement root
            testFixedDotnetValidation root
            printfn "PASS: %d conventional tool assertions" assertions
            0
        finally
            removeRoot root
