namespace AgentLang

open System
open System.IO
open System.Security
open System.Text

/// Selects the provider used for filesystem effects performed by an Engine.
[<RequireQualifiedAccess>]
type FileSystemMode =
    /// Read and write files beneath the Engine's project directory.
    | Real
    /// Keep files in the Engine's in-memory, snapshot-aware virtual filesystem.
    | Virtual

type internal FileSystemFailure =
    { Code: string
      Message: string
      Expected: string list
      Actual: string list }

/// Project-rooted UTF-8 filesystem operations used by the runtime provider.
/// Reparse-point checks reduce accidental traversal through links. They are
/// intentionally not a race-proof security boundary against concurrent changes.
module internal FileSystem =
    let private failure code message expected actual =
        { Code = code
          Message = message
          Expected = expected
          Actual = actual }

    let private ioFailure operation path (exceptionValue: exn) =
        match exceptionValue with
        | :? UnauthorizedAccessException
        | :? SecurityException ->
            failure "EFFECT_FILE_ACCESS_DENIED" $"The host denied permission to {operation} file '{path}'." [ "permitted project file access" ] [ path ]
        | _ ->
            failure "EFFECT_FILE_IO" $"The host could not {operation} file '{path}'." [ "accessible project file" ] [ path; exceptionValue.GetType().Name ]

    let private comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal

    let private isMissing (exceptionValue: exn) =
        match exceptionValue with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> true
        | _ -> false

    let private pathFailure path =
        failure "EFFECT_FILE_PATH_INVALID" $"File path '{path}' must stay beneath the project directory." [ "relative path within project directory" ] [ path ]

    let private rootUnavailable () =
        failure "EFFECT_FILE_ROOT_UNAVAILABLE" "Filesystem effects require an Engine project directory." [ "project directory" ] []

    let private tryLinkTarget (path: string) =
        let read (info: FileSystemInfo) =
            try
                info.Refresh()
                Option.ofObj info.LinkTarget
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> None
        match read (FileInfo(path) :> FileSystemInfo) with
        | Some target -> Some target
        | None -> read (DirectoryInfo(path) :> FileSystemInfo)

    let private getAttributesIfPresent (path: string) =
        try
            Some(File.GetAttributes path)
        with
        | exceptionValue when isMissing exceptionValue -> None

    let private reparsePointFailure path =
        failure "EFFECT_FILE_SYMLINK_UNSUPPORTED" $"File path '{path}' traverses a symbolic link or reparse point." [ "path without symbolic links or reparse points" ] [ path ]

    let private validateReparsePoints (root: string) (fullPath: string) (originalPath: string) =
        try
            let relative = Path.GetRelativePath(root, fullPath)
            let segments = relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
            let mutable current = root
            let mutable failureValue: FileSystemFailure option = None
            let mutable stop = false
            for segment in segments do
                if not stop && segment <> "." then
                    current <- Path.Combine(current, segment)
                    match getAttributesIfPresent current, tryLinkTarget current with
                    | Some attributes, _ when attributes.HasFlag(FileAttributes.ReparsePoint) ->
                        failureValue <- Some(reparsePointFailure originalPath)
                        stop <- true
                    | _, Some _ ->
                        failureValue <- Some(reparsePointFailure originalPath)
                        stop <- true
                    | None, _ ->
                        // A later component cannot exist when an ancestor is absent.
                        stop <- true
                    | _ -> ()
            match failureValue with
            | Some value -> Error value
            | None -> Ok fullPath
        with
        | exceptionValue -> Error(ioFailure "inspect" originalPath exceptionValue)

    let private resolve (projectDirectory: string option) (operation: string) (path: string) =
        match projectDirectory with
        | None -> Error(rootUnavailable ())
        | Some root when String.IsNullOrEmpty path -> Error(pathFailure path)
        | Some root ->
            try
                if Path.IsPathRooted path then Error(pathFailure path)
                else
                    let canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
                    let fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, path))
                    let rootPrefix =
                        if canonicalRoot.EndsWith(string Path.DirectorySeparatorChar, comparison) then canonicalRoot
                        else canonicalRoot + string Path.DirectorySeparatorChar
                    let contained =
                        String.Equals(fullPath, canonicalRoot, comparison)
                        || fullPath.StartsWith(rootPrefix, comparison)
                    if not contained || String.Equals(fullPath, canonicalRoot, comparison) then Error(pathFailure path)
                    else validateReparsePoints canonicalRoot fullPath path
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> Error(pathFailure path)
            | exceptionValue -> Error(ioFailure operation path exceptionValue)

    let private encoding = UTF8Encoding(false, true)
    let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

    let readText (projectDirectory: string option) (operation: string) (path: string) =
        match resolve projectDirectory operation path with
        | Error value -> Error value
        | Ok fullPath ->
            try
                let bytes = File.ReadAllBytes fullPath
                let offset = if bytes.Length >= utf8Bom.Length && Array.forall2 (=) utf8Bom bytes[0..2] then utf8Bom.Length else 0
                Ok(encoding.GetString(bytes, offset, bytes.Length - offset))
            with
            | exceptionValue when isMissing exceptionValue ->
                Error(failure "EFFECT_FILE_NOT_FOUND" $"File '{path}' does not exist." [ "existing project file" ] [ path ])
            | :? DecoderFallbackException ->
                Error(failure "EFFECT_FILE_ENCODING" $"File '{path}' is not valid UTF-8." [ "valid UTF-8" ] [ path ])
            | exceptionValue -> Error(ioFailure "read" path exceptionValue)

    let fileExists (projectDirectory: string option) (operation: string) (path: string) =
        match resolve projectDirectory operation path with
        | Error value -> Error value
        | Ok fullPath ->
            try
                match File.GetAttributes fullPath with
                | attributes when attributes.HasFlag(FileAttributes.Directory) -> Ok false
                | _ -> Ok true
            with
            | exceptionValue when isMissing exceptionValue -> Ok false
            | exceptionValue -> Error(ioFailure "check" path exceptionValue)

    let writeText (projectDirectory: string option) (operation: string) (path: string) (contents: string) =
        match resolve projectDirectory operation path with
        | Error value -> Error value
        | Ok fullPath ->
            try
                let bytes = encoding.GetBytes contents
                File.WriteAllBytes(fullPath, bytes)
                Ok()
            with
            | :? EncoderFallbackException ->
                Error(failure "EFFECT_FILE_ENCODING" $"Text for file '{path}' cannot be encoded as UTF-8." [ "valid Unicode text" ] [ path ])
            | exceptionValue -> Error(ioFailure "write" path exceptionValue)
