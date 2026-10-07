namespace AgentLang.Llvm

open System
open System.Diagnostics
open System.IO

[<RequireQualifiedAccess>]
type LlvmOptimization =
    | O0
    | O2

[<Sealed>]
type LlvmToolchain internal (clangPath: string, lldLinkPath: string, compilerRuntimeLibrary: string option) =
    member _.ClangPath = clangPath
    member _.LldLinkPath = lldLinkPath
    member _.CompilerRuntimeLibrary = compilerRuntimeLibrary

[<RequireQualifiedAccess>]
module LlvmToolchain =
    let private defaultClang =
        @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe"

    let private defaultLld =
        @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\lld-link.exe"

    let private normalizedPath (path: string) =
        path |> Path.GetFullPath |> Path.TrimEndingDirectorySeparator

    let private candidatesUnderMsvcRoot (root: string) =
        let libraryAt directory = Path.Combine(directory, "lib", "x64", "libcmt.lib")
        let direct = libraryAt root
        if File.Exists direct then [ direct ]
        elif not (Directory.Exists root) then []
        else
            Directory.GetDirectories root
            |> Array.toList
            |> List.map libraryAt
            |> List.filter File.Exists

    let private discoverCompilerRuntimeLibrary () =
        let explicitPath = Environment.GetEnvironmentVariable("AGENTLANG_COMPILER_RUNTIME_LIB")
        if not (String.IsNullOrWhiteSpace explicitPath) then
            if not (File.Exists explicitPath) then
                invalidArg "AGENTLANG_COMPILER_RUNTIME_LIB" $"Compiler runtime library does not exist: {explicitPath}"
            Some(Path.GetFullPath explicitPath)
        else
            let vctoolsRoot =
                Environment.GetEnvironmentVariable("VCToolsInstallDir")
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizedPath
            let vsRoot =
                Environment.GetEnvironmentVariable("VSINSTALLDIR")
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizedPath
                |> Option.map (fun path -> Path.Combine(path, "VC", "Tools", "MSVC"))
            let msvcRoots =
                [ vctoolsRoot
                  vsRoot
                  Some @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC" ]
                |> List.choose id
                |> List.distinct
            let candidates =
                msvcRoots
                |> List.collect candidatesUnderMsvcRoot
                |> List.distinct
            candidates
            |> List.sortByDescending (fun path ->
                let versionName =
                    path
                    |> Path.GetDirectoryName
                    |> Path.GetDirectoryName
                    |> Path.GetDirectoryName
                    |> Path.GetFileName
                match Version.TryParse versionName with
                | true, version -> version
                | false, _ -> Version(0, 0))
            |> List.tryHead

    let create clangPath lldLinkPath =
        if String.IsNullOrWhiteSpace clangPath || not (File.Exists clangPath) then
            invalidArg (nameof clangPath) $"LLVM clang executable does not exist: {clangPath}"
        if String.IsNullOrWhiteSpace lldLinkPath || not (File.Exists lldLinkPath) then
            invalidArg (nameof lldLinkPath) $"LLVM lld-link executable does not exist: {lldLinkPath}"
        LlvmToolchain(clangPath, lldLinkPath, discoverCompilerRuntimeLibrary ())

    let discover () =
        let clangPath =
            Environment.GetEnvironmentVariable("AGENTLANG_LLVM_CLANG")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue defaultClang
        let lldPath =
            Environment.GetEnvironmentVariable("AGENTLANG_LLVM_LLD")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue defaultLld
        create clangPath lldPath

    let private run (executable: string) (arguments: string list) (workingDirectory: string) =
        let startInfo = ProcessStartInfo(executable)
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        for argument in arguments do
            startInfo.ArgumentList.Add argument
        use child = new Process(StartInfo = startInfo)
        if not (child.Start()) then
            raise (InvalidOperationException($"Could not start LLVM tool '{executable}'."))
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        let stdout = stdoutTask.GetAwaiter().GetResult()
        let stderr = stderrTask.GetAwaiter().GetResult()
        if child.ExitCode <> 0 then
            let joinedArguments = String.concat " " arguments
            raise (InvalidOperationException(
                $"LLVM tool failed ({child.ExitCode}): {executable} {joinedArguments}{Environment.NewLine}{stdout}{stderr}"))

    /// Compile deterministic textual LLVM IR to a Windows x64 COFF object and
    /// link a DLL with lld-link. The installed MSVC static runtime archive is
    /// added when available so legitimate stack probes remain supported; with
    /// /nodefaultlib, only archive members needed by emitted code are included.
    let compileLibrary (toolchain: LlvmToolchain) optimization (llvmIrPath: string) (outputDllPath: string) =
        let irPath = Path.GetFullPath llvmIrPath
        let dllPath = Path.GetFullPath outputDllPath
        let workingDirectory = Path.GetDirectoryName dllPath
        Directory.CreateDirectory workingDirectory |> ignore
        if not (File.Exists irPath) then invalidArg (nameof llvmIrPath) "LLVM IR input does not exist."
        let objectPath = Path.ChangeExtension(dllPath, ".obj")
        let optimizationArgument =
            match optimization with
            | LlvmOptimization.O0 -> "-O0"
            | LlvmOptimization.O2 -> "-O2"
        run toolchain.ClangPath
            [ "--target=x86_64-pc-windows-msvc"
              "-x"; "ir"
              "-c"; irPath
              optimizationArgument
              "-o"; objectPath ]
            workingDirectory
        let runtimeLibraries = toolchain.CompilerRuntimeLibrary |> Option.toList
        run toolchain.LldLinkPath
            ([ "/dll"; "/noentry"; "/nodefaultlib"; "/machine:x64"
               $"/out:{dllPath}"; objectPath ] @ runtimeLibraries)
            workingDirectory
        if not (File.Exists dllPath) then
            raise (InvalidOperationException($"lld-link did not create its requested DLL: {dllPath}"))
        dllPath
