namespace AgentLang.Conventional.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
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

    let private makeByteLimitedDispatcher root fileByteLimit =
        let command =
            { Action = ValidationAction.Build
              ProjectFile = "Synthetic.fsproj"
              TimeoutMilliseconds = 10_000
              MaximumOutputCharactersPerStream = 2048 }
        new ConventionalDispatcher(root, command, maximumFileBytes = fileByteLimit)

    let private makeConfiguredDispatcher root fileByteLimit searchResults searchFiles searchEntries searchDepth =
        let command =
            { Action = ValidationAction.Build
              ProjectFile = "Synthetic.fsproj"
              TimeoutMilliseconds = 10_000
              MaximumOutputCharactersPerStream = 2048 }
        new ConventionalDispatcher(
            root,
            command,
            maximumFileBytes = fileByteLimit,
            maximumSearchResults = searchResults,
            maximumSearchFiles = searchFiles,
            maximumSearchEntries = searchEntries,
            maximumSearchDepth = searchDepth)

    let private hashBytes (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

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
            expectError "PATH_REPARSE_POINT" (call dispatcher "inspect" []) "overview over symbolic links"

            let movedRoot = project + ".physical"
            Directory.Move(project, movedRoot)
            try
                Directory.CreateSymbolicLink(project, movedRoot) |> ignore
                expectError "PATH_REPARSE_POINT" (call dispatcher "search" [ "query", text "marker" ]) "root replacement by symbolic link"
                expectError "PATH_REPARSE_POINT" (call dispatcher "inspect" []) "overview with root replaced by symbolic link"
            finally
                if Directory.Exists project && (File.GetAttributes(project) &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
                    Directory.Delete project
                if Directory.Exists movedRoot then Directory.Move(movedRoot, project)
        else
            printfn "SKIP symbolic-link fixture: this host does not permit creating links"

        File.WriteAllText(Path.Combine(project, "unsupported.bin"), "data")
        expectError "FILE_FORMAT_UNSUPPORTED" (call dispatcher "read" [ "path", text "unsupported.bin" ]) "unsupported file format"
        expectError "ARGUMENTS_INVALID" (call dispatcher "read" [ "path", text "safe.fs"; "unexpected", text "extra" ]) "extra read argument"

    let private testDeterministicOverviewAndInspectCompatibility root =
        let project = makeProject root "overview"
        Directory.CreateDirectory(Path.Combine(project, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(project, "bin")) |> ignore
        Directory.CreateDirectory(Path.Combine(project, "obj")) |> ignore
        File.WriteAllBytes(Path.Combine(project, "bad.fs"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        File.WriteAllText(Path.Combine(project, "src", "answer.fs"), "module Answer\nlet value = 42\n")
        File.WriteAllText(Path.Combine(project, "ignored.txt"), "not a supported source")
        File.WriteAllText(Path.Combine(project, "bin", "generated.fs"), "generated")
        File.WriteAllText(Path.Combine(project, "obj", "generated.fs"), "generated")

        // The tiny byte limit and invalid UTF-8 source prove that discovery only enumerates paths.
        let overviewDispatcher = makeConfiguredDispatcher project 1L 20 20 100 10
        let overview = call overviewDispatcher "inspect" []
        check (isOk overview) "empty inspect returns a project overview without reading source bytes"
        let data = overview["data"]
        equal "Synthetic.fsproj" (propertyString data "validationProject" "") "overview returns the configured relative validation target"
        let paths = data["files"].AsArray() |> Seq.map (fun path -> path.GetValue<string>()) |> Seq.toList
        equal [ "Synthetic.fsproj"; "bad.fs"; "src/answer.fs" ] paths "overview returns deterministic supported-source paths and skips generated directories"
        equal 6 (data["operations"].AsArray().Count) "overview documents all six operations"
        let schemas = data["operations"].AsArray()
        equal "inspect" (propertyString schemas[0] "op" "") "overview documents inspect first"
        let inspectSchema = schemas[0]
        let replaceSchema = schemas[3]
        let patchSchema = schemas[4]
        let inspectArguments = inspectSchema["argumentSets"].ToJsonString()
        let replaceArguments = replaceSchema["argumentSets"].ToJsonString()
        let patchArguments = patchSchema["argumentSets"].ToJsonString()
        equal "[[],[{\"name\":\"path\",\"type\":\"string\",\"required\":true}]]" inspectArguments "inspect schema exposes both empty and path forms with a top-level string field"
        equal "[[{\"name\":\"path\",\"type\":\"string\",\"required\":true},{\"name\":\"expectedSha256\",\"type\":\"string\",\"required\":true},{\"name\":\"content\",\"type\":\"string\",\"required\":true}]]" replaceArguments "replace schema lists typed required fields"
        equal "[[{\"name\":\"path\",\"type\":\"string\",\"required\":true},{\"name\":\"expectedSha256\",\"type\":\"string\",\"required\":true},{\"name\":\"oldText\",\"type\":\"string\",\"required\":true},{\"name\":\"newText\",\"type\":\"string\",\"required\":true}]]" patchArguments "patch schema lists typed required fields"
        check (not (data["truncated"].GetValue<bool>())) "complete overview is not marked truncated"
        check (not (data.AsObject().ContainsKey("sha256"))) "overview omits content hashes"
        check (not (data.AsObject().ContainsKey("content"))) "overview omits source contents"

        let ordinaryDispatcher = defaultDispatcher project ValidationAction.Build 10_000 2048
        let inspected = call ordinaryDispatcher "inspect" [ "path", text "src/answer.fs" ]
        check (isOk inspected) "inspect(path) remains available"
        equal "src/answer.fs" (propertyString inspected["data"] "path" "") "inspect(path) returns the normalized path"
        check ((propertyString inspected["data"] "sha256" "").Length = 64) "inspect(path) retains its content hash"
        expectError "ARGUMENTS_INVALID" (call ordinaryDispatcher "inspect" [ "path", text "src/answer.fs"; "unexpected", text "extra" ]) "extra inspect argument"
        expectError "ARGUMENT_INVALID" (call ordinaryDispatcher "inspect" [ "path", JsonValue.Create(42) ]) "malformed inspect path"
        expectError "ARGUMENTS_INVALID" (call ordinaryDispatcher "inspect" [ "unexpected", text "extra" ]) "unexpected inspect argument without path"

    let private testOverviewAndSearchTraversalLimits root =
        let resultProject = makeProject root "overview-result-limit"
        File.WriteAllText(Path.Combine(resultProject, "a.fs"), "module A\n")
        File.WriteAllText(Path.Combine(resultProject, "b.fs"), "module B\n")
        let resultLimited = makeConfiguredDispatcher resultProject 4096L 1 20 100 10
        let limitedOverview = call resultLimited "inspect" []
        check (isOk limitedOverview) "overview succeeds at its result cap"
        let limitedOverviewData = limitedOverview["data"]
        equal 1 (limitedOverviewData["files"].AsArray().Count) "overview caps returned file paths"
        check (limitedOverviewData["truncated"].GetValue<bool>()) "overview marks truncation after observing an excess file path"

        let exactResultLimit = makeConfiguredDispatcher resultProject 4096L 3 20 100 10
        let exactOverview = call exactResultLimit "inspect" []
        let exactOverviewData = exactOverview["data"]
        equal 3 (exactOverviewData["files"].AsArray().Count) "overview returns all paths at the exact result cap"
        check (not (exactOverviewData["truncated"].GetValue<bool>())) "overview is not truncated when no excess path exists"

        let fileProject = makeProject root "overview-file-limit"
        File.WriteAllText(Path.Combine(fileProject, "a.fs"), "module A\n")
        let fileLimited = makeConfiguredDispatcher fileProject 4096L 10 2 100 10
        let exactFileOverview = call fileLimited "inspect" []
        let exactFileOverviewData = exactFileOverview["data"]
        equal 2 (exactFileOverviewData["files"].AsArray().Count) "overview lists the exact file cap"
        check (not (exactFileOverviewData["truncated"].GetValue<bool>())) "overview does not mark an exact file cap without another supported file"
        let exactFileSearch = call fileLimited "search" [ "query", text "absent" ]
        let exactFileSearchData = exactFileSearch["data"]
        check (not (exactFileSearchData["truncated"].GetValue<bool>())) "search does not mark an exact file cap without an excess candidate"
        equal 2 (exactFileSearchData["filesScanned"].GetValue<int>()) "search scans no more than its file cap"
        File.WriteAllText(Path.Combine(fileProject, "b.fs"), "module B\n")
        let excessFileOverview = call fileLimited "inspect" []
        let excessFileOverviewData = excessFileOverview["data"]
        equal 2 (excessFileOverviewData["files"].AsArray().Count) "overview remains bounded after an additional file appears"
        check (excessFileOverviewData["truncated"].GetValue<bool>()) "overview marks truncation after observing an excess supported file"
        let excessFileSearch = call fileLimited "search" [ "query", text "absent" ]
        let excessFileSearchData = excessFileSearch["data"]
        check (excessFileSearchData["truncated"].GetValue<bool>()) "search marks truncation after observing an excess supported file"
        equal 2 (excessFileSearchData["filesScanned"].GetValue<int>()) "search stops at its file cap"

        let entryProject = makeProject root "overview-entry-limit"
        for name in [ "a.fs"; "b.fs"; "c.fs" ] do File.WriteAllText(Path.Combine(entryProject, name), "module Entry\n")
        let entryLimited = makeConfiguredDispatcher entryProject 4096L 10 20 3 10
        expectError "SEARCH_DIRECTORY_LIMIT" (call entryLimited "inspect" []) "overview per-directory limit plus one"
        expectError "SEARCH_DIRECTORY_LIMIT" (call entryLimited "search" [ "query", text "absent" ]) "search per-directory limit plus one"

        let globalEntryProject = makeProject root "overview-global-entry-limit"
        Directory.CreateDirectory(Path.Combine(globalEntryProject, "src")) |> ignore
        File.WriteAllText(Path.Combine(globalEntryProject, "src", "a.fs"), "module A\n")
        File.WriteAllText(Path.Combine(globalEntryProject, "src", "b.fs"), "module B\n")
        let globalEntryLimited = makeConfiguredDispatcher globalEntryProject 4096L 10 20 3 10
        let globalEntryOverview = call globalEntryLimited "inspect" []
        check (isOk globalEntryOverview) "global entry budget truncates after enumerating small directories"
        let globalEntryOverviewData = globalEntryOverview["data"]
        check (globalEntryOverviewData["truncated"].GetValue<bool>()) "overview reports its global entry budget"
        let globalEntrySearch = call globalEntryLimited "search" [ "query", text "absent" ]
        check (isOk globalEntrySearch) "search global entry budget returns a bounded partial result"
        let globalEntrySearchData = globalEntrySearch["data"]
        check (globalEntrySearchData["truncated"].GetValue<bool>()) "search reports its global entry budget"

        let depthProject = makeProject root "overview-depth-limit"
        Directory.CreateDirectory(Path.Combine(depthProject, "src", "deep")) |> ignore
        File.WriteAllText(Path.Combine(depthProject, "src", "deep", "leaf.fs"), "module Leaf\n")
        let depthLimited = makeConfiguredDispatcher depthProject 4096L 10 20 100 1
        let depthOverview = call depthLimited "inspect" []
        let depthOverviewData = depthOverview["data"]
        check (depthOverviewData["truncated"].GetValue<bool>()) "overview reports traversal depth truncation"
        let depthSearch = call depthLimited "search" [ "query", text "absent" ]
        let depthSearchData = depthSearch["data"]
        check (depthSearchData["truncated"].GetValue<bool>()) "search reports traversal depth truncation"

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

        let exactResultLimit = makeDispatcher project ValidationAction.Build 10_000 2048 5 20_000 64
        let exactResults = call exactResultLimit "search" [ "query", text "needle" ]
        let exactResultData = exactResults["data"]
        check (not (exactResultData["truncated"].GetValue<bool>())) "search does not mark an exact result cap without an excess match"

        let entryLimited = makeDispatcher project ValidationAction.Build 10_000 2048 200 4 64
        let bounded = call entryLimited "search" [ "query", text "needle" ]
        let boundedData = bounded["data"]
        check (boundedData["truncated"].GetValue<bool>()) "filesystem traversal stops at its entry cap"
        check (boundedData["filesScanned"].GetValue<int>() <= 4) "entry cap bounds file reads"
        let boundedOverview = call (makeConfiguredDispatcher project 4096L 200 5000 4 64) "inspect" []
        let boundedOverviewData = boundedOverview["data"]
        check (boundedOverviewData["truncated"].GetValue<bool>()) "overview reports global traversal entry truncation"

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

    let private testExactCompareAndSwapPatch root =
        let project = makeProject root "patch"
        let sourcePath = Path.Combine(project, "answer.fs")
        let original = "\uFEFFmodule Café\r\nlet value = \"こんにちは\" 41\r\nlet tail = \"keep\"\r\n"
        let originalBytes = UTF8Encoding(false, true).GetBytes(original)
        File.WriteAllBytes(sourcePath, originalBytes)
        let dispatcher = defaultDispatcher project ValidationAction.Build 10_000 2048
        let readBefore = call dispatcher "read" [ "path", text "answer.fs" ]
        check (isOk readBefore) "patch source read succeeds"
        equal original (propertyString readBefore.["data"] "content" "") "read preserves the UTF-8 BOM and exact line endings"
        let expectedHash = propertyString readBefore.["data"] "sha256" ""

        let oldText = "value = \"こんにちは\" 41"
        let newText = "value = \"こんにちは\" 42"
        let patchedContent = original.Replace(oldText, newText, StringComparison.Ordinal)
        let patched =
            call dispatcher "patch"
                [ "path", text "answer.fs"
                  "expectedSha256", text expectedHash
                  "oldText", text oldText
                  "newText", text newText ]
        if not (isOk patched) then failwith $"patch response: {patched.ToJsonString()}"
        equal "patch" (propertyString patched "kind" "") "successful patch has its own response kind"
        equal "answer.fs" (propertyString patched.["data"] "path" "") "patch reports the normalized path"
        let patchedBytes = UTF8Encoding(false, true).GetBytes(patchedContent)
        equal (int64 patchedBytes.Length) ((patched.["data"]).["bytes"].GetValue<int64>()) "patch reports output file bytes"
        equal (hashBytes patchedBytes) (propertyString patched.["data"] "sha256" "") "patch reports the output hash"
        equal 3 ((patched.["data"]).AsObject().Count) "patch result returns only path, hash, and bytes"
        check (not (patched.ToJsonString().Contains(patchedContent, StringComparison.Ordinal))) "patch response does not echo file contents"
        let readAfter = call dispatcher "read" [ "path", text "answer.fs" ]
        check (isOk readAfter) "patched file can be read again"
        equal patchedContent (propertyString readAfter.["data"] "content" "") "patch changes only the exact anchored text"
        equal (hashBytes patchedBytes) (propertyString readAfter.["data"] "sha256" "") "fresh read reports the patch hash"

        let deletionHash = propertyString readAfter.["data"] "sha256" ""
        let deleted =
            call dispatcher "patch"
                [ "path", text "answer.fs"
                  "expectedSha256", text deletionHash
                  "oldText", text "42"
                  "newText", text "" ]
        check (isOk deleted) "empty newText deletes the unique anchor"
        let afterDeletion = call dispatcher "read" [ "path", text "answer.fs" ]
        equal (patchedContent.Replace("42", "", StringComparison.Ordinal)) (propertyString afterDeletion.["data"] "content" "") "empty replacement deletes only its anchor"

        let assertRejected expectedCode label fields =
            let beforeBytes = File.ReadAllBytes(sourcePath)
            let beforeHash = hashBytes beforeBytes
            let rejected = call dispatcher "patch" fields
            expectError expectedCode rejected label
            equal beforeBytes (File.ReadAllBytes(sourcePath)) $"{label} leaves source bytes unchanged"
            equal beforeHash (hashBytes (File.ReadAllBytes(sourcePath))) $"{label} leaves source hash unchanged"
            equal 0 (Directory.GetFiles(project, ".agentlang-replace-*.tmp").Length) $"{label} leaves no temporary replacement file"

        let currentHash = propertyString afterDeletion.["data"] "sha256" ""
        let patchFieldsWithHash hash anchor replacement =
            [ "path", text "answer.fs"
              "expectedSha256", text hash
              "oldText", anchor
              "newText", replacement ]
        let patchFields = patchFieldsWithHash currentHash
        assertRejected "STALE_CONTENT" "stale hash is checked before the anchor" (patchFieldsWithHash (String('0', 64)) (text "absent anchor") (text "replacement"))
        assertRejected "HASH_INVALID" "invalid patch hash" (patchFieldsWithHash "not-a-hash" (text "keep") (text "replacement"))
        assertRejected "PATCH_ANCHOR_NOT_FOUND" "missing patch anchor" (patchFields (text "absent anchor") (text "replacement"))
        assertRejected "PATCH_ANCHOR_EMPTY" "empty patch anchor" (patchFields (text "") (text "replacement"))
        for argumentName in [ "path"; "expectedSha256"; "oldText"; "newText" ] do
            let invalidType =
                patchFields (text "keep") (text "replacement")
                |> List.map (fun (name, value) ->
                    name,
                    if name = argumentName then JsonValue.Create(42) :> JsonNode else value)
            assertRejected "ARGUMENT_INVALID" $"non-string patch {argumentName}" invalidType
        assertRejected "ARGUMENTS_INVALID" "extra patch argument" (patchFields (text "keep") (text "replacement") @ [ "extra", text "field" ])
        assertRejected "PATH_TRAVERSAL" "patch path confinement" (([ "path", text "../outside.fs" ] @ patchFields (text "keep") (text "replacement")) |> List.distinctBy fst)
        let invalidSurrogate = String([| char 0xD800 |])
        equal 0xD800 (int (invalidSurrogate.[0])) "invalid UTF-8 fixture contains an unpaired high surrogate"
        let invalidSurrogateNode = text invalidSurrogate
        let dispatchedSurrogate = invalidSurrogateNode.GetValue<string>()
        equal 0xD800 (int (dispatchedSurrogate.[0])) "JSON argument preserves the unpaired high surrogate"
        assertRejected "PATCH_OUTPUT_ENCODING_INVALID" "invalid UTF-8 patch output" (patchFields (text "keep") invalidSurrogateNode)

        let overlappingPath = Path.Combine(project, "overlapping.fs")
        File.WriteAllText(overlappingPath, "aaaaa", UTF8Encoding(false))
        let overlappingHash = hashBytes (File.ReadAllBytes(overlappingPath))
        let overlapping =
            call dispatcher "patch"
                [ "path", text "overlapping.fs"
                  "expectedSha256", text overlappingHash
                  "oldText", text "aaa"
                  "newText", text "x" ]
        expectError "PATCH_ANCHOR_AMBIGUOUS" overlapping "overlapping anchor occurrences are ambiguous"
        equal "aaaaa" (File.ReadAllText(overlappingPath, UTF8Encoding(false))) "ambiguous overlapping patch leaves file unchanged"
        equal 0 (Directory.GetFiles(project, ".agentlang-replace-*.tmp").Length) "ambiguous patch leaves no temporary replacement file"

        let outsidePath = Path.Combine(root, "outside-patch.fs")
        let outsideContent = "outside"
        File.WriteAllText(outsidePath, outsideContent, UTF8Encoding(false))
        let outsideHash = hashBytes (File.ReadAllBytes(outsidePath))
        let outsidePatch =
            call dispatcher "patch"
                [ "path", text "../outside-patch.fs"
                  "expectedSha256", text outsideHash
                  "oldText", text "outside"
                  "newText", text "changed" ]
        expectError "PATH_TRAVERSAL" outsidePatch "patch cannot escape the project root"
        equal outsideContent (File.ReadAllText(outsidePath, UTF8Encoding(false))) "confined patch leaves outside file unchanged"

        let limitedProject = makeProject root "patch-size"
        let limitedPath = Path.Combine(limitedProject, "small.fs")
        File.WriteAllText(limitedPath, "anchor", UTF8Encoding(false))
        let limitedDispatcher = makeByteLimitedDispatcher limitedProject 16L
        let limitedHash = hashBytes (File.ReadAllBytes(limitedPath))
        let tooLarge =
            call limitedDispatcher "patch"
                [ "path", text "small.fs"
                  "expectedSha256", text limitedHash
                  "oldText", text "anchor"
                  "newText", text "this output is too large" ]
        expectError "FILE_TOO_LARGE" tooLarge "patch output limit"
        equal "anchor" (File.ReadAllText(limitedPath, UTF8Encoding(false))) "oversized patch leaves source unchanged"
        equal 0 (Directory.GetFiles(limitedProject, ".agentlang-replace-*.tmp").Length) "oversized patch leaves no temporary file"

        let logs = dispatcher.OperationLog
        let sequence = logs |> Seq.map (fun item -> item["sequence"].GetValue<int64>()) |> Seq.toList
        equal [ 1L .. int64 logs.Count ] sequence "patch events retain monotonic ordered operation metadata"
        check (logs.ToJsonString().Contains("patch", StringComparison.Ordinal)) "operation log records patch operations"
        check (logs.ToJsonString().Contains("answer.fs", StringComparison.Ordinal)) "successful patch metadata includes its normalized path"
        let allowedLogKeys = Set.ofList [ "sequence"; "operation"; "status"; "path"; "durationMilliseconds"; "exitCode"; "timedOut" ]
        for entry in logs do
            let keys = entry.AsObject() |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
            check (Set.isSubset keys allowedLogKeys) "operation log entries retain the metadata-only schema"
        let logJson = logs.ToJsonString()
        check (not (logJson.Contains("let tail = \"keep\"", StringComparison.Ordinal))) "operation logs omit source contents"
        check (not (logJson.Contains("oldText", StringComparison.Ordinal))) "operation logs omit patch request fields"

    let private sourceForValidation body =
        $"""open System

[<EntryPoint>]
let main _ =
{body}
"""

    let private testFixedDotnetValidation root =
        let project = makeProject root "validation"
        let projectFile = Path.Combine(project, "Synthetic.fsproj")
        let guardedProject =
            File.ReadAllText(projectFile).Replace(
                "</Project>",
                """  <PropertyGroup>
    <NuGetAudit>true</NuGetAudit>
  </PropertyGroup>
  <Target Name="RequireNuGetAuditDisabled" BeforeTargets="Restore;Build">
    <Error Condition="'$(NuGetAudit)' != 'false'" Text="Validation must disable NuGetAudit." />
  </Target>
</Project>""",
                StringComparison.Ordinal)
        File.WriteAllText(projectFile, guardedProject)
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
            let buildCommand =
                buildData["command"].AsArray()
                |> Seq.map (fun argument -> argument.GetValue<string>())
                |> Seq.toList
            equal [ "build"; "Synthetic.fsproj"; "--nologo"; "-p:NuGetAudit=false" ] buildCommand "build response reports the NuGet audit override"
            let runValidator = defaultDispatcher project ValidationAction.Run 120_000 2048
            let ran = call runValidator "validate" []
            let runText = propertyString ran "text" ""
            check (isOk ran) $"fixed dotnet run passes: {runText}"
            let runData = ran["data"]
            let runCommand =
                runData["command"].AsArray()
                |> Seq.map (fun argument -> argument.GetValue<string>())
                |> Seq.toList
            equal [ "run"; "--project"; "Synthetic.fsproj"; "--no-launch-profile"; "-p:NuGetAudit=false" ] runCommand "run response reports the NuGet audit override"
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
            testDeterministicOverviewAndInspectCompatibility root
            testOverviewAndSearchTraversalLimits root
            testDeterministicBoundedSearch root
            testCompareAndSwapAtomicReplacement root
            testExactCompareAndSwapPatch root
            testFixedDotnetValidation root
            printfn "PASS: %d conventional tool assertions" assertions
            0
        finally
            removeRoot root
