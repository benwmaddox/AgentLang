namespace AgentLang

open System
open System.Text.Json

/// C library bindings (report 307):
///
///     fn zlib.crc32-update(crc: Int, data: Bytes) -> Int = c "unsigned long crc32_z(unsigned long, bytes)" from "z"
///
/// The quoted prototype names the C function and its parameter and return types
/// from a closed list, so both backends call it with each platform's widths
/// (`long` is 32-bit on Windows, 64-bit elsewhere). `bytes` and `text` stand for a
/// pointer and a size_t length: a Bytes or String argument, read-only for the call.
/// The library is loaded, and the symbol found, on the first call.
module ForeignCalls =
    [<RequireQualifiedAccess>]
    type CWidth =
        | Fixed of int
        /// C `long`: 32 bits on Windows, 64 bits elsewhere.
        | Long

    [<RequireQualifiedAccess>]
    type CType =
        | Integer of signed: bool * width: CWidth
        | Bool
        | Double
        /// A pointer and a size_t length: the bytes of a Bytes value.
        | Bytes
        /// A pointer and a size_t length: the UTF-8 bytes of a String.
        | Text

    type Prototype =
        { Return: CType
          Symbol: string
          Parameters: CType list }

    type Binding =
        { Library: string
          Prototype: Prototype
          /// The prototype as written, for rendering and fingerprints.
          PrototypeText: string }

    /// The reserved call a binding's body is (the word's source is the binding).
    let intrinsicName = "%c-call"

    let private typeNames =
        [ "int8_t", CType.Integer(true, CWidth.Fixed 8); "uint8_t", CType.Integer(false, CWidth.Fixed 8)
          "int16_t", CType.Integer(true, CWidth.Fixed 16); "uint16_t", CType.Integer(false, CWidth.Fixed 16)
          "int32_t", CType.Integer(true, CWidth.Fixed 32); "uint32_t", CType.Integer(false, CWidth.Fixed 32)
          "int64_t", CType.Integer(true, CWidth.Fixed 64); "uint64_t", CType.Integer(false, CWidth.Fixed 64)
          "short", CType.Integer(true, CWidth.Fixed 16); "unsigned short", CType.Integer(false, CWidth.Fixed 16)
          "int", CType.Integer(true, CWidth.Fixed 32); "unsigned", CType.Integer(false, CWidth.Fixed 32); "unsigned int", CType.Integer(false, CWidth.Fixed 32)
          "long", CType.Integer(true, CWidth.Long); "unsigned long", CType.Integer(false, CWidth.Long)
          "long long", CType.Integer(true, CWidth.Fixed 64); "unsigned long long", CType.Integer(false, CWidth.Fixed 64)
          "size_t", CType.Integer(false, CWidth.Fixed 64); "ptrdiff_t", CType.Integer(true, CWidth.Fixed 64)
          "bool", CType.Bool; "_Bool", CType.Bool; "double", CType.Double
          "bytes", CType.Bytes; "text", CType.Text ]
        |> Map.ofList

    let typeNameList = typeNames |> Map.toList |> List.map fst

    let formatType (ty: CType) =
        typeNames |> Map.toList |> List.find (fun (_, value) -> value = ty) |> fst

    let private normalize (text: string) = String.Join(" ", text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries))

    /// `<return type> <symbol>(<type>, ...)`, or None with the reason.
    let parsePrototype (text: string) : Result<Prototype, string> =
        let opening = text.IndexOf '('
        let closing = text.LastIndexOf ')'
        if opening < 0 || closing < opening || text.Substring(closing + 1).Trim() <> "" then
            Error "a prototype is `<return type> <symbol>(<parameter types>)`"
        else
            let head = normalize (text.Substring(0, opening))
            let split = head.LastIndexOf ' '
            if split < 0 then Error "a prototype names its return type before the symbol"
            else
                let returnName = head.Substring(0, split)
                let symbol = head.Substring(split + 1)
                let validSymbol = symbol.Length > 0 && (Char.IsLetter symbol[0] || symbol[0] = '_') && symbol |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
                let inside = text.Substring(opening + 1, closing - opening - 1).Trim()
                let parameterNames = if inside = "" || inside = "void" then [] else inside.Split(',') |> Array.map normalize |> List.ofArray
                if not validSymbol then Error $"'{symbol}' is not a C symbol"
                else
                    match typeNames.TryFind returnName with
                    | None -> Error $"'{returnName}' is not a supported return type"
                    | Some(CType.Bytes | CType.Text) -> Error "a binding returns a number or a bool, not bytes or text"
                    | Some returnType ->
                        let parameters = parameterNames |> List.map (fun name -> name, typeNames.TryFind name)
                        match parameters |> List.tryFind (fun (_, ty) -> ty.IsNone) with
                        | Some(name, _) -> Error $"'{name}' is not a supported parameter type"
                        | None -> Ok { Return = returnType; Symbol = symbol; Parameters = parameters |> List.map (snd >> Option.get) }

    /// The Flow type a C type carries, if the two fit.
    let fits (flow: LangType) (ty: CType) =
        match flow, ty with
        | TInt, CType.Integer _ -> true
        | TBool, (CType.Bool | CType.Integer _) -> true
        | TFloat, CType.Double -> true
        | TBytes, CType.Bytes -> true
        | TString, CType.Text -> true
        | _ -> false

    /// The bit width of an integer C type on this platform.
    let bits (windows: bool) (width: CWidth) =
        match width with
        | CWidth.Fixed count -> count
        | CWidth.Long -> if windows then 32 else 64

    /// The range of Ints an integer parameter accepts. A 64-bit unsigned parameter
    /// takes any Int and passes its bits, so it round-trips with a uint64 return.
    let range (windows: bool) (signed: bool) (width: CWidth) =
        let count = bits windows width
        if count = 64 then Int64.MinValue, Int64.MaxValue
        elif signed then -(1L <<< (count - 1)), (1L <<< (count - 1)) - 1L
        else 0L, (1L <<< count) - 1L

    let encode (binding: Binding) =
        let node = System.Text.Json.Nodes.JsonObject()
        node["library"] <- System.Text.Json.Nodes.JsonValue.Create(binding.Library)
        node["prototype"] <- System.Text.Json.Nodes.JsonValue.Create(binding.PrototypeText)
        node.ToJsonString()

    let decode (text: string) =
        use document = JsonDocument.Parse text
        let library = document.RootElement.GetProperty("library").GetString()
        let prototypeText = document.RootElement.GetProperty("prototype").GetString()
        match parsePrototype prototypeText with
        | Ok prototype -> Ok { Library = library; Prototype = prototype; PrototypeText = prototypeText }
        | Error reason -> Error reason

    /// The file names a library name may have on this platform, first match wins.
    let libraryCandidates (windows: bool) (library: string) =
        if library.Contains '.' || library.Contains '/' || library.Contains '\\' then [ library ]
        elif windows then [ library + ".dll"; "lib" + library + ".dll"; library + "1.dll"; "lib" + library + "1.dll" ]
        else [ "lib" + library + ".so"; "lib" + library + ".so.1"; library + ".so" ]
