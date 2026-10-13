namespace AgentLang.Campfire.NativeRoomRead.Tests

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

module Program =
    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let private requestedOptions (args: string array) =
        let mutable runRoot: string option = None
        let mutable runtimeProfile = "Diagnostic"
        let mutable index = 0
        while index < args.Length do
            match args[index] with
            | "--run-root" when index + 1 < args.Length ->
                runRoot <- Some(Path.GetFullPath args[index + 1])
                index <- index + 2
            | "--runtime-profile" when index + 1 < args.Length ->
                runtimeProfile <- args[index + 1]
                index <- index + 2
            | _ -> invalidArg (nameof args) "Expected --run-root <private-path> and optional --runtime-profile Diagnostic|TrustedGenerated."
        let selectedProfile =
            match runtimeProfile with
            | "Diagnostic" -> OwningRuntimeProfile.Diagnostic
            | "TrustedGenerated" -> OwningRuntimeProfile.TrustedGenerated
            | _ -> invalidArg (nameof runtimeProfile) "Runtime profile must be Diagnostic or TrustedGenerated."
        let selectedRunRoot =
            runRoot
            |> Option.defaultWith (fun () ->
                Path.Combine(
                    repositoryRoot,
                    ".agentlang",
                    "campfire-native-room-read-252",
                    "test-runs",
                    DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")))
        selectedRunRoot, runtimeProfile, selectedProfile

    [<EntryPoint>]
    let main args =
        let runRoot, runtimeProfileName, runtimeProfile = requestedOptions args
        Directory.CreateDirectory runRoot |> ignore
        try
            let toolchain = LlvmToolchain.discover()
            let assertionCount = NativeRoomReadTests.run toolchain repositoryRoot runRoot runtimeProfile
            printfn "Campfire native room-read (%s): %d assertions; passed=true; evidence=%s" runtimeProfileName assertionCount runRoot
            0
        with error ->
            let safeMessage =
                match error with
                | :? ArgumentException -> "native room-read verifier could not use the configured toolchain or fixture"
                | _ -> "native room-read verification failed; inspect the private test receipt"
            NativeRoomReadTests.writeFailureReceipt repositoryRoot runRoot runtimeProfileName safeMessage (error.ToString())
            eprintfn "%s" safeMessage
            eprintfn "Private evidence: %s" runRoot
            1
