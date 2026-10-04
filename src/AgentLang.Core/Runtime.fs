namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

module Runtime =
    type private ReplacementBackup =
        { Word: WordEntry
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition> }

    type private DictionaryState =
        { Words: Map<string, WordEntry>
          Records: Map<string, RecordEntry>
          Scalars: Map<string, ScalarEntry>
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition>
          History: Map<string, WordDefinition list>
          Replacements: Map<string, ReplacementBackup> }

    type private TaskSession =
        { Id: string
          Goal: string
          Snapshot: DictionaryState
          mutable Active: bool
          mutable Inspected: Set<string>
          mutable Used: Set<string>
          mutable Created: Set<string>
          mutable TestsRun: int
          mutable TestsFailed: int
          mutable EffectCounts: Map<string, int>
          mutable Errors: string list }

    type private TestCaseResult =
        { Name: string
          Word: string
          Passed: bool
          Error: Diagnostic option
          Actual: Value list
          Expected: Literal
          Instructions: Set<string>
          BranchOutcomes: Set<string> }

    type private Trace =
        { mutable Steps: int
          mutable CoverageInstructions: Set<string>
          mutable CoverageBranches: Set<string>
          mutable FileSystem: Map<string, string>
          mutable Effects: Map<string, int>
          mutable Console: string list
          CoverageTarget: string option
          Isolated: bool }

    let private jsonNode<'T> (value: 'T) : JsonNode = JsonSerializer.SerializeToNode<'T>(value)
    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode

    let private response (ok: bool) (kind: string) (text: string) (data: JsonNode option) (error: Diagnostic option) =
        let node = JsonObject()
        node["ok"] <- jbool ok
        node["kind"] <- jstr kind
        node["text"] <- jstr text
        match data with Some value -> node["data"] <- value | None -> ()
        match error with
        | Some value ->
            let problem = JsonObject()
            problem["code"] <- jstr value.Code
            problem["message"] <- jstr value.Message
            match value.Word with Some word -> problem["word"] <- jstr word | None -> ()
            match value.Span with
            | Some span ->
                let source = JsonObject()
                source["file"] <- jstr span.File
                source["line"] <- jint span.Line
                source["column"] <- jint span.Column
                source["length"] <- jint span.Length
                problem["span"] <- source
            | None -> ()
            problem["expected"] <- jsonNode value.Expected
            problem["actual"] <- jsonNode value.Actual
            node["error"] <- problem
        | None -> ()
        node

    let private success (kind: string) (text: string) (data: JsonNode option) = response true kind text data None

    let private error code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private lowerFirst (name: string) =
        if String.IsNullOrEmpty name then name else name.Substring(0, 1).ToLowerInvariant() + name.Substring(1)

    let private wordDef name inputs outputs effects docs source =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = 1
          Documentation = docs
          Body = []
          SourceText = source
          Span = { File = "<generated>"; Line = 1; Column = 1; Length = max 1 name.Length } }

    let private entry (definition: WordDefinition) (builtin: Builtin option) (status: WordStatus) (maturity: WordMaturity) (revision: int) : WordEntry =
        { Definition = definition; Builtin = builtin; Status = status; Maturity = maturity; Revision = revision }

    type Engine(projectDirectory: string, capabilities: Set<string>, ?clockValue: string) =
        let projectRoot = if String.IsNullOrWhiteSpace projectDirectory then None else Some(Path.GetFullPath projectDirectory)
        let dictionaryPath = projectRoot |> Option.map (fun path -> Path.Combine(path, "dictionary.agent"))
        let fixedClock = defaultArg clockValue "2000-01-01T00:00:00Z"
        let mutable data =
            { Words = Compiler.primitives
              Records = Map.empty
              Scalars = Map.empty
              Tests = Map.empty
              Examples = Map.empty
              History = Map.empty
              Replacements = Map.empty }
        let mutable virtualFiles = Map.empty
        let mutable activeTask: TaskSession option = None
        let mutable previousTaskLog: JsonObject option = None
        let mutable taskCounter = 0

        let userWords state = state.Words |> Map.filter (fun _ value -> value.Builtin.IsNone)
        let recordDefinitions (state: DictionaryState) = state.Records |> Map.map (fun _ value -> value.Definition)
        let scalarDefinitions (state: DictionaryState) = state.Scalars |> Map.map (fun _ value -> value.Definition)
        let knownTypes (state: DictionaryState) =
            Set.union (state.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (state.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

        let makeGenerated (state: DictionaryState) : Map<string, WordEntry> =
            let recordWords =
                state.Records
                |> Map.toList
                |> List.collect (fun (name, recordEntry) ->
                    let record = recordEntry.Definition
                    let prefix = lowerFirst name
                    let constructorName = prefix + ".new"
                    let constructor =
                        wordDef constructorName (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ] Set.empty "Constructs a value after all record fields pass their declared types." record.SourceText
                    let constructorEntry = entry constructor (Some(RecordConstructor name)) recordEntry.Status LibraryWord 1
                    let getters =
                        record.Fields
                        |> List.map (fun field ->
                            let accessorName = prefix + "." + field.Name
                            let definition = wordDef accessorName [ TNamed name ] [ field.Type ] Set.empty $"Returns the {field.Name} field of a {name}." record.SourceText
                            accessorName, entry definition (Some(RecordAccessor(name, field.Name))) recordEntry.Status LibraryWord 1)
                    (constructorName, constructorEntry) :: getters)
            let scalarWords =
                state.Scalars
                |> Map.toList
                |> List.collect (fun (name, scalarEntry) ->
                    let scalar = scalarEntry.Definition
                    let constructorName = name + ".new"
                    let effects =
                        scalar.Validator
                        |> Option.bind (fun validator -> state.Words.TryFind validator)
                        |> Option.map (fun validatorEntry -> validatorEntry.Definition.Effects)
                        |> Option.defaultValue Set.empty
                    let constructor = wordDef constructorName [ scalar.BaseType ] [ TNamed name ] effects "Constructs a nominal scalar; an optional pure validator must accept it." scalar.SourceText
                    let accessorName = name + ".value"
                    let accessor = wordDef accessorName [ TNamed name ] [ scalar.BaseType ] Set.empty "Explicitly unwraps a nominal scalar to its underlying value." scalar.SourceText
                    [ constructorName, entry constructor (Some(ScalarConstructor name)) scalarEntry.Status LibraryWord 1
                      accessorName, entry accessor (Some(ScalarAccessor name)) scalarEntry.Status LibraryWord 1 ])
            Map.ofList (recordWords @ scalarWords)

        let effectiveWords (state: DictionaryState) : Map<string, WordEntry> =
            let generated = makeGenerated state
            let baseWords = Compiler.primitives
            let collisions = generated |> Map.exists (fun name _ -> baseWords.ContainsKey name)
            if collisions then error "NAME_GENERATED_COLLISION" "A generated record/type word collides with a standard primitive." None None [] []
            let withGenerated = Map.fold (fun found name value -> Map.add name value found) baseWords generated
            Map.fold (fun found name value ->
                if value.Builtin.IsNone then Map.add name value found else found) withGenerated state.Words

        let log (kind: string) (name: string) =
            match activeTask with
            | Some task when task.Active ->
                match kind with
                | "inspect" -> task.Inspected <- Set.add name task.Inspected
                | "use" -> task.Used <- Set.add name task.Used
                | "create" -> task.Created <- Set.add name task.Created
                | "effect" -> task.EffectCounts <- Map.change name (fun count -> Some(defaultArg count 0 + 1)) task.EffectCounts
                | "error" -> task.Errors <- task.Errors @ [ name ]
                | _ -> ()
            | _ -> ()

        let instructionId (span: SourceSpan) = $"{span.File}:{span.Line}:{span.Column}"

        let rec collectInstructionSites (body: Expr list) =
            let own =
                body
                |> List.fold (fun sites expression ->
                    match expression with
                    | Push(_, span) | Call(_, span) | Let(_, span) | Load(_, span) | If(_, _, span) -> Set.add (instructionId span) sites) Set.empty
            body
            |> List.fold (fun sites expression ->
                match expression with
                | If(thenBranch, elseBranch, _) -> Set.union sites (Set.union (collectInstructionSites thenBranch) (collectInstructionSites elseBranch))
                | _ -> sites) own

        let rec collectBranchSites (body: Expr list) =
            body
            |> List.fold (fun sites expression ->
                match expression with
                | If(thenBranch, elseBranch, span) ->
                    let site = instructionId span
                    let withBranches = Set.add (site + ":true") (Set.add (site + ":false") sites)
                    Set.union withBranches (Set.union (collectBranchSites thenBranch) (collectBranchSites elseBranch))
                | _ -> sites) Set.empty

        let mutateEffect (trace: Trace) name =
            trace.Effects <- Map.change name (fun count -> Some(defaultArg count 0 + 1)) trace.Effects
            // Test effects run against isolated virtual providers and are not task effects.
            if not trace.Isolated then log "effect" name

        let requireCapabilities (trace: Trace) (entry: WordEntry) =
            let missing = Set.difference entry.Definition.Effects capabilities
            if not trace.Isolated && not (Set.isEmpty missing) then
                let missingText = String.concat ", " missing
                error "CAPABILITY_DENIED" $"Execution requires capabilities not granted by the host: {missingText}." (Some entry.Definition.Name) (Some entry.Definition.Span) (entry.Definition.Effects |> Set.toList) (capabilities |> Set.toList)

        let popArguments name (inputs: LangType list) (stack: Value list) =
            if stack.Length < inputs.Length then error "RUNTIME_STACK_UNDERFLOW" $"'{name}' requires {inputs.Length} value(s)." (Some name) None (inputs |> List.map Types.format) (stack |> List.map (Types.ofValue >> Types.format))
            let prefix = stack |> List.take (stack.Length - inputs.Length)
            let args = stack |> List.skip (stack.Length - inputs.Length)
            prefix, args

        let expectNumbers name args =
            match args with
            | [ IntValue left; IntValue right ] -> Choice1Of2(left, right)
            | [ FloatValue left; FloatValue right ] -> Choice2Of2(left, right)
            | _ -> error "RUNTIME_INTERNAL_TYPE" $"'{name}' received a value outside its checked signature." (Some name) None [] (args |> List.map (Types.ofValue >> Types.format))

        let countInstruction trace currentWord span =
            trace.Steps <- trace.Steps + 1
            if trace.Steps > 10000 then error "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit." (Some currentWord) (Some span) [] []
            if trace.CoverageTarget = Some currentWord then trace.CoverageInstructions <- Set.add (instructionId span) trace.CoverageInstructions

        let checkedIntOperation (name: string) (span: SourceSpan) (operation: int64 -> int64 -> int64) (left: int64) (right: int64) =
            try IntValue(operation left right)
            with :? OverflowException -> error "RUNTIME_OVERFLOW" $"'{name}' overflowed its Int64 result." (Some name) (Some span) [] [ string left; string right ]

        let rec invoke (state: DictionaryState) (words: Map<string, WordEntry>) (trace: Trace) (depth: int) (entry: WordEntry) (arguments: Value list) =
            if depth > 64 then error "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit." (Some entry.Definition.Name) (Some entry.Definition.Span) [] []
            requireCapabilities trace entry
            log "use" entry.Definition.Name
            match entry.Builtin with
            | None ->
                let stack, _ = runBody state words trace (depth + 1) entry.Definition.Name arguments Map.empty entry.Definition.Body
                stack
            | Some(BuiltinOp name) ->
                let result =
                    match name, arguments with
                    | "add", [ IntValue a; IntValue b ] -> checkedIntOperation name entry.Definition.Span (Checked.(+)) a b
                    | "subtract", [ IntValue a; IntValue b ] -> checkedIntOperation name entry.Definition.Span (Checked.(-)) a b
                    | "multiply", [ IntValue a; IntValue b ] -> checkedIntOperation name entry.Definition.Span (Checked.(*)) a b
                    | "divide", [ IntValue _; IntValue 0L ] -> error "RUNTIME_DIVIDE_BY_ZERO" "Integer division by zero." (Some name) None [] []
                    | "divide", [ IntValue a; IntValue b ] when a = Int64.MinValue && b = -1L -> error "RUNTIME_OVERFLOW" "Integer division overflow." (Some name) None [] []
                    | "divide", [ IntValue a; IntValue b ] -> IntValue(a / b)
                    | "float.add", [ FloatValue a; FloatValue b ] -> FloatValue(a + b)
                    | "float.subtract", [ FloatValue a; FloatValue b ] -> FloatValue(a - b)
                    | "float.multiply", [ FloatValue a; FloatValue b ] -> FloatValue(a * b)
                    | "float.divide", [ FloatValue _; FloatValue b ] when b = 0.0 -> error "RUNTIME_DIVIDE_BY_ZERO" "Float division by zero." (Some name) None [] []
                    | "float.divide", [ FloatValue a; FloatValue b ] -> FloatValue(a / b)
                    | "int.less-than", [ IntValue a; IntValue b ] -> BoolValue(a < b)
                    | "int.greater-than", [ IntValue a; IntValue b ] -> BoolValue(a > b)
                    | "int.less-or-equal", [ IntValue a; IntValue b ] -> BoolValue(a <= b)
                    | "int.greater-or-equal", [ IntValue a; IntValue b ] -> BoolValue(a >= b)
                    | "float.less-than", [ FloatValue a; FloatValue b ] -> BoolValue(a < b)
                    | "float.greater-than", [ FloatValue a; FloatValue b ] -> BoolValue(a > b)
                    | "float.less-or-equal", [ FloatValue a; FloatValue b ] -> BoolValue(a <= b)
                    | "float.greater-or-equal", [ FloatValue a; FloatValue b ] -> BoolValue(a >= b)
                    | "equals", [ a; b ] -> BoolValue(a = b)
                    | "bool.and", [ BoolValue a; BoolValue b ] -> BoolValue(a && b)
                    | "bool.or", [ BoolValue a; BoolValue b ] -> BoolValue(a || b)
                    | "bool.not", [ BoolValue value ] -> BoolValue(not value)
                    | "string.concat", [ StringValue a; StringValue b ] -> StringValue(a + b)
                    | "string.contains", [ StringValue value; StringValue sub ] -> BoolValue(value.Contains(sub, StringComparison.Ordinal))
                    | "string.starts-with", [ StringValue value; StringValue sub ] -> BoolValue(value.StartsWith(sub, StringComparison.Ordinal))
                    | "string.ends-with", [ StringValue value; StringValue sub ] -> BoolValue(value.EndsWith(sub, StringComparison.Ordinal))
                    | "string.length", [ StringValue value ] -> IntValue(int64 value.Length)
                    | "string.trim", [ StringValue value ] -> StringValue(value.Trim())
                    | "string.to-lower", [ StringValue value ] -> StringValue(value.ToLowerInvariant())
                    | "string.to-upper", [ StringValue value ] -> StringValue(value.ToUpperInvariant())
                    | "int.abs", [ IntValue Int64.MinValue ] -> error "RUNTIME_OVERFLOW" "Absolute value of Int64.MinValue overflows." (Some name) None [] []
                    | "int.abs", [ IntValue value ] -> IntValue(abs value)
                    | "int.min", [ IntValue a; IntValue b ] -> IntValue(min a b)
                    | "int.max", [ IntValue a; IntValue b ] -> IntValue(max a b)
                    | "int.to-float", [ IntValue value ] -> FloatValue(float value)
                    | "float.to-int", [ FloatValue value ] when not (Double.IsFinite value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0 -> error "RUNTIME_RANGE" "Float value is outside the Int64 range." (Some name) None [ "finite Int64 range" ] [ string value ]
                    | "float.to-int", [ FloatValue value ] -> IntValue(int64 value)
                    | "float.round", [ FloatValue value ] when not (Double.IsFinite value) -> error "RUNTIME_RANGE" "Float value is outside the Int64 range." (Some name) None [ "finite Int64 range" ] [ string value ]
                    | "float.round", [ FloatValue value ] ->
                        let rounded = Math.Round(value, MidpointRounding.AwayFromZero)
                        if rounded >= 9223372036854775808.0 || rounded < -9223372036854775808.0 then error "RUNTIME_RANGE" "Rounded Float value is outside the Int64 range." (Some name) None [ "finite Int64 range" ] [ string value ]
                        IntValue(int64 rounded)
                    | "int.to-string", [ IntValue value ] -> StringValue(string value)
                    | "float.to-string", [ FloatValue value ] -> StringValue(value.ToString("G", CultureInfo.InvariantCulture))
                    | "dup", [ value ] -> ListValue [ value; value ]
                    | "drop", [ _ ] -> ListValue []
                    | "swap", [ first; second ] -> ListValue [ second; first ]
                    | "file.read", [ StringValue path ] ->
                        mutateEffect trace "fs.read"
                        match trace.FileSystem.TryFind path with
                        | Some contents -> StringValue contents
                        | None -> error "EFFECT_FILE_NOT_FOUND" $"Virtual file '{path}' does not exist." (Some name) None [] [ path ]
                    | "file.exists?", [ StringValue path ] ->
                        mutateEffect trace "fs.read"
                        BoolValue(trace.FileSystem.ContainsKey path)
                    | "file.write", [ StringValue path; StringValue contents ] ->
                        mutateEffect trace "fs.write"
                        trace.FileSystem <- Map.add path contents trace.FileSystem
                        UnitValue
                    | "clock.now", [] -> mutateEffect trace "clock.read"; StringValue fixedClock
                    | "console.write", [ StringValue contents ] -> mutateEffect trace "console.write"; trace.Console <- trace.Console @ [ contents ]; UnitValue
                    | _ -> error "RUNTIME_INTERNAL_TYPE" $"Builtin '{name}' received a value outside its checked signature." (Some name) None [] (arguments |> List.map (Types.ofValue >> Types.format))
                match result with
                | ListValue values when name = "dup" || name = "drop" || name = "swap" -> values
                | UnitValue -> [ UnitValue ]
                | FloatValue value when not (Double.IsFinite value) -> error "RUNTIME_NONFINITE_FLOAT" "Float operation produced a nonfinite value." (Some name) None [ "finite Float" ] [ string value ]
                | value -> [ value ]
            | Some(RecordConstructor typeName) ->
                let definition = state.Records[typeName].Definition
                let values = List.zip definition.Fields arguments |> List.map (fun (field, value) -> field.Name, value) |> Map.ofList
                [ RecordValue(typeName, values) ]
            | Some(RecordAccessor(typeName, fieldName)) ->
                match arguments with
                | [ RecordValue(actualType, fields) ] when actualType = typeName -> [ fields[fieldName] ]
                | _ -> error "RUNTIME_INTERNAL_TYPE" "Record accessor received an invalid record value." (Some entry.Definition.Name) None [ typeName ] (arguments |> List.map (Types.ofValue >> Types.format))
            | Some(ScalarConstructor typeName) ->
                let scalar = state.Scalars[typeName].Definition
                match arguments with
                | [ baseValue ] ->
                    match scalar.Validator with
                    | None -> [ NamedValue(typeName, baseValue) ]
                    | Some validator ->
                        let validatorEntry = words[validator]
                        let checkedResult = invoke state words trace (depth + 1) validatorEntry [ baseValue ]
                        match checkedResult with
                        | [ BoolValue true ] -> [ NamedValue(typeName, baseValue) ]
                        | [ BoolValue false ] -> error "REFINEMENT_FAILED" $"Value does not satisfy {typeName}'s refinement validator." (Some entry.Definition.Name) (Some entry.Definition.Span) [ "validator returns true" ] [ "false" ]
                        | _ -> error "RUNTIME_VALIDATOR_RESULT" "Scalar validator did not return one Bool." (Some validator) None [ "Bool" ] (checkedResult |> List.map (Types.ofValue >> Types.format))
                | _ -> error "RUNTIME_INTERNAL_TYPE" "Scalar constructor received an invalid value." (Some entry.Definition.Name) None [ Types.format scalar.BaseType ] (arguments |> List.map (Types.ofValue >> Types.format))
            | Some(ScalarAccessor typeName) ->
                match arguments with
                | [ NamedValue(actual, value) ] when actual = typeName -> [ value ]
                | _ -> error "RUNTIME_INTERNAL_TYPE" "Scalar unwrapping received an invalid nominal value." (Some entry.Definition.Name) None [ typeName ] (arguments |> List.map (Types.ofValue >> Types.format))

        and runBody (state: DictionaryState) (words: Map<string, WordEntry>) (trace: Trace) (depth: int) (currentWord: string) (initialStack: Value list) (initialLocals: Map<string, Value>) (body: Expr list) =
            let mutable stack = initialStack
            let mutable locals = initialLocals
            for expression in body do
                let sourceSpan =
                    match expression with
                    | Push(_, span) | Call(_, span) | Let(_, span) | Load(_, span) | If(_, _, span) -> span
                countInstruction trace currentWord sourceSpan
                match expression with
                | Push(literal, _) -> stack <- stack @ [ Types.literalValue literal ]
                | Load(name, span) ->
                    match locals.TryFind name with
                    | Some value -> stack <- stack @ [ value ]
                    | None -> error "RUNTIME_UNKNOWN_LOCAL" $"Local '${name}' has not been bound." (Some currentWord) (Some span) [] [ name ]
                | Let(name, span) ->
                    if List.isEmpty stack then error "RUNTIME_STACK_UNDERFLOW" $"Binding '{name}' requires a stack value." (Some currentWord) (Some span) [ "value" ] []
                    locals <- Map.add name (List.last stack) locals
                    stack <- stack |> List.take (stack.Length - 1)
                | Call(name, span) ->
                    match words.TryFind name with
                    | None -> error "RUNTIME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some currentWord) (Some span) [] [ name ]
                    | Some target ->
                        let prefix, arguments = popArguments name target.Definition.Inputs stack
                        let result = invoke state words trace depth target arguments
                        stack <- prefix @ result
                | If(thenBranch, elseBranch, span) ->
                    match stack with
                    | _ when not (List.isEmpty stack) && (match List.last stack with BoolValue _ -> true | _ -> false) ->
                        let condition = match List.last stack with BoolValue value -> value | _ -> false
                        stack <- stack |> List.take (stack.Length - 1)
                        if trace.CoverageTarget = Some currentWord then
                            let branchId = instructionId span + (if condition then ":true" else ":false")
                            trace.CoverageBranches <- Set.add branchId trace.CoverageBranches
                        let branch = if condition then thenBranch else elseBranch
                        let branchStack, branchLocals = runBody state words trace depth currentWord stack locals branch
                        stack <- branchStack
                        locals <- branchLocals
                    | _ -> error "RUNTIME_IF_REQUIRES_BOOL" "'if' requires a Bool at the top of the stack." (Some currentWord) (Some span) [ "Bool" ] (stack |> List.tryLast |> Option.map (Types.ofValue >> Types.format) |> Option.toList)
            stack, locals

        let createTrace coverageTarget fileSystem =
            { Steps = 0
              CoverageInstructions = Set.empty
              CoverageBranches = Set.empty
              FileSystem = fileSystem
              Effects = Map.empty
              Console = []
              CoverageTarget = coverageTarget
              Isolated = coverageTarget.IsSome }

        let executeExpression (state: DictionaryState) (words: Map<string, WordEntry>) (coverageTarget: string option) (fileSystem: Map<string, string>) (expressions: Expr list) =
            let checkedExpression = Compiler.checkExpression (knownTypes state) words expressions
            if coverageTarget.IsNone then
                let missing = Set.difference checkedExpression.Effects capabilities
                if not (Set.isEmpty missing) then
                    error "CAPABILITY_DENIED" "The expression requires effects not granted by the host." None None (checkedExpression.Effects |> Set.toList) (capabilities |> Set.toList)
            let trace = createTrace coverageTarget fileSystem
            let stack, _ = runBody state words trace 0 "<eval>" [] Map.empty expressions
            stack, trace

        let topologicalWords (state: DictionaryState) : WordEntry list =
            let words = userWords state |> Map.filter (fun _ value -> value.Status = Persistent)
            let visited = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let visiting = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let output = ResizeArray<WordEntry>()
            let rec visit name =
                if visiting.Contains name then error "WORD_DEPENDENCY_CYCLE" $"Word dependency cycle includes '{name}'." (Some name) None [] [ name ]
                if not (visited.Contains name) then
                    visiting.Add name |> ignore
                    let word = words[name]
                    for dependency in Compiler.dependencies word.Definition.Body do
                        if words.ContainsKey dependency then visit dependency
                    visiting.Remove name |> ignore
                    visited.Add name |> ignore
                    output.Add word
            words |> Map.toSeq |> Seq.map fst |> Seq.iter visit
            List.ofSeq output

        let durableState (state: DictionaryState) =
            let restored: DictionaryState =
                Map.fold
                    (fun (current: DictionaryState) name (backup: ReplacementBackup) ->
                        match current.Words.TryFind name with
                        | Some value when value.Status = Candidate || value.Status = Temporary ->
                            let tests =
                                current.Tests
                                |> Map.filter (fun _ test -> test.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Tests
                            let examples =
                                current.Examples
                                |> Map.filter (fun _ example -> example.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Examples
                            { current with
                                Words = Map.add name backup.Word current.Words
                                Tests = tests
                                Examples = examples
                                Replacements = Map.remove name current.Replacements }
                        | _ -> current)
                    state
                    state.Replacements
            let projected =
                { restored with
                    Words = restored.Words |> Map.filter (fun _ value -> value.Builtin.IsSome || value.Status = Persistent)
                    Records = restored.Records |> Map.filter (fun _ value -> value.Status = Persistent)
                    Scalars = restored.Scalars |> Map.filter (fun _ value -> value.Status = Persistent)
                    Tests = Map.empty
                    Examples = Map.empty
                    Replacements = Map.empty }
            let words = effectiveWords projected
            let isDurableCase target body = words.ContainsKey target && (Compiler.dependencies body |> Set.forall words.ContainsKey)
            { projected with
                Tests = restored.Tests |> Map.filter (fun _ test -> isDurableCase test.Word test.Body)
                Examples = restored.Examples |> Map.filter (fun _ example -> isDurableCase example.Word example.Body) }

        let serializeWord (word: WordEntry) =
            let lines = word.Definition.SourceText.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun line -> not (line.Trim().StartsWith("maturity ", StringComparison.Ordinal) || line.Trim().StartsWith("revision ", StringComparison.Ordinal)))
            if lines.Length = 0 then word.Definition.SourceText
            else
                let maturity = if word.Maturity = LibraryWord then "maturity library" else "maturity project"
                String.concat Environment.NewLine ([ lines[0]; maturity; $"revision {word.Revision}" ] @ (lines |> Array.skip 1 |> Array.toList))

        let sourceFor (state: DictionaryState) =
            let state = durableState state
            let sections = ResizeArray<string>()
            for _, item in state.Records |> Map.toSeq |> Seq.sortBy fst do if item.Status = Persistent then sections.Add item.Definition.SourceText
            for _, item in state.Scalars |> Map.toSeq |> Seq.sortBy fst do if item.Status = Persistent then sections.Add item.Definition.SourceText
            for word in topologicalWords state do sections.Add(serializeWord word)
            let durableTypes =
                Set.union
                    (state.Records |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some(lowerFirst name) else None) |> Set.ofSeq)
                    (state.Scalars |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq)
            let durableTests =
                state.Tests
                |> Map.toList
                |> List.map snd
                |> List.filter (fun test ->
                    state.Words.TryFind test.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> test.Word = prefix + ".new" || test.Word = prefix + ".value" || test.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun test -> test.Word, test.Name)
            for test in durableTests do sections.Add test.SourceText
            let durableExamples =
                state.Examples
                |> Map.toList
                |> List.map snd
                |> List.filter (fun example ->
                    state.Words.TryFind example.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> example.Word = prefix + ".new" || example.Word = prefix + ".value" || example.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun example -> example.Word, example.Name)
            for example in durableExamples do sections.Add example.SourceText
            String.concat (Environment.NewLine + Environment.NewLine) sections + Environment.NewLine

        let sourceForAgent (source: string) =
            source.Replace("\r\n", "\n").Split('\n')
            |> Array.filter (fun line ->
                let trimmed = line.Trim()
                not (trimmed.StartsWith("maturity ", StringComparison.Ordinal) || trimmed.StartsWith("revision ", StringComparison.Ordinal)))
            |> String.concat Environment.NewLine

        let writeAtomic (path: string) (contents: string) =
            let directory = Path.GetDirectoryName path
            Directory.CreateDirectory directory |> ignore
            let temp = Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")
            File.WriteAllText(temp, contents, System.Text.UTF8Encoding(false))
            File.Move(temp, path, true)

        let persist (state: DictionaryState) =
            match dictionaryPath with
            | Some path -> writeAtomic path (sourceFor state)
            | None -> ()

        let addTest (results: Map<string, TestDefinition>) (test: TestDefinition) =
            Map.add ($"{test.Word}/{test.Name}") test results

        let addExample (results: Map<string, ExampleDefinition>) (example: ExampleDefinition) =
            Map.add ($"{example.Word}/{example.Name}") example results

        let validateGraph (state: DictionaryState) =
            let words = effectiveWords state
            let user = userWords state
            for entry in user |> Map.toSeq |> Seq.map snd do Compiler.checkDefinition (knownTypes state) words entry.Definition |> ignore
            for record in state.Records |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                for field in record.Fields do
                    match field.Type with
                    | TNamed name when not ((knownTypes state).Contains name) -> error "TYPE_UNKNOWN_FIELD_TYPE" $"Field '{record.Name}.{field.Name}' uses undeclared type '{name}'." (Some record.Name) (Some record.Span) [ "declared record or scalar type" ] [ name ]
                    | TList _ | TOption _ | TResult _ | TVar _ -> error "TYPE_UNSUPPORTED_FIELD_TYPE" "Record fields currently support primitive and nominal scalar/record types only." (Some record.Name) (Some record.Span) [] [ Types.format field.Type ]
                    | _ -> ()
            for scalar in state.Scalars |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                let deps = Compiler.checkScalarValidator (knownTypes state) words scalar
                match scalar.Validator with
                | Some validator when deps.Contains(scalar.Name + ".new") || deps.Contains(scalar.Name + ".value") ->
                    error "TYPE_VALIDATOR_RECURSION" $"Validator '{validator}' cannot construct or unwrap '{scalar.Name}'." (Some scalar.Name) (Some scalar.Span) [] (Set.toList deps)
                | _ -> ()
            for test in state.Tests |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkTest (knownTypes state) words test |> ignore
            for example in state.Examples |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkExample (knownTypes state) words example |> ignore
            let graph =
                words
                |> Map.toSeq
                |> Seq.choose (fun (name, value) ->
                    if value.Builtin.IsSome then None
                    else Some(name, Compiler.dependencies value.Definition.Body))
                |> Map.ofSeq
                |> fun initial ->
                    state.Scalars
                    |> Map.toSeq
                    |> Seq.fold (fun found (_, scalar) ->
                        match scalar.Definition.Validator with
                        | Some validator -> Map.add (scalar.Definition.Name + ".new") (Set.singleton validator) found
                        | None -> found) initial
            let colors = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
            let rec visit path name =
                match colors.TryGetValue name with
                | true, 1 ->
                    let cycle = String.concat " -> " (path @ [ name ])
                    error "WORD_DEPENDENCY_CYCLE" $"Word dependency cycle: {cycle}." (Some name) None [] (path @ [ name ])
                | true, 2 -> ()
                | _ ->
                    colors[name] <- 1
                    for dependency in graph.TryFind name |> Option.defaultValue Set.empty do
                        if graph.ContainsKey dependency then visit (path @ [ name ]) dependency
                    colors[name] <- 2
            graph |> Map.toSeq |> Seq.iter (fun (name, _) -> visit [] name)

        let loadProject () =
            match dictionaryPath with
            | Some path when File.Exists path ->
                let source = File.ReadAllText path
                match Parser.parse path source with
                | Error diagnostic -> raise (LanguageException diagnostic)
                | Ok parsed ->
                    let records: Map<string, RecordEntry> = parsed.Records |> List.map (fun (value: RecordDefinition) -> value.Name, ({ Definition = value; Status = Persistent }: RecordEntry)) |> Map.ofList
                    let scalars: Map<string, ScalarEntry> = parsed.Scalars |> List.map (fun (value: ScalarTypeDefinition) -> value.Name, ({ Definition = value; Status = Persistent }: ScalarEntry)) |> Map.ofList
                    let mutable proposed = { data with Records = records; Scalars = scalars }
                    let words = parsed.Words |> List.map (fun value -> value.Name, entry value None Persistent value.Maturity value.Revision) |> Map.ofList
                    proposed <- { proposed with Words = Map.fold (fun found name value -> Map.add name value found) Compiler.primitives words }
                    proposed <- { proposed with Tests = parsed.Tests |> List.fold addTest Map.empty; Examples = parsed.Examples |> List.fold addExample Map.empty }
                    validateGraph proposed
                    data <- proposed
            | _ -> ()

        do loadProject ()

        let toJsonValue value = jsonNode (Types.formatValue value)

        let checkedByTest (state: DictionaryState) (words: Map<string, WordEntry>) (test: TestDefinition) =
            let sites =
                words.TryFind test.Word
                |> Option.map (fun entry -> collectInstructionSites entry.Definition.Body)
                |> Option.defaultValue Set.empty
            let trace = createTrace (Some test.Word) Map.empty
            try
                Compiler.checkTest (knownTypes state) words test |> ignore
                let stack, _ = runBody state words trace 0 "<test>" [] Map.empty test.Body
                let expected = Types.literalValue test.Expected
                let passed = stack = [ expected ]
                { Name = test.Name
                  Word = test.Word
                  Passed = passed
                  Error = if passed then None else Some { Code = "TEST_ASSERTION_FAILED"; Message = "Actual value did not equal the expected literal."; Word = Some test.Word; Span = Some test.Span; Expected = [ Types.formatValue expected ]; Actual = stack |> List.map Types.formatValue }
                  Actual = stack
                  Expected = test.Expected
                  Instructions = trace.CoverageInstructions |> Set.intersect sites
                  BranchOutcomes = trace.CoverageBranches }
            with
            | LanguageException diagnostic ->
                { Name = test.Name
                  Word = test.Word
                  Passed = false
                  Error = Some diagnostic
                  Actual = []
                  Expected = test.Expected
                  Instructions = trace.CoverageInstructions |> Set.intersect sites
                  BranchOutcomes = trace.CoverageBranches }

        let resultJson (result: TestCaseResult) =
            let node = JsonObject()
            node["name"] <- jstr result.Name
            node["word"] <- jstr result.Word
            node["passed"] <- jbool result.Passed
            node["expected"] <- toJsonValue (Types.literalValue result.Expected)
            node["actual"] <- jsonNode (result.Actual |> List.map Types.formatValue)
            match result.Error with
            | Some diagnostic -> node["errorCode"] <- jstr diagnostic.Code; node["message"] <- jstr diagnostic.Message
            | None -> ()
            node

        let runTestsFor (state: DictionaryState) (words: Map<string, WordEntry>) target =
            let tests =
                state.Tests
                |> Map.toList
                |> List.map snd
                |> List.filter (fun test -> target |> Option.forall ((=) test.Word))
                |> List.sortBy (fun test -> test.Word, test.Name)
            tests |> List.map (checkedByTest state words)

        let coverageJson (word: string) (words: Map<string, WordEntry>) (results: TestCaseResult list) =
            let definition = words[word].Definition
            let requiredInstructions = collectInstructionSites definition.Body
            let requiredBranches = collectBranchSites definition.Body
            let actualInstructions = results |> List.fold (fun found result -> Set.union found result.Instructions) Set.empty
            let actualBranches = results |> List.fold (fun found result -> Set.union found result.BranchOutcomes) Set.empty
            let uncoveredInstructions = Set.difference requiredInstructions actualInstructions
            let uncoveredBranches = Set.difference requiredBranches actualBranches
            let node = JsonObject()
            node["instructionsCovered"] <- jint actualInstructions.Count
            node["instructionsTotal"] <- jint requiredInstructions.Count
            node["branchesCovered"] <- jint actualBranches.Count
            node["branchesTotal"] <- jint requiredBranches.Count
            node["uncoveredInstructions"] <- jsonNode (uncoveredInstructions |> Set.toList)
            node["uncoveredBranchOutcomes"] <- jsonNode (uncoveredBranches |> Set.toList)
            node

        let requireProjectPath () =
            match projectRoot with
            | Some path -> Directory.CreateDirectory path |> ignore; path
            | None -> error "PROJECT_PATH_REQUIRED" "This control-plane operation requires a configured project directory." None None [] []

        let makeTaskJson (task: TaskSession) =
            let node = JsonObject()
            node["task"] <- jstr task.Id
            node["goal"] <- jstr task.Goal
            node["active"] <- jbool task.Active
            node["wordsInspected"] <- jsonNode (task.Inspected |> Set.toList)
            node["wordsUsed"] <- jsonNode (task.Used |> Set.toList)
            node["wordsCreated"] <- jsonNode (task.Created |> Set.toList)
            node["testsRun"] <- jint task.TestsRun
            node["testsFailed"] <- jint task.TestsFailed
            node["effects"] <- jsonNode (task.EffectCounts |> Map.toSeq |> Map.ofSeq)
            node["errors"] <- jsonNode task.Errors
            node

        let mutable lastResults: TestCaseResult list = []

        let recordTestResults (results: TestCaseResult list) =
            match activeTask with
            | Some task when task.Active ->
                task.TestsRun <- task.TestsRun + results.Length
                task.TestsFailed <- task.TestsFailed + (results |> List.filter (fun result -> not result.Passed) |> List.length)
            | _ -> ()

        let cleanupTaskTemporaries (task: TaskSession) =
            let temporaryNames = data.Words |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Temporary then Some name else None) |> Seq.toList
            let mutable proposed = data
            for name in temporaryNames do
                match task.Snapshot.Words.TryFind name with
                | Some previous when previous.Status = Temporary ->
                    let tests =
                        proposed.Tests
                        |> Map.filter (fun _ test -> test.Word <> name)
                        |> fun current -> task.Snapshot.Tests |> Map.filter (fun _ test -> test.Word = name) |> Map.fold (fun found key value -> Map.add key value found) current
                    let examples =
                        proposed.Examples
                        |> Map.filter (fun _ example -> example.Word <> name)
                        |> fun current -> task.Snapshot.Examples |> Map.filter (fun _ example -> example.Word = name) |> Map.fold (fun found key value -> Map.add key value found) current
                    proposed <- { proposed with Words = Map.add name previous proposed.Words; Tests = tests; Examples = examples }
                | _ ->
                    match proposed.Replacements.TryFind name with
                    | Some backup ->
                        let tests =
                            proposed.Tests
                            |> Map.filter (fun _ test -> test.Word <> name)
                            |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Tests
                        let examples =
                            proposed.Examples
                            |> Map.filter (fun _ example -> example.Word <> name)
                            |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Examples
                        proposed <-
                            { proposed with
                                Words = Map.add name backup.Word proposed.Words
                                Tests = tests
                                Examples = examples
                                Replacements = Map.remove name proposed.Replacements }
                    | None ->
                        proposed <-
                            { proposed with
                                Words = Map.remove name proposed.Words
                                Tests = proposed.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                Examples = proposed.Examples |> Map.filter (fun _ example -> example.Word <> name) }
            validateGraph proposed
            data <- proposed
            lastResults <- []

        let availableDescription (state: DictionaryState) (words: Map<string, WordEntry>) name =
            match words.TryFind name with
            | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
            | Some item ->
                log "inspect" name
                let definition = item.Definition
                let direct =
                    match item.Builtin with
                    | Some(RecordConstructor record) -> Set.empty
                    | Some(ScalarConstructor scalar) -> scalarDefinitions state |> Map.tryFind scalar |> Option.bind (fun value -> value.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                    | Some _ -> Set.empty
                    | None -> Compiler.dependencies definition.Body
                let transitive =
                    let rec walk (pending: Set<string>) (found: Set<string>) =
                        match pending |> Set.toList with
                        | [] -> found
                        | head :: tail when found.Contains head -> walk (Set.ofList tail) found
                        | head :: tail ->
                            let next = words.TryFind head |> Option.map (fun value -> if value.Builtin.IsNone then Compiler.dependencies value.Definition.Body else Set.empty) |> Option.defaultValue Set.empty
                            walk (Set.union (Set.ofList tail) next) (Set.add head found)
                    walk direct Set.empty
                let callers =
                    userWords state
                    |> Map.toList
                    |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None)
                    |> List.sort
                let tests = state.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name) |> List.sortBy (fun test -> test.Name)
                let examples = state.Examples |> Map.toList |> List.map snd |> List.filter (fun example -> example.Word = name) |> List.sortBy (fun example -> example.Name)
                let sourceFile = definition.Span.File
                let sourceLine = definition.Span.Line
                let maturity = if item.Maturity = LibraryWord then "library" else "project"
                let status = match item.Status with Primitive -> "primitive" | Candidate -> "candidate" | Temporary -> "temporary" | Persistent -> "persistent"
                let kind =
                    match item.Builtin with
                    | Some(BuiltinOp _) -> "primitive"
                    | Some _ -> "generated"
                    | None -> "word"
                let obj = JsonObject()
                obj["name"] <- jstr name
                obj["inputs"] <- jsonNode (definition.Inputs |> List.map Types.format)
                obj["outputs"] <- jsonNode (definition.Outputs |> List.map Types.format)
                obj["effects"] <- jsonNode (definition.Effects |> Set.toList)
                obj["documentation"] <- jstr definition.Documentation
                obj["dependencies"] <- jsonNode (direct |> Set.toList)
                obj["transitiveDependencies"] <- jsonNode (transitive |> Set.toList)
                obj["callers"] <- jsonNode callers
                obj["tests"] <- jsonNode (tests |> List.map (fun test -> test.Name))
                obj["examples"] <- jsonNode (examples |> List.map (fun example -> example.Name))
                obj["source"] <- jstr sourceFile
                obj["line"] <- jint sourceLine
                obj["kind"] <- jstr kind
                obj["status"] <- jstr status
                obj["maturity"] <- jstr maturity
                obj["revision"] <- jint item.Revision
                obj["testCount"] <- jint tests.Length
                obj["exampleCount"] <- jint examples.Length
                let observed = lastResults |> List.filter (fun result -> result.Word = name)
                let currentCoverage = coverageJson name words observed
                currentCoverage["status"] <- jstr (if List.isEmpty observed then "not-run" else "current")
                obj["coverage"] <- currentCoverage
                obj

        let frozenValidatorWords (state: DictionaryState) (words: Map<string, WordEntry>) =
            let rec reach (pending: Set<string>) (found: Set<string>) =
                match pending |> Set.toList with
                | [] -> found
                | head :: rest when found.Contains head -> reach (Set.ofList rest) found
                | head :: rest ->
                    let next = words.TryFind head |> Option.map (fun entry -> if entry.Builtin.IsNone then Compiler.dependencies entry.Definition.Body else Set.empty) |> Option.defaultValue Set.empty
                    reach (Set.union (Set.ofList rest) next) (Set.add head found)
            state.Scalars
            |> Map.toSeq
            |> Seq.choose (fun (_, scalar) ->
                if scalar.Status <> Persistent then None
                else scalar.Definition.Validator |> Option.map (fun validator -> reach (Set.singleton validator) Set.empty))
            |> Seq.fold Set.union Set.empty

        let registerParsed (parsed: ParsedSource) (temporary: bool) =
            let old = data
            if parsed.Words |> List.exists (fun definition -> definition.Maturity = LibraryWord || definition.Revision <> 1) then
                error "WORD_METADATA_HOST_MANAGED" "Word maturity and revision are assigned by the host and cannot be set in source." None None [] []
            let duplicateNames =
                (parsed.Records |> List.map (fun value -> value.Name)) @ (parsed.Scalars |> List.map (fun value -> value.Name))
                |> List.groupBy id
                |> List.tryFind (fun (_, values) -> List.length values > 1)
            match duplicateNames with
            | Some(name, _) -> error "TYPE_DUPLICATE_NAME" $"Type name '{name}' is declared more than once." (Some name) None [] []
            | None -> ()
            let duplicateWords = parsed.Words |> List.groupBy (fun value -> value.Name) |> List.tryFind (fun (_, values) -> List.length values > 1)
            match duplicateWords with
            | Some(name, _) -> error "WORD_DUPLICATE_NAME" $"Word '{name}' is declared more than once in the same source block." (Some name) None [] []
            | None -> ()
            for record in parsed.Records do
                if old.Records.ContainsKey record.Name || old.Scalars.ContainsKey record.Name then error "TYPE_REDEFINITION" $"Type '{record.Name}' is immutable after declaration." (Some record.Name) (Some record.Span) [] []
            for scalar in parsed.Scalars do
                if old.Records.ContainsKey scalar.Name || old.Scalars.ContainsKey scalar.Name then error "TYPE_REDEFINITION" $"Type '{scalar.Name}' is immutable after declaration." (Some scalar.Name) (Some scalar.Span) [] []
            let status = if temporary then Temporary else Candidate
            let records: Map<string, RecordEntry> = parsed.Records |> List.fold (fun result (record: RecordDefinition) -> Map.add record.Name ({ Definition = record; Status = Candidate }: RecordEntry) result) old.Records
            let scalars: Map<string, ScalarEntry> = parsed.Scalars |> List.fold (fun result (scalar: ScalarTypeDefinition) -> Map.add scalar.Name ({ Definition = scalar; Status = Candidate }: ScalarEntry) result) old.Scalars
            let initialEntries, initialReplacements : Map<string, WordEntry> * Map<string, ReplacementBackup> =
                List.fold (fun (words: Map<string, WordEntry>, backups: Map<string, ReplacementBackup>) (definition: WordDefinition) ->
                    if Compiler.primitives.ContainsKey definition.Name || (makeGenerated { old with Records = records; Scalars = scalars }).ContainsKey definition.Name then
                        error "NAME_RESERVED" $"Word name '{definition.Name}' is reserved by the standard library or a generated type word." (Some definition.Name) (Some definition.Span) [] []
                    let previous = words.TryFind definition.Name
                    let maturity, revision =
                        match previous with
                        | Some value ->
                            let revision = if value.Status = Persistent then value.Revision + 1 else value.Revision
                            (if value.Maturity = LibraryWord then LibraryWord else ProjectWord), revision
                        | None -> ProjectWord, 1
                    let adjusted = { definition with Maturity = maturity; Revision = revision }
                    let backups =
                        match previous, backups.TryFind definition.Name with
                        | Some value, None when value.Status = Persistent ->
                            let originalTests = old.Tests |> Map.filter (fun _ test -> test.Word = definition.Name)
                            let originalExamples = old.Examples |> Map.filter (fun _ example -> example.Word = definition.Name)
                            Map.add definition.Name { Word = value; Tests = originalTests; Examples = originalExamples } backups
                        | _ -> backups
                    let words = Map.add definition.Name (entry adjusted None status maturity revision) words
                    words, backups) (old.Words, old.Replacements) parsed.Words
            let tests = parsed.Tests |> List.fold addTest old.Tests
            let examples = parsed.Examples |> List.fold addExample old.Examples
            let mutable entries = initialEntries
            let mutable replacements = initialReplacements
            let generated = makeGenerated { old with Records = records; Scalars = scalars }
            let metadataTargets =
                (parsed.Tests |> List.map (fun test -> test.Word)) @ (parsed.Examples |> List.map (fun example -> example.Word))
                |> Set.ofList
            for name in metadataTargets do
                match entries.TryFind name with
                | Some item when item.Builtin.IsNone && item.Status = Persistent ->
                    let backup =
                        replacements.TryFind name
                        |> Option.defaultWith (fun () ->
                            { Word = item
                              Tests = old.Tests |> Map.filter (fun _ test -> test.Word = name)
                              Examples = old.Examples |> Map.filter (fun _ example -> example.Word = name) })
                    let revision = item.Revision + 1
                    let definition = { item.Definition with Revision = revision }
                    entries <- Map.add name { item with Definition = definition; Status = Candidate; Revision = revision } entries
                    replacements <- Map.add name backup replacements
                | Some item when item.Builtin.IsSome && item.Status = Persistent ->
                    error "TEST_TARGET_IMMUTABLE" $"Tests and examples for generated type word '{name}' must be attached before the type is committed." (Some name) None [] []
                | Some item when item.Builtin.IsSome && item.Status = Primitive ->
                    error "TEST_TARGET_IMMUTABLE" $"Tests and examples cannot be added to trusted primitive '{name}' through project source." (Some name) None [] []
                | None ->
                    match generated.TryFind name with
                    | Some item when item.Status = Persistent -> error "TEST_TARGET_IMMUTABLE" $"Tests and examples for generated type word '{name}' must be attached before the type is committed." (Some name) None [] []
                    | _ -> ()
                | _ -> ()
            let proposed = { old with Records = records; Scalars = scalars; Words = entries; Tests = tests; Examples = examples; Replacements = replacements }
            validateGraph proposed
            let frozen = frozenValidatorWords old (effectiveWords old)
            let changed = parsed.Words |> List.map (fun word -> word.Name) |> Set.ofList
            let conflict = Set.intersect frozen changed
            if not (Set.isEmpty conflict) then
                error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{Set.minElement conflict}' while a scalar type is persistent." None None [] (Set.toList conflict)
            data <- proposed
            lastResults <- []
            for word in parsed.Words do log "create" word.Name
            for name in metadataTargets do
                if initialEntries.TryFind name |> Option.exists (fun item -> item.Status = Persistent) then log "create" name

        let saveTaskLog (task: TaskSession) =
            let node = makeTaskJson task
            match projectRoot with
            | Some root ->
                let history = Path.Combine(root, "history")
                Directory.CreateDirectory history |> ignore
                writeAtomic (Path.Combine(history, task.Id + ".json")) (node.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
            | None -> ()
            previousTaskLog <- Some node

        let nextTaskNumber () =
            let persistedMaximum =
                match projectRoot with
                | Some root ->
                    let directory = Path.Combine(root, "history")
                    if not (Directory.Exists directory) then 0
                    else
                        Directory.GetFiles(directory, "task-*.json")
                        |> Array.choose (fun path ->
                            let stem = Path.GetFileNameWithoutExtension path
                            match Int32.TryParse(stem.Substring("task-".Length), NumberStyles.None, CultureInfo.InvariantCulture) with
                            | true, value -> Some value
                            | _ -> None)
                        |> Array.fold max 0
                | None -> 0
            taskCounter <- max taskCounter persistedMaximum + 1
            taskCounter

        let commitCandidates target library =
            let candidateWords = data.Words |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateRecords = data.Records |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateScalars = data.Scalars |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateTypes = Set.union (candidateRecords |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (candidateScalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            let currentWords = effectiveWords data

            let rec wordClosure (pending: string list) (found: Set<string>) =
                match pending with
                | [] -> found
                | name :: rest when found.Contains name -> wordClosure rest found
                | name :: rest ->
                    match candidateWords.TryFind name with
                    | None -> wordClosure rest found
                    | Some value ->
                        let dependencies = Compiler.dependencies value.Definition.Body |> Set.toList
                        wordClosure (rest @ dependencies) (Set.add name found)

            let rec namedTypes (typeValue: LangType) =
                match typeValue with
                | TNamed name -> Set.singleton name
                | TList item | TOption item -> namedTypes item
                | TResult(ok, err) -> Set.union (namedTypes ok) (namedTypes err)
                | _ -> Set.empty

            let ownerOfGeneratedWord name =
                match currentWords.TryFind name with
                | Some { Builtin = Some(RecordConstructor owner) } -> Some owner
                | Some { Builtin = Some(RecordAccessor(owner, _)) } -> Some owner
                | Some { Builtin = Some(ScalarConstructor owner) } -> Some owner
                | Some { Builtin = Some(ScalarAccessor owner) } -> Some owner
                | _ -> None

            let typeReferencesForWord (name: string) =
                match data.Words.TryFind name with
                | None -> Set.empty
                | Some value ->
                    let signatures = value.Definition.Inputs @ value.Definition.Outputs |> List.map namedTypes |> Set.unionMany
                    let generatedDependencies =
                        Compiler.dependencies value.Definition.Body
                        |> Set.toList
                        |> List.choose ownerOfGeneratedWord
                        |> Set.ofList
                    Set.union signatures generatedDependencies

            let rec typeClosure (pending: string list) (found: Set<string>) =
                match pending with
                | [] -> found
                | name :: rest when found.Contains name -> typeClosure rest found
                | name :: rest when not (candidateTypes.Contains name) -> typeClosure rest found
                | name :: rest ->
                    let referenced =
                        match candidateRecords.TryFind name, candidateScalars.TryFind name with
                        | Some record, _ -> record.Definition.Fields |> List.map (fun field -> namedTypes field.Type) |> Set.unionMany
                        | _, Some scalar -> namedTypes scalar.Definition.BaseType
                        | _ -> Set.empty
                    typeClosure (rest @ Set.toList referenced) (Set.add name found)

            let mutable selectedWords, selectedTypes =
                match target with
                | None -> candidateWords |> Map.toSeq |> Seq.map fst |> Set.ofSeq, candidateTypes
                | Some name when candidateWords.ContainsKey name -> wordClosure [ name ] Set.empty, Set.empty
                | Some name when candidateTypes.Contains name -> Set.empty, typeClosure [ name ] Set.empty
                | Some name ->
                    match ownerOfGeneratedWord name with
                    | Some owner when candidateTypes.Contains owner -> Set.empty, typeClosure [ owner ] Set.empty
                    | _ -> error "COMMIT_NOT_CANDIDATE" $"'{name}' is not a candidate word or type." (Some name) None [] []

            let mutable changed = true
            while changed do
                let previousWords, previousTypes = selectedWords, selectedTypes
                let validatorWords =
                    selectedTypes
                    |> Set.toList
                    |> List.choose (fun name -> candidateScalars.TryFind name |> Option.bind (fun value -> value.Definition.Validator))
                    |> List.fold (fun found name -> Set.union found (wordClosure [ name ] Set.empty)) Set.empty
                selectedWords <- Set.union selectedWords validatorWords
                let referencedTypes = selectedWords |> Set.toList |> List.map typeReferencesForWord |> Set.unionMany
                selectedTypes <- typeClosure (Set.union selectedTypes referencedTypes |> Set.toList) selectedTypes
                changed <- previousWords <> selectedWords || previousTypes <> selectedTypes

            if Set.isEmpty selectedWords && Set.isEmpty selectedTypes then
                error "COMMIT_NO_CANDIDATES" "There are no candidate words or types to commit." None None [] []

            let proposedWords =
                data.Words
                |> Map.map (fun name value ->
                    if selectedWords.Contains name then
                        let promoteToLibrary = library && (target.IsNone || target = Some name)
                        { value with Status = Persistent; Maturity = if promoteToLibrary || value.Maturity = LibraryWord then LibraryWord else ProjectWord }
                    else value)
            let proposedRecords = data.Records |> Map.map (fun name value -> if selectedTypes.Contains name then { value with Status = Persistent } else value)
            let proposedScalars = data.Scalars |> Map.map (fun name value -> if selectedTypes.Contains name then { value with Status = Persistent } else value)
            let proposed =
                { data with
                    Words = proposedWords
                    Records = proposedRecords
                    Scalars = proposedScalars
                    Replacements = data.Replacements |> Map.filter (fun name _ -> not (selectedWords.Contains name)) }
            let finalWords = effectiveWords proposed
            validateGraph proposed
            for name in selectedWords do
                let candidate = candidateWords[name]
                let danglingTemp = Compiler.dependencies candidate.Definition.Body |> Set.filter (fun dependency -> data.Words.TryFind dependency |> Option.exists (fun value -> value.Status = Temporary))
                if not (Set.isEmpty danglingTemp) then error "COMMIT_TEMPORARY_DEPENDENCY" $"Candidate '{name}' depends on temporary word '{Set.minElement danglingTemp}'. Promote it and commit it before this word." (Some name) None [] (Set.toList danglingTemp)
                let attached = proposed.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name)
                if List.isEmpty attached then error "COMMIT_TEST_REQUIRED" $"Candidate '{name}' needs at least one attached passing test before commit." (Some name) None [ "attached passing test" ] []
            let results = selectedWords |> Set.toList |> List.collect (fun name -> runTestsFor proposed finalWords (Some name))
            lastResults <- results
            recordTestResults results
            let failed = results |> List.filter (fun result -> not result.Passed)
            if not (List.isEmpty failed) then
                let names = failed |> List.map (fun value -> $"{value.Word}/{value.Name}")
                error "COMMIT_TESTS_FAILED" "Candidate tests must pass against the proposed dictionary before commit." None None [] names
            for name in selectedWords do
                let candidate = candidateWords[name]
                if proposedWords[name].Maturity = LibraryWord then
                    let result = results |> List.filter (fun test -> test.Word = name)
                    let requiredInstructions = collectInstructionSites candidate.Definition.Body
                    let requiredBranches = collectBranchSites candidate.Definition.Body
                    let coveredInstructions = result |> List.fold (fun found test -> Set.union found test.Instructions) Set.empty
                    let coveredBranches = result |> List.fold (fun found test -> Set.union found test.BranchOutcomes) Set.empty
                    let uncovered = Set.difference requiredInstructions coveredInstructions
                    let missingBranches = Set.difference requiredBranches coveredBranches
                    if not (Set.isEmpty uncovered && Set.isEmpty missingBranches && not (List.isEmpty result)) then
                        error "LIBRARY_COVERAGE_INCOMPLETE" $"Library word '{name}' requires every instruction and both outcomes of every if to be exercised by attached tests." (Some name) None [] (Set.toList uncovered @ Set.toList missingBranches)
            let history =
                selectedWords
                |> Set.fold (fun (found: Map<string, WordDefinition list>) name ->
                    let candidate = candidateWords[name]
                    let previous = found.TryFind name |> Option.defaultValue []
                    Map.add name (previous @ [ candidate.Definition ]) found) data.History
            let finalState = { proposed with History = history }
            validateGraph (durableState finalState)
            persist finalState
            data <- finalState
            lastResults <- results
            results

        let readString (node: JsonObject) (key: string) (defaultValue: string) =
            match node[key] with
            | null -> defaultValue
            | value -> try value.GetValue<string>() with _ -> defaultValue

        let readBool (node: JsonObject) (key: string) (defaultValue: bool) =
            match node[key] with
            | null -> defaultValue
            | value -> try value.GetValue<bool>() with _ -> defaultValue

        let describeJson word =
            let words = effectiveWords data
            availableDescription data words word

        let resultList (kind: string) (text: string) (results: TestCaseResult list) (target: string option) =
            let array = JsonArray()
            results |> List.iter (fun result -> array.Add(resultJson result))
            let dataNode = JsonObject()
            dataNode["results"] <- array
            match target with Some word when (effectiveWords data).ContainsKey word -> dataNode["coverage"] <- coverageJson word (effectiveWords data) results | _ -> ()
            success kind text (Some dataNode)

        member _.ProjectDirectory = projectRoot
        member _.Capabilities = capabilities

        member _.Dispatch(operation: string, args: JsonObject) =
            try
                match operation with
                | "eval" ->
                    let code = readString args "code" ""
                    match Parser.parse "<eval>" code with
                    | Ok parsed when not (List.isEmpty parsed.Words && List.isEmpty parsed.Records && List.isEmpty parsed.Scalars && List.isEmpty parsed.Tests && List.isEmpty parsed.Examples) ->
                        registerParsed parsed (readBool args "temporary" false)
                        success "defined" "Definitions parsed, type checked, and staged as candidates." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                    | _ ->
                        match Parser.parseExpression "<eval>" code with
                        | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                        | Ok body ->
                            let words = effectiveWords data
                            let result, trace = executeExpression data words None virtualFiles body
                            virtualFiles <- trace.FileSystem
                            let values = JsonArray()
                            result |> List.iter (fun value -> values.Add(toJsonValue value))
                            let dataNode = JsonObject()
                            dataNode["stack"] <- values
                            dataNode["console"] <- jsonNode trace.Console
                            dataNode["effects"] <- jsonNode (trace.Effects |> Map.toSeq |> Map.ofSeq)
                            success "eval" (result |> List.map Types.formatValue |> String.concat " ") (Some dataNode)
                | "define" ->
                    let source = readString args "source" (readString args "code" "")
                    match Parser.parse "<definition>" source with
                    | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                    | Ok parsed ->
                        registerParsed parsed (readBool args "temporary" false)
                        success "defined" "Definitions parsed, type checked, and staged." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                | "words" ->
                    let words = effectiveWords data
                    let entries = words |> Map.toList |> List.map snd |> List.filter (fun item -> item.Status <> Primitive || item.Builtin.IsSome)
                    let array = JsonArray()
                    entries |> List.sortBy (fun item -> item.Definition.Name) |> List.iter (fun item ->
                        log "inspect" item.Definition.Name
                        let value = JsonObject()
                        value["name"] <- jstr item.Definition.Name
                        value["inputs"] <- jsonNode (item.Definition.Inputs |> List.map Types.format)
                        value["outputs"] <- jsonNode (item.Definition.Outputs |> List.map Types.format)
                        value["effects"] <- jsonNode (item.Definition.Effects |> Set.toList)
                        value["status"] <- jstr (match item.Status with Primitive -> "primitive" | Candidate -> "candidate" | Temporary -> "temporary" | Persistent -> "persistent")
                        value["maturity"] <- jstr (if item.Maturity = LibraryWord then "library" else "project")
                        array.Add value)
                    let payload = JsonObject()
                    payload["words"] <- array
                    success "words" $"{entries.Length} word(s)." (Some payload)
                | "describe" ->
                    let name = readString args "word" ""
                    success "describe" $"Description for {name}." (Some(describeJson name))
                | "search" ->
                    let query = readString args "query" (readString args "text" "")
                    let words = effectiveWords data
                    let matches =
                        words |> Map.toList |> List.map snd |> List.filter (fun item ->
                            item.Definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Definition.Documentation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            (item.Definition.Inputs @ item.Definition.Outputs |> List.exists (fun ty -> (Types.format ty).Contains(query, StringComparison.OrdinalIgnoreCase))))
                    let result = JsonArray()
                    matches |> List.sortBy (fun item -> item.Definition.Name) |> List.iter (fun item -> result.Add(jstr item.Definition.Name))
                    success "search" $"{matches.Length} match(es)." (Some(result :> JsonNode))
                | "source" ->
                    let name = readString args "word" ""
                    let words = effectiveWords data
                    match words.TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some item ->
                        log "inspect" name
                        let inputText = String.concat " " (item.Definition.Inputs |> List.map Types.format)
                        let outputText = String.concat " " (item.Definition.Outputs |> List.map Types.format)
                        let source =
                            match item.Builtin with
                            | Some(BuiltinOp _) -> $"primitive {name} : {inputText} -> {outputText}"
                            | Some _ -> sourceForAgent item.Definition.SourceText
                            | None -> sourceForAgent item.Definition.SourceText
                        success "source" source (Some(jstr source))
                | "dependencies" ->
                    let name = readString args "word" ""
                    let words = effectiveWords data
                    match words.TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some item ->
                        log "inspect" name
                        let deps = if item.Builtin.IsSome then Set.empty else Compiler.dependencies item.Definition.Body
                        let payload = JsonObject()
                        payload["dependencies"] <- jsonNode (deps |> Set.toList)
                        let callers = userWords data |> Map.toList |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None) |> List.sort
                        payload["callers"] <- jsonNode callers
                        success "dependencies" $"{deps.Count} direct dependency/dependencies." (Some payload)
                | "callers" ->
                    let name = readString args "word" ""
                    let callers = userWords data |> Map.toList |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None) |> List.sort
                    success "callers" $"{callers.Length} caller(s)." (Some(jsonNode callers))
                | "effects" ->
                    let name = readString args "word" ""
                    match (effectiveWords data).TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some value -> success "effects" $"Effects for {name}." (Some(jsonNode (value.Definition.Effects |> Set.toList)))
                | "ir" ->
                    let name = readString args "word" ""
                    match (effectiveWords data).TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some value when value.Builtin.IsSome -> success "ir" $"{name} is a trusted host primitive." (Some(jstr "primitive"))
                    | Some value ->
                        let body = Compiler.sourceExpressions value.Definition.Body
                        success "ir" "Checked expression tree (interpreted directly; no separate bytecode exists yet)." (Some(jstr body))
                | "tests" ->
                    let name = readString args "word" ""
                    let tests = data.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name) |> List.sortBy (fun test -> test.Name)
                    success "tests" $"{tests.Length} attached test(s)." (Some(jsonNode (tests |> List.map (fun test -> test.Name))))
                | "examples" ->
                    let name = readString args "word" ""
                    let examples = data.Examples |> Map.toList |> List.map snd |> List.filter (fun example -> example.Word = name) |> List.sortBy (fun example -> example.Name)
                    success "examples" $"{examples.Length} example(s)." (Some(jsonNode (examples |> List.map (fun example -> example.Name))))
                | "test" ->
                    let target = readString args "word" ""
                    let words = effectiveWords data
                    let results = runTestsFor data words (if target = "" then None else Some target)
                    lastResults <- results
                    recordTestResults results
                    resultList "test" $"{results |> List.filter (fun item -> item.Passed) |> List.length}/{results.Length} test(s) passed." results (if target = "" then None else Some target)
                | "test-all" ->
                    let words = effectiveWords data
                    let results = runTestsFor data words None
                    lastResults <- results
                    recordTestResults results
                    resultList "test" $"{results |> List.filter (fun item -> item.Passed) |> List.length}/{results.Length} test(s) passed." results None
                | "failed-tests" ->
                    let words = effectiveWords data
                    let allResults = runTestsFor data words None
                    recordTestResults allResults
                    let results = allResults |> List.filter (fun result -> not result.Passed)
                    resultList "failed-tests" $"{results.Length} failing test(s) on current dictionary." results None
                | "commit" | "commit-word" | "task.commit" ->
                    let name = readString args "word" ""
                    let library = readBool args "library" false
                    let hasCandidates =
                        (data.Words |> Map.exists (fun _ value -> value.Status = Candidate))
                        || (data.Records |> Map.exists (fun _ value -> value.Status = Candidate))
                        || (data.Scalars |> Map.exists (fun _ value -> value.Status = Candidate))
                    if operation = "task.commit" && not (activeTask |> Option.exists (fun task -> task.Active)) then
                        error "TASK_NOT_ACTIVE" "No active task can be committed." None None [] []
                    else
                        let results =
                            if operation = "task.commit" && not hasCandidates then []
                            else commitCandidates (if name = "" then None else Some name) library
                        if operation = "task.commit" then
                            match activeTask with
                            | Some task ->
                                cleanupTaskTemporaries task
                                task.Active <- false
                                saveTaskLog task
                                success "task.commit" "Task committed, temporary words were cleared, and the task log was saved." (Some(makeTaskJson task))
                            | None -> error "TASK_NOT_ACTIVE" "No active task can be committed." None None [] []
                        else success "commit" $"{results.Length} attached test(s) passed; selected candidates committed." (Some(jsonNode (results |> List.map (fun value -> $"{value.Word}/{value.Name}"))))
                | "promote" ->
                    let name = readString args "word" ""
                    match data.Words.TryFind name with
                    | Some item when item.Status = Temporary -> data <- { data with Words = Map.add name { item with Status = Candidate } data.Words }; lastResults <- []; success "promote" $"Promoted '{name}' to a candidate." None
                    | _ -> error "PROMOTE_NOT_TEMPORARY" $"'{name}' is not a temporary word." (Some name) None [] []
                | "discard" ->
                    let name = readString args "word" ""
                    match data.Words.TryFind name with
                    | Some item when item.Status = Temporary || item.Status = Candidate ->
                        let proposed =
                            match data.Replacements.TryFind name with
                            | Some backup ->
                                let tests =
                                    data.Tests
                                    |> Map.filter (fun _ test -> test.Word <> name)
                                    |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Tests
                                let examples =
                                    data.Examples
                                    |> Map.filter (fun _ example -> example.Word <> name)
                                    |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Examples
                                { data with
                                    Words = Map.add name backup.Word data.Words
                                    Tests = tests
                                    Examples = examples
                                    Replacements = Map.remove name data.Replacements }
                            | None ->
                                { data with
                                    Words = Map.remove name data.Words
                                    Tests = data.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                    Examples = data.Examples |> Map.filter (fun _ example -> example.Word <> name) }
                        validateGraph proposed
                        data <- proposed
                        lastResults <- []
                        success "discard" $"Discarded staged word '{name}'." None
                    | None when data.Records.TryFind name |> Option.exists (fun value -> value.Status = Candidate) ->
                        let prefix = lowerFirst name
                        let owns generated = generated = prefix + ".new" || generated = prefix + ".value" || generated.StartsWith(prefix + ".", StringComparison.Ordinal)
                        let proposed =
                            { data with
                                Records = Map.remove name data.Records
                                Tests = data.Tests |> Map.filter (fun _ test -> not (owns test.Word))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (owns example.Word)) }
                        validateGraph proposed
                        data <- proposed
                        lastResults <- []
                        success "discard" $"Discarded candidate type '{name}'." None
                    | None when data.Scalars.TryFind name |> Option.exists (fun value -> value.Status = Candidate) ->
                        let prefix = name
                        let owns generated = generated = prefix + ".new" || generated = prefix + ".value"
                        let proposed =
                            { data with
                                Scalars = Map.remove name data.Scalars
                                Tests = data.Tests |> Map.filter (fun _ test -> not (owns test.Word))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (owns example.Word)) }
                        validateGraph proposed
                        data <- proposed
                        lastResults <- []
                        success "discard" $"Discarded candidate scalar type '{name}'." None
                    | _ -> error "DISCARD_NOT_STAGED" $"'{name}' is not staged." (Some name) None [] []
                | "task.begin" ->
                    match activeTask with
                    | Some task when task.Active -> error "TASK_ALREADY_ACTIVE" "A task is already active." (Some task.Id) None [] []
                    | _ ->
                        let taskNumber = nextTaskNumber ()
                        let task = { Id = $"task-{taskNumber:D4}"; Goal = readString args "goal" ""; Snapshot = data; Active = true; Inspected = Set.empty; Used = Set.empty; Created = Set.empty; TestsRun = 0; TestsFailed = 0; EffectCounts = Map.empty; Errors = [] }
                        activeTask <- Some task
                        success "task.begin" $"Started {task.Id}." (Some(makeTaskJson task))
                | "task.status" ->
                    match activeTask with Some task -> success "task.status" $"Status for {task.Id}." (Some(makeTaskJson task)) | None -> success "task.status" "No active task." (previousTaskLog |> Option.map (fun value -> value :> JsonNode))
                | "task.log" ->
                    match activeTask with Some task -> success "task.log" $"Log for {task.Id}." (Some(makeTaskJson task)) | None -> success "task.log" "Most recent task log." (previousTaskLog |> Option.map (fun value -> value :> JsonNode))
                | "task.abort" ->
                    match activeTask with
                    | Some task when task.Active ->
                        let snap = task.Snapshot
                        persist snap
                        data <- snap
                        task.Active <- false
                        lastResults <- []
                        saveTaskLog task
                        success "task.abort" $"Aborted {task.Id}; dictionary changes were rolled back." (Some(makeTaskJson task))
                    | _ -> error "TASK_NOT_ACTIVE" "No active task can be aborted." None None [] []
                | "history" ->
                    let name = readString args "word" ""
                    let revisions = data.History.TryFind name |> Option.defaultValue []
                    let payload = revisions |> List.mapi (fun index definition -> {| revision = index + 1; source = definition.SourceText |})
                    success "history" $"{revisions.Length} revision(s) for {name}." (Some(jsonNode payload))
                | "diff" ->
                    let name = readString args "word" ""
                    let first = try (args["from"].GetValue<int>() - 1) with _ -> -1
                    let second = try (args["to"].GetValue<int>() - 1) with _ -> -1
                    let revisions = data.History.TryFind name |> Option.defaultValue []
                    if first < 0 || second < 0 || first >= revisions.Length || second >= revisions.Length then error "HISTORY_REVISION_UNKNOWN" "Requested word revision does not exist." (Some name) None [] []
                    else
                        let a = revisions[first].SourceText.Split('\n')
                        let b = revisions[second].SourceText.Split('\n')
                        let removed = String.concat "\n- " a
                        let added = String.concat "\n+ " b
                        let text = $"revision {first + 1} -> {second + 1}\n- {removed}\n+ {added}"
                        success "diff" text (Some(jstr text))
                | "stack" -> success "stack" "Evaluation is stateless; each eval begins with an empty stack." (Some(jsonNode ([]: string list)))
                | _ -> error "PROTOCOL_UNKNOWN_OPERATION" $"Unknown operation '{operation}'." None None [] [ operation ]
            with
            | LanguageException diagnostic ->
                log "error" diagnostic.Code
                response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
            | ex ->
                let diagnostic = { Code = "HOST_FAILURE"; Message = ex.Message; Word = None; Span = None; Expected = []; Actual = [] }
                log "error" diagnostic.Code
                response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
