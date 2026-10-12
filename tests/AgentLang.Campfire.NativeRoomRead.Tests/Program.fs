namespace AgentLang.Campfire.NativeRoomRead.Tests

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

module Program =
    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let private requestedRunRoot args =
        match args with
        | [| "--run-root"; path |] -> Path.GetFullPath path
        | [||] ->
            Path.Combine(
                repositoryRoot,
                ".agentlang",
                "campfire-native-room-read-252",
                "test-runs",
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
        | _ -> invalidArg (nameof args) "Expected no arguments or --run-root <private-path>."

    [<EntryPoint>]
    let main args =
        let runRoot = requestedRunRoot args
        Directory.CreateDirectory runRoot |> ignore
        try
            let toolchain = LlvmToolchain.discover()
            let assertionCount = NativeRoomReadTests.run toolchain repositoryRoot runRoot
            printfn "Campfire native room-read: %d assertions; passed=true; evidence=%s" assertionCount runRoot
            0
        with error ->
            let safeMessage =
                match error with
                | :? ArgumentException -> "native room-read verifier could not use the configured toolchain or fixture"
                | _ -> "native room-read verification failed; inspect the private test receipt"
            NativeRoomReadTests.writeFailureReceipt repositoryRoot runRoot safeMessage (error.ToString())
            eprintfn "%s" safeMessage
            eprintfn "Private evidence: %s" runRoot
            1
