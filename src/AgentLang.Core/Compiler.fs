namespace AgentLang

open System

module Compiler =
    type CheckedExpression =
        { Stack: LangType list
          Dependencies: Set<string>
          Effects: Set<string> }

    type CheckedWord =
        { Definition: WordDefinition
          Dependencies: Set<string>
          InferredEffects: Set<string> }

    let private validateType allowVariables (knownTypes: Set<string>) span word typeValue =
        let rec validate = function
            | TInt | TFloat | TBool | TString | TBytes | TJson | TUnit -> ()
            | TNamed name when knownTypes.Contains name -> ()
            | TNamed name -> Diagnostics.raiseError "TYPE_UNKNOWN_NAMED_TYPE" $"Type '{name}' has not been declared." (Some word) span [ "declared record" ] [ name ]
            | TVar name when allowVariables -> ()
            | TVar name -> Diagnostics.raiseError "TYPE_UNSUPPORTED_GENERIC" $"Generic type variable '{name}' is not supported in this prototype." (Some word) span [] [ name ]
            | TList item | TOption item -> validate item
            | TResult(ok, error) -> validate ok; validate error
        validate typeValue

    let private unify substitutions expected actual =
        let rec resolve (substitutions: Map<string, LangType>) (ty: LangType) =
            match ty with
            | TVar name when substitutions.ContainsKey name -> resolve substitutions substitutions[name]
            | TList item -> TList(resolve substitutions item)
            | TOption item -> TOption(resolve substitutions item)
            | TResult(ok, error) -> TResult(resolve substitutions ok, resolve substitutions error)
            | _ -> ty
        let rec contains (name: string) (ty: LangType) =
            match ty with
            | TVar other -> name = other
            | TList item | TOption item -> contains name item
            | TResult(ok, error) -> contains name ok || contains name error
            | _ -> false
        let rec loop (substitutions: Map<string, LangType>) (expected: LangType) (actual: LangType) =
            let expected = resolve substitutions expected
            let actual = resolve substitutions actual
            let bind (name: string) (ty: LangType) =
                if contains name ty then raise (InvalidOperationException("recursive type"))
                Map.add name ty substitutions
            match expected, actual with
            | TVar name, ty | ty, TVar name -> bind name ty
            | TList left, TList right | TOption left, TOption right -> loop substitutions left right
            | TResult(leftOk, leftError), TResult(rightOk, rightError) ->
                loop (loop substitutions leftOk rightOk) leftError rightError
            | left, right when left = right -> substitutions
            | _ -> raise (InvalidOperationException("type mismatch"))
        loop substitutions expected actual

    let private substitute (substitutions: Map<string, LangType>) typeValue =
        let rec resolve = function
            | TVar name when substitutions.ContainsKey name -> resolve substitutions[name]
            | TList item -> TList(resolve item)
            | TOption item -> TOption(resolve item)
            | TResult(ok, error) -> TResult(resolve ok, resolve error)
            | other -> other
        resolve typeValue

    let private builtinSignature name =
        let a = TVar "a"
        let b = TVar "b"
        match name with
        | "add" | "subtract" | "multiply" | "divide" | "int.remainder" | "int.min" | "int.max" -> [ TInt; TInt ], [ TInt ]
        | "int.bit-and" | "int.bit-or" | "int.bit-xor" | "int.shift-left" | "int.shift-right" -> [ TInt; TInt ], [ TInt ]
        | "int.bit-not" -> [ TInt ], [ TInt ]
        | "string.less-than" | "string.greater-than" | "string.less-or-equal" | "string.greater-or-equal" -> [ TString; TString ], [ TBool ]
        | "string.compare" -> [ TString; TString ], [ TInt ]
        | "float.add" | "float.subtract" | "float.multiply" | "float.divide" -> [ TFloat; TFloat ], [ TFloat ]
        | "int.less-than" | "int.greater-than" | "int.less-or-equal" | "int.greater-or-equal" -> [ TInt; TInt ], [ TBool ]
        | "float.less-than" | "float.greater-than" | "float.less-or-equal" | "float.greater-or-equal" -> [ TFloat; TFloat ], [ TBool ]
        | "equals" -> [ a; a ], [ TBool ]
        | "<expect-equal>" -> [ a; a ], []
        | "<expect>" -> [ TBool ], []
        | "bool.and" | "bool.or" -> [ TBool; TBool ], [ TBool ]
        | "bool.not" -> [ TBool ], [ TBool ]
        | "string.guid-canonical?" | "string.email-address-valid?" | "instant.is-canonical-utc?" | "string.url-safe?" -> [ TString ], [ TBool ]
        | "string.guid-normalize" | "instant.parse-utc" -> [ TString ], [ TResult(TString, TString) ]
        | "int.add-checked" | "int.multiply-checked" -> [ TInt; TInt ], [ TResult(TInt, TString) ]
        | "int.scale-ratio-toward-zero" -> [ TInt; TInt; TInt ], [ TResult(TInt, TString) ]
        | "instant.before?" -> [ TString; TString ], [ TBool ]
        | "instant.add-days" -> [ TString; TInt ], [ TResult(TString, TString) ]
        | "string.concat" -> [ TString; TString ], [ TString ]
        | "string.replace" -> [ TString; TString; TString ], [ TString ]
        | "string.split" -> [ TString; TString ], [ TList TString ]
        | "string.index-of" -> [ TString; TString; TInt ], [ TOption TInt ]
        | "string.slice" -> [ TString; TInt; TInt ], [ TOption TString ]
        | "string.byte-at" -> [ TString; TInt ], [ TOption TInt ]
        | "string.byte-at-or" -> [ TString; TInt; TInt ], [ TInt ]
        | "int.parse" -> [ TString ], [ TOption TInt ]
        | "string.to-lower-ascii" -> [ TString ], [ TString ]
        | "bytes.from-utf8" -> [ TString ], [ TBytes ]
        | "bytes.to-utf8" -> [ TBytes ], [ TOption TString ]
        | "bytes.length" -> [ TBytes ], [ TInt ]
        | "bytes.concat" -> [ TBytes; TBytes ], [ TBytes ]
        | "bytes.slice" -> [ TBytes; TInt; TInt ], [ TOption TBytes ]
        | "bytes.xor" -> [ TBytes; TBytes ], [ TOption TBytes ]
        | "bytes.equal-constant-time?" -> [ TBytes; TBytes ], [ TBool ]
        | "base64.encode" | "base64.url-encode" | "hex.encode" -> [ TBytes ], [ TString ]
        | "base64.decode" | "base64.url-decode" -> [ TString ], [ TOption TBytes ]
        | "crypto.sha256" -> [ TBytes ], [ TBytes ]
        | "crypto.hmac-sha256" -> [ TBytes; TBytes ], [ TBytes ]
        | "crypto.pbkdf2-sha256" -> [ TBytes; TBytes; TInt; TInt ], [ TOption TBytes ]
        | "crypto.random-bytes" -> [ TInt ], [ TBytes ]
        | "crypto.hmac-sha1" -> [ TBytes; TBytes ], [ TBytes ]
        | "crypto.aes-256-gcm-seal" -> [ TBytes; TBytes; TBytes; TBytes ], [ TOption TBytes ]
        | "crypto.aes-256-gcm-open" -> [ TBytes; TBytes; TBytes; TBytes; TBytes ], [ TOption TBytes ]
        | "json.parse" -> [ TString ], [ TOption TJson ]
        | "json.field" -> [ TJson; TString ], [ TOption TJson ]
        | "json.index" -> [ TJson; TInt ], [ TOption TJson ]
        | "json.key-at" -> [ TJson; TInt ], [ TOption TString ]
        | "json.count" -> [ TJson ], [ TOption TInt ]
        | "json.text" -> [ TJson ], [ TOption TString ]
        | "json.int" -> [ TJson ], [ TOption TInt ]
        | "json.bool" -> [ TJson ], [ TOption TBool ]
        | "json.null?" -> [ TJson ], [ TBool ]
        | "json.encode" -> [ TJson ], [ TString ]
        | "json.quote" -> [ TString ], [ TString ]
        | "cookie.find" -> [ TString; TString ], [ TOption TString ]
        | "cookie.count" -> [ TString; TString ], [ TInt ]
        | "cookie.escape" -> [ TString ], [ TString ]
        | "cookie.unescape" -> [ TString ], [ TString ]
        | "cookie.set-cookie" -> [ TString; TString; TString; TString; TString; TBool; TBool ], [ TOption TString ]
        | "time.add-years" -> [ TInt; TInt ], [ TOption TInt ]
        | "time.rfc1123" -> [ TInt ], [ TOption TString ]
        | "time.iso8601-ms" -> [ TInt ], [ TOption TString ]
        | "time.parse-iso8601" -> [ TString ], [ TOption TInt ]
        | "string.join" -> [ TList TString ], [ TString ]
        | "string.html-escape" -> [ TString ], [ TString ]
        | "string.html-fragment?" -> [ TString ], [ TBool ]
        | "string.contains" | "string.starts-with" | "string.ends-with" -> [ TString; TString ], [ TBool ]
        | "string.length" -> [ TString ], [ TInt ]
        | "string.byte-length" -> [ TString ], [ TInt ]
        | "string.char-length" -> [ TString ], [ TInt ]
        | "string.trim" | "string.to-lower" | "string.to-upper" -> [ TString ], [ TString ]
        | "int.abs" -> [ TInt ], [ TInt ]
        | "int.to-float" -> [ TInt ], [ TFloat ]
        | "float.to-int" | "float.round" -> [ TFloat ], [ TInt ]
        | "int.to-string" -> [ TInt ], [ TString ]
        | "float.to-string" -> [ TFloat ], [ TString ]
        | "list.count" -> [ TList a ], [ TInt ]
        | "list.tail" -> [ TList a ], [ TList a ]
        | "list.append" -> [ TList a; a ], [ TList a ]
        | "list.concat" -> [ TList a; TList a ], [ TList a ]
        | "list.get" -> [ TList a; TInt ], [ TOption a ]
        | "list.is-empty?" -> [ TList a ], [ TBool ]
        | "dup" -> [ a ], [ a; a ]
        | "drop" -> [ a ], []
        | "swap" -> [ a; b ], [ b; a ]
        | "file.read" -> [ TString ], [ TString ]
        | "file.write" -> [ TString; TString ], [ TUnit ]
        | "file.exists?" -> [ TString ], [ TBool ]
        | "clock.now" -> [], [ TString ]
        | "console.write" -> [ TString ], [ TUnit ]
        | _ -> [], []

    let private builtinEffects name =
        match name with
        | "file.read" | "file.exists?" -> Set.singleton "fs.read"
        | "file.write" -> Set.singleton "fs.write"
        | "clock.now" -> Set.singleton "clock.read"
        | "console.write" -> Set.singleton "console.write"
        | "crypto.random-bytes" -> Set.singleton "random.read"
        | _ -> Set.empty

    /// The initial standard vocabulary is deliberately small and boring.
    let private primitiveNames =
            [ "add"; "subtract"; "multiply"; "divide"; "int.remainder"; "float.add"; "float.subtract"; "float.multiply"; "float.divide"
              "int.bit-and"; "int.bit-or"; "int.bit-xor"; "int.bit-not"; "int.shift-left"; "int.shift-right"
              "string.less-than"; "string.greater-than"; "string.less-or-equal"; "string.greater-or-equal"; "string.compare"
              "string.url-safe?"
              "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
              "float.less-than"; "float.greater-than"; "float.less-or-equal"; "float.greater-or-equal"
              "equals"; "bool.and"; "bool.or"; "bool.not"
              "string.guid-canonical?"; "string.guid-normalize"; "string.email-address-valid?"
              "int.add-checked"; "int.multiply-checked"; "int.scale-ratio-toward-zero"
              "instant.parse-utc"; "instant.is-canonical-utc?"; "instant.before?"; "instant.add-days"
              "string.concat"; "string.replace"; "string.join"; "string.html-escape"; "string.html-fragment?"; "string.contains"; "string.starts-with"; "string.ends-with"
              "string.length"; "string.byte-length"; "string.char-length"; "string.trim"; "string.to-lower"; "string.to-upper"; "int.abs"; "int.min"; "int.max"; "int.to-float"; "float.to-int"
              "float.round"; "int.to-string"; "float.to-string"; "list.count"; "list.tail"; "list.append"; "list.concat"; "list.get"; "list.is-empty?"
              "dup"; "drop"; "swap"; "file.read"; "file.write"; "file.exists?"; "clock.now"; "console.write"
              "bytes.from-utf8"; "bytes.to-utf8"; "bytes.length"; "bytes.concat"; "bytes.slice"; "bytes.xor"; "bytes.equal-constant-time?"; "base64.encode"; "base64.decode"; "base64.url-encode"; "base64.url-decode"; "hex.encode"; "crypto.sha256"; "crypto.hmac-sha256"; "crypto.pbkdf2-sha256"; "crypto.random-bytes"; "string.split"; "string.index-of"; "string.slice"; "string.byte-at"; "string.byte-at-or"; "int.parse"; "string.to-lower-ascii"
              "crypto.hmac-sha1"; "crypto.aes-256-gcm-seal"; "crypto.aes-256-gcm-open"; "json.parse"; "json.field"; "json.index"; "json.key-at"; "json.count"; "json.text"; "json.int"; "json.bool"; "json.null?"; "json.encode"; "json.quote"; "cookie.find"; "cookie.count"; "cookie.escape"; "cookie.unescape"; "cookie.set-cookie"; "time.add-years"; "time.rfc1123"; "time.iso8601-ms"; "time.parse-iso8601" ]

    let private standardEntries (names: string list) =
        names
        |> List.map (fun name ->
            let inputs, outputs = builtinSignature name
            let definition =
                { Name = name
                  Inputs = inputs
                  Outputs = outputs
                  Effects = builtinEffects name
                  Maturity = LibraryWord
                  Revision = 1
                  Documentation =
                    match name with
                    | "string.byte-length" -> "Returns the String's length in UTF-8 bytes (constant time)."
                    | "string.char-length" -> "Returns the number of Unicode characters (scalar values) in the String."
                    | "string.length" -> "Deprecated alias of string.byte-length; use string.byte-length or string.char-length. Removed by 2026-11-01."
                    | "string.join" -> "Concatenates every String in List<String> in order with one copy of each part; an empty list returns the empty String."
                    | "string.html-escape" -> "Replaces exactly & < > \" ' with &amp; &lt; &gt; &quot; &#39; (Rails ERB::Util.html_escape); every other UTF-16 code unit is unchanged."
                    | "string.html-fragment?" -> "Returns true when the String is a well-formed HTML fragment: balanced elements (void elements and /> excepted), complete comments and <!doctype>, raw <script>/<style> text, and no bare <. Use it as the Html type validator: type Html : String { validate string.html-fragment? }."
                    | "string.guid-canonical?" -> "Returns true only for lower-case canonical D-format GUID text."
                    | "string.guid-normalize" -> "Accepts Guid.TryParse D, N, B, P, and X forms with surrounding whitespace; returns lower-case D-format text, or INVALID_GUID."
                    | "string.email-address-valid?" -> "Checks an ASCII address policy: local segments use letters, digits, underscore, percent, plus, or hyphen and are separated by single dots with no empty segments; the domain has at least two labels with alphanumeric ends and internal hyphens. Rejects whitespace and values over 254 characters."
                    | "int.add-checked" -> "Adds Int64 values exactly; returns INT_OVERFLOW on overflow."
                    | "int.multiply-checked" -> "Multiplies Int64 values exactly; returns INT_OVERFLOW on overflow."
                    | "int.scale-ratio-toward-zero" -> "Computes (value * numerator) / denominator with an exact Int128 intermediate for the full signed Int64 input range, truncating the quotient toward zero. Returns DIVIDE_BY_ZERO for a zero denominator and INT_OVERFLOW when the final quotient is outside Int64."
                    | "instant.parse-utc" -> "Parses invariant ISO yyyy-MM-ddTHH:mm:ss text with optional one-to-seven fractional digits and an explicit Z, z, or signed HH:mm offset; returns UTC round-trip text with seven fractional digits and +00:00. Unzoned or non-ISO input returns INVALID_INSTANT."
                    | "instant.is-canonical-utc?" -> "Returns true only for the exact UTC O-format text emitted by instant.parse-utc."
                    | "instant.before?" -> "Compares two canonical UTC O-format instants; equality is false and invalid operands raise RUNTIME_INVALID_INSTANT."
                    | "instant.add-days" -> "Adds an integral Int64 day count to a canonical UTC O-format instant; invalid input returns INVALID_INSTANT and range failures return INSTANT_RANGE."
                    | "int.bit-and" | "int.bit-or" | "int.bit-xor" -> "Bitwise and, or, or exclusive-or of two Int64 values (two's complement); the operators are a & b, a | b and a ^ b."
                    | "int.bit-not" -> "Flips every bit of an Int64 value (two's complement), so int.bit-not(x) is -x - 1."
                    | "int.shift-left" -> "Shifts an Int64 left by 0 to 63 bits (a << n); bits shifted out are dropped, with no overflow check. Any other count raises RUNTIME_SHIFT_RANGE."
                    | "int.shift-right" -> "Shifts an Int64 right by 0 to 63 bits (a >> n), keeping the sign (arithmetic shift). Any other count raises RUNTIME_SHIFT_RANGE."
                    | "string.less-than" | "string.greater-than" | "string.less-or-equal" | "string.greater-or-equal" -> "Compares two Strings by their UTF-8 bytes (the same order as Unicode code points, and as Ruby's String#<=>); not locale collation. The operators are <, >, <= and >=."
                    | "string.url-safe?" -> "Returns true when the String is safe as a URL attribute value: no control characters, no leading or trailing space, and a scheme, if any, of http, https, mailto or tel. Relative URLs (/path, ?query, #fragment, path) are safe. The validator of the built-in Url type."
                    | "string.compare" -> "Returns -1, 0 or 1 as the first String sorts before, equal to or after the second, by UTF-8 bytes (Ruby's String#<=>)."
                    | "list.count" -> "Returns the number of elements in List<T>; the closed List<T> parameter is inferred from the input."
                    | "list.tail" -> "Returns List<T> without its first element; empty lists remain empty, the element type and order are preserved, and the input is unchanged."
                    | "list.append" -> "Appends one value with exactly the element type T to List<T>."
                    | "list.concat" -> "Concatenates two lists with the same exact element type T."
                    | "list.get" -> "Returns Option<T>; out-of-range indices produce a typed none."
                    | "list.is-empty?" -> "Tests whether List<T> contains no elements."
                    | _ when builtinEffects name |> Set.isEmpty -> "Trusted deterministic value operation."
                    | _ -> "Host capability operation; denied unless the host grants its declared effect."
                  Body = []
                  SourceText = ""
                  Span = { File = "<standard>"; Line = 1; Column = 1; Length = name.Length } }
            name,
            { Definition = definition
              Builtin = Some(BuiltinOp name)
              Status = Primitive
              Maturity = LibraryWord
              Revision = 1 })
        |> Map.ofList

    let primitives = standardEntries primitiveNames

    /// Primitives the interpreter runs but the owning native backend does not, each with
    /// its reason (report 303). tests/AgentLang.NativeParity.Tests requires every other
    /// primitive to have a native lowering, and this list to stay exact.
    let interpreterOnlyPrimitives : Map<string, string> =
        Map.ofList
            [ "clock.now", "a host effect; native code reads time through the host's operations"
              "console.write", "a host effect; native hosts own their output"
              "file.read", "a host effect; native hosts own files"
              "file.write", "a host effect; native hosts own files"
              "file.exists?", "a host effect; native hosts own files"
              "instant.parse-utc", "business-language demo (.NET round-trip text); not used by native apps"
              "instant.is-canonical-utc?", "business-language demo; not used by native apps"
              "instant.before?", "business-language demo; not used by native apps"
              "instant.add-days", "business-language demo; not used by native apps"
              "int.add-checked", "business-language demo (Result of INT_OVERFLOW); use + with its overflow check"
              "int.multiply-checked", "business-language demo (Result of INT_OVERFLOW); use * with its overflow check"
              "int.scale-ratio-toward-zero", "business-language demo (Int128 intermediate)"
              "string.guid-canonical?", "business-language demo (.NET Guid parsing)"
              "string.guid-normalize", "business-language demo (.NET Guid parsing)"
              "string.email-address-valid?", "business-language demo address policy"
              "string.to-lower", "Unicode case mapping needs case tables; string.to-lower-ascii is native"
              "string.to-upper", "Unicode case mapping needs case tables"
              "list.tail", "a copy of every element but the first; for loops with an index, or list.get, are native" ]

    /// Test-only intrinsics: reachable only from test bodies through Call names, never listed in the public vocabulary.
    let testIntrinsics = standardEntries [ "<expect>"; "<expect-equal>" ]

    let testWords = Map.fold (fun words name entry -> Map.add name entry words) primitives testIntrinsics

    let private inferCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) wordName (span: SourceSpan) (stack: LangType list) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"Word '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if stack.Length < definition.Inputs.Length then
                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" $"'{wordName}' requires {definition.Inputs.Length} value(s), but the stack has {stack.Length}." (Some wordName) (Some span) (definition.Inputs |> List.map Types.format) (stack |> List.map Types.format)
            let args = stack |> List.skip (stack.Length - definition.Inputs.Length)
            let prefix = stack |> List.take (stack.Length - definition.Inputs.Length)
            let mutable substitutions = Map.empty
            try
                for expected, actual in List.zip definition.Inputs args do substitutions <- unify substitutions expected actual
            with _ ->
                Diagnostics.raiseError "TYPE_STACK_MISMATCH" $"Arguments passed to '{wordName}' do not match its signature." (Some wordName) (Some span) (definition.Inputs |> List.map Types.format) (args |> List.map Types.format)
            let callOutputs = definition.Outputs |> List.map (substitute substitutions)
            prefix @ callOutputs, entry, args, callOutputs

    let private higherOrderCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (span: SourceSpan) (itemType: LangType) (requiredOutput: LangType option) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"List operation target '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if definition.Inputs.Length <> 1 || definition.Outputs.Length <> 1 then
                let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
                let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
                Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' must have exactly one input and one output." (Some wordName) (Some span) [ "T -> U" ] [ inputs + " -> " + outputs ]
            let substitutions =
                try unify Map.empty definition.Inputs.Head itemType
                with _ -> Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' cannot accept list elements of type {Types.format itemType}." (Some wordName) (Some span) [ Types.format itemType ] (definition.Inputs |> List.map Types.format)
            let actualOutput = substitute substitutions definition.Outputs.Head
            match requiredOutput with
            | Some expected when actualOutput <> expected ->
                Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' must return {Types.format expected}." (Some wordName) (Some span) [ Types.format expected ] [ Types.format actualOutput ]
            | _ -> actualOutput, entry

    let private higherOrderFoldCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (span: SourceSpan) (itemType: LangType) (accumulatorType: LangType) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"List fold target '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do
                validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if definition.Inputs.Length <> 2 || definition.Outputs.Length <> 1 then
                let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
                let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
                Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must have exactly two inputs and one output." (Some wordName) (Some span)
                    [ $"{Types.format accumulatorType} {Types.format itemType} -> {Types.format accumulatorType}" ] [ inputs + " -> " + outputs ]
            let substitutions =
                try
                    let accumulatorSubstitutions = unify Map.empty definition.Inputs[0] accumulatorType
                    unify accumulatorSubstitutions definition.Inputs[1] itemType
                with _ ->
                    Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must accept accumulator {Types.format accumulatorType} followed by item {Types.format itemType}." (Some wordName) (Some span)
                        [ $"{Types.format accumulatorType} {Types.format itemType} -> {Types.format accumulatorType}" ]
                        (definition.Inputs |> List.map Types.format)
            let actualOutput = substitute substitutions definition.Outputs.Head
            if actualOutput <> accumulatorType then
                Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must return the exact accumulator type {Types.format accumulatorType}." (Some wordName) (Some span)
                    [ Types.format accumulatorType ] [ Types.format actualOutput ]
            entry

    let private containsOpenType typeValue =
        let rec visit = function
            | TVar _ -> true
            | TList item | TOption item -> visit item
            | TResult(okType, errorType) -> visit okType || visit errorType
            | _ -> false
        visit typeValue

    let private higherOrderMapWithContextCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (span: SourceSpan) (contextType: LangType) (itemType: LangType) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"List map target '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do
                validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if definition.Inputs.Length <> 2 || definition.Outputs.Length <> 1 then
                let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
                let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
                Diagnostics.raiseError "TYPE_LIST_MAP_CONTEXT_CALLBACK" $"List map target '{wordName}' with a context must have exactly two inputs and one output." (Some wordName) (Some span)
                    [ $"{Types.format contextType} {Types.format itemType} -> <output>" ] [ inputs + " -> " + outputs ]
            let substitutions =
                try
                    let contextSubstitutions = unify Map.empty definition.Inputs[0] contextType
                    unify contextSubstitutions definition.Inputs[1] itemType
                with _ ->
                    Diagnostics.raiseError "TYPE_LIST_MAP_CONTEXT_CALLBACK" $"List map target '{wordName}' must accept context {Types.format contextType} followed by item {Types.format itemType}." (Some wordName) (Some span)
                        [ $"{Types.format contextType} {Types.format itemType} -> <output>" ]
                        (definition.Inputs |> List.map Types.format)
            let outputType = substitute substitutions definition.Outputs.Head
            if containsOpenType outputType then
                Diagnostics.raiseError "TYPE_LIST_MAP_CONTEXT_CALLBACK" $"List map target '{wordName}' must produce a closed output type." (Some wordName) (Some span) [] [ Types.format outputType ]
            entry, outputType

    let listCallbackOutputType knownTypes words wordName span itemType =
        let outputType, _ = higherOrderCall knownTypes words wordName span itemType None
        outputType

    type private TypedCallSignature =
        { CallInputs: LangType list
          CallOutputs: LangType list }

    type private TypedNode =
        { SourceExpression: Expr
          InputStack: LangType list
          OutputStack: LangType list
          InputLocals: Map<string, LangType>
          OutputLocals: Map<string, LangType>
          CallSignature: TypedCallSignature option
          ChildBodies: TypedBody list }

    and private TypedBody =
        { EntryStack: LangType list
          ExitStack: LangType list
          EntryLocals: Map<string, LangType>
          ExitLocals: Map<string, LangType>
          Nodes: TypedNode list }

    type private BodyInference =
        { InferredBody: TypedBody
          Dependencies: Set<string>
          Effects: Set<string> }

    let private inferBody (knownTypes: Set<string>) (knownEnums: Map<string, EnumDefinition>) (words: Map<string, WordEntry>) (wordName: string) (wordSpan: SourceSpan option) (initialStack: LangType list) (initialLocals: Map<string, LangType>) (body: Expr list) =
        let mutable dependencies = Set.empty
        let mutable effects = Set.empty
        // Enclosing loops (Some loop-invariant stack) and compact barriers (None).
        let loopContexts = System.Collections.Generic.Stack<LangType list option>()
        let rec visit (entryStack: LangType list) (entryLocals: Map<string, LangType>) (expressions: Expr list) =
            let mutable stack = entryStack
            let mutable locals = entryLocals
            let nodes = ResizeArray<TypedNode>()
            for expression in expressions do
                let inputStack = stack
                let inputLocals = locals
                let mutable callSignature = None
                let mutable childBodies = []
                let nextStack, nextLocals =
                    match expression with
                    | Push(literal, _) -> stack @ [ literal |> Types.literalValue |> Types.ofValue ], locals
                    | Load(name, expressionSpan) ->
                        match locals.TryFind name with
                        | Some typeValue -> stack @ [ typeValue ], locals
                        | None -> Diagnostics.raiseError "NAME_UNKNOWN_LOCAL" $"Local '${name}' has not been bound." (Some wordName) (Some expressionSpan) [] [ name ]
                    | Let(name, expressionSpan) ->
                        if List.isEmpty stack then Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" $"Binding '{name}' requires one stack value." (Some wordName) (Some expressionSpan) [ "value" ] []
                        stack |> List.take (stack.Length - 1), Map.add name (List.last stack) locals
                    | Call(name, expressionSpan) ->
                        let output, entry, callInputs, callOutputs = inferCall knownTypes words name expressionSpan stack
                        callSignature <- Some { CallInputs = callInputs; CallOutputs = callOutputs }
                        dependencies <- Set.add name dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        output, locals
                    | ConstructContainer(kind, arguments, expressionSpan) ->
                        for typeValue in arguments do validateType false knownTypes (Some expressionSpan) wordName typeValue
                        let requireArity expected =
                            if arguments.Length <> expected then
                                Diagnostics.raiseError "TYPE_CONTAINER_CONSTRUCTOR" "Container constructor has the wrong number of explicit type arguments." (Some wordName) (Some expressionSpan) [ string expected ] [ string arguments.Length ]
                        let consume expectedType resultType =
                            if List.isEmpty stack then
                                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "Typed container constructor requires one payload value." (Some wordName) (Some expressionSpan) [ Types.format expectedType ] []
                            let prefix = stack |> List.take (stack.Length - 1)
                            let actual = List.last stack
                            try unify Map.empty expectedType actual |> ignore
                            with _ ->
                                Diagnostics.raiseError "TYPE_CONTAINER_PAYLOAD" "Typed container constructor payload does not match its explicit type argument." (Some wordName) (Some expressionSpan) [ Types.format expectedType ] [ Types.format actual ]
                            prefix @ [ resultType ], locals
                        match kind with
                        | ListEmpty ->
                            requireArity 1
                            stack @ [ TList arguments.Head ], locals
                        | ListSingleton ->
                            requireArity 1
                            consume arguments.Head (TList arguments.Head)
                        | ListOf count ->
                            requireArity 1
                            if count < 1 || stack.Length < count then
                                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List literal requires one value per element." (Some wordName) (Some expressionSpan) [ string count ] [ string stack.Length ]
                            let prefix = stack |> List.take (stack.Length - count)
                            for actual in stack |> List.skip (stack.Length - count) do
                                try unify Map.empty arguments.Head actual |> ignore
                                with _ ->
                                    Diagnostics.raiseError "TYPE_CONTAINER_PAYLOAD" "List literal element does not match the list element type." (Some wordName) (Some expressionSpan) [ Types.format arguments.Head ] [ Types.format actual ]
                            prefix @ [ TList arguments.Head ], locals
                        | OptionNone ->
                            requireArity 1
                            stack @ [ TOption arguments.Head ], locals
                        | OptionSome ->
                            requireArity 1
                            consume arguments.Head (TOption arguments.Head)
                        | ResultOk ->
                            requireArity 2
                            consume arguments[0] (TResult(arguments[0], arguments[1]))
                        | ResultError ->
                            requireArity 2
                            consume arguments[1] (TResult(arguments[0], arguments[1]))
                        | DbParams _ ->
                            requireArity 2
                            consume arguments[0] (TList arguments[1])
                        | DbDecode ->
                            requireArity 3
                            consume (TResult(TList arguments[1], arguments[2])) (TResult(TList arguments[0], arguments[2]))
                        | ForeignCall _ ->
                            // Inputs, then the output (report 307).
                            if arguments.IsEmpty then requireArity 1
                            let inputs = arguments |> List.take (arguments.Length - 1)
                            if stack.Length < inputs.Length then
                                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "A C binding call needs one value per parameter." (Some wordName) (Some expressionSpan) [ string inputs.Length ] [ string stack.Length ]
                            let prefix = stack |> List.take (stack.Length - inputs.Length)
                            for expected, actual in List.zip inputs (stack |> List.skip (stack.Length - inputs.Length)) do
                                if expected <> actual then
                                    Diagnostics.raiseError "TYPE_CONTAINER_PAYLOAD" "A C binding argument does not match its parameter type." (Some wordName) (Some expressionSpan) [ Types.format expected ] [ Types.format actual ]
                            prefix @ [ List.last arguments ], locals
                        | HtmlTemplate parts ->
                            requireArity 1
                            let holeTypes =
                                parts |> List.choose (function
                                    | HtmlTemplatePart.StringHole -> Some TString
                                    | HtmlTemplatePart.IntHole -> Some TInt
                                    | HtmlTemplatePart.HtmlHole -> Some arguments.Head
                                    | HtmlTemplatePart.HtmlListHole -> Some(TList arguments.Head)
                                    | HtmlTemplatePart.Text _ -> None)
                            if stack.Length < holeTypes.Length then
                                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "html template requires one value per hole." (Some wordName) (Some expressionSpan) [ string holeTypes.Length ] [ string stack.Length ]
                            let prefix = stack |> List.take (stack.Length - holeTypes.Length)
                            // A String hole also takes a refined String (its generated accessor
                            // unwraps it to a String): it is escaped the same way (report 302).
                            let refinedString name =
                                match words.TryFind(name + ".value") with
                                | Some entry -> entry.Definition.Inputs = [ TNamed name ] && entry.Definition.Outputs = [ TString ]
                                | None -> false
                            for expected, actual in List.zip holeTypes (stack |> List.skip (stack.Length - holeTypes.Length)) do
                                match expected, actual with
                                | TString, TNamed name when refinedString name -> ()
                                | _ ->
                                    try unify Map.empty expected actual |> ignore
                                    with _ ->
                                        Diagnostics.raiseError "TYPE_TEMPLATE_HOLE" "An html template hole must be a String, Int or refined String (escaped), or Html (inserted as-is)." (Some wordName) (Some expressionSpan) [ Types.format expected ] [ Types.format actual ]
                            prefix @ [ arguments.Head ], locals
                    | (MapList(target, expressionSpan) | FilterList(target, expressionSpan) | EachList(target, expressionSpan)) as operation ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List higher-order operation requires a list." (Some wordName) (Some expressionSpan) [ "List<T>" ] []
                        let itemType, prefix =
                            match List.last stack with
                            | TList item -> item, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_LIST_REQUIRED" "List higher-order operation requires List<T> at the top of the stack." (Some wordName) (Some expressionSpan) [ "List<T>" ] [ Types.format actual ]
                        let requiredOutput =
                            match operation with
                            | MapList _ -> None
                            | FilterList _ -> Some TBool
                            | EachList _ -> Some TUnit
                            | _ -> failwith "unreachable"
                        let callbackOutput, entry = higherOrderCall knownTypes words target expressionSpan itemType requiredOutput
                        callSignature <- Some { CallInputs = [ itemType ]; CallOutputs = [ callbackOutput ] }
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        let output =
                            match operation with
                            | MapList _ -> TList callbackOutput
                            | FilterList _ -> TList itemType
                            | EachList _ -> TUnit
                            | _ -> failwith "unreachable"
                        prefix @ [ output ], locals
                    | FoldList(target, expressionSpan) ->
                        if stack.Length < 2 then
                            Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List fold requires a List<T> followed by an initial accumulator." (Some wordName) (Some expressionSpan)
                                [ "List<T> Accumulator" ] (stack |> List.map Types.format)
                        let prefix = stack |> List.take (stack.Length - 2)
                        let actualInputs = stack |> List.skip (stack.Length - 2)
                        let itemType, accumulatorType =
                            match actualInputs with
                            | [ TList item; accumulator ] -> item, accumulator
                            | [ actualList; accumulator ] ->
                                Diagnostics.raiseError "TYPE_LIST_REQUIRED" "List fold requires List<T> below its initial accumulator." (Some wordName) (Some expressionSpan)
                                    [ "List<T> Accumulator" ] [ Types.format actualList; Types.format accumulator ]
                            | _ -> failwith "unreachable"
                        let entry = higherOrderFoldCall knownTypes words target expressionSpan itemType accumulatorType
                        callSignature <- Some { CallInputs = [ accumulatorType; itemType ]; CallOutputs = [ accumulatorType ] }
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        prefix @ [ accumulatorType ], locals
                    | MapListWithContext(target, expressionSpan) ->
                        if stack.Length < 2 then
                            Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List map with context requires a List<T> followed by a context value." (Some wordName) (Some expressionSpan)
                                [ "List<T> Context" ] (stack |> List.map Types.format)
                        let prefix = stack |> List.take (stack.Length - 2)
                        let actualInputs = stack |> List.skip (stack.Length - 2)
                        let itemType, contextType =
                            match actualInputs with
                            | [ TList item; context ] -> item, context
                            | [ actualList; context ] ->
                                Diagnostics.raiseError "TYPE_LIST_REQUIRED" "List map with context requires List<T> below its context value." (Some wordName) (Some expressionSpan)
                                    [ "List<T> Context" ] [ Types.format actualList; Types.format context ]
                            | _ -> failwith "unreachable"
                        let entry, callbackOutput = higherOrderMapWithContextCall knownTypes words target expressionSpan contextType itemType
                        callSignature <- Some { CallInputs = [ contextType; itemType ]; CallOutputs = [ callbackOutput ] }
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        prefix @ [ TList callbackOutput ], locals
                    | If(thenBranch, elseBranch, expressionSpan) ->
                        if List.isEmpty stack || List.last stack <> TBool then
                            let actual = stack |> List.tryLast |> Option.map Types.format |> Option.defaultValue "<empty>"
                            Diagnostics.raiseError "TYPE_IF_REQUIRES_BOOL" "'if' consumes a Bool from the top of the stack." (Some wordName) (Some expressionSpan) [ "Bool" ] [ actual ]
                        let before = stack |> List.take (stack.Length - 1)
                        let thenBody = visit before locals thenBranch
                        let elseBody = visit before locals elseBranch
                        if thenBody.ExitStack <> elseBody.ExitStack then Diagnostics.raiseError "TYPE_BRANCH_STACK_MISMATCH" "Both branches of an if expression must leave the same stack types." (Some wordName) (Some expressionSpan) (thenBody.ExitStack |> List.map Types.format) (elseBody.ExitStack |> List.map Types.format)
                        if thenBody.ExitLocals <> elseBody.ExitLocals then Diagnostics.raiseError "TYPE_BRANCH_LOCAL_MISMATCH" "Both branches of an if expression must bind the same locals with the same types." (Some wordName) (Some expressionSpan) (thenBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (elseBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ thenBody; elseBody ]
                        thenBody.ExitStack, thenBody.ExitLocals
                    | Scope(innerBody, _) ->
                        let scopedBody = visit stack locals innerBody
                        childBodies <- [ scopedBody ]
                        scopedBody.ExitStack, locals
                    | Compact(innerBody, expressionSpan) ->
                        loopContexts.Push None
                        let compactBody = try visit stack locals innerBody finally loopContexts.Pop() |> ignore
                        let exit = compactBody.ExitStack
                        if exit.Length <> stack.Length + 1 || List.take stack.Length exit <> stack then
                            Diagnostics.raiseError "TYPE_COMPACT_RESULT" "A compact block must leave the outer stack unchanged and produce exactly one value." (Some wordName) (Some expressionSpan)
                                ((stack |> List.map Types.format) @ [ "one value" ]) (exit |> List.map Types.format)
                        childBodies <- [ compactBody ]
                        exit, locals
                    | Assign(name, expressionSpan) ->
                        match locals.TryFind name, List.tryLast stack with
                        | None, _ -> Diagnostics.raiseError "NAME_UNKNOWN_LOCAL" $"Local '{name}' has not been bound." (Some wordName) (Some expressionSpan) [] [ name ]
                        | Some _, None -> Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" $"Assignment to '{name}' requires one stack value." (Some wordName) (Some expressionSpan) [ "value" ] []
                        | Some declared, Some actual when declared <> actual ->
                            Diagnostics.raiseError "TYPE_ASSIGN_MISMATCH" $"Assignment to '{name}' must keep its declared type." (Some wordName) (Some expressionSpan) [ Types.format declared ] [ Types.format actual ]
                        | Some _, Some _ -> stack |> List.take (stack.Length - 1), locals
                    | Break expressionSpan | Continue expressionSpan ->
                        match (if loopContexts.Count = 0 then None else loopContexts.Peek()) with
                        | None -> Diagnostics.raiseError "TYPE_LOOP_CONTROL_OUTSIDE_LOOP" "break and continue must appear inside a loop body (not inside a compact block within it)." (Some wordName) (Some expressionSpan) [ "enclosing loop" ] []
                        | Some loopStack when loopStack <> stack ->
                            Diagnostics.raiseError "TYPE_LOOP_CONTROL_STACK" "break and continue must be statements; no partial values may be pending." (Some wordName) (Some expressionSpan) (loopStack |> List.map Types.format) (stack |> List.map Types.format)
                        | Some _ -> stack, locals
                    | ForRange(indexName, body, expressionSpan) ->
                        let before, bounds = (if stack.Length < 2 then [], stack else List.take (stack.Length - 2) stack, List.skip (stack.Length - 2) stack)
                        if bounds <> [ TInt; TInt ] then
                            Diagnostics.raiseError "TYPE_LOOP_RANGE" "A range loop needs Int start and end values." (Some wordName) (Some expressionSpan) [ "Int"; "Int" ] (bounds |> List.map Types.format)
                        if locals.ContainsKey indexName then
                            Diagnostics.raiseError "TYPE_LOOP_LOCAL_SHADOW" $"Loop local '{indexName}' cannot shadow an existing local." (Some wordName) (Some expressionSpan) [] [ indexName ]
                        let bodyTyped = visitLoopBody before locals [ indexName, TInt ] body expressionSpan
                        childBodies <- [ bodyTyped ]
                        before, locals
                    | ForList(itemName, indexName, body, expressionSpan) ->
                        let itemType, before =
                            match List.tryLast stack with
                            | Some(TList item) -> item, List.take (stack.Length - 1) stack
                            | actual -> Diagnostics.raiseError "TYPE_LOOP_LIST" "A list loop needs a List<T> value." (Some wordName) (Some expressionSpan) [ "List<T>" ] (actual |> Option.map Types.format |> Option.toList)
                        let bound = (itemName, itemType) :: (indexName |> Option.map (fun name -> name, TInt) |> Option.toList)
                        for name, _ in bound do
                            if locals.ContainsKey name then
                                Diagnostics.raiseError "TYPE_LOOP_LOCAL_SHADOW" $"Loop local '{name}' cannot shadow an existing local." (Some wordName) (Some expressionSpan) [] [ name ]
                        if indexName = Some itemName then
                            Diagnostics.raiseError "TYPE_LOOP_LOCAL_SHADOW" $"Loop item and index cannot share the name '{itemName}'." (Some wordName) (Some expressionSpan) [] [ itemName ]
                        let bodyTyped = visitLoopBody before locals bound body expressionSpan
                        childBodies <- [ bodyTyped ]
                        before, locals
                    | MatchOption(someName, someBranch, noneBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] []
                        let itemType, before =
                            match List.last stack with
                            | TOption item -> item, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] [ Types.format actual ]
                        if locals.ContainsKey someName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" $"Match payload local {someName} cannot shadow an existing local." (Some wordName) (Some expressionSpan) [] [ someName ]
                        let someBody = visit before (Map.add someName itemType locals) someBranch
                        let someLocals = Map.remove someName someBody.ExitLocals
                        let noneBody = visit before locals noneBranch
                        if someBody.ExitStack <> noneBody.ExitStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both option match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (someBody.ExitStack |> List.map Types.format) (noneBody.ExitStack |> List.map Types.format)
                        if someLocals <> noneBody.ExitLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both option match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (someLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (noneBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ someBody; noneBody ]
                        someBody.ExitStack, someLocals
                    | MatchResult(okName, errorName, okBranch, errorBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] []
                        let okType, errorType, before =
                            match List.last stack with
                            | TResult(ok, error) -> ok, error, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] [ Types.format actual ]
                        if locals.ContainsKey okName || locals.ContainsKey errorName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" "Result match payload locals cannot shadow existing locals." (Some wordName) (Some expressionSpan) [] [ okName; errorName ]
                        let okBody = visit before (Map.add okName okType locals) okBranch
                        let okLocals = Map.remove okName okBody.ExitLocals
                        let errorBody = visit before (Map.add errorName errorType locals) errorBranch
                        let errorLocals = Map.remove errorName errorBody.ExitLocals
                        if okBody.ExitStack <> errorBody.ExitStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both result match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (okBody.ExitStack |> List.map Types.format) (errorBody.ExitStack |> List.map Types.format)
                        if okLocals <> errorLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both result match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (okLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (errorLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ okBody; errorBody ]
                        okBody.ExitStack, okLocals
                    | MatchEnum(cases, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match consumes a declared enum value from the top of the stack." (Some wordName) (Some expressionSpan) [ "enum value" ] []
                        let enumName =
                            match List.last stack with
                            | TNamed name when knownEnums.ContainsKey name -> name
                            | TNamed name -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match scrutinee must have a declared enum type." (Some wordName) (Some expressionSpan) [ "declared enum" ] [ name ]
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match scrutinee must have a declared enum type." (Some wordName) (Some expressionSpan) [ "declared enum" ] [ Types.format actual ]
                        let declaredCases = knownEnums[enumName].Cases
                        let authoredCases = cases |> List.map (fun (name, _, _) -> name)
                        let duplicateCases = authoredCases |> List.groupBy id |> List.choose (fun (name, values) -> if values.Length > 1 then Some name else None)
                        let unknownCases = authoredCases |> List.filter (fun name -> not (List.contains name declaredCases)) |> List.distinct
                        let missingCases = declaredCases |> List.filter (fun name -> not (List.contains name authoredCases))
                        if not duplicateCases.IsEmpty || not unknownCases.IsEmpty || not missingCases.IsEmpty then
                            Diagnostics.raiseError "TYPE_MATCH_ENUM_CASE_SET" "Enum match must contain every declared case exactly once and no other labels." (Some wordName) (Some expressionSpan)
                                declaredCases ((authoredCases |> List.distinct) @ (duplicateCases |> List.map (fun name -> "duplicate:" + name)) @ (unknownCases |> List.map (fun name -> "extra:" + name)) @ (missingCases |> List.map (fun name -> "missing:" + name)))
                        let before = stack |> List.take (stack.Length - 1)
                        let enumDefinition = knownEnums[enumName]
                        let branchBodies =
                            cases |> List.map (fun (caseName, binding, branch) ->
                                // A payload case may bind its payload to a local for its branch (report 306).
                                let payload = enumDefinition.Payloads[List.findIndex ((=) caseName) declaredCases]
                                match binding, payload with
                                | None, _ -> visit before locals branch
                                | Some bindingName, Some payloadType -> visit before (Map.add bindingName payloadType locals) branch
                                | Some bindingName, None ->
                                    Diagnostics.raiseError "TYPE_MATCH_ENUM_BINDING" $"Enum case '{caseName}' has no payload to bind to '{bindingName}'." (Some wordName) (Some expressionSpan) [ $"{caseName} =>" ] [ $"{caseName} {bindingName} =>" ])
                        // An arm's payload binding is its own local; the arms join on the outer locals.
                        let outerLocals =
                            List.zip cases branchBodies
                            |> List.map (fun ((_, binding, _), body) -> match binding with Some name -> Map.remove name body.ExitLocals | None -> body.ExitLocals)
                        match List.zip branchBodies outerLocals with
                        | [] -> Diagnostics.raiseError "TYPE_MATCH_ENUM_CASE_SET" "Enum match must contain at least one case." (Some wordName) (Some expressionSpan) declaredCases []
                        | (firstBranch, firstLocals) :: remainingBranches ->
                            for branch, branchLocals in remainingBranches do
                                if branch.ExitStack <> firstBranch.ExitStack then
                                    Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Every enum match case must leave the same output stack types." (Some wordName) (Some expressionSpan) (firstBranch.ExitStack |> List.map Types.format) (branch.ExitStack |> List.map Types.format)
                                if branchLocals <> firstLocals then
                                    Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Every enum match case must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan)
                                        (firstLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                                        (branchLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                            childBodies <- branchBodies
                            firstBranch.ExitStack, firstLocals
                nodes.Add
                    { SourceExpression = expression
                      InputStack = inputStack
                      OutputStack = nextStack
                      InputLocals = inputLocals
                      OutputLocals = nextLocals
                      CallSignature = callSignature
                      ChildBodies = childBodies }
                stack <- nextStack
                locals <- nextLocals
            { EntryStack = entryStack
              ExitStack = stack
              EntryLocals = entryLocals
              ExitLocals = locals
              Nodes = List.ofSeq nodes }
        and visitLoopBody (before: LangType list) (locals: Map<string, LangType>) (bound: (string * LangType) list) (body: Expr list) (loopSpan: SourceSpan) =
            let entryLocals = bound |> List.fold (fun found (name, ty) -> Map.add name ty found) locals
            loopContexts.Push(Some before)
            let bodyTyped = try visit before entryLocals body finally loopContexts.Pop() |> ignore
            if bodyTyped.ExitStack <> before then
                Diagnostics.raiseError "TYPE_LOOP_BODY_STACK" "A loop body must not leave values on the stack." (Some wordName) (Some loopSpan) (before |> List.map Types.format) (bodyTyped.ExitStack |> List.map Types.format)
            if locals |> Map.exists (fun name ty -> bodyTyped.ExitLocals.TryFind name <> Some ty) then
                Diagnostics.raiseError "TYPE_LOOP_BODY_LOCALS" "A loop body must keep every outer local with its type." (Some wordName) (Some loopSpan) [] []
            bodyTyped
        { InferredBody = visit initialStack initialLocals body
          Dependencies = dependencies
          Effects = effects }

    let private checkedExpression (inferred: BodyInference) =
        { Stack = inferred.InferredBody.ExitStack
          Dependencies = inferred.Dependencies
          Effects = inferred.Effects }

    let checkExpression knownTypes words body =
        inferBody knownTypes Map.empty words "<eval>" None [] Map.empty body |> checkedExpression

    let checkExpressionWithEnums knownTypes enums words body =
        inferBody knownTypes enums words "<eval>" None [] Map.empty body |> checkedExpression

    let private checkDefinitionDetailed knownTypes knownEnums words (definition: WordDefinition) =
        for typeValue in definition.Inputs @ definition.Outputs do validateType false knownTypes (Some definition.Span) definition.Name typeValue
        let inferred = inferBody knownTypes knownEnums words definition.Name (Some definition.Span) definition.Inputs Map.empty definition.Body
        if inferred.InferredBody.ExitStack <> definition.Outputs then
            Diagnostics.raiseError "TYPE_WORD_OUTPUT_MISMATCH" $"Word '{definition.Name}' does not leave its declared output stack." (Some definition.Name) (Some definition.Span) (definition.Outputs |> List.map Types.format) (inferred.InferredBody.ExitStack |> List.map Types.format)
        let undeclared = Set.difference inferred.Effects definition.Effects
        if not (Set.isEmpty undeclared) then
            let missing = String.concat ", " undeclared
            Diagnostics.raiseError "EFFECT_UNDECLARED" $"Word '{definition.Name}' uses effects absent from its declaration: {missing}." (Some definition.Name) (Some definition.Span) (definition.Effects |> Set.toList) (inferred.Effects |> Set.toList)
        { Definition = definition; Dependencies = inferred.Dependencies; InferredEffects = inferred.Effects }, inferred.InferredBody

    let checkDefinition knownTypes words definition =
        checkDefinitionDetailed knownTypes Map.empty words definition |> fst

    let checkDefinitionWithEnums knownTypes enums words definition =
        checkDefinitionDetailed knownTypes enums words definition |> fst

    let private checkTestDetailed knownTypes knownEnums words (test: TestDefinition) =
        // Test bodies alone may call the test-only `expect` intrinsics.
        let words = Map.fold (fun scope name entry -> Map.add name entry scope) words testIntrinsics
        match test.Expected with
        | ExpectedRuntimeError _ when List.isEmpty test.Body ->
            Diagnostics.raiseError "TEST_EXPECTED_ERROR_BODY_EMPTY" $"Runtime-error test '{test.Name}' must contain an expression to execute." (Some test.Word) (Some test.Span) [ "nonempty test body" ] []
        | ExpectedRuntimeError code when not (TestExpectation.isValidRuntimeErrorCode code) ->
            Diagnostics.raiseError "TEST_INVALID_EXPECTED_ERROR_CODE" $"Runtime-error test '{test.Name}' uses an invalid diagnostic code." (Some test.Word) (Some test.Span) [ "[A-Z][A-Z0-9_]*" ] [ code ]
        | ExpectedExpression _ when List.isEmpty test.Body ->
            Diagnostics.raiseError "TEST_EXPECTED_VALUE_BODY_EMPTY" $"Value-expectation test '{test.Name}' must contain an expression to test." (Some test.Word) (Some test.Span) [ "nonempty test body" ] []
        | _ -> ()
        let inferred = inferBody knownTypes knownEnums words (test.Word + "/" + test.Name) (Some test.Span) [] Map.empty test.Body
        let checkedExpression = checkedExpression inferred
        let expectedInference =
            match test.Expected with
            | ExpectedExpression expressions ->
                if List.isEmpty expressions then
                    Diagnostics.raiseError "TEST_EXPECTED_VALUE_EMPTY" $"Value expectation in test '{test.Name}' must contain an expression." (Some test.Word) (Some test.Span) [ "nonempty expected expression" ] []
                let expected = inferBody knownTypes knownEnums words (test.Word + "/" + test.Name + "/expected") (Some test.Span) [] Map.empty expressions
                match expected.InferredBody.ExitStack with
                | [ expectedType ] ->
                    validateType false knownTypes (Some test.Span) (test.Word + "/" + test.Name + "/expected") expectedType
                    if not (Set.isEmpty expected.Effects) then
                        Diagnostics.raiseError "TEST_EXPECTED_VALUE_EFFECTS" $"Value expectation in test '{test.Name}' must be pure." (Some test.Word) (Some test.Span) [] (Set.toList expected.Effects)
                    if checkedExpression.Stack <> [ expectedType ] then
                        Diagnostics.raiseError "TEST_EXPECTED_STACK" $"Test '{test.Name}' and its value expectation must each leave exactly one value of the same closed type." (Some test.Word) (Some test.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
                | actual ->
                    Diagnostics.raiseError "TEST_EXPECTED_VALUE_STACK" $"Value expectation in test '{test.Name}' must leave exactly one value." (Some test.Word) (Some test.Span) [ "one value" ] (actual |> List.map Types.format)
                Some expected
            | _ -> None
        match test.Expected with
        | ExpectedValue literal ->
            let expectedType = literal |> Types.literalValue |> Types.ofValue
            if checkedExpression.Stack <> [ expectedType ] then
                Diagnostics.raiseError "TEST_EXPECTED_STACK" $"Test '{test.Name}' must leave exactly one value matching its expected literal." (Some test.Word) (Some test.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        | ExpectedRuntimeError _ -> ()
        | ExpectedExpression _ -> ()
        | ExpectedChecks ->
            if checkedExpression.Stack <> [] then
                Diagnostics.raiseError "TEST_EXPECTED_CHECKS_STACK" $"Test '{test.Name}' with expect checks must leave no value on the stack." (Some test.Word) (Some test.Span) [ "empty stack" ] (checkedExpression.Stack |> List.map Types.format)
        checkedExpression, inferred.InferredBody, expectedInference

    let checkTest knownTypes words test = checkTestDetailed knownTypes Map.empty words test |> fun (checkedExpression, _, _) -> checkedExpression

    let checkTestWithEnums knownTypes enums words test = checkTestDetailed knownTypes enums words test |> fun (checkedExpression, _, _) -> checkedExpression

    let private checkExampleDetailed knownTypes knownEnums words (example: ExampleDefinition) =
        let inferred = inferBody knownTypes knownEnums words (example.Word + "/" + example.Name) (Some example.Span) [] Map.empty example.Body
        let checkedExpression = checkedExpression inferred
        let expectedType = example.Expected |> Types.literalValue |> Types.ofValue
        if checkedExpression.Stack <> [ expectedType ] then
            Diagnostics.raiseError "EXAMPLE_EXPECTED_STACK" $"Example '{example.Name}' must leave exactly one value matching its expected literal." (Some example.Word) (Some example.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        checkedExpression, inferred.InferredBody

    let checkExample knownTypes words example = checkExampleDetailed knownTypes Map.empty words example |> fst

    let checkExampleWithEnums knownTypes enums words example = checkExampleDetailed knownTypes enums words example |> fst

    let dependencies (body: Expr list) =
        let rec collect expressions =
            expressions
            |> List.fold (fun found expression ->
                match expression with
                | Call(name, _) when testIntrinsics.ContainsKey name -> found
                | Call(name, _) -> Set.add name found
                | MapList(name, _) | FilterList(name, _) | EachList(name, _) | FoldList(name, _) | MapListWithContext(name, _) -> Set.add name found
                | If(thenBranch, elseBranch, _) | MatchOption(_, thenBranch, elseBranch, _) ->
                    Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | Scope(innerBody, _) | Compact(innerBody, _) | ForRange(_, innerBody, _) | ForList(_, _, innerBody, _) -> Set.union found (collect innerBody)
                    | MatchResult(_, _, thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                    | MatchEnum(cases, _) -> cases |> List.fold (fun found (_, _, branch) -> Set.union found (collect branch)) found
                | _ -> found) Set.empty
        collect body

    let checkScalarValidator (knownTypes: Set<string>) (words: Map<string, WordEntry>) (scalar: ScalarTypeDefinition) =
        match scalar.BaseType with
        | TString | TInt | TFloat | TBool -> ()
        | _ -> Diagnostics.raiseError "TYPE_UNSUPPORTED_SCALAR_BASE" "Nominal scalar wrappers currently require a primitive String, Int, Float, or Bool base type." (Some scalar.Name) (Some scalar.Span) [ "String | Int | Float | Bool" ] [ Types.format scalar.BaseType ]
        match scalar.Validator with
        | None -> Set.empty
        | Some name ->
            match words.TryFind name with
            | None -> Diagnostics.raiseError "TYPE_UNKNOWN_VALIDATOR" $"Validator word '{name}' is not defined." (Some scalar.Name) (Some scalar.Span) [ "known word" ] [ name ]
            | Some entry ->
                let definition = entry.Definition
                validateType entry.Builtin.IsSome knownTypes (Some scalar.Span) scalar.Name (TNamed scalar.Name)
                if definition.Inputs <> [ scalar.BaseType ] || definition.Outputs <> [ TBool ] then
                    let actualInputs = String.concat " " (definition.Inputs |> List.map Types.format)
                    let actualOutputs = String.concat " " (definition.Outputs |> List.map Types.format)
                    Diagnostics.raiseError "TYPE_VALIDATOR_SIGNATURE" $"Validator '{name}' must have signature {Types.format scalar.BaseType} -> Bool." (Some scalar.Name) (Some scalar.Span) [ $"{Types.format scalar.BaseType} -> Bool" ] [ $"{actualInputs} -> {actualOutputs}" ]
                if not (Set.isEmpty definition.Effects) then
                    Diagnostics.raiseError "TYPE_VALIDATOR_EFFECT" "Scalar validators must be pure." (Some scalar.Name) (Some scalar.Span) [] (Set.toList definition.Effects)
                dependencies definition.Body

    let checkRecordValidator (knownTypes: Set<string>) (words: Map<string, WordEntry>) (record: RecordDefinition) =
        match record.Validator with
        | None -> Set.empty
        | Some name ->
            match words.TryFind name with
            | None -> Diagnostics.raiseError "TYPE_UNKNOWN_VALIDATOR" $"Validator word '{name}' is not defined." (Some record.Name) (Some record.Span) [ "known word" ] [ name ]
            | Some entry ->
                let definition = entry.Definition
                let recordType = TNamed record.Name
                validateType entry.Builtin.IsSome knownTypes (Some record.Span) record.Name recordType
                if definition.Inputs <> [ recordType ] || definition.Outputs <> [ TBool ] then
                    let actualInputs = String.concat " " (definition.Inputs |> List.map Types.format)
                    let actualOutputs = String.concat " " (definition.Outputs |> List.map Types.format)
                    Diagnostics.raiseError "TYPE_RECORD_VALIDATOR_SIGNATURE" $"Validator '{name}' must have signature {record.Name} -> Bool." (Some record.Name) (Some record.Span) [ $"{record.Name} -> Bool" ] [ $"{actualInputs} -> {actualOutputs}" ]
                if not (Set.isEmpty definition.Effects) then
                    Diagnostics.raiseError "TYPE_RECORD_VALIDATOR_EFFECT" "Record validators must be pure." (Some record.Name) (Some record.Span) [] (Set.toList definition.Effects)
                dependencies definition.Body

    let rec sourceExpressions = function
        | [] -> ""
        | expression :: rest ->
            let current =
                match expression with
                | Push(literal, _) -> Types.formatValue (Types.literalValue literal)
                | Call(name, _) -> name
                | ConstructContainer(kind, types, _) ->
                    let name =
                        match kind with
                        | ListEmpty -> "list.empty"
                        | ListSingleton -> "list.singleton"
                        | ListOf count -> $"list.of{count}"
                        | OptionNone -> "option.none"
                        | OptionSome -> "option.some"
                        | ResultOk -> "result.ok"
                        | ResultError -> "result.error"
                        | HtmlTemplate parts -> "html.template" + string parts.Length
                        | DbParams _ -> "db.params"
                        | DbDecode -> "db.decode"
                        | ForeignCall spec -> "c.call " + spec
                    name + "<" + (types |> List.map Types.format |> String.concat ", ") + ">"
                | MapList(name, _) -> "list.map " + name
                | FilterList(name, _) -> "list.filter " + name
                | EachList(name, _) -> "list.each " + name
                | FoldList(name, _) -> "list.fold " + name
                | MapListWithContext(name, _) -> "list.map-context " + name
                | Let(name, _) -> "let " + name
                | Load(name, _) -> $"${name}"
                | If(thenBranch, elseBranch, _) ->
                    let thenText = sourceExpressions thenBranch
                    let elseText = sourceExpressions elseBranch
                    if List.isEmpty elseBranch then "if\n" + thenText + "\nend" else "if\n" + thenText + "\nelse\n" + elseText + "\nend"
                | Scope(innerBody, _) -> "scope\n" + sourceExpressions innerBody + "\nend"
                | Compact(innerBody, _) -> "compact\n" + sourceExpressions innerBody + "\nend"
                | Assign(name, _) -> "assign " + name
                | ForRange(name, innerBody, _) -> "for-range " + name + "\n" + sourceExpressions innerBody + "\nend"
                | ForList(name, index, innerBody, _) -> "for-list " + name + (index |> Option.map (fun value -> " " + value) |> Option.defaultValue "") + "\n" + sourceExpressions innerBody + "\nend"
                | Break _ -> "break"
                | Continue _ -> "continue"
                | MatchOption(name, someBranch, noneBranch, _) ->
                    "match-option\nsome " + name + "\n" + sourceExpressions someBranch + "\nnone\n" + sourceExpressions noneBranch + "\nend"
                | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
                    "match-result\nok " + okName + "\n" + sourceExpressions okBranch + "\nerror " + errorName + "\n" + sourceExpressions errorBranch + "\nend"
                | MatchEnum(cases, _) ->
                    "match-enum\n" + (cases |> List.map (fun (caseName, binding, body) -> caseName + (match binding with Some name -> " " + name | None -> "") + "\n" + sourceExpressions body) |> String.concat "\n") + "\nend"
            String.concat "\n" [ current; sourceExpressions rest ] |> fun text -> text.Trim('\n')

    /// Complete source snapshot used by compiler lowering. Every effective
    /// dictionary word, including primitives and generated words, has an ID.
    type IrLoweringContext =
        { Words: Map<string, WordEntry>
          Records: Map<string, RecordDefinition>
          Scalars: Map<string, ScalarTypeDefinition>
          Enums: Map<string, EnumDefinition>
          WordIds: Map<string, WordId>
          HostOperations: Map<HostOperationId, HostOperationDefinition> }

    let private irFailure code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private irEffectsForWord word span (effects: Set<string>) =
        effects
        |> Set.fold (fun found name ->
            match IrEffects.ofName name with
            | Some effect -> Set.add effect found
            | None -> irFailure "IR_UNKNOWN_EFFECT" $"Effect '{name}' is not in the closed executable effect vocabulary." (Some word) span (IrEffects.names Set.empty) [ name ]) Set.empty

    let private typePatternOfLangType word (typeValue: LangType) =
        let variables = System.Collections.Generic.Dictionary<string, int>()
        let mutable nextVariable = 0
        let rec pattern = function
            | TInt -> PatternInt
            | TFloat -> PatternFloat
            | TBool -> PatternBool
            | TString -> PatternString
            | TBytes -> PatternBytes
            | TJson -> PatternJson
            | TUnit -> PatternUnit
            | TList item -> PatternList(pattern item)
            | TOption item -> PatternOption(pattern item)
            | TResult(ok, error) -> PatternResult(pattern ok, pattern error)
            | TVar name ->
                match variables.TryGetValue name with
                | true, index -> PatternVariable index
                | _ ->
                    let index = nextVariable
                    nextVariable <- nextVariable + 1
                    variables[name] <- index
                    PatternVariable index
            | TNamed name ->
                irFailure "IR_PRIMITIVE_TYPE_UNSUPPORTED" $"Trusted primitive '{word}' cannot use nominal type '{name}' in its generic contract." (Some word) None [] [ name ]
        pattern typeValue

    let private primitiveContractOfEntry (entry: WordEntry) operation =
        let definition = entry.Definition
        let typeVariables = System.Collections.Generic.Dictionary<string, int>()
        let mutable nextVariable = 0
        let rec pattern = function
            | TInt -> PatternInt
            | TFloat -> PatternFloat
            | TBool -> PatternBool
            | TString -> PatternString
            | TBytes -> PatternBytes
            | TJson -> PatternJson
            | TUnit -> PatternUnit
            | TList item -> PatternList(pattern item)
            | TOption item -> PatternOption(pattern item)
            | TResult(ok, error) -> PatternResult(pattern ok, pattern error)
            | TVar name ->
                match typeVariables.TryGetValue name with
                | true, index -> PatternVariable index
                | _ ->
                    let index = nextVariable
                    nextVariable <- nextVariable + 1
                    typeVariables[name] <- index
                    PatternVariable index
            | TNamed name ->
                irFailure "IR_PRIMITIVE_TYPE_UNSUPPORTED" $"Trusted primitive '{definition.Name}' cannot use nominal type '{name}' in its generic contract." (Some definition.Name) (Some definition.Span) [] [ name ]
        let effects = irEffectsForWord definition.Name (Some definition.Span) definition.Effects
        let primitive = PrimitiveId operation
        { Primitive = primitive
          InputPatterns = definition.Inputs |> List.map pattern
          OutputPatterns = definition.Outputs |> List.map pattern
          PrimitiveEffects = effects }

    /// Fixed host contract catalog. Source aliases resolve through BuiltinOp,
    /// so display names never select a host primitive implementation.
    let primitiveIrCatalog: IrPrimitiveCatalog =
        let grouped =
            testWords
            |> Map.toList
            |> List.choose (fun (_, entry) ->
                match entry.Builtin with
                | Some(BuiltinOp operation) -> Some(operation, entry)
                | _ -> None)
            |> List.groupBy fst
        grouped
        |> List.map (fun (operation, members) ->
            let entries = members |> List.map snd |> List.sortBy (fun entry -> entry.Definition.Name)
            let canonical = entries.Head
            for alias in entries.Tail do
                if alias.Definition.Inputs <> canonical.Definition.Inputs
                   || alias.Definition.Outputs <> canonical.Definition.Outputs
                   || alias.Definition.Effects <> canonical.Definition.Effects then
                    irFailure "IR_PRIMITIVE_REGISTRY_CONFLICT" $"Trusted BuiltinOp '{operation}' has aliases with different signatures or effects." (Some alias.Definition.Name) (Some alias.Definition.Span) [ canonical.Definition.Name ] [ alias.Definition.Name ]
            PrimitiveId operation, primitiveContractOfEntry canonical operation)
        |> Map.ofList

    let private verifyBuiltinMetadata (words: Map<string, WordEntry>) (hostOperations: Map<HostOperationId, HostOperationDefinition>) name (entry: WordEntry) =
        match entry.Builtin with
        | Some(BuiltinOp operation) ->
            match primitiveIrCatalog.TryFind(PrimitiveId operation),
                  (testWords |> Map.toList |> List.tryPick (fun (_, standard) -> if standard.Builtin = Some(BuiltinOp operation) then Some standard else None)) with
            | Some _, Some canonical ->
                if entry.Definition.Inputs <> canonical.Definition.Inputs
                   || entry.Definition.Outputs <> canonical.Definition.Outputs
                   || entry.Definition.Effects <> canonical.Definition.Effects then
                    irFailure "IR_PRIMITIVE_METADATA_MISMATCH" $"Primitive alias '{name}' does not match the trusted contract for BuiltinOp '{operation}'." (Some name) (Some entry.Definition.Span) [ String.concat " " (canonical.Definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (canonical.Definition.Outputs |> List.map Types.format); String.concat ", " canonical.Definition.Effects ] [ String.concat " " (entry.Definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (entry.Definition.Outputs |> List.map Types.format); String.concat ", " entry.Definition.Effects ]
            | _ ->
                irFailure "IR_UNKNOWN_PRIMITIVE_ID" $"BuiltinOp '{operation}' is not in the fixed trusted primitive registry." (Some name) (Some entry.Definition.Span) (primitiveIrCatalog |> Map.toList |> List.map (fun (id, _) -> sprintf "%A" id)) [ operation ]
        | Some(HostOperation operationId) ->
            match hostOperations.TryFind operationId with
            | None ->
                irFailure "IR_UNKNOWN_HOST_OPERATION_ID" $"Host operation '{name}' is absent from the exact lowering manifest." (Some name) (Some entry.Definition.Span)
                    (hostOperations |> Map.toList |> List.map (fun (id, _) -> sprintf "%A" id)) [ sprintf "%A" operationId ]
            | Some definition ->
                let expected = definition.Inputs, definition.Outputs, definition.Effects, definition.WordName
                let actual = entry.Definition.Inputs, entry.Definition.Outputs, entry.Definition.Effects, name
                if definition.Id <> operationId || definition.WordName <> name || expected <> actual then
                    irFailure "IR_HOST_OPERATION_METADATA_MISMATCH" $"Host operation word '{name}' differs from its exact registered definition." (Some name) (Some entry.Definition.Span)
                        [ definition.WordName
                          String.concat " " (definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (definition.Outputs |> List.map Types.format)
                          String.concat ", " (definition.Effects |> Set.toList |> List.sort) ]
                        [ name
                          String.concat " " (entry.Definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (entry.Definition.Outputs |> List.map Types.format)
                          String.concat ", " (entry.Definition.Effects |> Set.toList |> List.sort) ]
        | _ -> ()
        if entry.Definition.Name <> name then
            irFailure "IR_WORD_NAME_MISMATCH" "Dictionary key differs from its source word name." (Some name) (Some entry.Definition.Span) [ name ] [ entry.Definition.Name ]
        words |> ignore

    let private closedIrType (typeKeys: Map<string, ProgramTypeKey>) word span (typeValue: LangType) =
        let rec convert = function
            | TInt -> IrInt
            | TFloat -> IrFloat
            | TBool -> IrBool
            | TString -> IrString
            | TBytes -> IrBytes
            | TJson -> IrJson
            | TUnit -> IrUnit
            | TList item -> IrList(convert item)
            | TOption item -> IrOption(convert item)
            | TResult(ok, error) -> IrResult(convert ok, convert error)
            | TNamed name ->
                match typeKeys.TryFind name with
                | Some key -> IrNominal key
                | None -> irFailure "TYPE_UNKNOWN_NAMED_TYPE" $"Type '{name}' has not been declared." (Some word) span [ "declared record, scalar, or enum" ] [ name ]
            | TVar name -> irFailure "TYPE_UNSUPPORTED_GENERIC" $"Generic type variable '{name}' cannot appear in closed executable IR." (Some word) span [] [ name ]
        convert typeValue

    let private contextTypes (context: IrLoweringContext) =
        let recordNames = context.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let scalarNames = context.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let enumNames = context.Enums |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let duplicateNames =
            [ recordNames; scalarNames; enumNames ]
            |> List.collect Set.toList
            |> List.groupBy id
            |> List.choose (fun (name, values) -> if values.Length > 1 then Some name else None)
            |> Set.ofList
        if not (Set.isEmpty duplicateNames) then
            irFailure "IR_TYPE_NAME_COLLISION" "A source snapshot cannot declare nominal types with the same name." None None [] (duplicateNames |> Set.toList)
        for KeyValue(name, record) in context.Records do
            if record.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Record map key differs from its definition name." (Some name) (Some record.Span) [ name ] [ record.Name ]
        for KeyValue(name, scalar) in context.Scalars do
            if scalar.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Scalar map key differs from its definition name." (Some name) (Some scalar.Span) [ name ] [ scalar.Name ]
        for KeyValue(name, enumDefinition) in context.Enums do
            if enumDefinition.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Enum map key differs from its definition name." (Some name) (Some enumDefinition.Span) [ name ] [ enumDefinition.Name ]
        Set.unionMany [ recordNames; scalarNames; enumNames ]

    let private validateLoweringContext (context: IrLoweringContext) =
        let names = context.Words |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let idNames = context.WordIds |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if names <> idNames then
            irFailure "IR_WORD_ID_MAP_INCOMPLETE" "Every effective dictionary word must have exactly one supplied stable ID." None None (names |> Set.toList) (idNames |> Set.toList)
        let duplicateIds =
            context.WordIds
            |> Map.toList
            |> List.groupBy snd
            |> List.choose (fun (wordId, values) -> if values.Length > 1 then Some(sprintf "%A: %s" wordId (values |> List.map fst |> String.concat ", ")) else None)
        if not (List.isEmpty duplicateIds) then
            irFailure "IR_WORD_ID_COLLISION" "Effective dictionary words must have globally unique stable IDs." None None [ "unique WordId values" ] duplicateIds
        for KeyValue(name, WordId rawId) in context.WordIds do
            if String.IsNullOrWhiteSpace rawId then irFailure "IR_WORD_ID_INVALID" "Stable word IDs must be nonempty." (Some name) None [ "nonempty WordId" ] [ rawId ]
        for KeyValue(name, entry) in context.Words do
            verifyBuiltinMetadata context.Words context.HostOperations name entry
            if entry.Revision < 0 then irFailure "IR_INVALID_REVISION" "Dictionary word revisions cannot be negative." (Some name) (Some entry.Definition.Span) [ "nonnegative revision" ] [ string entry.Revision ]
        let hostEntries =
            context.Words
            |> Map.toList
            |> List.choose (fun (name, entry) ->
                match entry.Builtin with
                | Some(HostOperation id) -> Some(id, name)
                | _ -> None)
        let duplicateHostIds = hostEntries |> List.groupBy fst |> List.choose (fun (id, values) -> if values.Length > 1 then Some(sprintf "%A" id) else None)
        let hostIds = hostEntries |> List.map fst |> Set.ofList
        let manifestIds = context.HostOperations |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if hostIds <> manifestIds then
            irFailure "IR_HOST_OPERATION_WORD_SET_MISMATCH" "Every registered host operation must have exactly one effective builtin word." None None
                (manifestIds |> Set.toList |> List.map (sprintf "%A")) (hostIds |> Set.toList |> List.map (sprintf "%A"))
        if not duplicateHostIds.IsEmpty then
            irFailure "IR_HOST_OPERATION_ID_COLLISION" "A host operation ID cannot be exposed by more than one word." None None [ "unique operation IDs" ] duplicateHostIds
        let duplicateHostNames = hostEntries |> List.groupBy snd |> List.choose (fun (name, values) -> if values.Length > 1 then Some name else None)
        if not duplicateHostNames.IsEmpty then
            irFailure "IR_HOST_OPERATION_NAME_COLLISION" "A host operation word name cannot be exposed more than once." None None [ "unique operation names" ] duplicateHostNames
        for KeyValue(id, definition) in context.HostOperations do
            let (HostOperationId rawId) = id
            if String.IsNullOrWhiteSpace rawId || definition.Id <> id || String.IsNullOrWhiteSpace definition.WordName then
                irFailure "IR_HOST_OPERATION_DEFINITION_INVALID" "Host operation IDs and names must be nonempty and map to their exact manifest key." (Some definition.WordName) None [ sprintf "%A" id ] [ sprintf "%A" definition.Id ]
            for typeValue in definition.Inputs @ definition.Outputs do
                let rec ensureClosed = function
                    | TVar variable -> irFailure "IR_HOST_OPERATION_GENERIC_UNSUPPORTED" "Host operations require closed concrete signatures." (Some definition.WordName) None [ "closed type" ] [ variable ]
                    | TList item | TOption item -> ensureClosed item
                    | TResult(ok, error) -> ensureClosed ok; ensureClosed error
                    | _ -> ()
                ensureClosed typeValue
            for effect in definition.Effects do
                if IrEffects.ofName effect |> Option.isNone then
                    irFailure "IR_UNKNOWN_EFFECT" $"Host operation '{definition.WordName}' declares an effect outside the closed executable vocabulary." (Some definition.WordName) None (IrEffects.names Set.empty) [ effect ]
        contextTypes context

    /// A word entry's fingerprint digest, kept with the name and identity it was computed under.
    type private WordEntryDigest = { DigestName: string; DigestId: string; Digest: string }

    /// Entries are immutable, and a dictionary that gains a word keeps every other entry
    /// object, so each entry's digest is computed once (by reference) across snapshots.
    let private wordEntryDigests = System.Runtime.CompilerServices.ConditionalWeakTable<WordEntry, WordEntryDigest>()

    /// The compiler-input fingerprint: a hash over one digest per word entry and the
    /// serialized catalogs and origin map. Equal inputs give equal fingerprints whether
    /// the context was built from scratch or by adding words to an earlier one.
    let private snapshotFingerprint (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let mutable builder = System.Text.StringBuilder()
        let appendText (value: string) =
            if isNull value then builder.Append("N;") |> ignore
            else builder.Append('S').Append(value.Length).Append(':').Append(value).Append(';') |> ignore
        let appendInt (value: int) = builder.Append('I').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';') |> ignore
        let appendInt64 (value: int64) = builder.Append('L').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';') |> ignore
        let appendBool (value: bool) = builder.Append(if value then "B1;" else "B0;") |> ignore
        let appendSpan (span: SourceSpan) =
            appendText span.File
            appendInt span.Line
            appendInt span.Column
            appendInt span.Length
        let rec appendType (typeValue: LangType) =
            match typeValue with
            | TInt -> appendText "int"
            | TFloat -> appendText "float"
            | TBool -> appendText "bool"
            | TString -> appendText "string"
            | TBytes -> appendText "bytes"
            | TJson -> appendText "json"
            | TUnit -> appendText "unit"
            | TList item -> appendText "list"; appendType item
            | TOption item -> appendText "option"; appendType item
            | TResult(ok, error) -> appendText "result"; appendType ok; appendType error
            | TNamed name -> appendText "named"; appendText name
            | TVar name -> appendText "variable"; appendText name
        let appendTypes (values: LangType list) =
            appendInt values.Length
            values |> List.iter appendType
        let appendEffects (values: Set<string>) =
            let names = values |> Set.toList |> List.sort
            appendInt names.Length
            names |> List.iter appendText
        let appendLiteral (literal: Literal) =
            match literal with
            | LInt value -> appendText "int-literal"; appendInt64 value
            | LFloat value -> appendText "float-literal"; appendInt64 (BitConverter.DoubleToInt64Bits value)
            | LBool value -> appendText "bool-literal"; appendBool value
            | LString value -> appendText "string-literal"; appendText value
            | LUnit -> appendText "unit-literal"
        let rec appendExpressions (expressions: Expr list) =
            appendInt expressions.Length
            for expression in expressions do
                match expression with
                | Push(literal, span) -> appendText "push"; appendLiteral literal; appendSpan span
                | Call(name, span) -> appendText "call"; appendText name; appendSpan span
                | ConstructContainer(kind, typeValues, span) ->
                    appendText "construct-container"
                    appendText
                        (match kind with
                         | ListEmpty -> "list-empty"
                         | ListSingleton -> "list-singleton"
                         | ListOf count -> $"list-of-{count}"
                         | OptionNone -> "option-none"
                         | OptionSome -> "option-some"
                         | ResultOk -> "result-ok"
                         | ResultError -> "result-error"
                         | HtmlTemplate _ -> "html-template"
                         | DbParams _ -> "db-params"
                         | DbDecode -> "db-decode"
                         | ForeignCall _ -> "c-call")
                    match kind with
                    | DbParams order ->
                        appendInt order.Length
                        for index in order do appendInt index
                    | HtmlTemplate parts ->
                        appendInt parts.Length
                        for part in parts do
                            match part with
                            | HtmlTemplatePart.Text text -> appendText "text"; appendText text
                            | HtmlTemplatePart.StringHole -> appendText "string-hole"
                            | HtmlTemplatePart.IntHole -> appendText "int-hole"
                            | HtmlTemplatePart.HtmlHole -> appendText "html-hole"
                            | HtmlTemplatePart.HtmlListHole -> appendText "html-list-hole"
                    | _ -> ()
                    appendTypes typeValues
                    appendSpan span
                | MapList(name, span) -> appendText "map-list"; appendText name; appendSpan span
                | FilterList(name, span) -> appendText "filter-list"; appendText name; appendSpan span
                | EachList(name, span) -> appendText "each-list"; appendText name; appendSpan span
                | FoldList(name, span) -> appendText "fold-list"; appendText name; appendSpan span
                | MapListWithContext(name, span) -> appendText "map-list-context"; appendText name; appendSpan span
                | Let(name, span) -> appendText "let"; appendText name; appendSpan span
                | Load(name, span) -> appendText "load"; appendText name; appendSpan span
                | If(thenBranch, elseBranch, span) -> appendText "if"; appendExpressions thenBranch; appendExpressions elseBranch; appendSpan span
                | Scope(innerBody, span) -> appendText "scope"; appendExpressions innerBody; appendSpan span
                | Compact(innerBody, span) -> appendText "compact"; appendExpressions innerBody; appendSpan span
                | Assign(name, span) -> appendText "assign"; appendText name; appendSpan span
                | ForRange(name, innerBody, span) -> appendText "for-range"; appendText name; appendExpressions innerBody; appendSpan span
                | ForList(name, index, innerBody, span) ->
                    appendText "for-list"; appendText name; appendText (index |> Option.defaultValue ""); appendExpressions innerBody; appendSpan span
                | Break span -> appendText "break"; appendSpan span
                | Continue span -> appendText "continue"; appendSpan span
                | MatchOption(name, someBranch, noneBranch, span) -> appendText "match-option"; appendText name; appendExpressions someBranch; appendExpressions noneBranch; appendSpan span
                | MatchResult(okName, errorName, okBranch, errorBranch, span) ->
                    appendText "match-result"
                    appendText okName
                    appendText errorName
                    appendExpressions okBranch
                    appendExpressions errorBranch
                    appendSpan span
                | MatchEnum(cases, span) ->
                    appendText "match-enum"
                    appendInt cases.Length
                    for caseName, binding, branch in cases do
                        appendText caseName
                        // Only a payload binding adds text, so arms without one fingerprint as before.
                        binding |> Option.iter (fun name -> appendText ("bind:" + name))
                        appendExpressions branch
                    appendSpan span
        let appendBuiltin = function
            | None -> appendText "user-word"
            | Some(BuiltinOp operation) -> appendText "primitive"; appendText operation
            | Some(HostOperation(HostOperationId identity)) -> appendText "host-operation"; appendText identity
            | Some(RecordConstructor name) -> appendText "record-constructor"; appendText name
            | Some(RecordAccessor(name, field)) -> appendText "record-accessor"; appendText name; appendText field
            | Some(ScalarConstructor name) -> appendText "scalar-constructor"; appendText name
            | Some(ScalarAccessor name) -> appendText "scalar-accessor"; appendText name
            | Some(EnumCaseConstructor(typeName, caseName)) -> appendText "enum-case-constructor"; appendText typeName; appendText caseName
        let appendMaturity = function | ProjectWord -> appendText "project" | LibraryWord -> appendText "library"
        let appendStatus = function | Primitive -> appendText "primitive" | Candidate -> appendText "candidate" | Temporary -> appendText "temporary" | Persistent -> appendText "persistent"
        let digestOf (fill: unit -> unit) =
            let outer = builder
            builder <- System.Text.StringBuilder()
            fill ()
            let digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())))
            builder <- outer
            digest
        let appendWordEntry name value (entry: WordEntry) =
            appendText "word"
            appendText name
            appendText value
            appendInt entry.Revision
            appendBuiltin entry.Builtin
            appendStatus entry.Status
            appendMaturity entry.Maturity
            appendText entry.Definition.Name
            appendTypes entry.Definition.Inputs
            appendTypes entry.Definition.Outputs
            appendEffects entry.Definition.Effects
            appendMaturity entry.Definition.Maturity
            appendInt entry.Definition.Revision
            appendText entry.Definition.Documentation
            appendText entry.Definition.SourceText
            appendSpan entry.Definition.Span
            appendExpressions entry.Definition.Body
        appendText "agentlang-ir-snapshot-v5"
        for KeyValue(name, entry) in context.Words do
            let (WordId value) = context.WordIds[name]
            let digest =
                match wordEntryDigests.TryGetValue entry with
                | true, known when known.DigestName = name && known.DigestId = value -> known.Digest
                | _ ->
                    let digest = digestOf (fun () -> appendWordEntry name value entry)
                    wordEntryDigests.AddOrUpdate(entry, { DigestName = name; DigestId = value; Digest = digest })
                    digest
            appendText "word-digest"
            appendText digest
        for KeyValue(HostOperationId identity, definition) in context.HostOperations do
            appendText "host-operation-manifest"
            appendText identity
            appendText definition.WordName
            appendTypes definition.Inputs
            appendTypes definition.Outputs
            appendEffects definition.Effects
            appendText definition.Documentation
        for KeyValue(name, record) in context.Records do
            appendText "record"
            appendText name
            appendText record.Name
            appendText record.SourceText
            appendSpan record.Span
            appendInt record.Fields.Length
            for field in record.Fields do
                appendText field.Name
                appendType field.Type
            match record.Validator with
            | None -> appendText "no-validator"
            | Some validator -> appendText "validator"; appendText validator
        for KeyValue(name, scalar) in context.Scalars do
            appendText "scalar"
            appendText name
            appendText scalar.Name
            appendText scalar.SourceText
            appendSpan scalar.Span
            appendType scalar.BaseType
            match scalar.Validator with
            | None -> appendText "no-validator"
            | Some validator -> appendText "validator"; appendText validator
        for KeyValue(name, enumDefinition) in context.Enums do
            appendText "enum"
            appendText name
            appendText enumDefinition.Name
            appendText enumDefinition.SourceText
            appendSpan enumDefinition.Span
            appendInt enumDefinition.Cases.Length
            enumDefinition.Cases |> List.iter appendText
        appendText "source-origin-map"
        appendInt sourceOrigins.Count
        for KeyValue(marker, origin) in sourceOrigins do
            appendSpan marker
            appendSpan origin
        let digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))
        Convert.ToHexString(digest)

    let private typeKeysForContext (context: IrLoweringContext) =
        let names =
            [ context.Records |> Map.toSeq |> Seq.map fst
              context.Scalars |> Map.toSeq |> Seq.map fst
              context.Enums |> Map.toSeq |> Seq.map fst ]
            |> Seq.concat
            |> Seq.sort
            |> Seq.toList
        names |> List.mapi (fun index name -> name, ProgramTypeKey index) |> Map.ofList

    let private hostCatalogForContext (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) : IrHostOperationCatalog =
        context.HostOperations
        |> Map.map (fun operationId definition ->
            let effects = irEffectsForWord definition.WordName None definition.Effects
            { OperationId = operationId
              OperationName = definition.WordName
              InputTypes = definition.Inputs |> List.map (closedIrType typeKeys definition.WordName None)
              OutputTypes = definition.Outputs |> List.map (closedIrType typeKeys definition.WordName None)
              OperationEffects = effects })

    let private buildGeneratedTargets (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) =
        let sourceMap = ResizeArray<SourceSiteId * IrSourceSite>()
        let targets =
            context.Words
            |> Map.toList
            |> List.choose (fun (name, entry) ->
                let id = context.WordIds[name]
                let definition = entry.Definition
                let effects = irEffectsForWord name (Some definition.Span) definition.Effects
                let makeTarget operation inputs outputs sourceSpan sourceKind =
                    let site = SourceSiteId(Some id, 0)
                    sourceMap.Add(site, { SiteOwner = Some id; SiteSpan = sourceSpan; SourceKind = sourceKind })
                    Some
                        (name,
                         { TargetId = id
                           TargetRevision = entry.Revision
                           TargetName = name
                           Operation = operation
                           InputTypes = inputs
                           OutputTypes = outputs
                           TargetDeclaredEffects = effects
                           TargetEffects = effects
                           SourceSite = Some site })
                match entry.Builtin with
                | Some(RecordConstructor typeName) ->
                    match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                    | Some record, Some key ->
                        let inputs = record.Fields |> List.map (fun field -> closedIrType typeKeys name (Some record.Span) field.Type)
                        makeTarget (MakeRecordOperation key) inputs [ IrNominal key ] record.Span "record-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated constructor '{name}' has no matching record type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(RecordAccessor(typeName, fieldName)) ->
                    match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                    | Some record, Some key ->
                        match record.Fields |> List.tryFindIndex (fun field -> field.Name = fieldName) with
                        | Some index ->
                            let fieldType = closedIrType typeKeys name (Some record.Span) record.Fields[index].Type
                            makeTarget (GetRecordFieldOperation(key, index)) [ IrNominal key ] [ fieldType ] record.Span "record-field"
                        | None -> irFailure "IR_GENERATED_FIELD_UNKNOWN" $"Generated accessor '{name}' refers to missing field '{fieldName}'." (Some name) (Some definition.Span) [] [ fieldName ]
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated accessor '{name}' has no matching record type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(ScalarConstructor typeName) ->
                    match context.Scalars.TryFind typeName, typeKeys.TryFind typeName with
                    | Some scalar, Some key ->
                        let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                        makeTarget (WrapScalarOperation key) [ baseType ] [ IrNominal key ] scalar.Span "scalar-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated scalar constructor '{name}' has no matching scalar type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(ScalarAccessor typeName) ->
                    match context.Scalars.TryFind typeName, typeKeys.TryFind typeName with
                    | Some scalar, Some key ->
                        let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                        makeTarget (UnwrapScalarOperation key) [ IrNominal key ] [ baseType ] scalar.Span "scalar-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated scalar accessor '{name}' has no matching scalar type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(EnumCaseConstructor(typeName, caseName)) ->
                    match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                    | Some enumDefinition, Some key ->
                        match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                        | Some caseIndex ->
                            let payload = enumDefinition.Payloads[caseIndex] |> Option.map (closedIrType typeKeys name (Some enumDefinition.Span)) |> Option.toList
                            makeTarget (MakeEnumCaseOperation(key, caseIndex)) payload [ IrNominal key ] enumDefinition.Span "enum-case-declaration"
                        | None -> irFailure "IR_GENERATED_CASE_UNKNOWN" $"Generated enum constructor '{name}' refers to unknown case '{caseName}'." (Some name) (Some definition.Span) enumDefinition.Cases [ caseName ]
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated enum constructor '{name}' has no matching enum type." (Some name) (Some definition.Span) [] [ typeName ]
                | _ -> None)
        let duplicateIds = targets |> List.groupBy (fun (_, target) -> target.TargetId) |> List.choose (fun (id, values) -> if values.Length > 1 then Some(sprintf "%A" id) else None)
        if not (List.isEmpty duplicateIds) then irFailure "IR_WORD_ID_COLLISION" "Generated executable targets must have unique stable word IDs." None None [] duplicateIds
        let targetsByName = Map.ofList targets
        let targetsById = targets |> List.map (fun (_, target) -> target.TargetId, target) |> Map.ofList
        targetsByName, targetsById, Map.ofSeq sourceMap

    let private resolveIrCall (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) (generatedByName: Map<string, IrGeneratedTarget>) name span inputTypes outputTypes =
        match context.Words.TryFind name, testIntrinsics.TryFind name with
        | None, Some { Builtin = Some(BuiltinOp operation) } ->
            // Test-only intrinsics are type-checked only in test scope and lower straight to their primitive.
            let contract = primitiveIrCatalog[PrimitiveId operation]
            { ResolvedTarget = PrimitiveTarget(PrimitiveId operation)
              ResolvedName = name
              InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
              OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
              ResolvedDeclaredEffects = contract.PrimitiveEffects
              ResolvedEffects = contract.PrimitiveEffects }
        | None, _ -> irFailure "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) span [] []
        | Some entry, _ ->
            let id = context.WordIds[name]
            match entry.Builtin with
            | Some(BuiltinOp operation) ->
                verifyBuiltinMetadata context.Words context.HostOperations name entry
                let contract = primitiveIrCatalog[PrimitiveId operation]
                { ResolvedTarget = PrimitiveTarget(PrimitiveId operation)
                  ResolvedName = name
                  InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                  OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                  ResolvedDeclaredEffects = contract.PrimitiveEffects
                  ResolvedEffects = contract.PrimitiveEffects }
            | Some(HostOperation operationId) ->
                verifyBuiltinMetadata context.Words context.HostOperations name entry
                let contract = context.HostOperations[operationId]
                let effects = irEffectsForWord name span contract.Effects
                { ResolvedTarget = HostOperationTarget operationId
                  ResolvedName = name
                  InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                  OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                  ResolvedDeclaredEffects = effects
                  ResolvedEffects = effects }
            | None ->
                let effects = irEffectsForWord name span entry.Definition.Effects
                { ResolvedTarget = UserWordTarget(id, entry.Revision)
                  ResolvedName = name
                  InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                  OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                  ResolvedDeclaredEffects = effects
                  ResolvedEffects = effects }
            | Some(RecordConstructor _ | RecordAccessor _ | ScalarConstructor _ | ScalarAccessor _ | EnumCaseConstructor _) ->
                match generatedByName.TryFind name with
                | None -> irFailure "IR_GENERATED_TARGET_MISSING" $"Generated word '{name}' has no compiled operation target." (Some name) span [] []
                | Some target ->
                    { ResolvedTarget = GeneratedWordTarget(target.TargetId, target.TargetRevision)
                      ResolvedName = name
                      InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                      OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                      ResolvedDeclaredEffects = target.TargetDeclaredEffects
                      ResolvedEffects = target.TargetEffects }

    let private buildNominalTypes (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) generatedByName =
        let recordTypes =
            context.Records
            |> Map.toList
            |> List.map (fun (name, record) ->
                let key = typeKeys[name]
                let fields =
                    record.Fields
                    |> List.mapi (fun index field ->
                        { FieldIndex = index
                          FieldName = field.Name
                          FieldType = closedIrType typeKeys name (Some record.Span) field.Type })
                let validator =
                    record.Validator
                    |> Option.map (fun validatorName ->
                        resolveIrCall context typeKeys generatedByName validatorName (Some record.Span) [ TNamed name ] [ TBool ])
                key, IrRecordDefinition { TypeKey = key; TypeName = name; RecordFields = fields; ValidatorCall = validator })
        let scalarTypes =
            context.Scalars
            |> Map.toList
            |> List.map (fun (name, scalar) ->
                let key = typeKeys[name]
                let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                let validator =
                    scalar.Validator
                    |> Option.map (fun validatorName ->
                        resolveIrCall context typeKeys generatedByName validatorName (Some scalar.Span) [ scalar.BaseType ] [ TBool ])
                key, IrScalarDefinition { TypeKey = key; TypeName = name; BaseType = baseType; ValidatorCall = validator })
        let enumTypes =
            context.Enums
            |> Map.toList
            |> List.map (fun (name, enumDefinition) ->
                let key = typeKeys[name]
                let payloads = enumDefinition.Payloads |> List.map (Option.map (closedIrType typeKeys name (Some enumDefinition.Span)))
                key, IrEnumDefinition { TypeKey = key; TypeName = name; Cases = enumDefinition.Cases; CasePayloads = payloads })
        Map.ofList (recordTypes @ scalarTypes @ enumTypes)

    let private expressionSpan = function
        | Push(_, span) | Call(_, span) | ConstructContainer(_, _, span)
        | MapList(_, span) | FilterList(_, span) | EachList(_, span) | FoldList(_, span) | MapListWithContext(_, span)
        | Let(_, span) | Load(_, span) | If(_, _, span) | Scope(_, span) | Compact(_, span)
        | MatchOption(_, _, _, span) | MatchResult(_, _, _, _, span) | MatchEnum(_, span)
        | Assign(_, span) | ForRange(_, _, span) | ForList(_, _, _, span) | Break span | Continue span -> span

    let private expressionKind = function
        | Push _ -> "constant"
        | Call _ -> "call"
        | ConstructContainer(ListEmpty, _, _) -> "list-empty"
        | ConstructContainer(ListSingleton, _, _) -> "list-singleton"
        | ConstructContainer(ListOf _, _, _) -> "list-of"
        | ConstructContainer(OptionNone, _, _) -> "option-none"
        | ConstructContainer(OptionSome, _, _) -> "option-some"
        | ConstructContainer(ResultOk, _, _) -> "result-ok"
        | ConstructContainer(ResultError, _, _) -> "result-error"
        | ConstructContainer(HtmlTemplate _, _, _) -> "html-template"
        | ConstructContainer(DbParams _, _, _) -> "db-params"
        | ConstructContainer(DbDecode, _, _) -> "db-decode"
        | ConstructContainer(ForeignCall _, _, _) -> "c-call"
        | MapList _ -> "list-map"
        | FilterList _ -> "list-filter"
        | EachList _ -> "list-each"
        | FoldList _ -> "list-fold"
        | MapListWithContext _ -> "list-map-context"
        | Let _ -> "store-local"
        | Load _ -> "load-local"
        | If _ -> "if"
        | Scope _ -> "scope"
        | Compact _ -> "compact"
        | MatchOption _ -> "match-option"
        | MatchResult _ -> "match-result"
        | MatchEnum _ -> "match-enum"
        | Assign _ -> "assign-local"
        | ForRange _ -> "for-range"
        | ForList _ -> "for-list"
        | Break _ -> "break"
        | Continue _ -> "continue"

    let private zeroWidthMarkers (expressions: Expr list) =
        let rec collect (body: Expr list) =
            body
            |> List.fold (fun found expression ->
                let span = expressionSpan expression
                let found = if span.Length = 0 then Set.add span found else found
                match expression with
                | If(thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | Scope(innerBody, _) | Compact(innerBody, _) | ForRange(_, innerBody, _) | ForList(_, _, innerBody, _) -> Set.union found (collect innerBody)
                | MatchOption(_, someBranch, noneBranch, _) -> Set.union found (Set.union (collect someBranch) (collect noneBranch))
                | MatchResult(_, _, okBranch, errorBranch, _) -> Set.union found (Set.union (collect okBranch) (collect errorBranch))
                | MatchEnum(cases, _) -> cases |> List.fold (fun found (_, _, branch) -> Set.union found (collect branch)) found
                | _ -> found) Set.empty
        collect expressions

    let private contextZeroWidthMarkers (context: IrLoweringContext) =
        context.Words
        |> Map.toSeq
        |> Seq.fold (fun found (_, entry) -> Set.union found (zeroWidthMarkers entry.Definition.Body)) Set.empty

    let private ensureExactOriginKeys expectedKeys sourceOrigins owner span =
        let actualKeys = sourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let missing = Set.difference expectedKeys actualKeys
        let extra = Set.difference actualKeys expectedKeys
        if not (Set.isEmpty missing) then
            irFailure "IR_SOURCE_ORIGIN_MISSING" "Every private Flow source marker must have an authored source origin." owner span
                [ "one source origin per private marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        elif not (Set.isEmpty extra) then
            irFailure "IR_SOURCE_ORIGIN_SET_MISMATCH" "Private source-origin mappings must not contain markers outside this compilation unit." owner span
                [ "no unrelated source-origin mappings" ] [ sprintf "extra marker count=%d" extra.Count ]

    let private contextOriginsFromMap (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let markers = contextZeroWidthMarkers context
        let missing = Set.difference markers (sourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        if not (Set.isEmpty missing) then
            irFailure "IR_SOURCE_ORIGIN_MISSING" "The compiler snapshot contains private Flow markers without authored source origins." None None
                [ "one origin per compiler marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        sourceOrigins |> Map.filter (fun marker _ -> markers.Contains marker)

    let private lowerTypedBody (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (typeKeys: Map<string, ProgramTypeKey>) (nominalTypes: Map<ProgramTypeKey, IrNominalDefinition>) (generatedByName: Map<string, IrGeneratedTarget>) (ownerName: string) (ownerId: WordId option) (initialStack: LangType list) (effects: Set<string>) (typedBody: TypedBody) =
        let allNodes =
            let rec visit (body: TypedBody) =
                body.Nodes |> List.collect (fun node -> node :: (node.ChildBodies |> List.collect visit))
            visit typedBody
        let normalLocalNames =
            allNodes
            |> List.choose (fun node -> match node.SourceExpression with | Let(name, _) -> Some name | _ -> None)
            |> List.distinct
        let mutable nextSlot = 0
        let mutable localNames = Map.empty
        let mutable nextSite = 0
        let mutable sourceMap = Map.empty
        let normalSlots =
            normalLocalNames
            |> List.map (fun name ->
                let slot = LocalSlot nextSlot
                nextSlot <- nextSlot + 1
                localNames <- Map.add slot name localNames
                name, slot)
            |> Map.ofList
        let freshSlot name =
            let slot = LocalSlot nextSlot
            nextSlot <- nextSlot + 1
            localNames <- Map.add slot name localNames
            slot
        let siteFor span kind =
            let site = SourceSiteId(ownerId, nextSite)
            nextSite <- nextSite + 1
            let sourceKind =
                if span.Length <> 0 then kind
                else
                    match kind with
                    | "scope" -> "synthetic-scope"
                    | "store-local" -> "synthetic-store-local"
                    | "load-local" -> "synthetic-load-local"
                    | _ -> irFailure "IR_SYNTHETIC_SOURCE_KIND_INVALID" "A zero-length source span is reserved for internal lexical-scope and temporary-local operations." (Some ownerName) None [ "Scope | StoreLocal | LoadLocal" ] [ kind ]
            let displaySpan =
                if span.Length = 0 then
                    match sourceOrigins.TryFind span with
                    | Some origin -> origin
                    | None -> irFailure "IR_SOURCE_ORIGIN_MISSING" "A private zero-width source marker must be mapped to its authored Flow origin before verified IR is created." (Some ownerName) None [ "registered authored source origin" ] []
                else
                    if sourceOrigins.ContainsKey span then
                        irFailure "IR_SOURCE_ORIGIN_INVALID" "Only private zero-width markers can be remapped to an authored source origin." (Some ownerName) (Some span) [ "zero-width source marker" ] []
                    span
            sourceMap <- Map.add site { SiteOwner = ownerId; SiteSpan = displaySpan; SourceKind = sourceKind } sourceMap
            site
        let irType word span typeValue = closedIrType typeKeys word span typeValue
        let shape word (env: Map<string, LocalSlot>) (stack: LangType list) (locals: Map<string, LangType>) =
            let localTypes =
                locals
                |> Map.toList
                |> List.map (fun (name, typeValue) ->
                    match env.TryFind name with
                    | Some slot -> slot, irType word None typeValue
                    | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some word) None [] [ name ])
                |> Map.ofList
            { StackTypes = stack |> List.map (irType word None)
              LocalTypes = localTypes }
        let resolvedForNode name span signature =
            match signature with
            | None -> irFailure "IR_CALL_ANNOTATION_MISSING" $"Typed inference did not retain the concrete signature for '{name}'." (Some ownerName) (Some span) [] [ name ]
            | Some signature -> resolveIrCall context typeKeys generatedByName name (Some span) signature.CallInputs signature.CallOutputs
        let rec lowerBlock (env: Map<string, LocalSlot>) (typed: TypedBody) =
            let entryShape = shape ownerName env typed.EntryStack typed.EntryLocals
            let instructions =
                typed.Nodes
                |> List.map (fun node ->
                    let span = expressionSpan node.SourceExpression
                    let kind = expressionKind node.SourceExpression
                    let site = siteFor span kind
                    let operation =
                        match node.SourceExpression with
                        | Push(literal, _) ->
                            let outputType = node.OutputStack |> List.tryLast |> Option.defaultValue TUnit
                            IrOperation.Constant(literal, irType ownerName (Some span) outputType)
                        | Call(name, _) ->
                            let call = resolvedForNode name span node.CallSignature
                            match (context.Words.TryFind name |> Option.orElse (testIntrinsics.TryFind name) |> Option.get).Builtin with
                            | Some(RecordConstructor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key ->
                                    match nominalTypes.TryFind key with
                                    | Some(IrRecordDefinition record) -> IrOperation.MakeRecord(call, key, record.ValidatorCall)
                                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Record constructor '{name}' has no record type definition." (Some name) (Some span) [] [ typeName ]
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Record constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(RecordAccessor(typeName, fieldName)) ->
                                match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                                | Some record, Some key ->
                                    match record.Fields |> List.tryFindIndex (fun field -> field.Name = fieldName) with
                                    | Some index -> IrOperation.GetRecordField(call, key, index)
                                    | None -> irFailure "IR_GENERATED_FIELD_UNKNOWN" $"Record accessor '{name}' refers to missing field '{fieldName}'." (Some name) (Some span) [] [ fieldName ]
                                | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Record accessor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(ScalarConstructor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key ->
                                    match nominalTypes.TryFind key with
                                    | Some(IrScalarDefinition scalar) -> IrOperation.WrapScalar(call, key, scalar.ValidatorCall)
                                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar constructor '{name}' has no scalar type definition." (Some name) (Some span) [] [ typeName ]
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(ScalarAccessor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key -> IrOperation.UnwrapScalar(call, key)
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar accessor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(EnumCaseConstructor(typeName, caseName)) ->
                                match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                                | Some enumDefinition, Some key ->
                                    match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                                    | Some caseIndex -> IrOperation.MakeEnumCase(call, key, caseIndex)
                                    | None -> irFailure "IR_GENERATED_CASE_UNKNOWN" $"Enum constructor '{name}' refers to unknown case '{caseName}'." (Some name) (Some span) enumDefinition.Cases [ caseName ]
                                | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Enum constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(BuiltinOp _ | HostOperation _) -> IrOperation.Call call
                            | None -> IrOperation.Call call
                        | ConstructContainer(kind, arguments, _) ->
                            let args = arguments |> List.map (irType ownerName (Some span))
                            match kind, args with
                            | ListEmpty, [ item ] -> IrOperation.ListEmpty item
                            | ListSingleton, [ item ] -> IrOperation.ListSingleton item
                            | ListOf count, [ item ] -> IrOperation.ListOf(item, count)
                            | OptionNone, [ item ] -> IrOperation.OptionNone item
                            | OptionSome, [ item ] -> IrOperation.OptionSome item
                            | ResultOk, [ ok; error ] -> IrOperation.ResultOk(ok, error)
                            | ResultError, [ ok; error ] -> IrOperation.ResultError(ok, error)
                            | HtmlTemplate parts, [ IrNominal key ] -> IrOperation.HtmlTemplate(key, parts)
                            | DbParams order, [ IrNominal paramsKey; IrNominal dbValueKey ] -> IrOperation.DbParams(paramsKey, dbValueKey, order)
                            | DbDecode, [ IrNominal rowKey; IrNominal dbRowKey; IrNominal sqlErrorKey ] -> IrOperation.DbDecode(rowKey, dbRowKey, sqlErrorKey)
                            | ForeignCall spec, _ :: _ ->
                                match ForeignCalls.decode spec with
                                | Ok binding -> IrOperation.ForeignCall(binding, args |> List.take (args.Length - 1), List.last args)
                                | Error reason -> irFailure "IR_FOREIGN_CALL_TYPE" $"C binding prototype is invalid: {reason}." (Some ownerName) (Some span) [] [ spec ]
                            | _ -> irFailure "IR_CONTAINER_ANNOTATION_INVALID" "Typed inference produced malformed constructor arguments." (Some ownerName) (Some span) [] (args |> List.map IrTypes.format)
                        | MapList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Map lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            match callback.OutputTypes with
                            | [ output ] -> IrOperation.ListMap(callback, irType ownerName (Some span) itemType, output)
                            | _ -> irFailure "IR_CALLBACK_ANNOTATION_INVALID" "Map lowering requires one concrete callback output." (Some name) (Some span) [ "T -> U" ] (callback.OutputTypes |> List.map IrTypes.format)
                        | FilterList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Filter lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            IrOperation.ListFilter(callback, irType ownerName (Some span) itemType)
                        | EachList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Each lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            IrOperation.ListEach(callback, irType ownerName (Some span) itemType)
                        | FoldList(name, _) ->
                            let itemType, accumulatorType =
                                match node.InputStack |> List.rev |> List.take 2 with
                                | [ accumulator; TList item ] -> item, accumulator
                                | actual -> irFailure "IR_LIST_ANNOTATION_INVALID" "Fold lowering lost its List<T> and accumulator input annotations." (Some ownerName) (Some span) [ "List<T> Accumulator" ] (actual |> List.rev |> List.map Types.format)
                            let itemType = irType ownerName (Some span) itemType
                            let accumulatorType = irType ownerName (Some span) accumulatorType
                            let callback = resolvedForNode name span node.CallSignature
                            if callback.InputTypes <> [ accumulatorType; itemType ] || callback.OutputTypes <> [ accumulatorType ] then
                                irFailure "IR_CALLBACK_ANNOTATION_INVALID" "Fold lowering requires one concrete callback with signature Accumulator Item -> Accumulator." (Some name) (Some span)
                                    [ $"{IrTypes.format accumulatorType} {IrTypes.format itemType} -> {IrTypes.format accumulatorType}" ]
                                    [ (callback.InputTypes |> List.map IrTypes.format |> String.concat " ") + " -> " + (callback.OutputTypes |> List.map IrTypes.format |> String.concat " ") ]
                            IrOperation.ListFold(callback, itemType, accumulatorType)
                        | MapListWithContext(name, _) ->
                            let contextType, itemType =
                                match node.InputStack |> List.rev |> List.take 2 with
                                | [ context; TList item ] -> context, item
                                | actual -> irFailure "IR_LIST_ANNOTATION_INVALID" "Map-with-context lowering lost its List<T> and context input annotations." (Some ownerName) (Some span) [ "List<T> Context" ] (actual |> List.rev |> List.map Types.format)
                            let itemType = irType ownerName (Some span) itemType
                            let contextType = irType ownerName (Some span) contextType
                            let callback = resolvedForNode name span node.CallSignature
                            match callback.InputTypes, callback.OutputTypes with
                            | [ inputContext; inputItem ], [ output ] when inputContext = contextType && inputItem = itemType ->
                                IrOperation.ListMapWithContext(callback, contextType, itemType, output)
                            | _ ->
                                irFailure "IR_CALLBACK_ANNOTATION_INVALID" "Map-with-context lowering requires one concrete callback with signature Context Item -> Output." (Some name) (Some span)
                                    [ $"{IrTypes.format contextType} {IrTypes.format itemType} -> <output>" ]
                                    [ (callback.InputTypes |> List.map IrTypes.format |> String.concat " ") + " -> " + (callback.OutputTypes |> List.map IrTypes.format |> String.concat " ") ]
                        | Let(name, _) ->
                            match env.TryFind name with
                            | Some slot -> IrOperation.StoreLocal slot
                            | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some ownerName) (Some span) [] [ name ]
                        | Load(name, _) ->
                            match env.TryFind name with
                            | Some slot -> IrOperation.LoadLocal slot
                            | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some ownerName) (Some span) [] [ name ]
                        | If(_, _, _) ->
                            match node.ChildBodies with
                            | [ thenBody; elseBody ] -> IrOperation.If(lowerBlock env thenBody, lowerBlock env elseBody)
                            | _ -> irFailure "IR_BRANCH_ANNOTATION_INVALID" "If lowering requires exactly two checked branches." (Some ownerName) (Some span) [ "2 branches" ] [ string node.ChildBodies.Length ]
                        | Scope(_, _) ->
                            match node.ChildBodies with
                            | [ scopedBody ] -> IrOperation.Scope(lowerBlock env scopedBody)
                            | _ -> irFailure "IR_SCOPE_ANNOTATION_INVALID" "Scope lowering requires exactly one checked child block." (Some ownerName) (Some span) [ "1 child block" ] [ string node.ChildBodies.Length ]
                        | Compact(_, _) ->
                            match node.ChildBodies with
                            | [ compactBody ] -> IrOperation.Compact(lowerBlock env compactBody)
                            | _ -> irFailure "IR_SCOPE_ANNOTATION_INVALID" "Compact lowering requires exactly one checked child block." (Some ownerName) (Some span) [ "1 child block" ] [ string node.ChildBodies.Length ]
                        | Assign(name, _) ->
                            match env.TryFind name with
                            | Some slot -> IrOperation.AssignLocal slot
                            | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some ownerName) (Some span) [] [ name ]
                        | Break _ -> IrOperation.Break
                        | Continue _ -> IrOperation.Continue
                        | ForRange(indexName, _, _) ->
                            match node.ChildBodies with
                            | [ body ] ->
                                let indexSlot = freshSlot indexName
                                IrOperation.ForRange(indexSlot, lowerBlock (Map.add indexName indexSlot env) body)
                            | _ -> irFailure "IR_LOOP_ANNOTATION_INVALID" "Range loop lowering requires one checked body." (Some ownerName) (Some span) [ "1 child block" ] [ string node.ChildBodies.Length ]
                        | ForList(itemName, indexName, _, _) ->
                            match node.ChildBodies, List.last node.InputStack with
                            | [ body ], TList itemType ->
                                let itemSlot = freshSlot itemName
                                let indexSlot = indexName |> Option.map freshSlot
                                let bodyEnv =
                                    match indexName, indexSlot with
                                    | Some name, Some slot -> Map.add name slot (Map.add itemName itemSlot env)
                                    | _ -> Map.add itemName itemSlot env
                                IrOperation.ForList(irType ownerName (Some span) itemType, itemSlot, indexSlot, lowerBlock bodyEnv body)
                            | _ -> irFailure "IR_LOOP_ANNOTATION_INVALID" "List loop lowering requires a checked List<T> and one body." (Some ownerName) (Some span) [ "List<T> with 1 child block" ] [ string node.ChildBodies.Length ]
                        | MatchOption(someName, _, _, _) ->
                            match node.ChildBodies, List.last node.InputStack with
                            | [ someBody; noneBody ], TOption itemType ->
                                let someSlot = freshSlot someName
                                let someEnv = Map.add someName someSlot env
                                IrOperation.MatchOption(someSlot, lowerBlock someEnv someBody, lowerBlock env noneBody)
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Option match lowering requires a checked Option<T> and both case bodies." (Some ownerName) (Some span) [ "Option<T> with Some and None cases" ] []
                        | MatchResult(okName, errorName, _, _, _) ->
                            match node.ChildBodies, List.last node.InputStack with
                            | [ okBody; errorBody ], TResult _ ->
                                let okSlot = freshSlot okName
                                let errorSlot = freshSlot errorName
                                IrOperation.MatchResult(okSlot, errorSlot, lowerBlock (Map.add okName okSlot env) okBody, lowerBlock (Map.add errorName errorSlot env) errorBody)
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Result match lowering requires a checked Result<T, E> and both case bodies." (Some ownerName) (Some span) [ "Result<T, E> with Ok and Error cases" ] []
                        | MatchEnum(cases, _) ->
                            match node.ChildBodies, node.InputStack |> List.tryLast with
                            | childBodies, Some(TNamed typeName) when childBodies.Length = cases.Length ->
                                match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                                | Some enumDefinition, Some key ->
                                    let caseBlocks =
                                        List.zip cases childBodies
                                        |> List.map (fun ((caseName, binding, _), childBody) ->
                                            match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                                            | Some caseIndex ->
                                                match binding with
                                                | Some bindingName ->
                                                    let slot = freshSlot bindingName
                                                    caseIndex, Some slot, lowerBlock (Map.add bindingName slot env) childBody
                                                | None -> caseIndex, None, lowerBlock env childBody
                                            | None -> irFailure "IR_ENUM_MATCH_CASE_UNKNOWN" $"Enum match refers to unknown case '{caseName}'." (Some ownerName) (Some span) enumDefinition.Cases [ caseName ])
                                    IrOperation.MatchEnum(key, caseBlocks)
                                | _ -> irFailure "IR_ENUM_MATCH_TYPE_UNKNOWN" $"Enum match type '{typeName}' is absent from the frozen enum table." (Some ownerName) (Some span) [] [ typeName ]
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Enum match lowering requires a declared enum and one checked block for every authored case." (Some ownerName) (Some span) [ string cases.Length + " enum case blocks" ] [ string node.ChildBodies.Length ]
                    { Site = site; Operation = operation })
            let exitShape = shape ownerName env typed.ExitStack typed.ExitLocals
            { EntryShape = entryShape; ExitShape = exitShape; Code = instructions }
        let initialSlots = normalSlots
        let block = lowerBlock initialSlots typedBody
        let declaredAndInferred = irEffectsForWord ownerName None effects
        block, localNames, sourceMap, declaredAndInferred

    let private typeKeysFromProgram (program: IrProgram) =
        program.NominalTypesByKey
        |> Map.toList
        |> List.map (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record -> record.TypeName, key
            | IrScalarDefinition scalar -> scalar.TypeName, key
            | IrEnumDefinition enumDefinition -> enumDefinition.TypeName, key)
        |> Map.ofList

    let private generatedByNameFromProgram (program: IrProgram) =
        program.GeneratedTargetsById |> Map.toList |> List.map (fun (_, target) -> target.TargetName, target) |> Map.ofList

    let private validateSourceOrigins (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        for KeyValue(marker, origin) in sourceOrigins do
            if marker.Length <> 0 || origin.Length <= 0 || String.IsNullOrWhiteSpace origin.File || origin.Line < 1 || origin.Column < 1 then
                irFailure "IR_SOURCE_ORIGIN_INVALID" "Source-origin overrides must map a zero-width private marker to a valid authored span." None None [ "private marker -> authored span" ] [ "invalid mapping" ]

    let private ensureDisjointOriginMarkers owner leftMarkers rightMarkers =
        let overlap = Set.intersect leftMarkers rightMarkers
        if not (Set.isEmpty overlap) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Private source markers must be unique across compiler snapshots and attached bodies." owner None []
                [ sprintf "collision count=%d" overlap.Count ]

    let private remapSourceOriginDiagnostic (sourceOrigins: Map<SourceSpan, SourceSpan>) (action: unit -> 'value) : 'value =
        try action ()
        with
        | LanguageException diagnostic ->
            match diagnostic.Span with
            | Some marker when marker.Length = 0 ->
                match sourceOrigins.TryFind marker with
                | Some origin -> raise (LanguageException { diagnostic with Span = Some origin })
                | None -> reraise ()
            | _ -> reraise ()

    let private compileProgram (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        ensureExactOriginKeys (contextZeroWidthMarkers context) sourceOrigins None None
        let knownTypes = contextTypes context
        for KeyValue(_, record) in context.Records do checkRecordValidator knownTypes context.Words record |> ignore
        for KeyValue(_, scalar) in context.Scalars do checkScalarValidator knownTypes context.Words scalar |> ignore
        let typeKeys = typeKeysForContext context
        let hostCatalog = hostCatalogForContext context typeKeys
        let generatedByName, generatedById, generatedSourceMap = buildGeneratedTargets context typeKeys
        let nominalTypes = buildNominalTypes context typeKeys generatedByName
        let functions = ResizeArray<WordId * IrFunction>()
        let coverage = ResizeArray<WordId * IrCoverageObligations>()
        let sourceMap = ResizeArray<SourceSiteId * IrSourceSite>()
        generatedSourceMap |> Map.iter (fun site value -> sourceMap.Add(site, value))
        for KeyValue(name, entry) in context.Words do
            if entry.Builtin.IsNone then
                let checkedWord, typed = checkDefinitionDetailed knownTypes context.Enums context.Words entry.Definition
                let ownerId = context.WordIds[name]
                let block, localNames, wordSources, inferredEffects =
                    lowerTypedBody context sourceOrigins typeKeys nominalTypes generatedByName name (Some ownerId) entry.Definition.Inputs checkedWord.InferredEffects typed
                wordSources |> Map.iter (fun site value -> sourceMap.Add(site, value))
                let inputTypes = entry.Definition.Inputs |> List.map (closedIrType typeKeys name (Some entry.Definition.Span))
                let outputTypes = entry.Definition.Outputs |> List.map (closedIrType typeKeys name (Some entry.Definition.Span))
                let declaredEffects = irEffectsForWord name (Some entry.Definition.Span) entry.Definition.Effects
                let functionValue =
                    { FunctionId = ownerId
                      FunctionRevision = entry.Revision
                      FunctionName = name
                      InputTypes = inputTypes
                      OutputTypes = outputTypes
                      FunctionDeclaredEffects = declaredEffects
                      FunctionInferredEffects = inferredEffects
                      LocalNames = localNames
                      FunctionBody = block
                      FunctionInline = FlowSource.declaresInline entry.Definition.SourceText }
                functions.Add(ownerId, functionValue)
                coverage.Add(ownerId, IrVerifier.coverageObligationsWithTypes nominalTypes wordSources block)
        let program =
            { NominalTypesByKey = nominalTypes
              FunctionsById = Map.ofSeq functions
              GeneratedTargetsById = generatedById
              SourceMap = Map.ofSeq sourceMap
              CoverageByWord = Map.ofSeq coverage }
        program, hostCatalog, snapshotFingerprint context sourceOrigins

    let compileIrProgram (context: IrLoweringContext) =
        let program, hostCatalog, fingerprint = compileProgram context Map.empty
        IrVerifier.verifyCompilerProgram primitiveIrCatalog hostCatalog fingerprint program

    /// Compile a source snapshot with explicit private-marker to authored-span
    /// mappings. Only the verified source map is exported; marker coordinates
    /// remain part of the compiler input fingerprint but never reach IR.
    let compileIrProgramWithSourceOrigins (context: IrLoweringContext) sourceOrigins =
        let program, hostCatalog, fingerprint = compileProgram context sourceOrigins
        IrVerifier.verifyCompilerProgram primitiveIrCatalog hostCatalog fingerprint program

    /// What every test and example compiled against one lowering context shares:
    /// the context's validation, its private source markers, its type names, the test
    /// scope and the program snapshot fingerprint. A batch compiles hundreds of attached
    /// cases against the same context object, so these are computed once per context
    /// (by reference) instead of once per case.
    type private ContextFacts =
        { Markers: Set<SourceSpan>
          Types: Set<string>
          TestScopeWords: Map<string, WordEntry>
          mutable Fingerprint: (Map<SourceSpan, SourceSpan> * string) option
          // The program-level origin map (by reference) already checked against this context and a program fingerprint.
          mutable CheckedBase: (Map<SourceSpan, SourceSpan> * string) option }

    /// What lowering a detached body needs from one verified program, derived once per
    /// program (by reference).
    type private ProgramFacts =
        { Program: IrProgram
          TypeKeys: Map<string, ProgramTypeKey>
          GeneratedByName: Map<string, IrGeneratedTarget> }

    let private programFacts = System.Runtime.CompilerServices.ConditionalWeakTable<VerifiedIrProgram, ProgramFacts>()

    let private programFactsFor (verifiedProgram: VerifiedIrProgram) =
        match programFacts.TryGetValue verifiedProgram with
        | true, facts -> facts
        | _ ->
            let program = VerifiedIrProgram.inspect verifiedProgram
            let facts = { Program = program; TypeKeys = typeKeysFromProgram program; GeneratedByName = generatedByNameFromProgram program }
            programFacts.AddOrUpdate(verifiedProgram, facts)
            facts

    let private contextFacts = System.Runtime.CompilerServices.ConditionalWeakTable<IrLoweringContext, ContextFacts>()

    let private factsFor (context: IrLoweringContext) =
        match contextFacts.TryGetValue context with
        | true, facts -> facts
        | _ ->
            validateLoweringContext context |> ignore
            let facts =
                { Markers = contextZeroWidthMarkers context
                  Types = contextTypes context
                  TestScopeWords = Map.fold (fun words name entry -> Map.add name entry words) context.Words testIntrinsics
                  Fingerprint = None
                  CheckedBase = None }
            contextFacts.AddOrUpdate(context, facts)
            facts

    let private contextOriginsWith (markers: Set<SourceSpan>) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let missing = Set.difference markers (sourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        if not (Set.isEmpty missing) then
            irFailure "IR_SOURCE_ORIGIN_MISSING" "The compiler snapshot contains private Flow markers without authored source origins." None None
                [ "one origin per compiler marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        sourceOrigins |> Map.filter (fun marker _ -> markers.Contains marker)

    let private cachedFingerprint (facts: ContextFacts) (context: IrLoweringContext) (programOrigins: Map<SourceSpan, SourceSpan>) =
        match facts.Fingerprint with
        | Some(origins, value) when obj.ReferenceEquals(origins, programOrigins) || origins = programOrigins -> value
        | _ ->
            let value = snapshotFingerprint context programOrigins
            facts.Fingerprint <- Some(programOrigins, value)
            value

    /// The verified program for `context` when `context` is `parentContext` with some
    /// Flow-lowered user words added and the parent words in `recompiled` given new entries
    /// (same ids), and nothing else; `parentProgram` was compiled from `parentContext` and
    /// `parentOrigins`. Only the added and recompiled words are checked and lowered.
    /// Lowering one word reads only its own definition and the signatures it calls; no kept
    /// word can call an added one, and the verifier rejects a kept function that calls a
    /// recompiled one (the caller must recompile every caller). So every kept function,
    /// type, generated target and source site is what a from-scratch compile of `context`
    /// produces. `addedOrigins` maps exactly the compiled words' private markers; the
    /// result's origin map is the parent's without the recompiled words' old markers, plus
    /// those (returned beside the program).
    let extendIrProgramWithSourceOrigins (parentContext: IrLoweringContext) (parentOrigins: Map<SourceSpan, SourceSpan>) (parentProgram: VerifiedIrProgram) (recompiled: Set<string>) (context: IrLoweringContext) (addedOrigins: Map<SourceSpan, SourceSpan>) =
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog parentProgram
        let parentFacts = factsFor parentContext
        match parentProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = cachedFingerprint parentFacts parentContext parentOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "An extended program's parent does not match the context it was compiled from." None None [ "same compiler snapshot fingerprint" ] []
        let notPureAdd detail =
            irFailure "IR_EXTEND_NOT_PURE_ADD" "A program can be extended only by adding user words; every other entry must be unchanged." None None [ "parent entries unchanged" ] [ detail ]
        let same (left: 'value) (right: 'value) = obj.ReferenceEquals(left, right) || left = right
        if not (same parentContext.Records context.Records) then notPureAdd "records"
        if not (same parentContext.Scalars context.Scalars) then notPureAdd "scalars"
        if not (same parentContext.Enums context.Enums) then notPureAdd "enums"
        if not (same parentContext.HostOperations context.HostOperations) then notPureAdd "host operations"
        for KeyValue(name, entry) in parentContext.Words do
            match context.Words.TryFind name, context.WordIds.TryFind name with
            | Some kept, Some keptId when obj.ReferenceEquals(kept, entry) && keptId = parentContext.WordIds[name] -> ()
            | Some kept, Some keptId when recompiled.Contains name && keptId = parentContext.WordIds[name] && entry.Builtin.IsNone && kept.Builtin.IsNone -> ()
            | _ -> notPureAdd name
        for name in recompiled do
            if not (parentContext.Words.ContainsKey name) then notPureAdd (name + " is not a parent word")
        let added = context.Words |> Map.filter (fun name _ -> not (parentContext.Words.ContainsKey name))
        let compiled = recompiled |> Seq.fold (fun found name -> Map.add name context.Words[name] found) added
        if compiled.IsEmpty then notPureAdd "no added or recompiled words"
        if context.WordIds.Count <> context.Words.Count then
            irFailure "IR_WORD_ID_MAP_INCOMPLETE" "Every effective dictionary word must have exactly one supplied stable ID." None None [ string context.Words.Count ] [ string context.WordIds.Count ]
        let parentIds = parentContext.WordIds |> Map.toSeq |> Seq.map snd |> Set.ofSeq
        let mutable addedIds = Set.empty
        for KeyValue(name, entry) in added do
            if entry.Builtin.IsSome then notPureAdd (name + " is not a user word")
            match context.WordIds.TryFind name with
            | Some(WordId rawId) when String.IsNullOrWhiteSpace rawId -> irFailure "IR_WORD_ID_INVALID" "Stable word IDs must be nonempty." (Some name) None [ "nonempty WordId" ] [ rawId ]
            | Some identity when not (parentIds.Contains identity) && not (addedIds.Contains identity) -> addedIds <- Set.add identity addedIds
            | Some identity -> irFailure "IR_WORD_ID_COLLISION" "Effective dictionary words must have globally unique stable IDs." (Some name) None [ "unique WordId values" ] [ sprintf "%A" identity ]
            | None -> irFailure "IR_WORD_ID_MAP_INCOMPLETE" "Every effective dictionary word must have exactly one supplied stable ID." (Some name) None [ name ] []
            verifyBuiltinMetadata context.Words context.HostOperations name entry
            if entry.Revision < 0 then irFailure "IR_INVALID_REVISION" "Dictionary word revisions cannot be negative." (Some name) (Some entry.Definition.Span) [ "nonnegative revision" ] [ string entry.Revision ]
        for name in recompiled do
            let entry = context.Words[name]
            verifyBuiltinMetadata context.Words context.HostOperations name entry
            if entry.Revision < 0 then irFailure "IR_INVALID_REVISION" "Dictionary word revisions cannot be negative." (Some name) (Some entry.Definition.Span) [ "nonnegative revision" ] [ string entry.Revision ]
        let removedMarkers = recompiled |> Seq.fold (fun found name -> Set.union found (zeroWidthMarkers parentContext.Words[name].Definition.Body)) Set.empty
        let keptMarkers = Set.difference parentFacts.Markers removedMarkers
        let addedMarkers = compiled |> Map.fold (fun found _ entry -> Set.union found (zeroWidthMarkers entry.Definition.Body)) Set.empty
        let collisions = addedMarkers |> Set.filter keptMarkers.Contains
        if not (Set.isEmpty collisions) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Private source markers must be unique across compiler snapshots and attached bodies." None None [] [ sprintf "collision count=%d" collisions.Count ]
        validateSourceOrigins addedOrigins
        ensureExactOriginKeys addedMarkers addedOrigins None None
        let keptOrigins = removedMarkers |> Set.fold (fun found marker -> Map.remove marker found) parentOrigins
        let sourceOrigins = Map.fold (fun found marker origin -> Map.add marker origin found) keptOrigins addedOrigins
        let derived = programFactsFor parentProgram
        let parentIr = derived.Program
        let knownTypes = parentFacts.Types
        let mutable functions = Map.empty
        let mutable coverage = Map.empty
        let mutable sourceMap = Map.empty
        for KeyValue(name, entry) in compiled do
            let checkedWord, typed = checkDefinitionDetailed knownTypes context.Enums context.Words entry.Definition
            let ownerId = context.WordIds[name]
            let block, localNames, wordSources, inferredEffects =
                lowerTypedBody context sourceOrigins derived.TypeKeys parentIr.NominalTypesByKey derived.GeneratedByName name (Some ownerId) entry.Definition.Inputs checkedWord.InferredEffects typed
            wordSources |> Map.iter (fun site value -> sourceMap <- Map.add site value sourceMap)
            let functionValue =
                { FunctionId = ownerId
                  FunctionRevision = entry.Revision
                  FunctionName = name
                  InputTypes = entry.Definition.Inputs |> List.map (closedIrType derived.TypeKeys name (Some entry.Definition.Span))
                  OutputTypes = entry.Definition.Outputs |> List.map (closedIrType derived.TypeKeys name (Some entry.Definition.Span))
                  FunctionDeclaredEffects = irEffectsForWord name (Some entry.Definition.Span) entry.Definition.Effects
                  FunctionInferredEffects = inferredEffects
                  LocalNames = localNames
                  FunctionBody = block
                  FunctionInline = FlowSource.declaresInline entry.Definition.SourceText }
            functions <- Map.add ownerId functionValue functions
            coverage <- Map.add ownerId (IrVerifier.coverageObligationsWithTypes parentIr.NominalTypesByKey wordSources block) coverage
        let replacedIds = recompiled |> Set.map (fun name -> context.WordIds[name])
        if (functions |> Map.toSeq |> Seq.map fst |> Set.ofSeq) <> Set.union addedIds replacedIds then notPureAdd "compiled functions"
        let fingerprint = snapshotFingerprint context sourceOrigins
        let verified = IrVerifier.extendCompilerProgram parentProgram fingerprint replacedIds functions coverage sourceMap
        // The child's shared facts follow from the parent's and the added words, without
        // walking every retained body again.
        match contextFacts.TryGetValue context with
        | true, _ -> ()
        | _ ->
            contextFacts.AddOrUpdate(
                context,
                { Markers = Set.union keptMarkers addedMarkers
                  Types = parentFacts.Types
                  TestScopeWords = Map.fold (fun words name entry -> if testIntrinsics.ContainsKey name then words else Map.add name entry words) parentFacts.TestScopeWords compiled
                  Fingerprint = Some(sourceOrigins, fingerprint)
                  CheckedBase = None })
        verified, sourceOrigins

    /// Whether two entries differ at most in status and maturity (the entry's and its definition's).
    let sameExceptStatus (left: WordEntry) (right: WordEntry) =
        obj.ReferenceEquals(left, right)
        || { left with Status = right.Status; Maturity = right.Maturity; Definition = { left.Definition with Maturity = right.Definition.Maturity } } = right

    /// The verified program for `context` when it is `parentContext` with only the status and
    /// maturity of the words `restated` changed. Lowering reads neither, so the parent's IR is
    /// this context's IR; only the snapshot fingerprint (which covers both) changes.
    let restatusIrProgram (parentContext: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (parentProgram: VerifiedIrProgram) (context: IrLoweringContext) (restated: Set<string>) =
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog parentProgram
        let parentFacts = factsFor parentContext
        match parentProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = cachedFingerprint parentFacts parentContext sourceOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "A re-stated program's parent does not match the context it was compiled from." None None [ "same compiler snapshot fingerprint" ] []
        let notStatusOnly detail =
            irFailure "IR_EXTEND_NOT_STATUS_ONLY" "A program can be re-stated only when word status or maturity alone changed." None None [ "only status and maturity changed" ] [ detail ]
        let same (left: 'value) (right: 'value) = obj.ReferenceEquals(left, right) || left = right
        if not (same parentContext.Records context.Records && same parentContext.Scalars context.Scalars && same parentContext.Enums context.Enums
                && same parentContext.HostOperations context.HostOperations && same parentContext.WordIds context.WordIds) then notStatusOnly "catalogs"
        if parentContext.Words.Count <> context.Words.Count then notStatusOnly "word count"
        for KeyValue(name, entry) in parentContext.Words do
            match context.Words.TryFind name with
            | Some kept when obj.ReferenceEquals(kept, entry) -> ()
            | Some changed when restated.Contains name && sameExceptStatus changed entry -> ()
            | _ -> notStatusOnly name
        let fingerprint = snapshotFingerprint context sourceOrigins
        let verified = IrVerifier.refingerprintCompilerProgram parentProgram fingerprint
        match contextFacts.TryGetValue context with
        | true, _ -> ()
        | _ ->
            contextFacts.AddOrUpdate(
                context,
                { Markers = parentFacts.Markers
                  Types = parentFacts.Types
                  TestScopeWords = restated |> Set.fold (fun words name -> if testIntrinsics.ContainsKey name then words else Map.add name context.Words[name] words) parentFacts.TestScopeWords
                  Fingerprint = Some(sourceOrigins, fingerprint)
                  CheckedBase = None })
        verified

    let rec private bodyFromInference (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (verifiedProgram: VerifiedIrProgram) (name: string) (initialStack: LangType list) (effects: Set<string>) (typed: TypedBody) =
        let facts = factsFor context
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let programOrigins = contextOriginsWith facts.Markers sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = cachedFingerprint facts context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Detached body context does not match the exact program snapshot it will call." (Some name) None [ "same compiler snapshot fingerprint" ] []
        bodyFromInferenceChecked context sourceOrigins verifiedProgram name initialStack effects typed

    /// bodyFromInference after its context, origin and fingerprint checks have passed.
    and private bodyFromInferenceChecked (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (verifiedProgram: VerifiedIrProgram) (name: string) (initialStack: LangType list) (effects: Set<string>) (typed: TypedBody) =
        let derived = programFactsFor verifiedProgram
        let program = derived.Program
        let typeKeys = derived.TypeKeys
        let generatedByName = derived.GeneratedByName
        let block, localNames, sourceMap, inferredEffects = lowerTypedBody context sourceOrigins typeKeys program.NominalTypesByKey generatedByName name None initialStack effects typed
        let inputTypes = initialStack |> List.map (closedIrType typeKeys name None)
        let outputTypes = typed.ExitStack |> List.map (closedIrType typeKeys name None)
        let body =
            { BodyName = name
              BodyInputTypes = inputTypes
              BodyOutputTypes = outputTypes
              BodyDeclaredEffects = inferredEffects
              BodyInferredEffects = inferredEffects
              BodyLocalNames = localNames
              BodyBlock = block
              BodySourceMap = sourceMap
              BodyCoverage = IrVerifier.coverageObligationsWithTypes program.NominalTypesByKey sourceMap block }
        IrVerifier.verifyBody verifiedProgram body

    let compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram name initialStack expressions sourceOrigins =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = contextZeroWidthMarkers context
        let bodyMarkers = zeroWidthMarkers expressions
        let overlap = Set.intersect contextMarkers bodyMarkers
        if not (Set.isEmpty overlap) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Detached Flow markers must be unique from compiler-snapshot markers." (Some name) None [] [ sprintf "collision count=%d" overlap.Count ]
        ensureExactOriginKeys (Set.union contextMarkers bodyMarkers) sourceOrigins (Some name) None
        let programOrigins = contextOriginsFromMap context sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = snapshotFingerprint context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Detached body context does not match the exact program snapshot it will call." (Some name) None [ "same compiler snapshot fingerprint" ] []
        let knownTypes = contextTypes context
        for typeValue in initialStack do validateType false knownTypes None name typeValue
        let inferred = inferBody knownTypes context.Enums context.Words name None initialStack Map.empty expressions
        bodyFromInference context sourceOrigins verifiedProgram name initialStack inferred.Effects inferred.InferredBody

    let compileIrBodyAgainstProgram context verifiedProgram name initialStack expressions =
        compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram name initialStack expressions Map.empty

    let compileIrBody context name initialStack expressions =
        let verifiedProgram = compileIrProgram context
        compileIrBodyAgainstProgram context verifiedProgram name initialStack expressions

    let compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram (test: TestDefinition) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let facts = factsFor context
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = facts.Markers
        let actualMarkers = zeroWidthMarkers test.Body
        let expectedExpressions =
            match test.Expected with
            | ExpectedExpression expressions -> expressions
            | ExpectedValue _ | ExpectedRuntimeError _ | ExpectedChecks -> []
        let expectedMarkers = zeroWidthMarkers expectedExpressions
        ensureDisjointOriginMarkers (Some test.Word) contextMarkers actualMarkers
        ensureDisjointOriginMarkers (Some test.Word) contextMarkers expectedMarkers
        ensureDisjointOriginMarkers (Some test.Word) actualMarkers expectedMarkers
        ensureExactOriginKeys (Set.unionMany [ contextMarkers; actualMarkers; expectedMarkers ]) sourceOrigins (Some test.Word) (Some test.Span)
        let programOrigins = contextOriginsWith contextMarkers sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = cachedFingerprint facts context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Test context does not match the exact program snapshot it will call." (Some test.Word) (Some test.Span) [ "same compiler snapshot fingerprint" ] []

        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let knownTypes = facts.Types
            let testScopeWords = facts.TestScopeWords
            let checkedTest, typed, expected = checkTestDetailed knownTypes context.Enums testScopeWords test
            let actualBody = bodyFromInference context sourceOrigins verifiedProgram (test.Word + "/" + test.Name) [] checkedTest.Effects typed
            let expectedBody =
                expected
                |> Option.map (fun inferred ->
                    bodyFromInference context sourceOrigins verifiedProgram (test.Word + "/" + test.Name + "/expected") [] Set.empty inferred.InferredBody)
            actualBody, expectedBody)

    /// The program-level part of an attached case's origins, checked once per
    /// (context, origin map, program fingerprint): its keys are exactly the context's
    /// private markers, every mapping is valid, and it matches the program snapshot.
    let private checkBaseOrigins (facts: ContextFacts) (context: IrLoweringContext) (verifiedProgram: VerifiedIrProgram) (baseOrigins: Map<SourceSpan, SourceSpan>) owner span =
        let programFingerprint = verifiedProgram.CompilerSnapshotFingerprint |> Option.defaultValue ""
        match facts.CheckedBase with
        | Some(checkedOrigins, checkedFingerprint) when obj.ReferenceEquals(checkedOrigins, baseOrigins) && checkedFingerprint = programFingerprint -> ()
        | _ ->
            validateSourceOrigins baseOrigins
            ensureExactOriginKeys facts.Markers baseOrigins owner span
            match verifiedProgram.CompilerSnapshotFingerprint with
            | Some fingerprint when fingerprint = cachedFingerprint facts context baseOrigins -> ()
            | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Test context does not match the exact program snapshot it will call." owner span [ "same compiler snapshot fingerprint" ] []
            facts.CheckedBase <- Some(baseOrigins, programFingerprint)

    /// An attached case's own origins: valid, disjoint from the program's markers, and
    /// exactly the case's markers. Cost is the case's size (lookups into the program set).
    let private checkCaseOrigins (facts: ContextFacts) (caseMarkers: Set<SourceSpan>) (caseOrigins: Map<SourceSpan, SourceSpan>) owner span =
        validateSourceOrigins caseOrigins
        let collisions = caseMarkers |> Set.filter facts.Markers.Contains
        if not (Set.isEmpty collisions) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Private source markers must be unique across compiler snapshots and attached bodies." owner None [] [ sprintf "collision count=%d" collisions.Count ]
        ensureExactOriginKeys caseMarkers caseOrigins owner span

    /// compileIrTestWithExpectationAgainstProgramWithSourceOrigins with the program-level
    /// origins (shared by every case against this program) and the case's own origins given
    /// apart, so the program-level checks run once instead of once per case.
    let compileIrTestWithExpectationAgainstProgramWithSplitOrigins context verifiedProgram (test: TestDefinition) (baseOrigins: Map<SourceSpan, SourceSpan>) (caseOrigins: Map<SourceSpan, SourceSpan>) =
        let facts = factsFor context
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        checkBaseOrigins facts context verifiedProgram baseOrigins (Some test.Word) (Some test.Span)
        let actualMarkers = zeroWidthMarkers test.Body
        let expectedExpressions =
            match test.Expected with
            | ExpectedExpression expressions -> expressions
            | ExpectedValue _ | ExpectedRuntimeError _ | ExpectedChecks -> []
        let expectedMarkers = zeroWidthMarkers expectedExpressions
        ensureDisjointOriginMarkers (Some test.Word) actualMarkers expectedMarkers
        checkCaseOrigins facts (Set.union actualMarkers expectedMarkers) caseOrigins (Some test.Word) (Some test.Span)
        let sourceOrigins = Map.fold (fun found marker origin -> Map.add marker origin found) baseOrigins caseOrigins
        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let checkedTest, typed, expected = checkTestDetailed facts.Types context.Enums facts.TestScopeWords test
            let actualBody = bodyFromInferenceChecked context sourceOrigins verifiedProgram (test.Word + "/" + test.Name) [] checkedTest.Effects typed
            let expectedBody =
                expected
                |> Option.map (fun inferred ->
                    bodyFromInferenceChecked context sourceOrigins verifiedProgram (test.Word + "/" + test.Name + "/expected") [] Set.empty inferred.InferredBody)
            actualBody, expectedBody)

    /// The example counterpart of compileIrTestWithExpectationAgainstProgramWithSplitOrigins.
    let compileIrExampleAgainstProgramWithSplitOrigins context verifiedProgram (example: ExampleDefinition) (baseOrigins: Map<SourceSpan, SourceSpan>) (caseOrigins: Map<SourceSpan, SourceSpan>) =
        let facts = factsFor context
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        checkBaseOrigins facts context verifiedProgram baseOrigins (Some example.Word) (Some example.Span)
        checkCaseOrigins facts (zeroWidthMarkers example.Body) caseOrigins (Some example.Word) (Some example.Span)
        let sourceOrigins = Map.fold (fun found marker origin -> Map.add marker origin found) baseOrigins caseOrigins
        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let checkedExample, typed = checkExampleDetailed facts.Types context.Enums context.Words example
            bodyFromInferenceChecked context sourceOrigins verifiedProgram (example.Word + "/" + example.Name) [] checkedExample.Effects typed)

    let compileIrTestWithExpectationAgainstProgram context verifiedProgram test =
        compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram test Map.empty

    let compileIrTestAgainstProgramWithSourceOrigins context verifiedProgram test sourceOrigins =
        compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram test sourceOrigins |> fst

    let compileIrTestAgainstProgram context verifiedProgram test =
        compileIrTestAgainstProgramWithSourceOrigins context verifiedProgram test Map.empty

    let compileIrTest context test =
        let verifiedProgram = compileIrProgram context
        compileIrTestAgainstProgram context verifiedProgram test

    let compileIrExampleAgainstProgramWithSourceOrigins context verifiedProgram (example: ExampleDefinition) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let facts = factsFor context
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = facts.Markers
        let bodyMarkers = zeroWidthMarkers example.Body
        ensureDisjointOriginMarkers (Some example.Word) contextMarkers bodyMarkers
        ensureExactOriginKeys (Set.union contextMarkers bodyMarkers) sourceOrigins (Some example.Word) (Some example.Span)
        let programOrigins = contextOriginsWith contextMarkers sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = cachedFingerprint facts context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Example context does not match the exact program snapshot it will call." (Some example.Word) (Some example.Span) [ "same compiler snapshot fingerprint" ] []

        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let knownTypes = facts.Types
            let checkedExample, typed = checkExampleDetailed knownTypes context.Enums context.Words example
            bodyFromInference context sourceOrigins verifiedProgram (example.Word + "/" + example.Name) [] checkedExample.Effects typed)

    let compileIrExampleAgainstProgram context verifiedProgram example =
        compileIrExampleAgainstProgramWithSourceOrigins context verifiedProgram example Map.empty

    let compileIrExample context example =
        let verifiedProgram = compileIrProgram context
        compileIrExampleAgainstProgram context verifiedProgram example
