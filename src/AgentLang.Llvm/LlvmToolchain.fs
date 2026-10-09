namespace AgentLang.Llvm

open System
open System.Diagnostics
open System.IO

[<RequireQualifiedAccess>]
type LlvmOptimization =
    | O0
    | O2

[<RequireQualifiedAccess>]
type OwningRuntimeProfile =
    | Diagnostic
    | TrustedGenerated

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

    let private optimizationArgument = function
        | LlvmOptimization.O0 -> "-O0"
        | LlvmOptimization.O2 -> "-O2"

    let private compileLlvmObject (toolchain: LlvmToolchain) optimization (llvmIrPath: string) (objectPath: string) workingDirectory =
        if File.Exists objectPath then File.Delete objectPath
        run toolchain.ClangPath
            [ "--target=x86_64-pc-windows-msvc"
              "-x"; "ir"
              "-c"; Path.GetFullPath llvmIrPath
              optimizationArgument optimization
              "-o"; objectPath ]
            workingDirectory

    let private compileCObject (toolchain: LlvmToolchain) optimization (runtimeProfile: OwningRuntimeProfile) (sourcePath: string) (includeDirectory: string) (objectPath: string) workingDirectory =
        if File.Exists objectPath then File.Delete objectPath
        let profileArguments =
            match runtimeProfile with
            | OwningRuntimeProfile.Diagnostic -> []
            | OwningRuntimeProfile.TrustedGenerated -> [ "-DAL_OWNING_TRUSTED_GENERATED=1" ]
        let arguments =
            [ "--target=x86_64-pc-windows-msvc"
              "-std=c11"
              "-ffreestanding"
              "-fno-builtin"
              "-Wall"
              "-Wextra"
              "-Werror"
              optimizationArgument optimization ]
            @ profileArguments
            @ [
              "-c"; Path.GetFullPath sourcePath
              "-I"; Path.GetFullPath includeDirectory
              "-o"; objectPath ]
        run toolchain.ClangPath arguments workingDirectory

    let private linkObjects (toolchain: LlvmToolchain) (outputDllPath: string) (objectPaths: string list) =
        if File.Exists outputDllPath then File.Delete outputDllPath
        let workingDirectory = Path.GetDirectoryName outputDllPath
        let runtimeLibraries = toolchain.CompilerRuntimeLibrary |> Option.toList
        run toolchain.LldLinkPath
            ([ "/dll"; "/noentry"; "/nodefaultlib"; "/machine:x64"
               $"/out:{outputDllPath}" ] @ objectPaths @ runtimeLibraries)
            workingDirectory
        if not (File.Exists outputDllPath) then
            raise (InvalidOperationException($"lld-link did not create its requested DLL: {outputDllPath}"))
        outputDllPath

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
        compileLlvmObject toolchain optimization irPath objectPath workingDirectory |> ignore
        linkObjects toolchain dllPath [ objectPath ]

    /// Compile the freestanding arena runtime at the same optimization level
    /// as the emitted LLVM body and link both fresh objects into one DLL.
    let compileLibraryWithRuntime
        (toolchain: LlvmToolchain)
        optimization
        (llvmIrPath: string)
        (runtimeSourcePath: string)
        (runtimeIncludeDirectory: string)
        (outputDllPath: string) =
        let irPath = Path.GetFullPath llvmIrPath
        let sourcePath = Path.GetFullPath runtimeSourcePath
        let includeDirectory = Path.GetFullPath runtimeIncludeDirectory
        let dllPath = Path.GetFullPath outputDllPath
        let workingDirectory = Path.GetDirectoryName dllPath
        Directory.CreateDirectory workingDirectory |> ignore
        if not (File.Exists irPath) then invalidArg (nameof llvmIrPath) "LLVM IR input does not exist."
        if not (File.Exists sourcePath) then invalidArg (nameof runtimeSourcePath) "Native runtime source does not exist."
        if not (Directory.Exists includeDirectory) then invalidArg (nameof runtimeIncludeDirectory) "Native runtime include directory does not exist."
        let llvmObjectPath = Path.Combine(workingDirectory, "agentlang-native.obj")
        let runtimeObjectPath = Path.Combine(workingDirectory, "agentlang-arena-runtime.obj")
        compileLlvmObject toolchain optimization irPath llvmObjectPath workingDirectory |> ignore
        if File.Exists runtimeObjectPath then File.Delete runtimeObjectPath
        run toolchain.ClangPath
            [ "--target=x86_64-pc-windows-msvc"
              "-std=c11"
              "-ffreestanding"
              "-fno-builtin"
              "-Wall"
              "-Wextra"
              "-Werror"
              optimizationArgument optimization
              "-c"; sourcePath
              "-I"; includeDirectory
              "-o"; runtimeObjectPath ]
            workingDirectory
        linkObjects toolchain dllPath [ llvmObjectPath; runtimeObjectPath ]

    /// Compile a deterministic set of per-entry LLVM objects, one immutable
    /// module-metadata C object, and one arena runtime object into a DLL.
    /// Returns the DLL path and the object paths in link order.
    let compileModuleLibraryWithProfile
        (toolchain: LlvmToolchain)
        optimization
        (runtimeProfile: OwningRuntimeProfile)
        (llvmIrPaths: string list)
        (metadataSourcePath: string)
        (runtimeSourcePath: string)
        (includeDirectory: string)
        (outputDllPath: string) =
        if List.isEmpty llvmIrPaths then invalidArg (nameof llvmIrPaths) "A native module must contain at least one entry object."
        let irPaths = llvmIrPaths |> List.map Path.GetFullPath
        let sourcePath = Path.GetFullPath metadataSourcePath
        let runtimePath = Path.GetFullPath runtimeSourcePath
        let includePath = Path.GetFullPath includeDirectory
        let dllPath = Path.GetFullPath outputDllPath
        let workingDirectory = Path.GetDirectoryName dllPath
        Directory.CreateDirectory workingDirectory |> ignore
        for irPath in irPaths do
            if not (File.Exists irPath) then invalidArg (nameof llvmIrPaths) $"LLVM IR input does not exist: {irPath}"
        if not (File.Exists sourcePath) then invalidArg (nameof metadataSourcePath) "Native module metadata source does not exist."
        if not (File.Exists runtimePath) then invalidArg (nameof runtimeSourcePath) "Native runtime source does not exist."
        if not (Directory.Exists includePath) then invalidArg (nameof includeDirectory) "Native module include directory does not exist."

        let entryObjects =
            irPaths
            |> List.mapi (fun index irPath ->
                let objectPath = Path.Combine(workingDirectory, $"agentlang-entry-{index:D3}.obj")
                compileLlvmObject toolchain optimization irPath objectPath workingDirectory |> ignore
                objectPath)
        let metadataObjectPath = Path.Combine(workingDirectory, "agentlang-module-metadata.obj")
        compileCObject toolchain optimization runtimeProfile sourcePath includePath metadataObjectPath workingDirectory
        let runtimeObjectPath = Path.Combine(workingDirectory, "agentlang-arena-runtime.obj")
        compileCObject toolchain optimization runtimeProfile runtimePath includePath runtimeObjectPath workingDirectory
        let objectPaths = entryObjects @ [ metadataObjectPath; runtimeObjectPath ]
        linkObjects toolchain dllPath objectPaths, objectPaths

    /// Compile a module with diagnostic runtime checks enabled.
    let compileModuleLibrary
        (toolchain: LlvmToolchain)
        optimization
        (llvmIrPaths: string list)
        (metadataSourcePath: string)
        (runtimeSourcePath: string)
        (includeDirectory: string)
        (outputDllPath: string) =
        compileModuleLibraryWithProfile
            toolchain optimization OwningRuntimeProfile.Diagnostic
            llvmIrPaths metadataSourcePath runtimeSourcePath includeDirectory outputDllPath
