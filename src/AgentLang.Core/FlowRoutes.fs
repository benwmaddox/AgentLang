namespace AgentLang

open System
open System.Text
open System.Text.Json

/// Typed routes (report 306). An enum whose cases carry `at "/rooms/{room}"`
/// paths gets ordinary Flow words, generated when the enum is defined:
/// `t.parse(target) -> Option<T>` (the route a path names), `t.path(route) -> Url`
/// (the path of a route, with String parameters percent-encoded) and, when a
/// path has a String parameter, `t.escape(text)`. `t` is the enum's name with a
/// lower-case first letter, as a record's `t.new`. Each generated word has a
/// generated test. Patterns are literal segments, `{name}` (one non-empty
/// segment) and `prefix{name}`; a parameter is a String or an Int (a segment that
/// does not parse as an Int names no route). Paths are matched as given, with no
/// percent-decoding, case-sensitively. No two paths may match the same text.
module FlowRoutes =
    type Segment =
        | Literal of string
        | Placeholder of prefix: string * name: string

    type private Parameter = { Name: string; Type: LangType }

    type private Route =
        { CaseName: string
          Payload: LangType option
          Pattern: string
          Segments: Segment list
          /// The payload record's name and parameter order, for a record payload.
          Record: (string * Parameter list) option
          Parameters: Parameter list }

    let private fail code message (definition: EnumDefinition) expected actual =
        Diagnostics.raiseError code message (Some definition.Name) (Some definition.Span) expected actual

    let private validName (name: string) =
        name.Length > 0 && Char.IsLetter name[0] && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')

    /// "SiteRoute" -> "siteRoute", as record constructors are named.
    let wordPrefix (typeName: string) = string (Char.ToLowerInvariant typeName[0]) + typeName.Substring 1

    /// The words a routed enum generates, for collision checks.
    let generatedNames (definition: EnumDefinition) =
        if definition.Paths |> List.forall Option.isNone then []
        else
            let prefix = wordPrefix definition.Name
            [ prefix + ".parse"; prefix + ".path"; prefix + ".escape" ]

    let parsePattern (definition: EnumDefinition) (caseName: string) (pattern: string) =
        let bad reason = fail "FLOW_ROUTE_PATTERN" $"Route path {JsonSerializer.Serialize pattern} of {definition.Name}.{caseName}: {reason}." definition [ "/literal/{name}/prefix{name}" ] [ pattern ]
        if not (pattern.StartsWith("/", StringComparison.Ordinal)) then bad "a path starts with /"
        if pattern = "/" then []
        else
            pattern.Substring(1).Split('/')
            |> Array.map (fun part ->
                if part = "" then bad "segments are not empty"
                let openAt = part.IndexOf '{'
                if openAt < 0 then
                    if part.Contains '}' then bad "a } without its {"
                    if part.Contains '?' || part.Contains '#' then bad "a path has no query or fragment"
                    Literal part
                else
                    if part.IndexOf('{', openAt + 1) >= 0 || part.IndexOf '}' <> part.Length - 1 then bad "a parameter ends its segment: {name} or prefix{name}"
                    let name = part.Substring(openAt + 1, part.Length - openAt - 2)
                    if not (validName name) then bad $"'{name}' is not a parameter name"
                    Placeholder(part.Substring(0, openAt), name))
            |> List.ofArray

    /// Whether two segments can match the same path segment.
    let private segmentsOverlap left right =
        match left, right with
        | Literal a, Literal b -> a = b
        | Literal text, Placeholder(prefix, _) | Placeholder(prefix, _), Literal text -> text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length
        | Placeholder(a, _), Placeholder(b, _) -> a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)

    let private routesOf (records: string -> RecordDefinition option) (definition: EnumDefinition) =
        if definition.Paths.Length <> definition.Cases.Length then fail "FLOW_ROUTE_PATTERN" "An enum gives one path entry per case." definition [] []
        let cases = List.zip3 definition.Cases definition.Payloads definition.Paths
        match cases |> List.tryFind (fun (_, _, path) -> path.IsNone) with
        | Some(caseName, _, _) -> fail "FLOW_ROUTE_MISSING" $"{definition.Name}.{caseName} has no path; when one case of an enum has a path, every case does." definition [ $"{caseName} at \"/...\"" ] [ caseName ]
        | None -> ()
        let routes =
            cases |> List.map (fun (caseName, payload, path) ->
                let pattern = Option.get path
                let segments = parsePattern definition caseName pattern
                let names = segments |> List.choose (function Placeholder(_, name) -> Some name | Literal _ -> None)
                match names |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(name, _) -> fail "FLOW_ROUTE_PATTERN" $"Route path of {definition.Name}.{caseName} names {{{name}}} twice." definition [] [ name ]
                | None -> ()
                let parameter name ty =
                    match ty with
                    | TString | TInt -> { Name = name; Type = ty }
                    | other -> fail "FLOW_ROUTE_PAYLOAD" $"Route parameter {{{name}}} of {definition.Name}.{caseName} is {Types.format other}; route parameters are String or Int." definition [ "String"; "Int" ] [ Types.format other ]
                let record, parameters =
                    match payload, names with
                    | None, [] -> None, []
                    | None, _ -> fail "FLOW_ROUTE_PAYLOAD" $"{definition.Name}.{caseName} has no payload for the parameters of its path." definition [ $"{caseName}(Type)" ] names
                    | Some((TString | TInt) as ty), [ name ] -> None, [ parameter name ty ]
                    | Some(TString | TInt), _ -> fail "FLOW_ROUTE_PAYLOAD" $"The path of {definition.Name}.{caseName} needs exactly one parameter for its {Types.format (Option.get payload)} payload." definition [ "one {name}" ] names
                    | Some(TNamed recordName), _ ->
                        match records recordName with
                        | Some record when record.Validator.IsSome ->
                            fail "FLOW_ROUTE_PAYLOAD" $"Route payload record {recordName} has a validator; route payloads are plain records of Strings and Ints." definition [] [ recordName ]
                        | Some record ->
                            let fieldNames = record.Fields |> List.map (fun field -> field.Name)
                            if Set.ofList fieldNames <> Set.ofList names then
                                fail "FLOW_ROUTE_PAYLOAD" $"The path of {definition.Name}.{caseName} must name each field of {recordName} once." definition fieldNames names
                            let fields = record.Fields |> List.map (fun field -> parameter field.Name field.Type)
                            Some(recordName, fields), fields
                        | None -> fail "FLOW_ROUTE_PAYLOAD" $"Route payload {recordName} of {definition.Name}.{caseName} is not a record." definition [ "String"; "Int"; "record" ] [ recordName ]
                    | Some other, _ -> fail "FLOW_ROUTE_PAYLOAD" $"Route payload of {definition.Name}.{caseName} is {Types.format other}; it is a String, an Int or a record of them." definition [ "String"; "Int"; "record" ] [ Types.format other ]
                { CaseName = caseName; Payload = payload; Pattern = pattern; Segments = segments; Record = record; Parameters = parameters })
        for left, right in List.allPairs routes routes do
            if String.CompareOrdinal(left.CaseName, right.CaseName) < 0 && left.Segments.Length = right.Segments.Length
               && List.forall2 segmentsOverlap left.Segments right.Segments then
                fail "FLOW_ROUTE_OVERLAP" $"Paths {JsonSerializer.Serialize left.Pattern} ({left.CaseName}) and {JsonSerializer.Serialize right.Pattern} ({right.CaseName}) can match the same path." definition [ "paths that never match the same text" ] [ left.Pattern; right.Pattern ]
        routes

    let private quote (text: string) = JsonSerializer.Serialize text

    let private typeOf (parameters: Parameter list) name = (parameters |> List.find (fun p -> p.Name = name)).Type

    /// The Flow source of one routed enum's words and their tests.
    let private generate (definition: EnumDefinition) (routes: Route list) =
        let t = wordPrefix definition.Name
        let T = definition.Name
        let hasStrings = routes |> List.exists (fun route -> route.Parameters |> List.exists (fun p -> p.Type = TString))
        let source = StringBuilder()
        let line (text: string) = source.Append(text).Append('\n') |> ignore
        let width = routes |> List.map (fun route -> route.Segments.Length) |> List.fold max 0
        // parse
        line $"fn {t}.parse(target: String) -> Option<{T}> {{"
        line $"    doc \"The {T} that a path names, by {T}'s at paths; none when no path matches it. Generated (report 306).\""
        line "    let rooted = string.starts-with(target, \"/\")"
        line "    let rest = match string.slice(target, 1, string.byte-length(target) - 1) { none => \"\"; some text => text }"
        line "    let parts = if rest == \"\" { list.empty<String>() } else { string.split(rest, \"/\") }"
        line "    let count = if rooted { list.count(parts) } else { -1 }"
        for index in 0 .. width - 1 do
            line $"    let part{index} = match list.get(parts, {index}) {{ none => \"\"; some part => part }}"
        let valueText index prefix =
            if prefix = "" then $"part{index}"
            else $"match string.slice(part{index}, {Encoding.UTF8.GetByteCount(prefix: string)}, string.byte-length(part{index}) - {Encoding.UTF8.GetByteCount(prefix: string)}) {{ none => \"\"; some text => text }}"
        let routeTest (route: Route) =
            let checks =
                route.Segments |> List.mapi (fun index segment ->
                    match segment with
                    | Literal text -> [ $"part{index} == {quote text}" ]
                    | Placeholder(prefix, name) ->
                        let present =
                            if prefix = "" then $"part{index} != \"\""
                            else $"string.starts-with(part{index}, {quote prefix}) && string.byte-length(part{index}) > {Encoding.UTF8.GetByteCount(prefix: string)}"
                        if typeOf route.Parameters name = TInt then [ present; $"int.parse({valueText index prefix}) != option.none<Int>()" ] else [ present ])
                |> List.concat
            String.Join(" && ", $"count == {route.Segments.Length}" :: checks)
        let valueOf (route: Route) name =
            let index, prefix =
                route.Segments |> List.indexed |> List.pick (fun (index, segment) ->
                    match segment with
                    | Placeholder(prefix, found) when found = name -> Some(index, prefix)
                    | _ -> None)
            let text = valueText index prefix
            if typeOf route.Parameters name = TInt then $"match int.parse({text}) {{ none => 0; some number => number }}" else text
        let construct (route: Route) =
            match route.Payload, route.Record with
            | None, _ -> $"{T}.{route.CaseName}()"
            | Some _, Some(recordName, fields) ->
                let arguments = fields |> List.map (fun field -> $"{field.Name} = {valueOf route field.Name}")
                let joined = String.concat ", " arguments
                $"{T}.{route.CaseName}({wordPrefix recordName}.new({joined}))"
            | Some _, None -> $"{T}.{route.CaseName}({valueOf route route.Parameters.Head.Name})"
        routes |> List.iteri (fun index route ->
            let keyword = if index = 0 then "    if" else "    } else if"
            line $"{keyword} {routeTest route} {{"
            line $"        option.some<{T}>({construct route})")
        line "    } else {"
        line $"        option.none<{T}>()"
        line "    }"
        line "}"
        line ""
        // path
        let parameterText (route: Route) (name: string) =
            let access = match route.Record with Some _ -> $"payload.{name}" | None -> "payload"
            if typeOf route.Parameters name = TInt then $"int.to-string({access})" else $"{t}.escape({access})"
        line $"fn {t}.path(given: {T}) -> Url {{"
        line $"    doc \"The path of a {T}, by its at path, with String parameters percent-encoded. Generated (report 306).\""
        line "    match given {"
        for route in routes do
            // Literal text between parameters is one constant: "/users/me/sidebar", not three.
            let pieces =
                if route.Segments.IsEmpty then [ Choice1Of2 "/" ]
                else
                    route.Segments |> List.collect (fun segment ->
                        match segment with
                        | Literal text -> [ Choice1Of2("/" + text) ]
                        | Placeholder(prefix, name) -> [ Choice1Of2("/" + prefix); Choice2Of2(parameterText route name) ])
            let merged =
                pieces
                |> List.fold (fun (acc: Choice<string, string> list) piece ->
                    match piece, acc with
                    | Choice1Of2 text, Choice1Of2 previous :: rest -> Choice1Of2(previous + text) :: rest
                    | _ -> piece :: acc) []
                |> List.rev
                |> List.map (function Choice1Of2 text -> quote text | Choice2Of2 expression -> expression)
            let binding = if route.Payload.IsSome then " payload" else ""
            let body =
                match merged with
                | [ single ] -> single
                | _ -> "string.join([" + String.concat ", " merged + "])"
            line $"        {route.CaseName}{binding} => Url.new({body})"
        line "    }"
        line "}"
        line ""
        // escape
        if hasStrings then
            line $"fn {t}.escape(text: String) -> String {{"
            line "    doc \"Percent-encodes one path segment: A-Z, a-z, 0-9 and - . _ ~ stay, every other byte becomes %XX. Generated (report 306).\""
            line "    let length = string.byte-length(text)"
            // One scan for a byte outside the unreserved set (report 312).
            line "    if string.next-byte-not-in(text, \"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~\", 0) == length {"
            line "        text"
            line "    } else {"
            // Each run of unreserved bytes is one slice; each other byte becomes %XX.
            line "        var pieces = list.empty<String>()"
            line "        var at = 0"
            line "        for step in 0..length {"
            line "            if at >= length {"
            line "                break"
            line "            }"
            line "            let clean-end = string.next-byte-not-in(text, \"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~\", at)"
            line "            if clean-end > at {"
            line "                pieces = list.append(pieces, match string.slice(text, at, clean-end - at) { none => \"\"; some piece => piece })"
            line "            }"
            line "            if clean-end < length {"
            line "                let byte = string.byte-at-or(text, clean-end, 0)"
            line "                let high = match string.slice(\"0123456789ABCDEF\", byte >> 4, 1) { none => \"\"; some digit => digit }"
            line "                let low = match string.slice(\"0123456789ABCDEF\", byte & 15, 1) { none => \"\"; some digit => digit }"
            line "                pieces = list.append(pieces, string.join([\"%\", high, low]))"
            line "            }"
            line "            at = clean-end + 1"
            line "        }"
            line "        string.join(pieces)"
            line "    }"
            line "}"
            line ""
            line $"test {t}.escape/bytes {{"
            line $"    expect {t}.escape(\"Az09-._~\") == \"Az09-._~\""
            line ($"    expect {t}.escape(\"a b/é?\") == " + "\"a%20b%2F%C3%A9%3F\"")
            line "}"
            line ""
        // tests: every case's sample path, and the round trip
        let sample (route: Route) =
            let value (parameter: Parameter) = if parameter.Type = TInt then "7" else "\"x\""
            match route.Payload, route.Record with
            | None, _ -> $"{T}.{route.CaseName}()"
            | Some _, Some(recordName, fields) ->
                let arguments = fields |> List.map (fun field -> $"{field.Name} = {value field}")
                let joined = String.concat ", " arguments
                $"{T}.{route.CaseName}({wordPrefix recordName}.new({joined}))"
            | Some _, None -> $"{T}.{route.CaseName}({value route.Parameters.Head})"
        let samplePath (route: Route) =
            if route.Segments.IsEmpty then "/"
            else
                route.Segments |> List.map (fun segment ->
                    match segment with
                    | Literal text -> "/" + text
                    | Placeholder(prefix, name) -> "/" + prefix + (if typeOf route.Parameters name = TInt then "7" else "x"))
                |> String.concat ""
        line $"test {t}.path/each-case {{"
        for route in routes do
            line $"    expect Url.value({t}.path({sample route})) == {quote (samplePath route)}"
        line "}"
        line ""
        line $"test {t}.parse/round-trip {{"
        for route in routes do
            line $"    expect {t}.parse(Url.value({t}.path({sample route}))) == option.some<{T}>({sample route})"
        line $"    expect {t}.parse(\"\") == option.none<{T}>()"
        line $"    expect {t}.parse(\"no-slash\") == option.none<{T}>()"
        line "}"
        source.ToString()

    /// The generated words and tests of every routed enum in `document`, with
    /// `records` resolving payload record names (document or dictionary).
    let expand (records: string -> RecordDefinition option) (document: FlowProjectDocument) =
        let routed = document.Enums |> List.filter (fun definition -> definition.Paths |> List.exists Option.isSome)
        if routed.IsEmpty then document, false
        else
            let generated =
                routed |> List.map (fun definition ->
                    let routes = routesOf records definition
                    let text = generate definition routes
                    match FlowParser.parseDocumentWithVersion 2 $"<routes:{definition.Name}>" text with
                    | Ok parsed -> parsed
                    | Error diagnostic -> raise (LanguageException diagnostic))
            let names = generated |> List.collect (fun parsed -> parsed.Words |> List.map (fun word -> word.Name)) |> Set.ofList
            match document.Words |> List.tryFind (fun word -> names.Contains word.Name) with
            | Some word -> Diagnostics.raiseError "FLOW_ROUTE_NAME" $"Word '{word.Name}' is generated from a route enum's paths; rename it." (Some word.Name) (Some word.Span) [] [ word.Name ]
            | None -> ()
            { document with
                Words = document.Words @ (generated |> List.collect (fun parsed -> parsed.Words))
                Tests = document.Tests @ (generated |> List.collect (fun parsed -> parsed.Tests)) },
            true
