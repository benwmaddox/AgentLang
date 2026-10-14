namespace AgentLang

open System
open System.Globalization
open System.Text

/// Standard library slice 2 (report 274): pure helpers behind the AES-GCM,
/// JSON, cookie and time primitives. The owning runtime implements the same
/// algorithms in native/owning_stdlib2.h; every none case matches it.
module StdlibSlice2 =
    let private utf8 = UTF8Encoding(false, true)

    // ------------------------------------------------------------------ time

    /// The .NET DateTime range in Unix microseconds (0001-01-01 .. 9999-12-31).
    let minUnixMicroseconds = -62135596800000000L
    let maxUnixMicroseconds = 253402300799999999L
    let private epochTicks = 621355968000000000L

    let private dateTime (micros: int64) =
        if micros < minUnixMicroseconds || micros > maxUnixMicroseconds then None
        else Some(DateTime(epochTicks + micros * 10L, DateTimeKind.Utc))

    let private microsOf (value: DateTime) = (value.Ticks - epochTicks) / 10L

    /// DateTime.AddYears: Feb 29 becomes Feb 28 in a common year.
    let addYears (micros: int64) (years: int64) =
        match dateTime micros with
        | Some value when years >= -10000L && years <= 10000L ->
            let year = int64 value.Year + years
            if year < 1L || year > 9999L then None else Some(microsOf (value.AddYears(int years)))
        | _ -> None

    /// RFC 1123 (HTTP date, cookie Expires): "Wed, 14 Oct 2026 12:00:00 GMT".
    let rfc1123 (micros: int64) =
        dateTime micros |> Option.map (fun value -> value.ToString("r", CultureInfo.InvariantCulture))

    /// ISO 8601 UTC with milliseconds, as Rails writes "exp": "2046-10-14T12:00:00.123Z".
    let iso8601Milliseconds (micros: int64) =
        dateTime micros |> Option.map (fun value -> value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))

    let private isLeap (year: int) = (year % 4 = 0 && year % 100 <> 0) || year % 400 = 0

    /// yyyy-MM-ddTHH:mm:ss[.f{1,7}](Z|z|+HH:mm|-HH:mm), T or t. Returns Unix
    /// microseconds; sub-microsecond digits round up, so `now >= exp` in
    /// microseconds equals the tick comparison Rails' parser implies.
    let parseIso8601 (text: string) : int64 option =
        let x = Encoding.UTF8.GetBytes text
        let length = x.Length
        let digit (b: byte) = b >= byte '0' && b <= byte '9'
        let d2 at = int (x[at] - byte '0') * 10 + int (x[at + 1] - byte '0')
        if length < 20 then None
        else
            let mutable ok = true
            let mutable zoneIndex = -1
            let mutable dateTimeLength = 0
            let mutable offsetSign = 0
            let mutable offsetMinutes = 0
            if x[length - 1] = byte 'Z' || x[length - 1] = byte 'z' then
                zoneIndex <- length - 1
                dateTimeLength <- zoneIndex
            else
                let mutable lastPlus = -1
                let mutable lastMinus = -1
                for index in 0 .. length - 1 do
                    if x[index] = byte '+' then lastPlus <- index
                    elif x[index] = byte '-' then lastMinus <- index
                if lastPlus < 0 && lastMinus < 0 then ok <- false
                else
                    zoneIndex <- max lastPlus lastMinus
                    if zoneIndex < 19 || length - zoneIndex <> 6 || x[zoneIndex + 3] <> byte ':' then ok <- false
                    elif not (digit x[zoneIndex + 1] && digit x[zoneIndex + 2] && digit x[zoneIndex + 4] && digit x[zoneIndex + 5]) then ok <- false
                    else
                        let hours = d2 (zoneIndex + 1)
                        let minutes = d2 (zoneIndex + 4)
                        if minutes > 59 || hours > 14 || (hours = 14 && minutes <> 0) then ok <- false
                        else
                            offsetSign <- (if x[zoneIndex] = byte '+' then 1 else -1)
                            offsetMinutes <- hours * 60 + minutes
                            dateTimeLength <- zoneIndex
            if not ok || dateTimeLength < 19
               || x[4] <> byte '-' || x[7] <> byte '-' || (x[10] <> byte 'T' && x[10] <> byte 't')
               || x[13] <> byte ':' || x[16] <> byte ':'
               || not ([ 0; 1; 2; 3; 5; 6; 8; 9; 11; 12; 14; 15; 17; 18 ] |> List.forall (fun at -> digit x[at])) then None
            else
                let mutable fractionalTicks = 0L
                if dateTimeLength <> 19 then
                    if x[19] <> byte '.' || dateTimeLength < 21 || dateTimeLength > 27 then ok <- false
                    else
                        let digits = dateTimeLength - 20
                        for index in 0 .. digits - 1 do
                            if not (digit x[20 + index]) then ok <- false
                            else fractionalTicks <- fractionalTicks * 10L + int64 (x[20 + index] - byte '0')
                        for _ in digits .. 6 do fractionalTicks <- fractionalTicks * 10L
                let year = d2 0 * 100 + d2 2
                let month = d2 5
                let day = d2 8
                let hour = d2 11
                let minute = d2 14
                let second = d2 17
                if not ok || year = 0 || month = 0 || month > 12 || hour > 23 || minute > 59 || second > 59 || day = 0
                   || day > DateTime.DaysInMonth(year, month) then None
                else
                    let localTicks = DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks + fractionalTicks
                    let utcTicks = localTicks - int64 offsetSign * int64 offsetMinutes * 600000000L
                    if utcTicks < 0L || utcTicks > DateTime.MaxValue.Ticks then None
                    else
                        let relative = utcTicks - epochTicks
                        // Ceiling division by 10 (F# division truncates toward zero).
                        Some(if relative >= 0L then (relative + 9L) / 10L else relative / 10L)

    // --------------------------------------------------------------- cookies

    let private cookieWhitespace (scalar: int) =
        (scalar >= 0x09 && scalar <= 0x0d) || scalar = 0x20 || scalar = 0x85 || scalar = 0xa0 || scalar = 0x1680
        || (scalar >= 0x2000 && scalar <= 0x200a) || scalar = 0x2028 || scalar = 0x2029 || scalar = 0x202f
        || scalar = 0x205f || scalar = 0x3000

    /// The Cookie header pairs as raw (name, value) strings, in order. Each
    /// ';'-separated part is trimmed of Unicode whitespace; a part without '='
    /// is skipped; the name is the text before the first '='.
    let cookiePairs (header: string) =
        header.Split(';')
        |> Array.choose (fun part ->
            let runes = part.EnumerateRunes() |> Array.ofSeq
            let mutable first = 0
            let mutable last = runes.Length
            while first < last && cookieWhitespace runes[first].Value do first <- first + 1
            while last > first && cookieWhitespace runes[last - 1].Value do last <- last - 1
            let trimmed = String.Concat(runes[first .. last - 1] |> Array.map (fun rune -> rune.ToString()))
            match trimmed.IndexOf '=' with
            | -1 -> None
            | equal -> Some(trimmed.Substring(0, equal), trimmed.Substring(equal + 1)))

    /// The first cookie value with exactly this name (Rack keeps the first).
    let cookieFind (header: string) (name: string) =
        cookiePairs header |> Array.tryPick (fun (key, value) -> if key = name then Some value else None)

    let cookieCount (header: string) (name: string) =
        cookiePairs header |> Array.filter (fun (key, _) -> key = name) |> Array.length |> int64

    let private rackSafe (value: byte) =
        (value >= byte 'a' && value <= byte 'z') || (value >= byte 'A' && value <= byte 'Z')
        || (value >= byte '0' && value <= byte '9') || value = byte '-' || value = byte '.' || value = byte '_' || value = byte '*'

    /// Rack's URI.encode_www_form_component over UTF-8 bytes: space is '+',
    /// unreserved a-z A-Z 0-9 - . _ * stay, everything else is %XX (upper case).
    let cookieEscape (text: string) =
        let output = StringBuilder()
        for value in Encoding.UTF8.GetBytes text do
            if rackSafe value then output.Append(char value) |> ignore
            elif value = byte ' ' then output.Append('+') |> ignore
            else output.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture)) |> ignore
        output.ToString()

    /// Rack's cookie value decoding: '+' is space and %XX a byte; any malformed
    /// '%' keeps the whole value unchanged. Invalid UTF-8 becomes U+FFFD per
    /// maximal subpart (.NET and the native runtime agree).
    let cookieUnescape (wire: string) =
        let bytes = Encoding.UTF8.GetBytes wire
        let output = ResizeArray<byte>(bytes.Length)
        let hex (value: byte) =
            if value >= byte '0' && value <= byte '9' then int value - int '0'
            elif value >= byte 'a' && value <= byte 'f' then int value - int 'a' + 10
            elif value >= byte 'A' && value <= byte 'F' then int value - int 'A' + 10
            else -1
        let mutable index = 0
        let mutable malformed = false
        while index < bytes.Length && not malformed do
            if bytes[index] = byte '+' then
                output.Add(byte ' ')
                index <- index + 1
            elif bytes[index] = byte '%' then
                if index + 2 >= bytes.Length then malformed <- true
                else
                    let high = hex bytes[index + 1]
                    let low = hex bytes[index + 2]
                    if high < 0 || low < 0 then malformed <- true
                    else
                        output.Add(byte (high * 16 + low))
                        index <- index + 3
            else
                output.Add(bytes[index])
                index <- index + 1
        if malformed then wire else UTF8Encoding(false, false).GetString(output.ToArray())

    let private tokenByte (value: byte) =
        value > 0x20uy && value < 0x7fuy && not ("()<>@,;:\\\"/[]?={}".Contains(char value))

    let private attributeText (text: string) =
        text |> Seq.forall (fun ch -> ch >= ' ' && ch < '\u007f' && ch <> ';')

    /// A Set-Cookie value: name=escaped-value; Path=p; SameSite=s; Expires=e;
    /// Secure; HttpOnly (empty path, same-site or expires are left out). None
    /// for an empty or non-token name, or an attribute with a control byte,
    /// non-ASCII text or ';'.
    let setCookie (name: string) (value: string) (path: string) (expires: string) (sameSite: string) (secure: bool) (httpOnly: bool) =
        let nameBytes = Encoding.UTF8.GetBytes name
        if nameBytes.Length = 0 || not (Array.forall tokenByte nameBytes)
           || not (attributeText path && attributeText expires && attributeText sameSite) then None
        else
            let output = StringBuilder()
            output.Append(name).Append('=').Append(cookieEscape value) |> ignore
            if path <> "" then output.Append("; Path=").Append(path) |> ignore
            if sameSite <> "" then output.Append("; SameSite=").Append(sameSite) |> ignore
            if expires <> "" then output.Append("; Expires=").Append(expires) |> ignore
            if secure then output.Append("; Secure") |> ignore
            if httpOnly then output.Append("; HttpOnly") |> ignore
            Some(output.ToString())

    // ------------------------------------------------------------------ JSON

    /// Containers may nest 32 deep (System.Text.Json MaxDepth = 32).
    let jsonMaxDepth = 32

    let private isWhitespace (b: byte) = b = 0x20uy || b = 0x09uy || b = 0x0auy || b = 0x0duy

    let private skipWhitespace (x: byte[]) (index: int) =
        let mutable i = index
        while i < x.Length && isWhitespace x[i] do i <- i + 1
        i

    let private hexDigit (b: byte) =
        if b >= byte '0' && b <= byte '9' then int b - int '0'
        elif b >= byte 'a' && b <= byte 'f' then int b - int 'a' + 10
        elif b >= byte 'A' && b <= byte 'F' then int b - int 'A' + 10
        else -1

    let private hex4 (x: byte[]) (at: int) =
        if at + 4 > x.Length then -1
        else
            let digits = [| hexDigit x[at]; hexDigit x[at + 1]; hexDigit x[at + 2]; hexDigit x[at + 3] |]
            if Array.exists (fun d -> d < 0) digits then -1
            else digits[0] * 4096 + digits[1] * 256 + digits[2] * 16 + digits[3]

    /// End of the string token starting at x[index] = '"', or -1. Rejects raw
    /// control bytes, unknown escapes and unpaired surrogate escapes.
    let private stringEnd (x: byte[]) (index: int) =
        let mutable i = index + 1
        let mutable result = -2
        while result = -2 do
            if i >= x.Length then result <- -1
            else
                let c = x[i]
                if c = byte '"' then result <- i + 1
                elif c < 0x20uy then result <- -1
                elif c = byte '\\' then
                    if i + 1 >= x.Length then result <- -1
                    else
                        let e = x[i + 1]
                        if e = byte '"' || e = byte '\\' || e = byte '/' || e = byte 'b' || e = byte 'f' || e = byte 'n' || e = byte 'r' || e = byte 't' then i <- i + 2
                        elif e = byte 'u' then
                            let v = hex4 x (i + 2)
                            if v < 0 || (v >= 0xDC00 && v <= 0xDFFF) then result <- -1
                            elif v >= 0xD800 && v <= 0xDBFF then
                                if i + 7 < x.Length && x[i + 6] = byte '\\' && x[i + 7] = byte 'u' then
                                    let low = hex4 x (i + 8)
                                    if low >= 0xDC00 && low <= 0xDFFF then i <- i + 12 else result <- -1
                                else result <- -1
                            else i <- i + 6
                        else result <- -1
                else i <- i + 1
        result

    let private numberEnd (x: byte[]) (index: int) =
        let digit at = at < x.Length && x[at] >= byte '0' && x[at] <= byte '9'
        let mutable i = index
        if i < x.Length && x[i] = byte '-' then i <- i + 1
        if not (digit i) then -1
        else
            if x[i] = byte '0' then i <- i + 1
            else while digit i do i <- i + 1
            let mutable ok = true
            if i < x.Length && x[i] = byte '.' then
                i <- i + 1
                if not (digit i) then ok <- false
                while digit i do i <- i + 1
            if ok && i < x.Length && (x[i] = byte 'e' || x[i] = byte 'E') then
                i <- i + 1
                if i < x.Length && (x[i] = byte '+' || x[i] = byte '-') then i <- i + 1
                if not (digit i) then ok <- false
                while digit i do i <- i + 1
            if ok then i else -1

    let private literalEnd (x: byte[]) (index: int) (word: string) =
        let w = Encoding.ASCII.GetBytes word
        if index + w.Length <= x.Length && Array.forall2 (=) w x[index .. index + w.Length - 1] then index + w.Length else -1

    /// End of the JSON value starting at index (no leading whitespace), or -1.
    let rec private valueEnd (x: byte[]) (index: int) (depth: int) : int =
        if index >= x.Length then -1
        else
            match char x[index] with
            | '"' -> stringEnd x index
            | 't' -> literalEnd x index "true"
            | 'f' -> literalEnd x index "false"
            | 'n' -> literalEnd x index "null"
            | '{' | '[' as opener ->
                let inner = depth + 1
                if inner > jsonMaxDepth then -1
                else
                    let closer = if opener = '{' then byte '}' else byte ']'
                    let mutable i = skipWhitespace x (index + 1)
                    if i < x.Length && x[i] = closer then i + 1
                    else
                        let mutable result = -2
                        while result = -2 do
                            if opener = '{' then
                                if i >= x.Length || x[i] <> byte '"' then result <- -1
                                else
                                    let keyEnd = stringEnd x i
                                    if keyEnd < 0 then result <- -1
                                    else
                                        i <- skipWhitespace x keyEnd
                                        if i >= x.Length || x[i] <> byte ':' then result <- -1
                                        else i <- skipWhitespace x (i + 1)
                            if result = -2 then
                                let e = valueEnd x i inner
                                if e < 0 then result <- -1
                                else
                                    i <- skipWhitespace x e
                                    if i < x.Length && x[i] = byte ',' then i <- skipWhitespace x (i + 1)
                                    elif i < x.Length && x[i] = closer then result <- i + 1
                                    else result <- -1
                        result
            | _ -> numberEnd x index

    /// Strict RFC 8259 parse of one value with optional surrounding whitespace.
    /// The Json value is the exact text of that value.
    let jsonParse (text: byte[]) =
        let start = skipWhitespace text 0
        let e = valueEnd text start 0
        if e < 0 || skipWhitespace text e <> text.Length then None else Some(text[start .. e - 1])

    /// Decoded UTF-8 bytes of a validated string token x[start..end).
    let private decodeString (x: byte[]) (start: int) (stop: int) =
        let output = ResizeArray<byte>()
        let mutable i = start + 1
        while i < stop - 1 do
            if x[i] = byte '\\' then
                let e = x[i + 1]
                match char e with
                | 'u' ->
                    let v = hex4 x (i + 2)
                    let scalar, width =
                        if v >= 0xD800 && v <= 0xDBFF then 0x10000 + ((v - 0xD800) <<< 10) + (hex4 x (i + 8) - 0xDC00), 12
                        else v, 6
                    output.AddRange(Encoding.UTF8.GetBytes(Rune(scalar).ToString()))
                    i <- i + width
                | other ->
                    output.Add(match other with 'b' -> 8uy | 'f' -> 12uy | 'n' -> 10uy | 'r' -> 13uy | 't' -> 9uy | _ -> e)
                    i <- i + 2
            else
                output.Add(x[i])
                i <- i + 1
        output.ToArray()

    /// Members of a validated object or elements of an array: (key, value)
    /// spans, with key = (-1, -1) for arrays.
    let private children (x: byte[]) =
        let items = ResizeArray<(int * int) * (int * int)>()
        if x.Length > 0 && (x[0] = byte '{' || x[0] = byte '[') then
            let isObject = x[0] = byte '{'
            let mutable i = skipWhitespace x 1
            while i < x.Length - 1 do
                let mutable key = (-1, -1)
                if isObject then
                    let keyEnd = stringEnd x i
                    key <- (i, keyEnd)
                    i <- skipWhitespace x (skipWhitespace x keyEnd + 1)
                let e = valueEnd x i 0
                items.Add((key, (i, e)))
                i <- skipWhitespace x e
                if x[i] = byte ',' then i <- skipWhitespace x (i + 1)
        items

    let jsonField (x: byte[]) (name: string) =
        if x.Length = 0 || x[0] <> byte '{' then None
        else
            let wanted = Encoding.UTF8.GetBytes name
            children x
            |> Seq.filter (fun ((ks, ke), _) -> decodeString x ks ke = wanted)
            |> Seq.tryLast
            |> Option.map (fun (_, (vs, ve)) -> x[vs .. ve - 1])

    let jsonIndex (x: byte[]) (index: int64) =
        let items = children x
        if index < 0L || index >= int64 items.Count then None
        else let _, (vs, ve) = items[int index] in Some(x[vs .. ve - 1])

    let jsonKeyAt (x: byte[]) (index: int64) =
        if x.Length = 0 || x[0] <> byte '{' then None
        else
            let items = children x
            if index < 0L || index >= int64 items.Count then None
            else let (ks, ke), _ = items[int index] in Some(utf8.GetString(decodeString x ks ke))

    let jsonCount (x: byte[]) =
        if x.Length > 0 && (x[0] = byte '{' || x[0] = byte '[') then Some(int64 (children x).Count) else None

    let jsonText (x: byte[]) =
        if x.Length > 0 && x[0] = byte '"' then Some(utf8.GetString(decodeString x 0 x.Length)) else None

    /// An integer literal (no fraction or exponent) within Int64.
    let jsonInt (x: byte[]) =
        if x.Length = 0 || not (x[0] = byte '-' || (x[0] >= byte '0' && x[0] <= byte '9'))
           || Array.exists (fun b -> b = byte '.' || b = byte 'e' || b = byte 'E') x then None
        else
            match Int64.TryParse(Encoding.ASCII.GetString x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, value -> Some value
            | _ -> None

    let jsonBool (x: byte[]) =
        if x = "true"B then Some true elif x = "false"B then Some false else None

    let jsonIsNull (x: byte[]) = x = "null"B

    /// A JSON string literal escaped as Rails (ActiveSupport) does for the
    /// cookie envelope: \" \\ \b \f \n \r \t, other controls as \u00xx, and
    /// < > & as < > &; '/' and non-ASCII stay raw.
    let jsonQuote (text: string) =
        let output = StringBuilder("\"")
        for ch in text do
            match ch with
            | '"' -> output.Append("\\\"") |> ignore
            | '\\' -> output.Append("\\\\") |> ignore
            | '<' -> output.Append("\\u003c") |> ignore
            | '>' -> output.Append("\\u003e") |> ignore
            | '&' -> output.Append("\\u0026") |> ignore
            | '\b' -> output.Append("\\b") |> ignore
            | '\f' -> output.Append("\\f") |> ignore
            | '\n' -> output.Append("\\n") |> ignore
            | '\r' -> output.Append("\\r") |> ignore
            | '\t' -> output.Append("\\t") |> ignore
            | c when c < ' ' -> output.Append("\\u00").Append((int c).ToString("x2", CultureInfo.InvariantCulture)) |> ignore
            | c -> output.Append(c) |> ignore
        output.Append('"').ToString()

    // ------------------------------------------------------------------- AES

    /// AES-256-GCM with a 12-byte nonce: ciphertext followed by the 16-byte tag.
    let aesGcmSeal (key: byte[]) (nonce: byte[]) (plaintext: byte[]) (aad: byte[]) =
        if key.Length <> 32 || nonce.Length <> 12 then None
        else
            use aes = new Security.Cryptography.AesGcm(key, 16)
            let ciphertext = Array.zeroCreate<byte> plaintext.Length
            let tag = Array.zeroCreate<byte> 16
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad)
            Some(Array.append ciphertext tag)

    let aesGcmOpen (key: byte[]) (nonce: byte[]) (ciphertext: byte[]) (tag: byte[]) (aad: byte[]) =
        if key.Length <> 32 || nonce.Length <> 12 || tag.Length <> 16 then None
        else
            use aes = new Security.Cryptography.AesGcm(key, 16)
            let plaintext = Array.zeroCreate<byte> ciphertext.Length
            try
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad)
                Some plaintext
            with :? Security.Cryptography.AuthenticationTagMismatchException -> None
