namespace AgentLang.Llvm

open System
open System.Globalization
open System.IO

[<Struct>]
type internal NativeFrameUsage =
    { Frame: string
      SizeBytes: uint64 }

[<Sealed>]
type NativeStackUsageException internal
    (code: string,
     usageFile: string,
     frame: string option,
     sizeBytes: uint64 option,
     limitBytes: uint64,
     reason: string) =
    inherit InvalidOperationException(
        match code, frame, sizeBytes with
        | "IR_NATIVE_STACK_FRAME_LIMIT", Some frameName, Some size ->
            $"{code}: native frame '{frameName}' uses {size.ToString(CultureInfo.InvariantCulture)} bytes, exceeding the {limitBytes.ToString(CultureInfo.InvariantCulture)} byte per-frame limit reported by '{usageFile}'. Reduce or split the frame. This screen does not bound cumulative call-chain stack use."
        | _ ->
            let frameSuffix = frame |> Option.map (fun frameName -> $" for frame '{frameName}'") |> Option.defaultValue ""
            $"{code}: native stack usage{frameSuffix} could not be established from '{usageFile}': {reason}. The compiler must report static -fstack-usage rows. This screen does not bound cumulative call-chain stack use.")

    member _.Code = code
    member _.UsageFile = usageFile
    member _.Frame = frame
    member _.SizeBytes = sizeBytes
    member _.LimitBytes = limitBytes
    member _.Reason = reason

[<RequireQualifiedAccess>]
module internal NativeStackUsage =
    /// This is a conservative per-function frame policy. It is not a call-chain budget.
    let perFrameLimitBytes = 64UL * 1024UL

    let private unknown usageFile frame reason =
        raise (NativeStackUsageException(
            "IR_NATIVE_STACK_USAGE_UNKNOWN", usageFile, frame, None, perFrameLimitBytes, reason))

    let private frameName (locationAndName: string) =
        let separator = locationAndName.LastIndexOf(':')
        if separator <= 0 || String.IsNullOrWhiteSpace(locationAndName.Substring(0, separator)) then None
        else Some(locationAndName.Substring(separator + 1))

    let private isAsciiDecimal (value: string) =
        not (String.IsNullOrEmpty value)
        && (value |> Seq.forall (fun character -> character >= '0' && character <= '9'))

    /// Parse Clang's tab-separated path:function, byte-count, static|dynamic rows.
    /// Repeated function labels are retained and screened independently; no row is discarded.
    let parseText (usageFile: string) (contents: string) : NativeFrameUsage list =
        if isNull contents then unknown usageFile None "the .su file contents are null"

        let rows = ResizeArray<NativeFrameUsage>()
        use reader = new StringReader(contents)
        let mutable lineNumber = 0
        let mutable line = reader.ReadLine()
        while not (isNull line) do
            lineNumber <- lineNumber + 1
            let fields = line.Split([| '\t' |], StringSplitOptions.None)
            if fields.Length <> 3 || String.IsNullOrWhiteSpace fields[0] then
                unknown usageFile None $"line {lineNumber} is not a three-column Clang stack-usage row"

            let frame =
                match frameName fields[0] with
                | Some name when not (String.IsNullOrWhiteSpace name) -> name
                | _ -> unknown usageFile None $"line {lineNumber} does not identify a source location and function"

            if not (isAsciiDecimal fields[1]) then
                unknown usageFile (Some frame) $"line {lineNumber} has a non-decimal or out-of-range byte count"

            match fields[2] with
            | "dynamic" -> unknown usageFile (Some frame) $"line {lineNumber} reports dynamic stack usage"
            | "static" ->
                match UInt64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, sizeBytes -> rows.Add { Frame = frame; SizeBytes = sizeBytes }
                | false, _ -> unknown usageFile (Some frame) $"line {lineNumber} has an out-of-range byte count"
            | qualifier ->
                unknown usageFile (Some frame) $"line {lineNumber} uses unsupported stack-usage qualifier '{qualifier}'"

            line <- reader.ReadLine()

        if rows.Count = 0 then unknown usageFile None "the .su file contains no valid stack-usage rows"
        rows |> Seq.toList

    let checkFrames (usageFile: string) (limitBytes: uint64) (frames: NativeFrameUsage list) =
        if List.isEmpty frames then unknown usageFile None "the .su file contains no valid stack-usage rows"

        for frame in frames do
            if frame.SizeBytes > limitBytes then
                raise (NativeStackUsageException(
                    "IR_NATIVE_STACK_FRAME_LIMIT",
                    usageFile,
                    Some frame.Frame,
                    Some frame.SizeBytes,
                    limitBytes,
                    $"the frame exceeds the configured {limitBytes.ToString(CultureInfo.InvariantCulture)} byte limit"))

    let verifyFreshFile (usageFile: string) =
        if not (File.Exists usageFile) then
            unknown usageFile None "clang did not produce a fresh .su sidecar"

        try
            File.ReadAllText usageFile
            |> parseText usageFile
            |> checkFrames usageFile perFrameLimitBytes
        with
        | :? NativeStackUsageException -> reraise ()
        | error -> unknown usageFile None $"the .su sidecar could not be read ({error.Message})"
