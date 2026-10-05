namespace AgentLang.Conventional.Cli.Tests

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

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
                let exitCode = AgentLang.Conventional.Cli.Program.main arguments
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

    let private replaceRequest expectedSha256 content =
        JsonSerializer.Serialize
            {| op = "replace"
               path = "Target.fs"
               expectedSha256 = expectedSha256
               content = content |}

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
        group "malformed lines consume request budget" testMalformedLineConsumesBudget
        group "default 100 request boundary" testDefaultCapBoundaries
        group "invalid startup is rejected before dispatch" testInvalidStartupDoesNotDispatch
        printfn "PASS %d groups, %d assertions" groups assertions
        0
