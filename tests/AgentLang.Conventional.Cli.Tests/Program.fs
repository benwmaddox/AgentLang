namespace AgentLang.Conventional.Cli.Tests

open System
open System.Globalization
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

module Program =
    type private Invocation =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    let mutable private assertions = 0
    let mutable private groups = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            failwith $"{message}: expected {expected}, got {actual}"

    let private group name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private runCli arguments (inputLines: string list) =
        let previousInput = Console.In
        let previousOutput = Console.Out
        let previousError = Console.Error
        use input = new StringReader(String.concat Environment.NewLine inputLines + (if inputLines.IsEmpty then "" else Environment.NewLine))
        use output = new StringWriter(CultureInfo.InvariantCulture)
        use error = new StringWriter(CultureInfo.InvariantCulture)

        let invocation =
            try
                Console.SetIn input
                Console.SetOut output
                Console.SetError error
                let exitCode = AgentLang.Conventional.Cli.Program.run arguments
                { ExitCode = exitCode
                  StandardOutput = output.ToString()
                  StandardError = error.ToString() }
            finally
                Console.SetIn previousInput
                Console.SetOut previousOutput
                Console.SetError previousError

        check (Object.ReferenceEquals(previousInput, Console.In)) "CLI restores Console.In"
        check (Object.ReferenceEquals(previousOutput, Console.Out)) "CLI restores Console.Out"
        check (Object.ReferenceEquals(previousError, Console.Error)) "CLI restores Console.Error"
        invocation

    let private conventionalCliAssembly () =
        let outputDirectory = DirectoryInfo(AppContext.BaseDirectory.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]))
        let configuration = outputDirectory.Parent.Name
        let assembly =
            Path.GetFullPath(
                Path.Combine(
                    __SOURCE_DIRECTORY__,
                    "..",
                    "..",
                    "experiments",
                    "AgentLang.Conventional.Cli",
                    "bin",
                    configuration,
                    "net9.0",
                    "AgentLang.Conventional.Cli.dll"))
        if not (File.Exists assembly) then failwith $"Conventional CLI assembly was not built: {assembly}"
        assembly

    let private runCliWithUtf8Stdin arguments (inputLines: string list) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.StandardInputEncoding <- UTF8Encoding(false)
        startInfo.StandardOutputEncoding <- UTF8Encoding(false)
        startInfo.StandardErrorEncoding <- UTF8Encoding(false)
        startInfo.ArgumentList.Add(conventionalCliAssembly ())
        for argument in arguments do startInfo.ArgumentList.Add(argument)

        use child = new Process(StartInfo = startInfo)
        if not (child.Start()) then failwith "Could not start the Conventional CLI process."
        let stdout: Task<string> = child.StandardOutput.ReadToEndAsync()
        let stderr: Task<string> = child.StandardError.ReadToEndAsync()
        let outputTasks: Task array = [| stdout :> Task; stderr :> Task |]
        let outputDiagnostic (task: Task<string>) =
            if task.IsCompletedSuccessfully then task.Result
            elif task.IsFaulted then task.Exception.ToString()
            else "<stream read did not complete>"
        for line in inputLines do child.StandardInput.WriteLine(line)
        child.StandardInput.Close()
        if not (child.WaitForExit 30000) then
            child.Kill(true)
            child.WaitForExit()
            let streamsDrained = Task.WaitAll(outputTasks, 10000)
            failwith $"Conventional CLI did not exit within 30000 ms. Output reads drained: {streamsDrained}. stdout: {outputDiagnostic stdout}; stderr: {outputDiagnostic stderr}"
        if not (Task.WaitAll(outputTasks, 10000)) then
            failwith $"Conventional CLI exited, but output reads did not complete within 10000 ms. Exit code: {child.ExitCode}. stdout: {outputDiagnostic stdout}; stderr: {outputDiagnostic stderr}"
        { ExitCode = child.ExitCode
          StandardOutput = stdout.Result
          StandardError = stderr.Result }

    let private responseLines (invocation: Invocation) =
        invocation.StandardOutput.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let private responseSucceeded (line: string) =
        use document = JsonDocument.Parse(line)
        document.RootElement.GetProperty("ok").GetBoolean()

    let private responseErrorCode (line: string) =
        use document = JsonDocument.Parse(line)
        document.RootElement.GetProperty("error").GetProperty("code").GetString()

    let private searchRequest query =
        JsonSerializer.Serialize {| op = "search"; query = query |}

    let private emptyInspectRequest = """{"op":"inspect"}"""

    let private emptyNestedInspectRequest = """{"op":"inspect","args":{}}"""

    let private replaceRequest expectedSha256 content =
        JsonSerializer.Serialize
            {| op = "replace"
               path = "Target.fs"
               expectedSha256 = expectedSha256
               content = content |}

    let private patchRequest expectedSha256 oldText newText =
        JsonSerializer.Serialize
            {| op = "patch"
               path = "Target.fs"
               expectedSha256 = expectedSha256
               oldText = oldText
               newText = newText |}

    let private nestedPatchRequest expectedSha256 oldText newText =
        JsonSerializer.Serialize
            {| op = "patch"
               args =
                {| path = "Target.fs"
                   expectedSha256 = expectedSha256
                   oldText = oldText
                   newText = newText |} |}

    let private baseArguments projectRoot =
        [| "--project"
           projectRoot
           "--jsonl"
           "--validation-project"
           "Validation.fsproj" |]

    let private argumentsWithLimit projectRoot limit =
        Array.append (baseArguments projectRoot) [| "--max-requests"; string limit |]

    let private withScratchProject action =
        let tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        let directoryName = $"agentlang-cli-tests-{Guid.NewGuid():N}"
        let path = Path.GetFullPath(Path.Combine(tempRoot, directoryName))
        let pathComparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        if Path.GetDirectoryName(path) <> tempRoot
           || not (Path.GetFileName(path).StartsWith("agentlang-cli-tests-", pathComparison)) then
            failwith "Refusing to create a scratch project outside the expected temporary directory prefix."

        Directory.CreateDirectory(path) |> ignore
        let originalText = "module Fixture\nlet marker = 7\n"
        let changedText = "module Fixture\nlet marker = 8\n"
        let target = Path.Combine(path, "Target.fs")
        File.WriteAllText(target, originalText, UTF8Encoding(false))
        File.WriteAllText(Path.Combine(path, "Validation.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", UTF8Encoding(false))

        try
            action path target originalText changedText
        finally
            if Directory.Exists path then Directory.Delete(path, true)

    let private fileHash path =
        File.ReadAllBytes(path)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private textHash (value: string) =
        UTF8Encoding(false).GetBytes(value)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    let private assertSuccessfulResponses expectedCount (invocation: Invocation) =
        let lines = responseLines invocation
        equal expectedCount lines.Length "response count"
        for line in lines do check (responseSucceeded line) "expected a successful JSONL response"

    let private testDefaultAndCustomExactLimits () =
        withScratchProject (fun projectRoot _ _ _ ->
            let request = searchRequest "marker"
            let normal = runCli (baseArguments projectRoot) [ request; request ]
            equal 0 normal.ExitCode $"default limit allows ordinary requests; stderr={normal.StandardError}; stdout={normal.StandardOutput}"
            assertSuccessfulResponses 2 normal

            let exact = runCli (argumentsWithLimit projectRoot 2) [ request; request ]
            equal 0 exact.ExitCode "EOF exactly at a configured limit exits successfully"
            assertSuccessfulResponses 2 exact)

    let private testExcessMutationIsRejected () =
        withScratchProject (fun projectRoot target originalText changedText ->
            let request = searchRequest "marker"
            let mutation = replaceRequest (fileHash target) changedText
            let invocation =
                runCli
                    (argumentsWithLimit projectRoot 2)
                    [ request; request; mutation; mutation ]
            let lines = responseLines invocation

            equal 2 invocation.ExitCode "an over-limit session exits with code 2"
            equal 3 lines.Length "the host emits one response for each accepted line and one limit response"
            check (responseSucceeded lines[0]) "first in-limit request succeeds"
            check (responseSucceeded lines[1]) "second in-limit request succeeds"
            equal "PROTOCOL_REQUEST_LIMIT" (responseErrorCode lines[2]) "the third line receives the limit error"
            equal originalText (File.ReadAllText(target)) "over-limit mutation is never dispatched")

    let private testPatchTopLevelAndNestedJsonlRecovery () =
        withScratchProject (fun projectRoot target originalText changedText ->
            let originalHash = fileHash target
            let changedHash = textHash changedText
            let topLevel = patchRequest originalHash "marker = 7" "marker = 8"
            let rejected = patchRequest changedHash "anchor is absent" "unused"
            let nested = nestedPatchRequest changedHash "marker = 8" "marker = 7"
            let invocation =
                runCli
                    (argumentsWithLimit projectRoot 3)
                    [ topLevel; rejected; nested ]
            let lines = responseLines invocation

            equal 0 invocation.ExitCode $"valid requests after a rejected patch keep the JSONL session alive; stderr={invocation.StandardError}"
            equal 3 lines.Length "each top-level, rejected, and nested patch gets a response"
            check (responseSucceeded lines[0]) "flat top-level patch arguments are accepted"
            equal "PATCH_ANCHOR_NOT_FOUND" (responseErrorCode lines[1]) "missing anchor returns a structured patch error"
            check (responseSucceeded lines[2]) "nested args patch is accepted after an error"
            equal originalText (File.ReadAllText(target)) "nested patch can restore the exact original contents after recovery")

    let private testNoArgumentInspectOverviewJsonl () =
        withScratchProject (fun projectRoot _ _ _ ->
            let invocation = runCli (baseArguments projectRoot) [ emptyInspectRequest; emptyNestedInspectRequest ]
            equal 0 invocation.ExitCode $"no-argument inspect is available over JSONL; stderr={invocation.StandardError}"
            let lines = responseLines invocation
            equal 2 lines.Length "each no-argument inspect request receives a JSONL response"
            check (responseSucceeded lines[0]) "top-level no-argument inspect succeeds"
            check (responseSucceeded lines[1]) "empty nested args inspect succeeds"

            use first = JsonDocument.Parse(lines[0])
            use second = JsonDocument.Parse(lines[1])
            let firstData = first.RootElement.GetProperty("data")
            let secondData = second.RootElement.GetProperty("data")
            let firstDataJson = firstData.GetRawText()
            let secondDataJson = secondData.GetRawText()
            equal firstDataJson secondDataJson "no-argument overview is deterministic across JSONL encodings"
            equal "Project overview. Send op and fields from one argument set at the top level; extra fields are rejected." (first.RootElement.GetProperty("text").GetString()) "overview response teaches the canonical wire shape"
            equal "Validation.fsproj" (firstData.GetProperty("validationProject").GetString()) "overview exposes the configured relative validation target"
            let paths = firstData.GetProperty("files").EnumerateArray() |> Seq.map (fun path -> path.GetString()) |> Seq.toList
            equal [ "Target.fs"; "Validation.fsproj" ] paths "overview exposes bounded supported-source paths"
            equal 6 (firstData.GetProperty("operations").GetArrayLength()) "overview exposes all six operation schemas"
            let schemas = firstData.GetProperty("operations").EnumerateArray() |> Seq.toArray
            equal "inspect" (schemas[0].GetProperty("op").GetString()) "inspect is the first documented operation"
            equal "[[],[{\"name\":\"path\",\"type\":\"string\",\"required\":true}]]" (schemas[0].GetProperty("argumentSets").GetRawText()) "inspect schema documents both no-argument and top-level path forms"
            equal "[[{\"name\":\"path\",\"type\":\"string\",\"required\":true},{\"name\":\"expectedSha256\",\"type\":\"string\",\"required\":true},{\"name\":\"oldText\",\"type\":\"string\",\"required\":true},{\"name\":\"newText\",\"type\":\"string\",\"required\":true}]]" (schemas[4].GetProperty("argumentSets").GetRawText()) "patch schema documents typed canonical top-level fields"
            let mutable content = Unchecked.defaultof<JsonElement>
            check (not (firstData.TryGetProperty("content", &content))) "overview response has no source-content field"
            check (not (firstData.GetProperty("truncated").GetBoolean())) "small fixture overview is complete")

    let private testUtf8ReadPathAndContent () =
        withScratchProject (fun projectRoot _ _ _ ->
            let relativePath = "配置 Ω 🌿.fs"
            let expectedContent = "module Fixture\nlet greeting = \"Ω π 🌿 配置\"\n"
            File.WriteAllText(Path.Combine(projectRoot, relativePath), expectedContent, UTF8Encoding(false))

            let request = """{"op":"read","path":"配置 Ω 🌿.fs"}"""
            let invocation = runCliWithUtf8Stdin (baseArguments projectRoot) [ request ]
            equal 0 invocation.ExitCode $"UTF-8 JSONL read request; stderr={invocation.StandardError}; stdout={invocation.StandardOutput}"
            let lines = responseLines invocation
            equal 1 lines.Length "Unicode read request receives one JSONL response"
            use response = JsonDocument.Parse(lines.Head)
            check (response.RootElement.GetProperty("ok").GetBoolean()) "Unicode read request succeeds"
            let data = response.RootElement.GetProperty("data")
            equal relativePath (data.GetProperty("path").GetString()) "literal Unicode request path selects the matching file"
            equal expectedContent (data.GetProperty("content").GetString()) "read response preserves the Unicode file contents exactly")

    let private testPatchRespectsRequestLimit () =
        withScratchProject (fun projectRoot target originalText _ ->
            let patch = patchRequest (fileHash target) "marker = 7" "marker = 8"
            let invocation =
                runCli
                    (argumentsWithLimit projectRoot 1)
                    [ searchRequest "marker"; patch ]
            let lines = responseLines invocation

            equal 2 invocation.ExitCode "patch after the request cap returns the established limit exit code"
            equal 2 lines.Length "over-limit patch receives one limit response"
            check (responseSucceeded lines[0]) "request before the cap is dispatched"
            equal "PROTOCOL_REQUEST_LIMIT" (responseErrorCode lines[1]) "over-limit patch is rejected by the request limit"
            equal originalText (File.ReadAllText(target)) "over-limit patch cannot mutate the project")

    let private testMalformedLineConsumesBudget () =
        withScratchProject (fun projectRoot target originalText changedText ->
            let mutation = replaceRequest (fileHash target) changedText
            let invocation = runCli (argumentsWithLimit projectRoot 1) [ "{"; mutation ]
            let lines = responseLines invocation

            equal 2 invocation.ExitCode "a request after a malformed line exceeds the limit"
            equal 2 lines.Length "malformed request and limit each receive a response"
            equal "PROTOCOL_INVALID_JSON" (responseErrorCode lines[0]) "malformed JSON has its parse error"
            equal "PROTOCOL_REQUEST_LIMIT" (responseErrorCode lines[1]) "the following line is rejected by the limit"
            equal originalText (File.ReadAllText(target)) "mutation after malformed line is never dispatched")

    let private testDefaultCapBoundaries () =
        withScratchProject (fun projectRoot _ _ _ ->
            let request = searchRequest "marker"
            let exactlyOneHundred = runCli (baseArguments projectRoot) (List.replicate 100 request)
            equal 0 exactlyOneHundred.ExitCode "EOF after the default 100 requests exits successfully"
            assertSuccessfulResponses 100 exactlyOneHundred

            let oneHundredAndOne = runCli (baseArguments projectRoot) (List.replicate 101 request)
            let lines = responseLines oneHundredAndOne
            equal 2 oneHundredAndOne.ExitCode "the 101st default-limit request exits with code 2"
            equal 101 lines.Length "the 101st request receives one limit response"
            for line in lines[0 .. 99] do check (responseSucceeded line) "each of the first 100 requests is accepted"
            equal "PROTOCOL_REQUEST_LIMIT" (responseErrorCode lines[100]) "the 101st request receives the limit error")

    let private testInvalidStartupDoesNotDispatch () =
        withScratchProject (fun projectRoot target originalText changedText ->
            let mutation = replaceRequest (fileHash target) changedText
            let validArguments = baseArguments projectRoot
            let invalidCases =
                [ "missing project value", [| "--project"; "--jsonl"; "--validation-project"; "Validation.fsproj" |]
                  "missing validation value", [| "--project"; projectRoot; "--jsonl"; "--validation-project" |]
                  "missing request limit value", Array.append validArguments [| "--max-requests" |]
                  "duplicate project", Array.append validArguments [| "--project"; projectRoot |]
                  "duplicate request limit", Array.append (argumentsWithLimit projectRoot 1) [| "--max-requests"; "1" |]
                  "duplicate help", [| "--help"; "--help" |]
                  "unknown option", Array.append validArguments [| "--unknown" |]
                  "zero request limit", Array.append validArguments [| "--max-requests"; "0" |]
                  "out-of-range request limit", Array.append validArguments [| "--max-requests"; "101" |] ]

            for name, arguments in invalidCases do
                let invocation = runCli arguments [ mutation ]
                equal 64 invocation.ExitCode $"{name} fails during startup"
                equal 0 (responseLines invocation).Length $"{name} does not enter JSONL dispatch"
                check (invocation.StandardError.Contains("Host configuration error", StringComparison.Ordinal)) $"{name} reports a startup error"
                equal originalText (File.ReadAllText(target)) $"{name} leaves the project unchanged" )

    [<EntryPoint>]
    let main _ =
        group "default and custom exact limits" testDefaultAndCustomExactLimits
        group "excess mutation rejected before dispatch" testExcessMutationIsRejected
        group "top-level and nested patch JSONL recovery" testPatchTopLevelAndNestedJsonlRecovery
        group "no-argument inspect overview JSONL" testNoArgumentInspectOverviewJsonl
        group "raw UTF-8 JSONL read path and content" testUtf8ReadPathAndContent
        group "patch respects the request limit" testPatchRespectsRequestLimit
        group "malformed lines consume request budget" testMalformedLineConsumesBudget
        group "default 100 request boundary" testDefaultCapBoundaries
        group "invalid startup is rejected before dispatch" testInvalidStartupDoesNotDispatch
        printfn "PASS %d groups, %d assertions" groups assertions
        0
