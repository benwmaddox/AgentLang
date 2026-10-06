namespace AgentLang.Conventional

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ValidationAction =
    | Build
    | Run

/// A validation command is selected by the host when it constructs the dispatcher.
/// Agent requests can invoke it, but cannot choose its executable, project, or arguments.
type ValidationCommand =
    { Action: ValidationAction
      ProjectFile: string
      TimeoutMilliseconds: int
      MaximumOutputCharactersPerStream: int }

exception private DispatcherError of code: string * message: string

module private Json =
    let text (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode
    let boolean (value: bool) : JsonNode = JsonValue.Create(value) :> JsonNode
    let integer (value: int) : JsonNode = JsonValue.Create(value) :> JsonNode
    let int64 (value: int64) : JsonNode = JsonValue.Create(value) :> JsonNode

    let property (node: JsonNode) name =
        match node with
        | :? JsonObject as value ->
            let mutable child = Unchecked.defaultof<JsonNode>
            if value.TryGetPropertyValue(name, &child) then Some child else None
        | _ -> None

    let stringValue (node: JsonNode) =
        try Some(node.GetValue<string>()) with _ -> None

    let asObject (node: JsonNode) =
        match node with
        | :? JsonObject as value -> Some value
        | _ -> None

    let propertyString (node: JsonNode) name fallback =
        property node name |> Option.bind stringValue |> Option.defaultValue fallback

    let compact (node: JsonNode) = node.ToJsonString()

module private Responses =
    let private make (ok: bool) (kind: string) (text: string) (data: JsonNode option) (error: JsonNode option) =
        let result = JsonObject()
        result["ok"] <- Json.boolean ok
        result["kind"] <- Json.text kind
        result["text"] <- Json.text text
        result["data"] <- data |> Option.map (fun value -> value.DeepClone()) |> Option.defaultValue null
        result["error"] <- error |> Option.map (fun value -> value.DeepClone()) |> Option.defaultValue null
        result

    let success kind text data = make true kind text data None

    let error code message data =
        let detail = JsonObject()
        detail["code"] <- Json.text code
        detail["message"] <- Json.text message
        make false "error" message data (Some(detail :> JsonNode))

    let validationFailure code message data =
        let detail = JsonObject()
        detail["code"] <- Json.text code
        detail["message"] <- Json.text message
        make false "validation" message (Some data) (Some(detail :> JsonNode))

/// A deterministic, root-confined set of conventional repository tools.
/// It does not provide an operating-system sandbox for projects executed during validation.
type ConventionalDispatcher
    (projectRoot: string,
     validationCommand: ValidationCommand,
     ?dotnetExecutable: string,
     ?maximumFileBytes: int64,
     ?maximumSearchFiles: int,
     ?maximumSearchResults: int,
     ?maximumSearchEntries: int,
     ?maximumSearchDepth: int) =

    let root = Path.GetFullPath(projectRoot)
    let dotnet = defaultArg dotnetExecutable "dotnet"
    let fileByteLimit = defaultArg maximumFileBytes 1_048_576L
    let searchFileLimit = defaultArg maximumSearchFiles 5_000
    let searchResultLimit = defaultArg maximumSearchResults 200
    let searchEntryLimit = defaultArg maximumSearchEntries 20_000
    let searchDepthLimit = defaultArg maximumSearchDepth 64
    let lockObject = obj ()
    let logEntries = ResizeArray<JsonObject>()
    let mutable nextSequence = 1L
    let allowedExtensions =
        Set.ofList [ ".fs"; ".fsx"; ".cs"; ".fsproj"; ".csproj"; ".sln"; ".slnx"; ".json"; ".md"; ".props"; ".targets" ]
    let skippedSearchDirectories = Set.ofList [ ".git"; "bin"; "obj"; ".vs"; "node_modules" ]

    let pathComparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    let fail code message = raise (DispatcherError(code, message))

    let isReparsePoint (path: string) =
        let attributes = File.GetAttributes(path)
        (attributes &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0

    let checkRoot () =
        if not (Directory.Exists root) then fail "PROJECT_ROOT_UNAVAILABLE" "The project root is no longer an existing directory."
        if isReparsePoint root then fail "PATH_REPARSE_POINT" "The project root cannot be a reparse point or symbolic link."

    let resolveRelativePath (relativePath: string) (requireExisting: bool) =
        if String.IsNullOrWhiteSpace relativePath then fail "PATH_REQUIRED" "A non-empty relative path is required."
        if Path.IsPathRooted relativePath then fail "PATH_ABSOLUTE" "Absolute paths are not allowed."
        let components = relativePath.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        if components.Length = 0 || (components |> Array.exists (fun part -> part = "..")) then
            fail "PATH_TRAVERSAL" "Path traversal is not allowed."
        for part in components do
            let normalized = part.TrimEnd(' ', '.')
            if normalized = ".." then fail "PATH_TRAVERSAL" "Path traversal aliases are not allowed."
            if normalized = "" || normalized = "." then fail "PATH_INVALID" "Empty or dot path components are not allowed."
            if part <> normalized then fail "PATH_COMPONENT_INVALID" "Trailing spaces and dots are not allowed in path components."
            if part |> Seq.exists (fun character -> Char.IsControl character || "<>:\"|?*".Contains(character)) then
                fail "PATH_COMPONENT_INVALID" "Windows-invalid path characters and alternate data stream syntax are not allowed."
            let deviceBase = (part.Split([| '.' |])[0]).TrimEnd(' ', '.')
            let reservedDevice =
                let upper = deviceBase.ToUpperInvariant()
                Set.contains upper (Set.ofList [ "CON"; "PRN"; "AUX"; "NUL" ])
                || ([ 1 .. 9 ] |> List.exists (fun number -> upper = $"COM{number}" || upper = $"LPT{number}"))
            if reservedDevice then fail "PATH_COMPONENT_INVALID" "Reserved Windows device names are not allowed as path components."
        let full = Path.GetFullPath(Path.Combine(root, Path.Combine components))
        let relative = Path.GetRelativePath(root, full)
        if Path.IsPathRooted relative || relative = ".." || relative.StartsWith(".." + string Path.DirectorySeparatorChar, pathComparison) then
            fail "PATH_TRAVERSAL" "The requested path resolves outside the project root."

        let parts = relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
        let mutable current = root
        for index = 0 to parts.Length - 1 do
            current <- Path.Combine(current, parts[index])
            let exists = File.Exists current || Directory.Exists current
            if exists then
                if isReparsePoint current then fail "PATH_REPARSE_POINT" "Paths through a reparse point or symbolic link are not allowed."
                if index < parts.Length - 1 && not (Directory.Exists current) then
                    fail "PATH_NOT_DIRECTORY" "A parent path component is not a directory."
            elif requireExisting && index < parts.Length - 1 then
                fail "PATH_NOT_FOUND" "A parent path component does not exist."

        if requireExisting && not (File.Exists full) then fail "PATH_NOT_FOUND" "The requested file does not exist."
        full, relative.Replace(Path.DirectorySeparatorChar, '/')

    let resolveSourceFile relativePath requireExisting =
        let full, relative = resolveRelativePath relativePath requireExisting
        let extension = Path.GetExtension(full)
        if not (allowedExtensions.Contains(extension.ToLowerInvariant())) then
            fail "FILE_FORMAT_UNSUPPORTED" $"File format '{extension}' is not supported by repository tools."
        full, relative

    let readBytes (path: string) =
        if File.Exists path && isReparsePoint path then fail "PATH_REPARSE_POINT" "Reading through a reparse point or symbolic link is not allowed."
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan)
        if stream.Length > fileByteLimit then fail "FILE_TOO_LARGE" $"File exceeds the {fileByteLimit}-byte read limit."
        use buffer = new MemoryStream()
        let chunk = Array.zeroCreate<byte> 8192
        let mutable count = stream.Read(chunk, 0, chunk.Length)
        while count > 0 do
            if buffer.Length + int64 count > fileByteLimit then fail "FILE_TOO_LARGE" $"File grew beyond the {fileByteLimit}-byte read limit."
            buffer.Write(chunk, 0, count)
            count <- stream.Read(chunk, 0, chunk.Length)
        buffer.ToArray()

    let decodeUtf8 (path: string) (bytes: byte array) =
        try UTF8Encoding(false, true).GetString(bytes)
        with :? DecoderFallbackException -> fail "TEXT_ENCODING_INVALID" $"File '{path}' is not valid UTF-8 text."

    let hashBytes (bytes: byte array) = SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let exactArguments (args: JsonObject) (required: string list) =
        if isNull args then fail "ARGUMENTS_INVALID" "Arguments must be a JSON object."
        let actual = args |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
        let expected = Set.ofList required
        if actual <> expected then
            let requiredText = String.concat ", " required
            let actualText = String.concat ", " (Set.toList actual)
            fail "ARGUMENTS_INVALID" $"Expected exactly [{requiredText}], received [{actualText}]."

    let requiredString (args: JsonObject) (name: string) =
        Json.property args name
        |> Option.bind Json.stringValue
        |> Option.defaultWith (fun () -> fail "ARGUMENT_INVALID" $"Argument '{name}' must be a string.")

    let dataObject (fields: (string * JsonNode) list) =
        let value = JsonObject()
        for key, node in fields do value[key] <- if isNull node then null else node.DeepClone()
        value

    let fileInfoData (full: string) (relative: string) (bytes: byte array) (text: string) =
        let lines = text.Split('\n').Length
        dataObject
            [ "path", Json.text relative
              "format", Json.text (Path.GetExtension(full))
              "bytes", Json.int64 (int64 bytes.Length)
              "lines", Json.integer lines
              "sha256", Json.text (hashBytes bytes) ]

    let inspect (relativePath: string) =
        let full, relative = resolveSourceFile relativePath true
        let bytes = readBytes full
        let content = decodeUtf8 relative bytes
        Responses.success "inspect" $"Inspected {relative}." (Some(fileInfoData full relative bytes content :> JsonNode))

    let read (relativePath: string) =
        let full, relative = resolveSourceFile relativePath true
        let bytes = readBytes full
        let content = decodeUtf8 relative bytes
        let data = fileInfoData full relative bytes content
        data["content"] <- Json.text content
        Responses.success "read" $"Read {relative}." (Some(data :> JsonNode))

    let search (query: string) =
        if String.IsNullOrWhiteSpace query then fail "QUERY_REQUIRED" "Search requires a non-empty literal query."
        if query.Length > 512 then fail "QUERY_TOO_LONG" "Search queries are limited to 512 characters."
        let matches = ResizeArray<JsonNode>()
        let mutable scanned = 0
        let mutable skippedLarge = 0
        let mutable truncated = false
        let mutable stopped = false
        let mutable entriesVisited = 0
        let processFile full relative =
            if scanned >= searchFileLimit then
                truncated <- true
                stopped <- true
            else
                scanned <- scanned + 1
                try
                    let content = readBytes full |> decodeUtf8 relative
                    let lines = content.Split('\n')
                    let mutable lineIndex = 0
                    while lineIndex < lines.Length && not stopped do
                        let line = lines[lineIndex].TrimEnd('\r')
                        if line.Contains(query, StringComparison.OrdinalIgnoreCase) then
                            if matches.Count >= searchResultLimit then
                                truncated <- true
                                stopped <- true
                            else
                                let preview, lineTruncated =
                                    if line.Length <= 512 then line, false
                                    else line.Substring(0, 512), true
                                let item = JsonObject()
                                item["path"] <- Json.text relative
                                item["line"] <- Json.integer (lineIndex + 1)
                                item["text"] <- Json.text preview
                                item["lineTruncated"] <- Json.boolean lineTruncated
                                matches.Add(item :> JsonNode)
                        lineIndex <- lineIndex + 1
                with DispatcherError("FILE_TOO_LARGE", _) -> skippedLarge <- skippedLarge + 1

        let rec visitDirectory depth directory =
            if not stopped then
                if depth > searchDepthLimit then
                    truncated <- true
                    stopped <- true
                else
                    let entries = ResizeArray<string>()
                    use iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator()
                    let mutable hasMore = true
                    while hasMore && entries.Count <= searchEntryLimit do
                        if iterator.MoveNext() then entries.Add iterator.Current else hasMore <- false
                    if entries.Count > searchEntryLimit then
                        fail "SEARCH_DIRECTORY_LIMIT" $"A directory exceeded the {searchEntryLimit}-entry search limit."
                    let ordered = entries.ToArray() |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right)))
                    for entry in ordered do
                        if not stopped then
                            if entriesVisited >= searchEntryLimit then
                                truncated <- true
                                stopped <- true
                            else
                                entriesVisited <- entriesVisited + 1
                                if isReparsePoint entry then
                                    fail "PATH_REPARSE_POINT" "Search encountered a reparse point or symbolic link and stopped."
                                elif Directory.Exists entry then
                                    if not (skippedSearchDirectories.Contains(Path.GetFileName(entry))) then visitDirectory (depth + 1) entry
                                elif allowedExtensions.Contains(Path.GetExtension(entry).ToLowerInvariant()) then
                                    processFile entry (Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/'))

        visitDirectory 0 root
        let matchArray = JsonArray()
        matches |> Seq.iter matchArray.Add
        let data = JsonObject()
        data["query"] <- Json.text query
        data["matches"] <- matchArray
        data["filesScanned"] <- Json.integer scanned
        data["largeFilesSkipped"] <- Json.integer skippedLarge
        data["truncated"] <- Json.boolean truncated
        Responses.success "search" $"Found {matches.Count} match(es)." (Some(data :> JsonNode))

    let replace (relativePath: string) (expectedHash: string) (content: string) =
        if expectedHash.Length <> 64 || (expectedHash |> Seq.exists (fun character -> not (Uri.IsHexDigit character))) then
            fail "HASH_INVALID" "expectedSha256 must contain exactly 64 hexadecimal characters."
        let full, relative = resolveSourceFile relativePath true
        let currentBytes = readBytes full
        let currentHash = hashBytes currentBytes
        if not (String.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase)) then
            fail "STALE_CONTENT" "The file changed since it was read; inspect and read it again before replacing."
        let outputBytes = UTF8Encoding(false).GetBytes(content)
        if int64 outputBytes.Length > fileByteLimit then fail "FILE_TOO_LARGE" $"Replacement exceeds the {fileByteLimit}-byte file limit."

        let temporary = Path.Combine(Path.GetDirectoryName(full), $".agentlang-replace-{Guid.NewGuid():N}.tmp")
        try
            do
                use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
                stream.Write(outputBytes, 0, outputBytes.Length)
                stream.Flush(true)
                let latestBytes = readBytes full
                if not (String.Equals(hashBytes latestBytes, expectedHash, StringComparison.OrdinalIgnoreCase)) then
                    fail "STALE_CONTENT" "The file changed before replacement could be committed; inspect and read it again."
                resolveSourceFile relative true |> ignore
            File.Move(temporary, full, true)
        finally
            if File.Exists temporary then File.Delete temporary

        let newHash = hashBytes outputBytes
        let data = JsonObject()
        data["path"] <- Json.text relative
        data["sha256"] <- Json.text newHash
        data["bytes"] <- Json.int64 (int64 outputBytes.Length)
        Responses.success "replace" $"Replaced {relative}." (Some(data :> JsonNode))

    let patch (relativePath: string) (expectedHash: string) (oldText: string) (newText: string) =
        if expectedHash.Length <> 64 || (expectedHash |> Seq.exists (fun character -> not (Uri.IsHexDigit character))) then
            fail "HASH_INVALID" "expectedSha256 must contain exactly 64 hexadecimal characters."

        let full, relative = resolveSourceFile relativePath true
        let currentBytes = readBytes full
        let currentHash = hashBytes currentBytes
        if not (String.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase)) then
            fail "STALE_CONTENT" "The file changed since it was read; inspect and read it again before patching."

        if String.IsNullOrEmpty oldText then fail "PATCH_ANCHOR_EMPTY" "oldText must be a non-empty exact text anchor."
        let currentContent = decodeUtf8 relative currentBytes
        let firstMatch = currentContent.IndexOf(oldText, StringComparison.Ordinal)
        if firstMatch < 0 then fail "PATCH_ANCHOR_NOT_FOUND" "oldText does not occur in the current file."
        let nextMatch = currentContent.IndexOf(oldText, firstMatch + 1, StringComparison.Ordinal)
        if nextMatch >= 0 then fail "PATCH_ANCHOR_AMBIGUOUS" "oldText occurs more than once in the current file."

        let updatedContent =
            currentContent.Substring(0, firstMatch)
            + newText
            + currentContent.Substring(firstMatch + oldText.Length)
        let outputBytes =
            try UTF8Encoding(false, true).GetBytes(updatedContent)
            with :? EncoderFallbackException ->
                fail "PATCH_OUTPUT_ENCODING_INVALID" "The patched output is not valid UTF-8 text."
        if int64 outputBytes.Length > fileByteLimit then
            fail "FILE_TOO_LARGE" $"Patch output exceeds the {fileByteLimit}-byte file limit."

        let result = replace relativePath expectedHash updatedContent
        result["kind"] <- Json.text "patch"
        result["text"] <- Json.text $"Patched {relative}."
        result

    let removeCredentialEnvironment (environment: System.Collections.Generic.IDictionary<string, string>) =
        let sensitiveMarkers = [ "KEY"; "TOKEN"; "PASSWORD"; "SECRET"; "CREDENTIAL"; "AUTH"; "PRIVATE" ]
        let keys = environment.Keys |> Seq.cast<obj> |> Seq.map string |> Seq.toArray
        for key in keys do
            let normalized = key.ToUpperInvariant()
            if sensitiveMarkers |> List.exists normalized.Contains then environment.Remove(key) |> ignore

    let readCapped (reader: StreamReader) (maximumCharacters: int) =
        task {
            let buffer = Array.zeroCreate<char> 2048
            let captured = StringBuilder(min maximumCharacters 4096)
            let mutable finished = false
            let mutable truncated = false
            while not finished do
                let! count = reader.ReadAsync(buffer, 0, buffer.Length)
                if count = 0 then finished <- true
                else
                    let remaining = maximumCharacters - captured.Length
                    let kept = min count (max 0 remaining)
                    if kept > 0 then captured.Append(buffer, 0, kept) |> ignore
                    if kept < count then truncated <- true
            return captured.ToString(), truncated
        }

    let validate () =
        task {
            let projectPath, _ = resolveSourceFile validationCommand.ProjectFile true
            if not (String.Equals(Path.GetExtension(projectPath), ".fsproj", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase)) then
                fail "VALIDATION_PROJECT_INVALID" "The configured validation target must be an F# or C# project file."
            checkRoot ()
            let latestProjectPath, latestRelativeProject = resolveSourceFile validationCommand.ProjectFile true
            if not (String.Equals(projectPath, latestProjectPath, pathComparison)) then
                fail "VALIDATION_PROJECT_CHANGED" "The configured validation project path changed before process launch."
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- dotnet
            startInfo.WorkingDirectory <- root
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.StandardOutputEncoding <- UTF8Encoding(false)
            startInfo.StandardErrorEncoding <- UTF8Encoding(false)
            startInfo.Environment["DOTNET_NOLOGO"] <- "1"
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
            removeCredentialEnvironment startInfo.Environment
            let commandArguments =
                match validationCommand.Action with
                | ValidationAction.Build -> [ "build"; latestRelativeProject; "--nologo" ]
                | ValidationAction.Run -> [ "run"; "--project"; latestRelativeProject; "--no-launch-profile" ]
            commandArguments |> List.iter startInfo.ArgumentList.Add

            use childProcess = new Process(StartInfo = startInfo)
            let timer = Stopwatch.StartNew()
            checkRoot ()
            let started = childProcess.Start()
            if not started then fail "VALIDATION_START_FAILED" "The configured dotnet validation process did not start."
            let stdoutTask = readCapped childProcess.StandardOutput validationCommand.MaximumOutputCharactersPerStream
            let stderrTask = readCapped childProcess.StandardError validationCommand.MaximumOutputCharactersPerStream
            use timeout = new CancellationTokenSource(validationCommand.TimeoutMilliseconds)
            let! timedOut =
                task {
                    try
                        do! childProcess.WaitForExitAsync(timeout.Token)
                        return false
                    with :? OperationCanceledException ->
                        try childProcess.Kill(true) with _ -> ()
                        try do! childProcess.WaitForExitAsync() with _ -> ()
                        return true
                }
            let! (stdout, stdoutTruncated) = stdoutTask
            let! (stderr, stderrTruncated) = stderrTask
            timer.Stop()
            let exitCode = if timedOut then None else Some childProcess.ExitCode
            let command = JsonArray()
            commandArguments |> List.iter (Json.text >> command.Add)
            let data = JsonObject()
            data["command"] <- command
            data["project"] <- Json.text latestRelativeProject
            data["exitCode"] <- exitCode |> Option.map Json.integer |> Option.defaultValue null
            data["timedOut"] <- Json.boolean timedOut
            data["stdout"] <- Json.text stdout
            data["stderr"] <- Json.text stderr
            data["stdoutTruncated"] <- Json.boolean stdoutTruncated
            data["stderrTruncated"] <- Json.boolean stderrTruncated
            data["durationMilliseconds"] <- Json.int64 timer.ElapsedMilliseconds
            if timedOut then return Responses.validationFailure "VALIDATION_TIMEOUT" $"Validation exceeded its {validationCommand.TimeoutMilliseconds} ms timeout." data
            elif exitCode <> Some 0 then return Responses.validationFailure "VALIDATION_FAILED" $"The configured dotnet command exited with code {childProcess.ExitCode}." data
            else return Responses.success "validation" "The configured dotnet command passed." (Some(data :> JsonNode))
        }

    do
        checkRoot ()
        if String.IsNullOrWhiteSpace dotnet then invalidArg (nameof dotnetExecutable) "A host-selected dotnet executable is required."
        if fileByteLimit <= 0L then invalidArg (nameof maximumFileBytes) "maximumFileBytes must be positive."
        if searchFileLimit <= 0 then invalidArg (nameof maximumSearchFiles) "maximumSearchFiles must be positive."
        if searchResultLimit <= 0 then invalidArg (nameof maximumSearchResults) "maximumSearchResults must be positive."
        if searchEntryLimit <= 0 then invalidArg (nameof maximumSearchEntries) "maximumSearchEntries must be positive."
        if searchDepthLimit <= 0 then invalidArg (nameof maximumSearchDepth) "maximumSearchDepth must be positive."
        if fileByteLimit > 16_777_216L then invalidArg (nameof maximumFileBytes) "maximumFileBytes cannot exceed 16 MiB."
        if searchFileLimit > 50_000 || searchEntryLimit > 100_000 || searchDepthLimit > 256 then
            invalidArg (nameof maximumSearchFiles) "Search bounds exceed the supported host limits."
        if validationCommand.TimeoutMilliseconds <= 0 then invalidArg (nameof validationCommand.TimeoutMilliseconds) "Validation timeout must be positive."
        if validationCommand.TimeoutMilliseconds > 600_000 then invalidArg (nameof validationCommand.TimeoutMilliseconds) "Validation timeout cannot exceed ten minutes."
        if validationCommand.MaximumOutputCharactersPerStream <= 0 then invalidArg (nameof validationCommand.MaximumOutputCharactersPerStream) "Validation output limit must be positive."
        if validationCommand.MaximumOutputCharactersPerStream > 1_000_000 then invalidArg (nameof validationCommand.MaximumOutputCharactersPerStream) "Validation output cannot exceed one million characters per stream."
        resolveSourceFile validationCommand.ProjectFile true |> ignore
        let configuredProject = Path.GetExtension(validationCommand.ProjectFile).ToLowerInvariant()
        if configuredProject <> ".fsproj" && configuredProject <> ".csproj" then
            invalidArg (nameof validationCommand.ProjectFile) "The configured validation target must be an F# or C# project file."

    /// A snapshot of metadata-only operation events, in dispatch order.
    member _.OperationLog =
        lock lockObject (fun () ->
            let result = JsonArray()
            logEntries |> Seq.iter (fun item -> result.Add(item.DeepClone()))
            result)

    /// Dispatch one closed conventional repository operation.
    member this.Dispatch(operation: string, arguments: JsonObject) =
        lock lockObject (fun () ->
            let timer = Stopwatch.StartNew()
            let result =
                try
                    checkRoot ()
                    match operation with
                    | "inspect" ->
                        exactArguments arguments [ "path" ]
                        inspect (requiredString arguments "path")
                    | "read" ->
                        exactArguments arguments [ "path" ]
                        read (requiredString arguments "path")
                    | "search" ->
                        exactArguments arguments [ "query" ]
                        search (requiredString arguments "query")
                    | "replace" ->
                        exactArguments arguments [ "path"; "expectedSha256"; "content" ]
                        replace (requiredString arguments "path") (requiredString arguments "expectedSha256") (requiredString arguments "content")
                    | "patch" ->
                        exactArguments arguments [ "path"; "expectedSha256"; "oldText"; "newText" ]
                        patch
                            (requiredString arguments "path")
                            (requiredString arguments "expectedSha256")
                            (requiredString arguments "oldText")
                            (requiredString arguments "newText")
                    | "validate" ->
                        exactArguments arguments []
                        validate () |> fun pending -> pending.GetAwaiter().GetResult()
                    | _ -> fail "OPERATION_UNSUPPORTED" $"Operation '{operation}' is not available."
                with
                | DispatcherError(code, message) -> Responses.error code message None
                | :? UnauthorizedAccessException -> Responses.error "ACCESS_DENIED" "The requested repository operation is not allowed by the host filesystem." None
                | :? IOException as ex -> Responses.error "IO_ERROR" ex.Message None
                | ex -> Responses.error "HOST_ERROR" $"The repository operation failed ({ex.GetType().Name})." None
            timer.Stop()
            let record = JsonObject()
            record["sequence"] <- Json.int64 nextSequence
            nextSequence <- nextSequence + 1L
            record["operation"] <- Json.text (if Set.contains operation (Set.ofList [ "inspect"; "read"; "search"; "replace"; "patch"; "validate" ]) then operation else "unsupported")
            record["status"] <- Json.text (if result["ok"].GetValue<bool>() then "ok" else Json.propertyString result["error"] "code" "failed")
            match Json.property result "data" with
            | Some (:? JsonObject as data) ->
                match Json.property data "path" |> Option.bind Json.stringValue with
                | Some path -> record["path"] <- Json.text path
                | None -> ()
                match Json.property data "project" |> Option.bind Json.stringValue with
                | Some path -> record["path"] <- Json.text path
                | None -> ()
                match Json.property data "exitCode" with
                | Some code when not (isNull code) -> record["exitCode"] <- code.DeepClone()
                | _ -> ()
                match Json.property data "timedOut" with
                | Some value -> record["timedOut"] <- value.DeepClone()
                | _ -> ()
            | _ -> ()
            record["durationMilliseconds"] <- Json.int64 timer.ElapsedMilliseconds
            logEntries.Add record
            result)
