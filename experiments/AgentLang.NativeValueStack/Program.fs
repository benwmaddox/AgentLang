module AgentLang.NativeValueStack.Program

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

type private EntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      Initialize: VerifiedIrBody
      TurnOne: VerifiedIrBody
      TurnEight: VerifiedIrBody
      Branch: VerifiedIrBody
      UnitValue: VerifiedIrBody
      UnitDrop: VerifiedIrBody
      EmptyValue: VerifiedIrBody
      EmptyIdentity: VerifiedIrBody
      ScopedWidth: VerifiedIrBody
      ScopeShadow: VerifiedIrBody
      Depth64: VerifiedIrBody
      Depth65: VerifiedIrBody
      DirectDupDrop: VerifiedIrBody
      StableDeadOnlySixLocals: VerifiedIrBody
      StableEscapingScopeResult: VerifiedIrBody
      StableBranchEscapingScopeResult: VerifiedIrBody
      FailAfterAllocation: VerifiedIrBody }

type private StringEntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      SourceOrigins: Map<SourceSpan, SourceSpan>
      TurnText: VerifiedIrBody
      IdentityString: VerifiedIrBody
      JoinStrings: VerifiedIrBody
      DirectConcat: VerifiedIrBody
      MixedConstruct: VerifiedIrBody
      MixedProjectTextAndSentinel: VerifiedIrBody
      MixedProjectEmptyMarker: VerifiedIrBody
      DynamicBranch: VerifiedIrBody
      FailAfterTextAllocation: VerifiedIrBody
      DirectDupDrop: VerifiedIrBody
      ScopeShadow: VerifiedIrBody
      ZeroOutputUserCall: VerifiedIrBody
      StableDeadOnlySixLocals: VerifiedIrBody
      StableEscapingScopeResult: VerifiedIrBody
      StableBindingRoundTrip: VerifiedIrBody
      StableUncertainCallScope: VerifiedIrBody }

type private EnumEntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      SourceOrigins: Map<SourceSpan, SourceSpan>
      Cases: string list
      Constructors: Map<string, VerifiedIrBody>
      Matches: Map<string, VerifiedIrBody>
      Equalities: Map<string * string, VerifiedIrBody>
      LocalCallRoundTrip: VerifiedIrBody
      MakeFixedRecord: VerifiedIrBody
      ProjectFixedRecord: VerifiedIrBody
      MakeDynamicRecords: Map<string, VerifiedIrBody>
      ProjectDynamicRecord: VerifiedIrBody
      IgnoreEnum: VerifiedIrBody
      IgnoreFixedRecord: VerifiedIrBody
      IgnoreDynamicRecord: VerifiedIrBody }

type private SumEntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      SourceOrigins: Map<SourceSpan, SourceSpan>
      Cases: Map<string, VerifiedIrBody>
      IdentityBodies: Map<string, VerifiedIrBody>
      LocalCallBodies: Map<string, VerifiedIrBody>
      Equalities: Map<int, VerifiedIrBody>
      OptionIntMatch: VerifiedIrBody
      ResultIntStringMatch: VerifiedIrBody
      ResultStringIntMatch: VerifiedIrBody
      ResultStringIntErrorMatch: VerifiedIrBody
      OptionStringMatch: VerifiedIrBody
      NestedMatch: VerifiedIrBody
      NestedErrorIntMatch: VerifiedIrBody
      BranchJoin: VerifiedIrBody
      DuplicateDrop: VerifiedIrBody
      TailChoice: VerifiedIrBody
      TailProjection: VerifiedIrBody
      RawIgnoreBodies: Map<string, VerifiedIrBody>
      FailAfterSumAllocation: VerifiedIrBody }

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawOwningStackContext =
    val mutable AbiVersion: uint32
    val mutable StackCapacityBytes: uint32
    val mutable CursorBytes: uint32
    val mutable PeakCursorBytes: uint32
    val mutable LivePayloadBytes: uint32
    val mutable PeakLivePayloadBytes: uint32
    val mutable ActiveLocalReservedBytes: uint32
    val mutable PeakLocalReservedBytes: uint32
    val mutable LiveLocalPayloadBytes: uint32
    val mutable PeakLiveLocalPayloadBytes: uint32
    val mutable StepsConsumed: uint32
    val mutable TraceEventCount: uint32
    val mutable TraceEventCapacity: uint32
    val mutable TraceTruncated: uint32
    val mutable DuplicateDisjointChecks: uint32
    val mutable DropSurvivorChecks: uint32
    val mutable PoisonReuseChecks: uint32
    val mutable CursorInvariantChecks: uint32
    val mutable FrameReturnCount: uint32
    val mutable Status: uint32
    val mutable ErrorId: uint32
    val mutable RequiredBytes: uint32
    val mutable AvailableBytes: uint32
    val mutable InitBitmapBytes: uint32
    val mutable CallDepth: uint32
    val mutable StackData: nativeint
    val mutable InitBitmap: nativeint
    val mutable PoisonBitmap: nativeint
    val mutable TraceEvents: nativeint
    val mutable DeepCopyBytes: uint64
    val mutable MoveBytes: uint64
    val mutable InputCopyBytes: uint64
    val mutable RetainedCopyBytes: uint64

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RawOwningExecuteDelegate = delegate of nativeint * nativeint * uint32 * nativeint * uint32 * nativeint * uint32 -> int32

type private RawOwningInvocation =
    { NativeStatus: int32
      ContextStatus: uint32
      RetainedOutput: byte array }

type private LayoutDepthCase =
    { Program: VerifiedIrProgram
      Body: VerifiedIrBody
      RecordLevels: int
      LayoutLevels: int
      TopTypeName: string }

let private span file column =
    { File = file
      Line = 1
      Column = column
      Length = 1 }

let private lowerFirst (name: string) =
    if String.IsNullOrEmpty name then name
    else string (Char.ToLowerInvariant name[0]) + name.Substring(1)

let private generatedRecordEntries (records: Map<string, RecordDefinition>) =
    records
    |> Map.toList
    |> List.collect (fun (name, record) ->
        let prefix = lowerFirst name
        let definition wordName inputs outputs =
            { Name = wordName
              Inputs = inputs
              Outputs = outputs
              Effects = Set.empty
              Maturity = LibraryWord
              Revision = 1
              Documentation = "Generated record operation."
              Body = []
              SourceText = ""
              Span = span "<generated-native-value-stack-record>" 1 }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let constructor =
            entry (RecordConstructor name) (prefix + ".new") (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ]
        let accessors =
            record.Fields
            |> List.map (fun field ->
                entry (RecordAccessor(name, field.Name)) (prefix + "." + field.Name) [ TNamed name ] [ field.Type ])
        constructor :: accessors)

let private generatedEnumEntries (enums: Map<string, EnumDefinition>) =
    enums
    |> Map.toList
    |> List.collect (fun (name, definition) ->
        definition.Cases
        |> List.map (fun caseName ->
            let wordName = name + "." + caseName
            let wordDefinition =
                { Name = wordName
                  Inputs = []
                  Outputs = [ TNamed name ]
                  Effects = Set.empty
                  Maturity = LibraryWord
                  Revision = 1
                  Documentation = "Generated enum case constructor."
                  Body = []
                  SourceText = ""
                  Span = span "<generated-native-value-stack-enum>" 1 }
            { Definition = wordDefinition
              Builtin = Some(EnumCaseConstructor(name, caseName))
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }))

let private turnSource repetition =
    let repeatedCalls =
        [ 1 .. repetition ]
        |> List.map (fun _ -> "    mailbox::exercise(value);")
        |> String.concat "\n"
    $"""fn mailbox.turn-{repetition}(state: State, value: Int) -> State {{
    effects none
    let returned = mailbox::make-envelope(value, add(value, 1000));
{repeatedCalls}
    let observed = returned.leaf.value;
    let increment = if equals(observed, value) {{ 1 }} else {{ 2 }};
    state::new(count = add(state.count, increment), last = returned)
}}"""

let private depthChainSource maxDepth =
    [ 0 .. maxDepth ]
    |> List.map (fun depth ->
        let body = if depth = 0 then "value" else $"mailbox::depth-{depth - 1}(value)"
        $"""fn mailbox.depth-{depth}(value: Int) -> Int {{
    effects none
    {body}
}}""")
    |> String.concat "\n\n"

let private compileFlowProgram (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 "value-stack.flow" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records =
        document.Records
        |> List.map (fun record -> record.Name, record)
        |> Map.ofList
        |> Map.add "Empty"
            { Name = "Empty"
              Fields = []
              Validator = None
              SourceText = "host-built conformance record; Flow source syntax rejects empty records"
              Span = span "<native-value-stack-empty-record>" 1 }
    let enums = document.Enums |> List.map (fun definition -> definition.Name, definition) |> Map.ofList
    let words =
        (generatedRecordEntries records @ generatedEnumEntries enums)
        |> List.fold (fun current entry -> Map.add entry.Definition.Name entry current) Compiler.primitives
    let wordIds =
        words
        |> Map.toList
        |> List.map (fun (name, entry) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    let compilerContext: Compiler.IrLoweringContext =
        { Words = words
          Records = records
          Scalars = Map.empty
          Enums = enums
          WordIds = wordIds }
    let context: FlowLowering.Context =
        { CompilerContext = compilerContext
          ParameterNames = Map.empty
          Flow2OwnerIds = Set.empty
          SourceOrigins = Map.empty }
    let changes: FlowLowering.FlowWordChange list =
        document.Words
        |> List.map (fun definition ->
            { FlowLowering.FlowWordChange.Definition = definition
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId("native-value-stack-source-" + definition.Name), 1) })
    let compiled = FlowLowering.compileBatchWords context changes
    compiled

let private compileEntries (source: string) =
    let compiled = compileFlowProgram source
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, span ("<" + entryName + ">") 1) ]
            compiled.Context.SourceOrigins
    let entries =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          Initialize = compileEntry "mailbox.initialize.entry" "mailbox.initialize" []
          TurnOne = compileEntry "mailbox.turn-1.entry" "mailbox.turn-1" [ TNamed "State"; TInt ]
          TurnEight = compileEntry "mailbox.turn-8.entry" "mailbox.turn-8" [ TNamed "State"; TInt ]
          Branch = compileEntry "mailbox.branch.entry" "mailbox.branch" [ TInt; TBool ]
          UnitValue = compileEntry "mailbox.unit-value.entry" "mailbox.unit-value" []
          UnitDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-unit-drop"
                []
                [ Push(LUnit, span "<native-value-stack-unit-drop>" 1)
                  Call("drop", span "<native-value-stack-unit-drop>" 2)
                  Push(LInt 123L, span "<native-value-stack-unit-drop>" 3) ]
                compiled.Context.SourceOrigins
          EmptyValue = compileEntry "mailbox.empty-value.entry" "mailbox.empty-value" []
          EmptyIdentity =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-empty-record-identity"
                [ TNamed "Empty" ]
                []
                compiled.Context.SourceOrigins
          ScopedWidth = compileEntry "mailbox.scoped-width.entry" "mailbox.scoped-width" [ TInt ]
          Depth64 = compileEntry "mailbox.depth-64.entry" "mailbox.depth-64" [ TInt ]
          Depth65 = compileEntry "mailbox.depth-65.entry" "mailbox.depth-65" [ TInt ]
          ScopeShadow =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-scope-local-shadow"
                []
                [ Push(LInt 17L, span "<native-value-stack-scope-local-shadow>" 1)
                  Push(LInt 700L, span "<native-value-stack-local-shadow>" 2)
                  Call("mailbox.make-envelope", span "<native-value-stack-local-shadow>" 3)
                  Let("shadow", span "<native-value-stack-local-shadow>" 4)
                  Scope(
                    [ Push(LInt 99L, span "<native-value-stack-local-shadow>" 5)
                      Call("leaf.new", span "<native-value-stack-local-shadow>" 6)
                      Let("shadow", span "<native-value-stack-local-shadow>" 7)
                      Load("shadow", span "<native-value-stack-local-shadow>" 8)
                      Call("leaf.value", span "<native-value-stack-local-shadow>" 9)
                      Call("drop", span "<native-value-stack-local-shadow>" 10) ],
                    span "<native-value-stack-local-shadow>" 11)
                  Load("shadow", span "<native-value-stack-local-shadow>" 12) ]
                compiled.Context.SourceOrigins
          DirectDupDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-direct-dup-drop"
                [ TInt; TInt ]
                [ Call("mailbox.make-envelope", span "<native-value-stack-direct-dup-drop>" 1)
                  Call("dup", span "<native-value-stack-direct-dup-drop>" 2)
                  Call("drop", span "<native-value-stack-direct-dup-drop>" 3) ]
                compiled.Context.SourceOrigins
          StableDeadOnlySixLocals =
            let localNames = [ "stable0"; "stable1"; "stable2"; "stable3"; "stable4"; "stable5" ]
            let site label column = span ("<native-value-stack-stable-dead-six-" + label + ">") column
            let deadInnerScope =
                Scope(
                    [ Push(LInt 777L, site "temporary" 1)
                      Call("leaf.new", site "construct-temporary" 2)
                      Call("drop", site "drop-temporary" 3) ],
                    site "inner-scope" 4)
            let allocationAfterRewind =
                [ Push(LInt 888L, site "reuse-value" 5)
                  Call("leaf.new", site "reuse-construction" 6)
                  Call("drop", site "drop-reused-value" 7) ]
            let localStores = localNames |> List.mapi (fun index name -> Let(name, site ("store-" + name) (index + 1)))
            let localLoadsAndDrops =
                localNames
                |> List.mapi (fun index name ->
                    [ Load(name, site ("load-" + name) (index + 10))
                      Call("drop", site ("drop-" + name) (index + 20)) ])
                |> List.concat
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-stable-dead-only-six-locals"
                [ TInt; TInt; TInt; TInt; TInt; TInt ]
                [ Scope(localStores @ [ deadInnerScope ] @ allocationAfterRewind @ localLoadsAndDrops, site "outer-scope" 30) ]
                compiled.Context.SourceOrigins
          StableEscapingScopeResult =
            let site label column = span ("<native-value-stack-stable-escaping-result-" + label + ">") column
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-stable-escaping-scope-result"
                [ TInt; TInt ]
                [ Scope(
                    [ Push(LInt 99L, site "temporary-value" 1)
                      Call("leaf.new", site "temporary-leaf" 2)
                      Call("drop", site "drop-temporary" 3)
                      Call("mailbox.make-envelope", site "escaping-result" 4) ],
                    site "inner-scope" 5) ]
                compiled.Context.SourceOrigins
          StableBranchEscapingScopeResult =
            let site label column = span ("<native-value-stack-stable-branch-result-" + label + ">") column
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-stable-branch-escaping-scope-result"
                [ TInt; TBool ]
                [ Scope(
                    [ If(
                        [ Push(LInt 700L, site "left-tag" 1)
                          Call("mailbox.make-envelope", site "left-envelope" 2) ],
                        [ Push(LInt 800L, site "right-tag" 3)
                          Call("mailbox.make-envelope", site "right-envelope" 4) ],
                        site "branch" 5) ],
                    site "outer-scope" 6) ]
                compiled.Context.SourceOrigins
          FailAfterAllocation = compileEntry "mailbox.fail-after-allocation.entry" "mailbox.fail-after-allocation" [ TNamed "State"; TInt ] }
    let bodies =
        [ entries.Initialize; entries.TurnOne; entries.TurnEight; entries.Branch; entries.UnitValue; entries.UnitDrop; entries.EmptyValue; entries.EmptyIdentity; entries.ScopedWidth; entries.Depth64; entries.Depth65; entries.ScopeShadow; entries.DirectDupDrop; entries.StableDeadOnlySixLocals; entries.StableEscapingScopeResult; entries.StableBranchEscapingScopeResult; entries.FailAfterAllocation ]
    if not (VerifiedIrProgram.isBackendExecutable entries.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, entries.Program))) then
        invalidOp "Every comparison body must share the exact VerifiedIrProgram instance."
    entries

let private compileEnumEntries () =
    let source =
        """enum Signal {
    case off;
    case on;
    case alarm;
}

record SignalPacket {
    field signal: Signal;
    field sequence: Int;
}

record TextSignalPacket {
    field note: String;
    field signal: Signal;
    field sequence: Int;
}

fn signal.identity(value: Signal) -> Signal {
    effects none
    value
}

fn signal.describe(value: Signal) -> Int {
    effects none
    match value {
        off => { 10 }
        on => { 20 }
        alarm => { 30 }
    }
}"""
    let compiled = compileFlowProgram source
    let site name column = span ("<native-value-stack-enum-" + name + ">") column
    let compileBody name inputTypes expressions =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            name
            inputTypes
            expressions
            compiled.Context.SourceOrigins
    let enumDefinition = compiled.Context.CompilerContext.Enums["Signal"]
    let cases = enumDefinition.Cases
    let constructors =
        cases
        |> List.map (fun caseName ->
            caseName,
            compileBody ("native-value-stack-enum-construct-" + caseName) []
                [ Call("Signal." + caseName, site ("construct-" + caseName) 1) ])
        |> Map.ofList
    let matches =
        cases
        |> List.map (fun caseName ->
            caseName,
            compileBody ("native-value-stack-enum-match-" + caseName) []
                [ Call("Signal." + caseName, site ("match-construct-" + caseName) 1)
                  Call("signal.describe", site ("match-call-" + caseName) 2) ])
        |> Map.ofList
    let equalities =
        [ for left in cases do
              for right in cases do
                  let body =
                      compileBody ($"native-value-stack-enum-equals-{left}-{right}") []
                          [ Call("Signal." + left, site ("equals-left-" + left + "-" + right) 1)
                            Call("Signal." + right, site ("equals-right-" + left + "-" + right) 2)
                            Call("equals", site ("equals-" + left + "-" + right) 3) ]
                  yield (left, right), body ]
        |> Map.ofList
    let dynamicConstructors =
        cases
        |> List.map (fun caseName ->
            caseName,
            compileBody ("native-value-stack-text-signal-construct-" + caseName) []
                [ Push(LString "x", site ("text-" + caseName) 1)
                  Call("Signal." + caseName, site ("text-case-" + caseName) 2)
                  Push(LInt 99L, site ("text-sequence-" + caseName) 3)
                  Call("textSignalPacket.new", site ("text-record-" + caseName) 4) ])
        |> Map.ofList
    let bodies =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          SourceOrigins = compiled.Context.SourceOrigins
          Cases = cases
          Constructors = constructors
          Matches = matches
          Equalities = equalities
          LocalCallRoundTrip =
            compileBody "native-value-stack-enum-local-call-round-trip" [ TNamed "Signal" ]
                [ Let("saved", site "local-store" 1)
                  Load("saved", site "local-load" 2)
                  Call("signal.identity", site "identity-call" 3) ]
          MakeFixedRecord =
            compileBody "native-value-stack-enum-fixed-record-construction" [ TNamed "Signal"; TInt ]
                [ Call("signalPacket.new", site "fixed-record-construction" 1) ]
          ProjectFixedRecord =
            compileBody "native-value-stack-enum-fixed-record-projection" [ TNamed "SignalPacket" ]
                [ Call("signalPacket.signal", site "fixed-record-projection" 1) ]
          MakeDynamicRecords = dynamicConstructors
          ProjectDynamicRecord =
            compileBody "native-value-stack-enum-dynamic-record-projection" [ TNamed "TextSignalPacket" ]
                [ Call("textSignalPacket.signal", site "dynamic-record-projection" 1) ]
          IgnoreEnum =
            compileBody "native-value-stack-enum-unused-input" [ TNamed "Signal" ]
                [ Call("drop", site "unused-enum-drop" 1)
                  Push(LInt 5L, site "unused-enum-result" 2) ]
          IgnoreFixedRecord =
            compileBody "native-value-stack-enum-unused-fixed-record-input" [ TNamed "SignalPacket" ]
                [ Call("drop", site "unused-fixed-drop" 1)
                  Push(LInt 5L, site "unused-fixed-result" 2) ]
          IgnoreDynamicRecord =
            compileBody "native-value-stack-enum-unused-dynamic-record-input" [ TNamed "TextSignalPacket" ]
                [ Call("drop", site "unused-dynamic-drop" 1)
                  Push(LInt 5L, site "unused-dynamic-result" 2) ] }
    let bodiesToCheck =
        [ yield! constructors |> Map.toList |> List.map snd
          yield! matches |> Map.toList |> List.map snd
          yield! equalities |> Map.toList |> List.map snd
          yield! dynamicConstructors |> Map.toList |> List.map snd
          yield bodies.LocalCallRoundTrip
          yield bodies.MakeFixedRecord
          yield bodies.ProjectFixedRecord
          yield bodies.ProjectDynamicRecord
          yield bodies.IgnoreEnum
          yield bodies.IgnoreFixedRecord
          yield bodies.IgnoreDynamicRecord ]
    if not (VerifiedIrProgram.isBackendExecutable bodies.Program)
       || bodiesToCheck |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, bodies.Program))) then
        invalidOp "Every enum conformance comparison must share one backend-authorized VerifiedIrProgram instance."
    bodies

let private compileStringEntries (source: string) =
    let flowCompiled = compileFlowProgram source
    let helperSpan = span "<native-value-stack-zero-output-helper>" 1
    let discardDefinition: WordDefinition =
        { Name = "mailbox.discard-text-envelope"
          Inputs = [ TNamed "TextEnvelope" ]
          Outputs = []
          Effects = Set.empty
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Compiler-minted no-output helper used by the owning-stack return conformance case."
          Body = [ Call("drop", helperSpan) ]
          SourceText = "compiler-minted zero-output helper"
          Span = helperSpan }
    let discardEntry: WordEntry =
        { Definition = discardDefinition
          Builtin = None
          Status = Persistent
          Maturity = LibraryWord
          Revision = 1 }
    let discardName = discardDefinition.Name
    let uncertainIdentityDefinition: WordDefinition =
        { Name = "mailbox.uncertain-identity"
          Inputs = [ TString ]
          Outputs = [ TString ]
          Effects = Set.empty
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Conformance target with an intentionally unavailable provenance summary."
          Body = []
          SourceText = "compiler-minted uncertain identity"
          Span = span "<native-value-stack-uncertain-identity>" 1 }
    let uncertainIdentityEntry: WordEntry =
        { Definition = uncertainIdentityDefinition
          Builtin = None
          Status = Persistent
          Maturity = LibraryWord
          Revision = 1 }
    let compilerContext =
        { flowCompiled.Context.CompilerContext with
            Words = flowCompiled.Context.CompilerContext.Words |> Map.add discardName discardEntry |> Map.add uncertainIdentityDefinition.Name uncertainIdentityEntry
            WordIds = flowCompiled.Context.CompilerContext.WordIds |> Map.add discardName (WordId "native-value-stack-string-discard-text-envelope") |> Map.add uncertainIdentityDefinition.Name (WordId "native-value-stack-uncertain-identity") }
    let verifiedProgram = Compiler.compileIrProgramWithSourceOrigins compilerContext flowCompiled.Context.SourceOrigins
    let compiled =
        { flowCompiled with
            Program = verifiedProgram
            Context = { flowCompiled.Context with CompilerContext = compilerContext } }
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, span ("<" + entryName + ">") 1) ]
            compiled.Context.SourceOrigins
    let stringSpan name column = span ("<native-value-stack-string-" + name + ">") column
    let entries =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          SourceOrigins = compiled.Context.SourceOrigins
          TurnText = compileEntry "mailbox.turn-text.entry" "mailbox.turn-text" [ TNamed "TextState" ]
          IdentityString = compileEntry "mailbox.identity-string.entry" "mailbox.identity-string" [ TString ]
          JoinStrings = compileEntry "mailbox.join-strings.entry" "mailbox.join-strings" [ TString; TString ]
          DirectConcat =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-direct-string-concat-capacity"
                [ TString; TString ]
                [ Call("string.concat", stringSpan "direct-string-concat-capacity" 1) ]
                compiled.Context.SourceOrigins
          MixedConstruct = compileEntry "mailbox.make-text-mixed.entry" "mailbox.make-text-mixed" [ TString; TInt ]
          MixedProjectTextAndSentinel = compileEntry "mailbox.project-text-mixed.entry" "mailbox.project-text-mixed" [ TNamed "TextMixed" ]
          MixedProjectEmptyMarker = compileEntry "mailbox.project-text-mixed-marker.entry" "mailbox.project-text-mixed-marker" [ TNamed "TextMixed" ]
          DynamicBranch = compileEntry "mailbox.choose-text-envelope.entry" "mailbox.choose-text-envelope" [ TNamed "TextEnvelope"; TNamed "TextEnvelope"; TBool ]
          FailAfterTextAllocation = compileEntry "mailbox.fail-after-text-allocation.entry" "mailbox.fail-after-text-allocation" [ TNamed "TextState" ]
          DirectDupDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-direct-dup-drop"
                [ TNamed "TextEnvelope" ]
                [ Call("dup", stringSpan "direct-dup-drop" 1)
                  Call("drop", stringSpan "direct-dup-drop" 2) ]
                compiled.Context.SourceOrigins
          ScopeShadow =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-scope-shadow"
                [ TNamed "TextEnvelope"; TNamed "TextEnvelope" ]
                [ Call("swap", stringSpan "scope-shadow" 1)
                  Let("shadow", stringSpan "scope-shadow" 2)
                  Scope(
                    [ Let("shadow", stringSpan "scope-shadow" 3) ],
                    stringSpan "scope-shadow" 4)
                  Load("shadow", stringSpan "scope-shadow" 5) ]
                compiled.Context.SourceOrigins
          ZeroOutputUserCall =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-zero-output-user-call"
                [ TNamed "TextEnvelope" ]
                [ Call("mailbox.discard-text-envelope", stringSpan "zero-output-user-call" 1) ]
                compiled.Context.SourceOrigins
          StableDeadOnlySixLocals =
            let localNames = [ "string0"; "string1"; "string2"; "string3"; "string4"; "string5" ]
            let localStores = localNames |> List.mapi (fun index name -> Let(name, stringSpan ("stable-dead-store-" + name) (index + 1)))
            let deadInnerScope =
                Scope(
                    [ Load("string0", stringSpan "stable-dead-load-left" 10)
                      Load("string1", stringSpan "stable-dead-load-right" 11)
                      Call("string.concat", stringSpan "stable-dead-concat" 12)
                      Call("drop", stringSpan "stable-dead-drop" 13) ],
                    stringSpan "stable-dead-inner-scope" 14)
            let allocationAfterRewind =
                [ Push(LString "z", stringSpan "stable-dead-post-rewind-allocation" 15)
                  Call("drop", stringSpan "stable-dead-post-rewind-drop" 16) ]
            let localLoadsAndDrops =
                localNames
                |> List.mapi (fun index name ->
                    [ Load(name, stringSpan ("stable-dead-load-" + name) (index + 20))
                      Call("drop", stringSpan ("stable-dead-drop-" + name) (index + 30)) ])
                |> List.concat
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-stable-dead-only-six-locals"
                (List.replicate 6 TString)
                [ Scope(localStores @ [ deadInnerScope ] @ allocationAfterRewind @ localLoadsAndDrops, stringSpan "stable-dead-outer-scope" 40) ]
                compiled.Context.SourceOrigins
          StableEscapingScopeResult =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-stable-escaping-scope-result"
                []
                [ Scope(
                    [ Push(LString "dead", stringSpan "stable-escaping-temporary" 1)
                      Let("temporary", stringSpan "stable-escaping-store-temporary" 2)
                      Push(LString "a", stringSpan "stable-escaping-left" 3)
                      Push(LString "b", stringSpan "stable-escaping-right" 4)
                      Call("string.concat", stringSpan "stable-escaping-concat" 5) ],
                    stringSpan "stable-escaping-inner-scope" 6) ]
                compiled.Context.SourceOrigins
          StableBindingRoundTrip =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compilerContext
                verifiedProgram
                "native-value-stack-string-stable-binding-round-trip"
                []
                [ Scope(
                    [ Push(LString "stable", stringSpan "stable-binding-value" 1)
                      Let("saved", stringSpan "stable-binding-store" 2)
                      Load("saved", stringSpan "stable-binding-load" 3) ],
                    stringSpan "stable-binding-scope" 4) ]
                compiled.Context.SourceOrigins
          StableUncertainCallScope =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compilerContext
                verifiedProgram
                "native-value-stack-string-stable-uncertain-call-scope"
                [ TString ]
                [ Scope(
                    [ Call("mailbox.uncertain-identity", stringSpan "stable-uncertain-call" 1) ],
                    stringSpan "stable-uncertain-call-scope" 2) ]
                compiled.Context.SourceOrigins }
    let bodies =
        [ entries.TurnText
          entries.IdentityString
          entries.JoinStrings
          entries.DirectConcat
          entries.MixedConstruct
          entries.MixedProjectTextAndSentinel
          entries.MixedProjectEmptyMarker
          entries.DynamicBranch
          entries.FailAfterTextAllocation
          entries.DirectDupDrop
          entries.ScopeShadow
          entries.ZeroOutputUserCall
          entries.StableDeadOnlySixLocals
          entries.StableEscapingScopeResult
          entries.StableBindingRoundTrip
          entries.StableUncertainCallScope ]
    if not (VerifiedIrProgram.isBackendExecutable entries.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized String VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, entries.Program))) then
        invalidOp "Every String comparison body must share the exact VerifiedIrProgram instance."
    entries

let private compileTextStateFactory (entries: StringEntryBodies) (caseName: string) (value: Value) =
    let count, tag, codeUnits, text =
        match value with
        | RecordValue("TextState", state) ->
            match state["count"], state["last"] with
            | IntValue count, RecordValue("TextEnvelope", envelope) ->
                match envelope["tag"], envelope["leaf"] with
                | IntValue tag, RecordValue("TextLeaf", leaf) ->
                    match leaf["text"], leaf["codeUnits"] with
                    | StringValue text, IntValue codeUnits -> count, tag, codeUnits, text
                    | _ -> invalidOp "TextState fixture factory requires StringValue and IntValue leaf fields."
                | _ -> invalidOp "TextState fixture factory requires an Int tag and TextLeaf."
            | _ -> invalidOp "TextState fixture factory requires an Int count and TextEnvelope."
        | _ -> invalidOp "TextState fixture factory requires a TextState Value."
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-input-{caseName}")
        []
        [ Push(LInt count, site "input-state-count" 1)
          Push(LString text, site "input-string" 2)
          Push(LInt codeUnits, site "input-code-units" 3)
          Call("textLeaf.new", site "input-leaf" 4)
          Push(LInt tag, site "input-tag" 5)
          Call("textEnvelope.new", site "input-envelope" 6)
          Call("textState.new", site "input-state" 7) ]
        entries.SourceOrigins

let private compileTextEnvelopePairFactory (entries: StringEntryBodies) (caseName: string) (first: Value) (second: Value) =
    let envelopeParts = function
        | RecordValue("TextEnvelope", envelope) ->
            match envelope["tag"], envelope["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> tag, codeUnits, text
                | _ -> invalidOp "TextEnvelope fixture factory requires StringValue and IntValue leaf fields."
            | _ -> invalidOp "TextEnvelope fixture factory requires an Int tag and TextLeaf."
        | _ -> invalidOp "TextEnvelope fixture factory requires TextEnvelope Values."
    let firstTag, firstLength, firstText = envelopeParts first
    let secondTag, secondLength, secondText = envelopeParts second
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    let expressions =
        [ Push(LString firstText, site "first-text" 1)
          Push(LInt firstLength, site "first-length" 2)
          Call("textLeaf.new", site "first-leaf" 3)
          Push(LInt firstTag, site "first-tag" 4)
          Call("textEnvelope.new", site "first-envelope" 5)
          Push(LString secondText, site "second-text" 6)
          Push(LInt secondLength, site "second-length" 7)
          Call("textLeaf.new", site "second-leaf" 8)
          Push(LInt secondTag, site "second-tag" 9)
          Call("textEnvelope.new", site "second-envelope" 10) ]
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-envelope-pair-{caseName}")
        []
        expressions
        entries.SourceOrigins

let private compileStringPairFactory (entries: StringEntryBodies) (caseName: string) (left: string) (right: string) =
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-pair-{caseName}")
        []
        [ Push(LString left, site "left" 1); Push(LString right, site "right" 2) ]
        entries.SourceOrigins

let private compileStringFactory (entries: StringEntryBodies) (caseName: string) (value: string) =
    let site = span ($"<native-value-stack-string-{caseName}-literal>") 1
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-value-{caseName}")
        []
        [ Push(LString value, site) ]
        entries.SourceOrigins

let private compileTextMixedInputFactory (entries: StringEntryBodies) (caseName: string) (text: string) (sentinel: int64) =
    let site name column = span ($"<native-value-stack-mixed-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-mixed-input-{caseName}")
        []
        [ Push(LString text, site "text" 1); Push(LInt sentinel, site "sentinel" 2) ]
        entries.SourceOrigins

let private compileLayoutDepthCase recordLevels =
    let recordSource =
        [ 0 .. recordLevels - 1 ]
        |> List.map (fun depth ->
            let name = $"DepthNode{depth}"
            let field = if depth = 0 then "field value: Int;" else $"field next: DepthNode{depth - 1};"
            $"record {name} {{ {field} }}")
        |> String.concat "\n"
    let topType = $"DepthNode{recordLevels - 1}"
    let functionSource = $"""fn mailbox.layout-depth(value: {topType}) -> {topType} {{
    effects none
    value
}}"""
    let compiled = compileFlowProgram (String.concat "\n\n" [ recordSource; functionSource ])
    let body =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            $"native-value-stack-layout-depth-{recordLevels}"
            [ TNamed topType ]
            [ Call("mailbox.layout-depth", span ($"<layout-depth-{recordLevels}>") 1) ]
            compiled.Context.SourceOrigins
    { Program = compiled.Program
      Body = body
      RecordLevels = recordLevels
      LayoutLevels = recordLevels + 1
      TopTypeName = topType }

let private layoutDepthValue (layoutCase: LayoutDepthCase) =
    let mutable value = RecordValue("DepthNode0", Map.ofList [ "value", IntValue 42L ])
    for depth in 1 .. layoutCase.RecordLevels - 1 do
        value <- RecordValue($"DepthNode{depth}", Map.ofList [ "next", value ])
    value

let private noOpHost (sources: NativeDiagnosticSources) : IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
      WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
      PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }

let private hashBytes (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private hashText (value: string) =
    hashBytes (Encoding.UTF8.GetBytes value)

let private hashFile (path: string) =
    File.ReadAllBytes path |> hashBytes

let private getProperty (target: obj) (name: string) =
    if isNull target then null
    else
        let property = target.GetType().GetProperty(name)
        if isNull property then null else property.GetValue target

let private int64Property target name =
    let value = getProperty target name
    if isNull value then invalidOp $"Expected metric property '{name}' on {target.GetType().FullName}."
    Convert.ToInt64(value, CultureInfo.InvariantCulture)

let private optionalInt64 target name =
    let value = getProperty target name
    if isNull value then -1L else Convert.ToInt64(value, CultureInfo.InvariantCulture)

let private jsonNode (options: JsonSerializerOptions) (value: obj) =
    JsonNode.Parse(JsonSerializer.Serialize(value, options))

let private jsonObject properties =
    let result = Dictionary<string, obj>(StringComparer.Ordinal)
    for name, value in properties do result.Add(name, value)
    result

let private valueInt (element: JsonElement) (name: string) =
    element.GetProperty(name).GetInt64() |> IntValue

let private bytesFromInt64s (values: int64 list) =
    values
    |> List.collect (fun value -> BitConverter.GetBytes(value) |> Array.toList)
    |> List.toArray

let private bytesHex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

let private bytesFromHex (value: string) = Convert.FromHexString value

let private sumStringFromCodeUnitsHex (hex: string) =
    let bytes = bytesFromHex hex
    if bytes.Length % 2 <> 0 then invalidArg (nameof hex) "Sum fixture UTF-16 hex must contain complete code units."
    String(Array.init (bytes.Length / 2) (fun index -> char (uint16 bytes[index * 2] ||| (uint16 bytes[index * 2 + 1] <<< 8))))

let rec private parseSumLangType (typeName: string) =
    let typeName = typeName.Trim()
    if String.Equals(typeName, "Int", StringComparison.Ordinal) then TInt
    elif String.Equals(typeName, "Bool", StringComparison.Ordinal) then TBool
    elif String.Equals(typeName, "String", StringComparison.Ordinal) then TString
    elif String.Equals(typeName, "Unit", StringComparison.Ordinal) then TUnit
    elif typeName.StartsWith("Option<", StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal) then
        let inner = typeName.Substring(7, typeName.Length - 8)
        TOption(parseSumLangType inner)
    elif typeName.StartsWith("Result<", StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal) then
        let inner = typeName.Substring(7, typeName.Length - 8)
        let mutable depth = 0
        let mutable separator = -1
        for index in 0 .. inner.Length - 1 do
            match inner[index] with
            | '<' -> depth <- depth + 1
            | '>' -> depth <- depth - 1
            | ',' when depth = 0 && separator < 0 -> separator <- index
            | _ -> ()
        if separator < 0 then invalidOp $"Fixture Result type '{typeName}' has no top-level type separator."
        TResult(parseSumLangType (inner.Substring(0, separator)), parseSumLangType (inner.Substring(separator + 1)))
    else TNamed typeName

let rec private parseSumFixtureValue (element: JsonElement) =
    let declaredType = parseSumLangType (element.GetProperty("type").GetString())
    let parsePayload expectedType =
        let payload = element.GetProperty("payload")
        let actualType = parseSumLangType (payload.GetProperty("type").GetString())
        if actualType <> expectedType then
            invalidOp (sprintf "Fixture payload type %A does not match its declared sum payload type %A." actualType expectedType)
        parseSumFixtureValue payload
    match declaredType with
    | TInt -> IntValue(element.GetProperty("value").GetInt64())
    | TBool -> BoolValue(element.GetProperty("value").GetBoolean())
    | TString -> StringValue(sumStringFromCodeUnitsHex (element.GetProperty("codeUnitsHex").GetString()))
    | TUnit -> UnitValue
    | TOption itemType ->
        match element.GetProperty("case").GetString() with
        | "none" -> OptionValue(itemType, None)
        | "some" -> OptionValue(itemType, Some(parsePayload itemType))
        | caseName -> invalidOp $"Unknown Option fixture case '{caseName}'."
    | TResult(okType, errorType) ->
        match element.GetProperty("case").GetString() with
        | "ok" -> ResultValue(okType, errorType, Ok(parsePayload okType))
        | "error" -> ResultValue(okType, errorType, Error(parsePayload errorType))
        | caseName -> invalidOp $"Unknown Result fixture case '{caseName}'."
    | TNamed name ->
        let mutable enumCase = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty("case", &enumCase) then
            EnumValue(name, enumCase.GetString())
        else
            let fields =
                element.GetProperty("fields").EnumerateObject()
                |> Seq.map (fun field -> field.Name, parseSumFixtureValue field.Value)
                |> Map.ofSeq
            RecordValue(name, fields)
    | other -> invalidOp (sprintf "Unsupported type in sum fixture value: %A." other)

let rec private sumValueExpressions
    (compilerContext: Compiler.IrLoweringContext)
    (spanForColumn: int -> SourceSpan)
    (column: int)
    (value: Value) =
    let construct kind types = [ ConstructContainer(kind, types, spanForColumn column) ]
    match value with
    | IntValue number -> [ Push(LInt number, spanForColumn column) ]
    | BoolValue boolean -> [ Push(LBool boolean, spanForColumn column) ]
    | StringValue text -> [ Push(LString text, spanForColumn column) ]
    | UnitValue -> [ Push(LUnit, spanForColumn column) ]
    | EnumValue(name, caseName) -> [ Call(name + "." + caseName, spanForColumn column) ]
    | OptionValue(itemType, None) -> construct OptionNone [ itemType ]
    | OptionValue(itemType, Some item) ->
        sumValueExpressions compilerContext spanForColumn (column + 1) item
        @ construct OptionSome [ itemType ]
    | ResultValue(okType, errorType, Ok item) ->
        sumValueExpressions compilerContext spanForColumn (column + 1) item
        @ construct ResultOk [ okType; errorType ]
    | ResultValue(okType, errorType, Error item) ->
        sumValueExpressions compilerContext spanForColumn (column + 1) item
        @ construct ResultError [ okType; errorType ]
    | RecordValue(name, fields) ->
        let definition =
            compilerContext.Records.TryFind name
            |> Option.defaultWith (fun () -> invalidOp $"Sum fixture refers to record '{name}' missing from the VerifiedIrProgram context.")
        let arguments =
            definition.Fields
            |> List.collect (fun field ->
                let fieldValue =
                    fields.TryFind field.Name
                    |> Option.defaultWith (fun () -> invalidOp $"Sum fixture value omits {name}.{field.Name}.")
                sumValueExpressions compilerContext spanForColumn (column + 1) fieldValue)
        arguments @ [ Call(lowerFirst name + ".new", spanForColumn column) ]
    | other -> invalidOp $"Unsupported value in sum fixture constructor: {Types.formatValue other}."

let private compileSumEntries (fixture: JsonElement) =
    let source =
        """enum SumSignal {
    case ready;
    case failed;
}

record SumTextTail {
    field choice: Option<String>;
    field tail: Int;
}

fn sums.option-int-identity(value: Option<Int>) -> Option<Int> {
    effects none
    value
}

fn sums.option-string-identity(value: Option<String>) -> Option<String> {
    effects none
    value
}

fn sums.option-bool-identity(value: Option<Bool>) -> Option<Bool> {
    effects none
    value
}

fn sums.option-signal-identity(value: Option<SumSignal>) -> Option<SumSignal> {
    effects none
    value
}

fn sums.result-int-string-identity(value: Result<Int, String>) -> Result<Int, String> {
    effects none
    value
}

fn sums.result-string-int-identity(value: Result<String, Int>) -> Result<String, Int> {
    effects none
    value
}

fn sums.result-unit-bool-identity(value: Result<Unit, Bool>) -> Result<Unit, Bool> {
    effects none
    value
}

fn sums.option-empty-identity(value: Option<Empty>) -> Option<Empty> {
    effects none
    value
}

fn sums.result-empty-identity(value: Result<Empty, Empty>) -> Result<Empty, Empty> {
    effects none
    value
}

fn sums.nested-option-result-identity(value: Option<Result<String, Int>>) -> Option<Result<String, Int>> {
    effects none
    value
}

fn sums.text-tail-identity(value: SumTextTail) -> SumTextTail {
    effects none
    value
}"""
    let compiled = compileFlowProgram source
    let site name column = span ("<native-value-stack-sum-" + name + ">") column
    let compileBody name inputTypes expressions =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            name
            inputTypes
            expressions
            compiled.Context.SourceOrigins
    let oracle = fixture.GetProperty("sumConformance")
    let caseElements = oracle.GetProperty("cases").EnumerateArray() |> Seq.toArray
    let caseValues =
        caseElements
        |> Array.map (fun item -> item.GetProperty("name").GetString(), parseSumFixtureValue (item.GetProperty("expected")))
        |> Map.ofArray
    let caseBodies =
        caseElements
        |> Array.map (fun item ->
            let name = item.GetProperty("name").GetString()
            let expectedValue = caseValues[name]
            let body =
                compileBody
                    ("native-value-stack-sum-construct-" + name)
                    []
                    (sumValueExpressions compiled.Context.CompilerContext (site name) 1 expectedValue)
            name, body)
        |> Map.ofArray
    let identityTargets =
        caseElements
        |> Array.map (fun item -> item.GetProperty("identityBody").GetString(), item.GetProperty("type").GetString())
        |> Array.distinctBy fst
        |> Array.toList
    let identityBodies, localCallBodies =
        identityTargets
        |> List.mapi (fun index (target, typeName) ->
            let ty = parseSumLangType typeName
            let identity = compileBody ("native-value-stack-sum-host-round-trip-" + string index) [ ty ] [ Call(target, site "host-round-trip" 1) ]
            let localCall =
                compileBody ("native-value-stack-sum-local-call-" + string index) [ ty ]
                    [ Let("sum_saved", site "local-store" 1)
                      Load("sum_saved", site "local-load" 2)
                      Call(target, site "user-call" 3) ]
            target, identity, localCall)
        |> List.fold (fun (identityMap, localMap) (target, identity, localCall) ->
            Map.add target identity identityMap, Map.add target localCall localMap) (Map.empty, Map.empty)
    let equalityBodies =
        oracle.GetProperty("equalityCases").EnumerateArray()
        |> Seq.mapi (fun index equalityCase ->
            let leftName = equalityCase.GetProperty("left").GetString()
            let rightName = equalityCase.GetProperty("right").GetString()
            let leftValue = caseValues[leftName]
            let rightValue = caseValues[rightName]
            let leftType = parseSumLangType (caseElements |> Array.find (fun item -> item.GetProperty("name").GetString() = leftName) |> fun item -> item.GetProperty("type").GetString())
            let rightType = parseSumLangType (caseElements |> Array.find (fun item -> item.GetProperty("name").GetString() = rightName) |> fun item -> item.GetProperty("type").GetString())
            if leftType <> rightType then invalidOp (sprintf "Sum equality fixture compares unlike types %A and %A." leftType rightType)
            let body =
                compileBody ($"native-value-stack-sum-equals-{index}") []
                    (sumValueExpressions compiled.Context.CompilerContext (site "equality-left") 1 leftValue
                     @ sumValueExpressions compiled.Context.CompilerContext (site "equality-right") 1 rightValue
                     @ [ Call("equals", site "equality-call" 3) ])
            index, body)
        |> Map.ofSeq
    let optionIntMatch =
        compileBody "native-value-stack-sum-match-option-int" [ TOption TInt ]
            [ MatchOption(
                "sum_option_payload",
                [ Load("sum_option_payload", site "option-some-payload" 1) ],
                [ Push(LInt -1L, site "option-none-result" 2) ],
                site "option-match" 3) ]
    let resultIntStringMatch =
        compileBody "native-value-stack-sum-match-result-int-string" [ TResult(TInt, TString) ]
            [ MatchResult(
                "sum_result_ok",
                "sum_result_error",
                [ Push(LString "ok", site "result-ok-result" 1) ],
                [ Load("sum_result_error", site "result-error-payload" 2) ],
                site "result-match" 3) ]
    let resultStringIntMatch =
        compileBody "native-value-stack-sum-match-result-string-int" [ TResult(TString, TInt) ]
            [ MatchResult(
                "sum_string_ok",
                "sum_int_error",
                [ Load("sum_string_ok", site "result-ok-payload" 1) ],
                [ Push(LString "error", site "result-error-result" 2) ],
                site "result-string-match" 3) ]
    let resultStringIntErrorMatch =
        compileBody "native-value-stack-sum-match-result-string-int-error-payload" [ TResult(TString, TInt) ]
            [ MatchResult(
                "sum_string_ok_unused",
                "sum_int_error_payload",
                [ Push(LInt -1L, site "result-error-int-ok-sentinel" 1) ],
                [ Load("sum_int_error_payload", site "result-error-int-payload" 2) ],
                site "result-error-int-match" 3) ]
    let optionStringMatch =
        compileBody "native-value-stack-sum-match-option-string" [ TOption TString ]
            [ MatchOption(
                "sum_text_payload",
                [ Load("sum_text_payload", site "option-string-payload" 1) ],
                [ Push(LString "", site "option-string-none" 2) ],
                site "option-string-match" 3) ]
    let nestedMatch =
        compileBody "native-value-stack-sum-match-nested-option-result" [ TOption(TResult(TString, TInt)) ]
            [ MatchOption(
                "sum_nested_result",
                [ Load("sum_nested_result", site "nested-result-load" 1)
                  MatchResult(
                      "sum_nested_ok",
                      "sum_nested_error",
                      [ Load("sum_nested_ok", site "nested-ok-payload" 2) ],
                      [ Push(LString "error", site "nested-error-result" 3) ],
                      site "nested-result-match" 4) ],
                [ Push(LString "none", site "nested-option-none" 5) ],
                site "nested-option-match" 6) ]
    let nestedErrorIntMatch =
        compileBody "native-value-stack-sum-match-nested-error-int-payload" [ TOption(TResult(TString, TInt)) ]
            [ MatchOption(
                "sum_nested_error_result",
                [ Load("sum_nested_error_result", site "nested-error-result-load" 1)
                  MatchResult(
                      "sum_nested_error_ok_unused",
                      "sum_nested_error_payload",
                      [ Push(LInt -1L, site "nested-error-ok-sentinel" 2) ],
                      [ Load("sum_nested_error_payload", site "nested-error-int-payload" 3) ],
                      site "nested-error-result-int-match" 4) ],
                [ Push(LInt -1L, site "nested-error-none-sentinel" 5) ],
                site "nested-error-option-match" 6) ]
    let branchJoin =
        compileBody "native-value-stack-sum-branch-join-option-int" [ TBool ]
            [ If(
                [ Push(LInt 42L, site "branch-some-payload" 1)
                  ConstructContainer(OptionSome, [ TInt ], site "branch-some" 2) ],
                [ ConstructContainer(OptionNone, [ TInt ], site "branch-none" 3) ],
                site "branch-join" 4) ]
    let duplicateDrop =
        compileBody "native-value-stack-sum-direct-dup-drop" [ TOption TString ]
            [ Call("dup", site "sum-dup" 1); Call("drop", site "sum-drop" 2) ]
    let tailChoice =
        compileBody "native-value-stack-sum-tail-choice-projection" [ TNamed "SumTextTail" ]
            [ Call("sumTextTail.choice", site "tail-choice" 1) ]
    let tailProjection =
        compileBody "native-value-stack-sum-tail-int-projection" [ TNamed "SumTextTail" ]
            [ Call("sumTextTail.tail", site "tail-int" 1) ]
    let rawIgnoreBodies =
        [ "optionInt", TOption TInt
          "resultIntString", TResult(TInt, TString)
          "nestedOptionResult", TOption(TResult(TString, TInt)) ]
        |> List.map (fun (name, ty) ->
            name,
            compileBody ("native-value-stack-sum-raw-ignore-" + name) [ ty ]
                [ Call("drop", site ("raw-drop-" + name) 1)
                  Push(LInt 5L, site ("raw-result-" + name) 2) ])
        |> Map.ofList
    let failAfterSumAllocation =
        compileBody "native-value-stack-sum-failure-after-allocation" []
            [ Push(LString "sum-before-failure", site "failure-payload" 1)
              ConstructContainer(OptionSome, [ TString ], site "failure-sum" 2)
              Push(LInt 1L, site "failure-dividend" 3)
              Push(LInt 0L, site "failure-divisor" 4)
              Call("divide", site "failure-divide" 5) ]
    let bodies =
        [ yield! caseBodies |> Map.toList |> List.map snd
          yield! identityBodies |> Map.toList |> List.map snd
          yield! localCallBodies |> Map.toList |> List.map snd
          yield! equalityBodies |> Map.toList |> List.map snd
          yield optionIntMatch
          yield resultIntStringMatch
          yield resultStringIntMatch
          yield resultStringIntErrorMatch
          yield optionStringMatch
          yield nestedMatch
          yield nestedErrorIntMatch
          yield branchJoin
          yield duplicateDrop
          yield tailChoice
          yield tailProjection
          yield! rawIgnoreBodies |> Map.toList |> List.map snd
          yield failAfterSumAllocation ]
    if not (VerifiedIrProgram.isBackendExecutable compiled.Program)
       || bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, compiled.Program))) then
        invalidOp "Every sum conformance comparison must share one backend-authorized VerifiedIrProgram instance."
    { Program = compiled.Program
      CompilerContext = compiled.Context.CompilerContext
      SourceOrigins = compiled.Context.SourceOrigins
      Cases = caseBodies
      IdentityBodies = identityBodies
      LocalCallBodies = localCallBodies
      Equalities = equalityBodies
      OptionIntMatch = optionIntMatch
      ResultIntStringMatch = resultIntStringMatch
      ResultStringIntMatch = resultStringIntMatch
      ResultStringIntErrorMatch = resultStringIntErrorMatch
      OptionStringMatch = optionStringMatch
      NestedMatch = nestedMatch
      NestedErrorIntMatch = nestedErrorIntMatch
      BranchJoin = branchJoin
      DuplicateDrop = duplicateDrop
      TailChoice = tailChoice
      TailProjection = tailProjection
      RawIgnoreBodies = rawIgnoreBodies
      FailAfterSumAllocation = failAfterSumAllocation }

let private inspectRawOwningContextAbi (abiOracle: JsonElement) =
    let expectedSize = abiOracle.GetProperty("contextSizeBytes").GetInt32()
    let actualSize = Marshal.SizeOf<RawOwningStackContext>()
    let expectedOffsets = abiOracle.GetProperty("contextOffsetsBytes")
    let checks = ResizeArray<obj>()
    let mutable passed = actualSize = expectedSize
    checks.Add(box (jsonObject [
        "member", box "context-size"
        "expectedBytes", box expectedSize
        "actualBytes", box actualSize ]))
    let contextFields = [
        "stack_data", "StackData"
        "init_bitmap", "InitBitmap"
        "poison_bitmap", "PoisonBitmap"
        "trace_events", "TraceEvents"
        "deep_copy_bytes", "DeepCopyBytes"
        "move_bytes", "MoveBytes"
        "input_copy_bytes", "InputCopyBytes"
        "retained_copy_bytes", "RetainedCopyBytes" ]
    for oracleName, fieldName in contextFields do
        let expected = expectedOffsets.GetProperty(oracleName).GetInt32()
        let actual = Marshal.OffsetOf<RawOwningStackContext>(fieldName).ToInt32()
        let fieldPassed = actual = expected
        passed <- passed && fieldPassed
        checks.Add(box (jsonObject [
            "member", box oracleName
            "expectedOffsetBytes", box expected
            "actualOffsetBytes", box actual ]))
    passed, jsonObject [ "checks", box (checks.ToArray()) ]

let private invokeRawOwningEntry
    (program: OwningStackCompiledProgram)
    (abiOracle: JsonElement)
    (inputBytes: byte array)
    (inputExtents: uint32 array)
    (inputCount: uint32)
    (retainedOutput: byte array) =
    let contextAbiPassed, _ = inspectRawOwningContextAbi abiOracle
    if not contextAbiPassed then
        invalidOp "Raw owning-entry tests require the context layout pinned by the independent ABI oracle."
    let stackCapacity = 512
    let bitmapBytes = stackCapacity / 8
    let traceCapacity = 64
    let allocate byteCount = Marshal.AllocHGlobal(max 1 byteCount)
    let mutable libraryHandle = IntPtr.Zero
    let mutable contextPointer = IntPtr.Zero
    let mutable stackPointer = IntPtr.Zero
    let mutable initBitmapPointer = IntPtr.Zero
    let mutable poisonBitmapPointer = IntPtr.Zero
    let mutable tracePointer = IntPtr.Zero
    let mutable inputPointer = IntPtr.Zero
    let mutable extentsPointer = IntPtr.Zero
    let mutable outputPointer = IntPtr.Zero
    try
        libraryHandle <- NativeLibrary.Load(program.LibraryPath)
        let address = NativeLibrary.GetExport(libraryHandle, "agentlang_owning_execute")
        let execute = Marshal.GetDelegateForFunctionPointer<RawOwningExecuteDelegate>(address)
        contextPointer <- allocate (Marshal.SizeOf<RawOwningStackContext>())
        stackPointer <- allocate stackCapacity
        initBitmapPointer <- allocate bitmapBytes
        poisonBitmapPointer <- allocate bitmapBytes
        tracePointer <- allocate (traceCapacity * 40)
        inputPointer <- allocate inputBytes.Length
        let extentSlots = max inputExtents.Length (int inputCount)
        extentsPointer <- allocate (extentSlots * sizeof<uint32>)
        outputPointer <- allocate retainedOutput.Length
        let clearPointer pointer count =
            if count > 0 then Marshal.Copy(Array.zeroCreate<byte> count, 0, pointer, count)
        clearPointer stackPointer stackCapacity
        clearPointer initBitmapPointer bitmapBytes
        clearPointer poisonBitmapPointer bitmapBytes
        clearPointer tracePointer (traceCapacity * 40)
        clearPointer inputPointer inputBytes.Length
        clearPointer extentsPointer (extentSlots * sizeof<uint32>)
        if inputBytes.Length > 0 then Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length)
        for index, extent in inputExtents |> Array.indexed do
            Marshal.WriteInt32(extentsPointer, index * sizeof<uint32>, int extent)
        Marshal.Copy(retainedOutput, 0, outputPointer, retainedOutput.Length)
        let mutable context = Unchecked.defaultof<RawOwningStackContext>
        context.AbiVersion <- 1u
        context.StackCapacityBytes <- uint32 stackCapacity
        context.TraceEventCapacity <- uint32 traceCapacity
        context.InitBitmapBytes <- uint32 bitmapBytes
        context.StackData <- stackPointer
        context.InitBitmap <- initBitmapPointer
        context.PoisonBitmap <- poisonBitmapPointer
        context.TraceEvents <- tracePointer
        Marshal.StructureToPtr(context, contextPointer, false)
        let status =
            execute.Invoke(
                contextPointer,
                inputPointer,
                uint32 inputBytes.Length,
                extentsPointer,
                inputCount,
                outputPointer,
                uint32 retainedOutput.Length)
        let returnedContext = Marshal.PtrToStructure<RawOwningStackContext>(contextPointer)
        let outputAfter = Array.zeroCreate<byte> retainedOutput.Length
        if outputAfter.Length > 0 then Marshal.Copy(outputPointer, outputAfter, 0, outputAfter.Length)
        { NativeStatus = status
          ContextStatus = returnedContext.Status
          RetainedOutput = outputAfter }
    finally
        if outputPointer <> IntPtr.Zero then Marshal.FreeHGlobal outputPointer
        if extentsPointer <> IntPtr.Zero then Marshal.FreeHGlobal extentsPointer
        if inputPointer <> IntPtr.Zero then Marshal.FreeHGlobal inputPointer
        if tracePointer <> IntPtr.Zero then Marshal.FreeHGlobal tracePointer
        if poisonBitmapPointer <> IntPtr.Zero then Marshal.FreeHGlobal poisonBitmapPointer
        if initBitmapPointer <> IntPtr.Zero then Marshal.FreeHGlobal initBitmapPointer
        if stackPointer <> IntPtr.Zero then Marshal.FreeHGlobal stackPointer
        if contextPointer <> IntPtr.Zero then Marshal.FreeHGlobal contextPointer
        if libraryHandle <> IntPtr.Zero then NativeLibrary.Free libraryHandle

let private codeUnitsHexFromString (value: string) =
    let bytes = Array.zeroCreate<byte> (value.Length * 2)
    for index in 0 .. value.Length - 1 do
        let codeUnit = uint16 value[index]
        bytes[index * 2] <- byte (codeUnit &&& 0x00ffus)
        bytes[index * 2 + 1] <- byte (codeUnit >>> 8)
    bytesHex bytes

let rec private codeUnitSafeValueData (value: Value) : obj =
    let nested = codeUnitSafeValueData
    match value with
    | IntValue number -> jsonObject [ "type", box "Int"; "value", box number ]
    | FloatValue number -> jsonObject [ "type", box "Float"; "value", box number ]
    | BoolValue boolean -> jsonObject [ "type", box "Bool"; "value", box boolean ]
    | StringValue text ->
        jsonObject [
            "type", box "String"
            "codeUnitCount", box text.Length
            "codeUnitsHex", box (codeUnitsHexFromString text) ]
    | UnitValue -> jsonObject [ "type", box "Unit" ]
    | ListValue(elementType, items) ->
        jsonObject [
            "type", box "List"
            "elementType", box (sprintf "%A" elementType)
            "items", box (items |> List.map nested) ]
    | OptionValue(elementType, item) ->
        jsonObject [
            "type", box "Option"
            "elementType", box (sprintf "%A" elementType)
            "value", box (item |> Option.map nested) ]
    | ResultValue(okType, errorType, result) ->
        match result with
        | Ok item -> jsonObject [ "type", box "Result"; "okType", box (sprintf "%A" okType); "errorType", box (sprintf "%A" errorType); "ok", box (nested item) ]
        | Error item -> jsonObject [ "type", box "Result"; "okType", box (sprintf "%A" okType); "errorType", box (sprintf "%A" errorType); "error", box (nested item) ]
    | RecordValue(name, fields) ->
        jsonObject [
            "type", box "Record"
            "name", box name
            "fields", box (fields |> Map.toList |> List.map (fun (fieldName, fieldValue) -> jsonObject [ "name", box fieldName; "value", nested fieldValue ])) ]
    | EnumValue(name, variant) -> jsonObject [ "type", box "Enum"; "name", box name; "variant", box variant ]
    | NamedValue(name, inner) -> jsonObject [ "type", box "Named"; "name", box name; "value", nested inner ]

let private codeUnitSafeValuesData values = values |> List.map codeUnitSafeValueData

let private codeUnitSafeValuesJson values = JsonSerializer.Serialize(codeUnitSafeValuesData values)

let private codeUnitsFromHex (hex: string) =
    let bytes = bytesFromHex hex
    if bytes.Length % 2 <> 0 then invalidArg (nameof hex) "UTF-16 code-unit hex must contain complete little-endian code units."
    Array.init (bytes.Length / 2) (fun index -> char (uint16 bytes[index * 2] ||| (uint16 bytes[index * 2 + 1] <<< 8)))

let private stringFromCodeUnitsHex (hex: string) =
    String(codeUnitsFromHex hex)

let private stringBytesFromCodeUnitsHex (hex: string) =
    let data = bytesFromHex hex
    if data.Length % 2 <> 0 then invalidArg (nameof hex) "UTF-16 code-unit hex must contain complete little-endian code units."
    let count = uint32 (data.Length / 2)
    let payloadBytes = 8 + data.Length
    let extentBytes = (payloadBytes + 7) / 8 * 8
    let result = Array.zeroCreate<byte> extentBytes
    let countBytes = BitConverter.GetBytes(count)
    Array.Copy(countBytes, 0, result, 0, 4)
    Array.Copy(data, 0, result, 8, data.Length)
    result

let private textStateBytes count tag codeUnits stringCodeUnitsHex =
    Array.concat [
        bytesFromInt64s [ int64 count ]
        stringBytesFromCodeUnitsHex stringCodeUnitsHex
        bytesFromInt64s [ int64 codeUnits; int64 tag ] ]

let private textStateValue count tag codeUnits text =
    RecordValue("TextState", Map.ofList [
        "count", IntValue(int64 count)
        "last", RecordValue("TextEnvelope", Map.ofList [
            "leaf", RecordValue("TextLeaf", Map.ofList [
                "text", StringValue text
                "codeUnits", IntValue(int64 codeUnits) ])
            "tag", IntValue(int64 tag) ]) ])

let private textEnvelopeValue tag text codeUnits =
    RecordValue("TextEnvelope", Map.ofList [
        "leaf", RecordValue("TextLeaf", Map.ofList [
            "text", StringValue text
            "codeUnits", IntValue(int64 codeUnits) ])
        "tag", IntValue(int64 tag) ])

let private textStateFromCase (caseElement: JsonElement) =
    let mutable inputProperty = Unchecked.defaultof<JsonElement>
    let input = if caseElement.TryGetProperty("input", &inputProperty) then inputProperty else caseElement
    let codeUnitsHex = caseElement.GetProperty("inputCodeUnitsHex").GetString()
    let mutable countProperty = Unchecked.defaultof<JsonElement>
    let mutable tagProperty = Unchecked.defaultof<JsonElement>
    let mutable codeUnitsProperty = Unchecked.defaultof<JsonElement>
    let count = if input.TryGetProperty("count", &countProperty) then countProperty.GetInt64() else 10L
    let tag = if input.TryGetProperty("tag", &tagProperty) then tagProperty.GetInt64() else 70L
    let codeUnits = if input.TryGetProperty("codeUnits", &codeUnitsProperty) then codeUnitsProperty.GetInt64() else caseElement.GetProperty("inputCodeUnitCount").GetInt64()
    textStateValue count tag codeUnits (stringFromCodeUnitsHex codeUnitsHex)

let private textExpectedOutputs (caseElement: JsonElement) =
    let expected = caseElement.GetProperty("expectedOutputs").EnumerateArray() |> Seq.toArray
    let state = expected[0]
    let codeUnitsHex = caseElement.GetProperty("outputCodeUnitsHex").GetString()
    [ textStateValue
          (state.GetProperty("count").GetInt64())
          (state.GetProperty("tag").GetInt64())
          (state.GetProperty("codeUnits").GetInt64())
          (stringFromCodeUnitsHex codeUnitsHex)
      IntValue(expected[1].GetProperty("value").GetInt64()) ]

let private layoutTypeNames = [ "Leaf"; "Envelope"; "State"; "Empty" ]

let private validateTypeLayouts (fixture: JsonElement) (typeNames: string list) (layouts: OwningStackTypeLayout list) =
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let layoutOracle = fixture.GetProperty("layouts")
    for typeName in layoutTypeNames |> List.filter (fun typeName -> List.contains typeName typeNames) do
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None -> failures.Add($"Missing type layout for {typeName}.")
        | Some layout ->
            let expectedPayload = layoutOracle.GetProperty("payloadBytes").GetProperty(typeName).GetInt32()
            let expectedExtent = layoutOracle.GetProperty("stackExtentBytes").GetProperty(typeName).GetInt32()
            let fields =
                layoutOracle.GetProperty("declarationOrderFields").GetProperty(typeName).EnumerateArray()
                |> Seq.map (fun field -> field.GetString())
                |> Seq.toList
            checks.Add(box (jsonObject [ "type", box typeName; "expectedPayloadBytes", box expectedPayload; "actualPayloadBytes", box layout.PayloadBytes; "expectedStackExtentBytes", box expectedExtent; "actualStackExtentBytes", box layout.ExtentBytes ]))
            if layout.PayloadBytes <> expectedPayload || layout.ExtentBytes <> expectedExtent then failures.Add($"{typeName} expected payload and stack extent {expectedPayload}/{expectedExtent}, got {layout.PayloadBytes}/{layout.ExtentBytes}.")
            if layout.Fields.Length <> fields.Length then failures.Add($"{typeName} expected {fields.Length} declaration-order fields, got {layout.Fields.Length}.")
            for fieldName in fields do
                let fieldKey = $"{typeName}.{fieldName}"
                let expectedOffset = layoutOracle.GetProperty("fieldOffsets").GetProperty(fieldKey).GetInt32()
                let expectedFieldPayload = layoutOracle.GetProperty("fieldPayloadBytes").GetProperty(fieldKey).GetInt32()
                let expectedFieldExtent = layoutOracle.GetProperty("fieldExtentBytes").GetProperty(fieldKey).GetInt32()
                match layout.Fields |> List.tryFind (fun field -> String.Equals(field.FieldName, fieldName, StringComparison.Ordinal)) with
                | None ->
                    checks.Add(box (jsonObject [ "type", box typeName; "field", box fieldName; "present", box false ]))
                    failures.Add($"{typeName}.{fieldName} is absent from the physical layout.")
                | Some field ->
                    let contained = field.OffsetBytes >= 0 && field.ExtentBytes >= field.PayloadBytes && field.OffsetBytes + field.ExtentBytes <= layout.PayloadBytes
                    let passed = field.OffsetBytes = expectedOffset && field.PayloadBytes = expectedFieldPayload && field.ExtentBytes = expectedFieldExtent && contained
                    checks.Add(box (jsonObject [
                        "type", box typeName
                        "field", box fieldName
                        "expectedOffset", box expectedOffset
                        "actualOffset", box field.OffsetBytes
                        "expectedPayloadBytes", box expectedFieldPayload
                        "actualPayloadBytes", box field.PayloadBytes
                        "expectedExtentBytes", box expectedFieldExtent
                        "actualExtentBytes", box field.ExtentBytes
                        "containedInOwnerPayload", box contained ]))
                    if field.OffsetBytes <> expectedOffset then failures.Add($"{typeName}.{fieldName} expected offset {expectedOffset}, got {field.OffsetBytes}.")
                    if field.PayloadBytes <> expectedFieldPayload || field.ExtentBytes <> expectedFieldExtent then failures.Add($"{typeName}.{fieldName} expected payload/extent {expectedFieldPayload}/{expectedFieldExtent}, got {field.PayloadBytes}/{field.ExtentBytes}.")
                    if not contained then failures.Add($"{typeName}.{fieldName} extends outside its owning {typeName} payload.")
            let noInterFieldGaps =
                layout.Fields
                |> List.map (fun field -> int64 field.OffsetBytes, int64 field.ExtentBytes)
                |> List.pairwise
                |> List.forall (fun ((leftOffset, leftExtent), (rightOffset, _)) -> leftOffset + leftExtent = rightOffset)
            checks.Add(box (jsonObject [ "type", box typeName; "interFieldGapPresent", box (not noInterFieldGaps) ]))
            if not noInterFieldGaps then failures.Add($"{typeName} contains a gap between declaration-order fields.")
    let summary =
        layouts
        |> List.filter (fun layout -> typeNames |> List.contains layout.TypeName)
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private validateEnumTypeLayouts (fixture: JsonElement) (layouts: OwningStackTypeLayout list) =
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let oracle = fixture.GetProperty("enumConformance").GetProperty("layouts")
    for expectedType in oracle.EnumerateObject() do
        let typeName = expectedType.Name
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None ->
            checks.Add(box (jsonObject [ "type", box typeName; "present", box false ]))
            failures.Add($"Enum oracle type layout is missing for {typeName}.")
        | Some layout ->
            let expected = expectedType.Value
            let expectedPayload = expected.GetProperty("payloadBytes").GetInt32()
            let expectedExtent = expected.GetProperty("extentBytes").GetInt32()
            let expectedDynamic = expected.GetProperty("isDynamic").GetBoolean()
            let expectedMinimumPayload = expected.GetProperty("minimumPayloadBytes").GetInt32()
            let expectedMinimumExtent = expected.GetProperty("minimumExtentBytes").GetInt32()
            let typePassed =
                layout.PayloadBytes = expectedPayload
                && layout.ExtentBytes = expectedExtent
                && layout.IsDynamic = expectedDynamic
                && layout.MinimumPayloadBytes = expectedMinimumPayload
                && layout.MinimumExtentBytes = expectedMinimumExtent
            checks.Add(box (jsonObject [
                "type", box typeName
                "payloadBytesExpected", box expectedPayload
                "payloadBytesActual", box layout.PayloadBytes
                "extentBytesExpected", box expectedExtent
                "extentBytesActual", box layout.ExtentBytes
                "dynamicExpected", box expectedDynamic
                "dynamicActual", box layout.IsDynamic
                "minimumPayloadBytesExpected", box expectedMinimumPayload
                "minimumPayloadBytesActual", box layout.MinimumPayloadBytes
                "minimumExtentBytesExpected", box expectedMinimumExtent
                "minimumExtentBytesActual", box layout.MinimumExtentBytes ]))
            if not typePassed then failures.Add($"Enum oracle type layout does not match {typeName}.")
            let expectedFields = expected.GetProperty("fields").EnumerateArray() |> Seq.toArray
            if layout.Fields.Length <> expectedFields.Length then
                failures.Add($"{typeName} expected {expectedFields.Length} enum-layout field(s), got {layout.Fields.Length}.")
            for expectedField in expectedFields do
                let fieldName = expectedField.GetProperty("name").GetString()
                match layout.Fields |> List.tryFind (fun field -> String.Equals(field.FieldName, fieldName, StringComparison.Ordinal)) with
                | None ->
                    checks.Add(box (jsonObject [ "type", box typeName; "field", box fieldName; "present", box false ]))
                    failures.Add($"Enum oracle field {typeName}.{fieldName} is missing.")
                | Some field ->
                    let expectedOffset = expectedField.GetProperty("offsetBytes").GetInt32()
                    let expectedFieldPayload = expectedField.GetProperty("payloadBytes").GetInt32()
                    let expectedFieldExtent = expectedField.GetProperty("extentBytes").GetInt32()
                    let expectedOffsetDynamic = expectedField.GetProperty("isOffsetDynamic").GetBoolean()
                    let expectedFieldDynamic = expectedField.GetProperty("isDynamic").GetBoolean()
                    let expectedFieldMinimumPayload = expectedField.GetProperty("minimumPayloadBytes").GetInt32()
                    let expectedFieldMinimumExtent = expectedField.GetProperty("minimumExtentBytes").GetInt32()
                    let passed =
                        field.OffsetBytes = expectedOffset
                        && field.PayloadBytes = expectedFieldPayload
                        && field.ExtentBytes = expectedFieldExtent
                        && field.IsOffsetDynamic = expectedOffsetDynamic
                        && field.IsDynamic = expectedFieldDynamic
                        && field.MinimumPayloadBytes = expectedFieldMinimumPayload
                        && field.MinimumExtentBytes = expectedFieldMinimumExtent
                    checks.Add(box (jsonObject [
                        "type", box typeName
                        "field", box fieldName
                        "offsetBytesExpected", box expectedOffset
                        "offsetBytesActual", box field.OffsetBytes
                        "payloadBytesExpected", box expectedFieldPayload
                        "payloadBytesActual", box field.PayloadBytes
                        "extentBytesExpected", box expectedFieldExtent
                        "extentBytesActual", box field.ExtentBytes
                        "offsetDynamicExpected", box expectedOffsetDynamic
                        "offsetDynamicActual", box field.IsOffsetDynamic
                        "dynamicExpected", box expectedFieldDynamic
                        "dynamicActual", box field.IsDynamic
                        "minimumPayloadBytesExpected", box expectedFieldMinimumPayload
                        "minimumPayloadBytesActual", box field.MinimumPayloadBytes
                        "minimumExtentBytesExpected", box expectedFieldMinimumExtent
                        "minimumExtentBytesActual", box field.MinimumExtentBytes ]))
                    if not passed then failures.Add($"Enum oracle field layout does not match {typeName}.{fieldName}.")
    let summary =
        layouts
        |> List.filter (fun layout ->
            let mutable ignored = Unchecked.defaultof<JsonElement>
            oracle.TryGetProperty(layout.TypeName, &ignored))
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "minimumPayloadBytes", box layout.MinimumPayloadBytes
                "minimumExtentBytes", box layout.MinimumExtentBytes
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes; "isOffsetDynamic", box field.IsOffsetDynamic ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private validateSumTypeLayouts (fixture: JsonElement) (layouts: OwningStackTypeLayout list) =
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let oracle = fixture.GetProperty("sumConformance").GetProperty("layouts")
    let typeNameFor (ty: IrType) =
        layouts
        |> List.tryFind (fun layout -> layout.Type = ty)
        |> Option.map (fun layout -> layout.TypeName)
        |> Option.defaultValue (IrTypes.format ty)
    for expectedType in oracle.EnumerateObject() do
        let typeName = expectedType.Name
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None ->
            checks.Add(box (jsonObject [ "type", box typeName; "present", box false ]))
            failures.Add($"Sum oracle type layout is missing for {typeName}.")
        | Some layout ->
            let expected = expectedType.Value
            let expectedPayload = expected.GetProperty("payloadBytes").GetInt32()
            let expectedExtent = expected.GetProperty("extentBytes").GetInt32()
            let expectedDynamic = expected.GetProperty("isDynamic").GetBoolean()
            let expectedMinimumPayload = expected.GetProperty("minimumPayloadBytes").GetInt32()
            let expectedMinimumExtent = expected.GetProperty("minimumExtentBytes").GetInt32()
            let typePassed =
                layout.PayloadBytes = expectedPayload
                && layout.ExtentBytes = expectedExtent
                && layout.IsDynamic = expectedDynamic
                && layout.MinimumPayloadBytes = expectedMinimumPayload
                && layout.MinimumExtentBytes = expectedMinimumExtent
            checks.Add(box (jsonObject [
                "type", box typeName
                "payloadBytesExpected", box expectedPayload
                "payloadBytesActual", box layout.PayloadBytes
                "extentBytesExpected", box expectedExtent
                "extentBytesActual", box layout.ExtentBytes
                "dynamicExpected", box expectedDynamic
                "dynamicActual", box layout.IsDynamic
                "minimumPayloadBytesExpected", box expectedMinimumPayload
                "minimumPayloadBytesActual", box layout.MinimumPayloadBytes
                "minimumExtentBytesExpected", box expectedMinimumExtent
                "minimumExtentBytesActual", box layout.MinimumExtentBytes ]))
            if not typePassed then failures.Add($"Sum oracle type layout does not match {typeName}.")
            let mutable expectedCasesElement = Unchecked.defaultof<JsonElement>
            let expectedCases =
                if expected.TryGetProperty("cases", &expectedCasesElement) then expectedCasesElement.EnumerateArray() |> Seq.toArray
                else [||]
            if layout.Cases.Length <> expectedCases.Length then
                failures.Add($"{typeName} expected {expectedCases.Length} sum case row(s), got {layout.Cases.Length}.")
            for index in 0 .. min (layout.Cases.Length - 1) (expectedCases.Length - 1) do
                let expectedCase = expectedCases[index]
                let actualCase = layout.Cases[index]
                let expectedCaseName = expectedCase.GetProperty("name").GetString()
                let expectedTag = expectedCase.GetProperty("tag").GetInt32()
                let expectedPayloadType =
                    let mutable typeNameElement = Unchecked.defaultof<JsonElement>
                    if expectedCase.TryGetProperty("typeName", &typeNameElement) && typeNameElement.ValueKind <> JsonValueKind.Null then
                        Some(typeNameElement.GetString())
                    else None
                let actualPayloadType = actualCase.PayloadType |> Option.map typeNameFor
                let expectedOffset = expectedCase.GetProperty("offsetBytes").GetInt32()
                let expectedCasePayload = expectedCase.GetProperty("payloadBytes").GetInt32()
                let expectedCaseExtent = expectedCase.GetProperty("extentBytes").GetInt32()
                let expectedCaseDynamic = expectedCase.GetProperty("isDynamic").GetBoolean()
                let expectedCaseMinimumPayload = expectedCase.GetProperty("minimumPayloadBytes").GetInt32()
                let expectedCaseMinimumExtent = expectedCase.GetProperty("minimumExtentBytes").GetInt32()
                let casePassed =
                    actualCase.CaseName = expectedCaseName
                    && actualCase.Tag = expectedTag
                    && actualPayloadType = expectedPayloadType
                    && actualCase.OffsetBytes = expectedOffset
                    && actualCase.PayloadBytes = expectedCasePayload
                    && actualCase.ExtentBytes = expectedCaseExtent
                    && actualCase.IsDynamic = expectedCaseDynamic
                    && actualCase.MinimumPayloadBytes = expectedCaseMinimumPayload
                    && actualCase.MinimumExtentBytes = expectedCaseMinimumExtent
                checks.Add(box (jsonObject [
                    "type", box typeName
                    "case", box actualCase.CaseName
                    "caseExpected", box expectedCaseName
                    "tag", box actualCase.Tag
                    "tagExpected", box expectedTag
                    "payloadType", box (actualPayloadType |> Option.map box |> Option.defaultValue null)
                    "payloadTypeExpected", box (expectedPayloadType |> Option.map box |> Option.defaultValue null)
                    "offsetBytes", box actualCase.OffsetBytes
                    "offsetBytesExpected", box expectedOffset
                    "payloadBytes", box actualCase.PayloadBytes
                    "payloadBytesExpected", box expectedCasePayload
                    "extentBytes", box actualCase.ExtentBytes
                    "extentBytesExpected", box expectedCaseExtent
                    "isDynamic", box actualCase.IsDynamic
                    "isDynamicExpected", box expectedCaseDynamic
                    "minimumPayloadBytes", box actualCase.MinimumPayloadBytes
                    "minimumPayloadBytesExpected", box expectedCaseMinimumPayload
                    "minimumExtentBytes", box actualCase.MinimumExtentBytes
                    "minimumExtentBytesExpected", box expectedCaseMinimumExtent ]))
                if not casePassed then failures.Add($"Sum oracle case layout does not match {typeName}.{expectedCaseName}.")
            let mutable expectedFieldsElement = Unchecked.defaultof<JsonElement>
            let expectedFields =
                if expected.TryGetProperty("fields", &expectedFieldsElement) then expectedFieldsElement.EnumerateArray() |> Seq.toArray
                else [||]
            if layout.Fields.Length <> expectedFields.Length then
                failures.Add($"{typeName} expected {expectedFields.Length} sum-layout record field(s), got {layout.Fields.Length}.")
            for expectedField in expectedFields do
                let fieldName = expectedField.GetProperty("name").GetString()
                match layout.Fields |> List.tryFind (fun field -> field.FieldName = fieldName) with
                | None ->
                    checks.Add(box (jsonObject [ "type", box typeName; "field", box fieldName; "present", box false ]))
                    failures.Add($"Sum oracle record field {typeName}.{fieldName} is missing.")
                | Some field ->
                    let expectedFieldOffset = expectedField.GetProperty("offsetBytes").GetInt32()
                    let expectedFieldPayload = expectedField.GetProperty("payloadBytes").GetInt32()
                    let expectedFieldExtent = expectedField.GetProperty("extentBytes").GetInt32()
                    let expectedFieldOffsetDynamic = expectedField.GetProperty("isOffsetDynamic").GetBoolean()
                    let expectedFieldDynamic = expectedField.GetProperty("isDynamic").GetBoolean()
                    let expectedFieldMinimumPayload = expectedField.GetProperty("minimumPayloadBytes").GetInt32()
                    let expectedFieldMinimumExtent = expectedField.GetProperty("minimumExtentBytes").GetInt32()
                    let fieldPassed =
                        field.OffsetBytes = expectedFieldOffset
                        && field.PayloadBytes = expectedFieldPayload
                        && field.ExtentBytes = expectedFieldExtent
                        && field.IsOffsetDynamic = expectedFieldOffsetDynamic
                        && field.IsDynamic = expectedFieldDynamic
                        && field.MinimumPayloadBytes = expectedFieldMinimumPayload
                        && field.MinimumExtentBytes = expectedFieldMinimumExtent
                    checks.Add(box (jsonObject [
                        "type", box typeName
                        "field", box fieldName
                        "offsetBytes", box field.OffsetBytes
                        "offsetBytesExpected", box expectedFieldOffset
                        "payloadBytes", box field.PayloadBytes
                        "payloadBytesExpected", box expectedFieldPayload
                        "extentBytes", box field.ExtentBytes
                        "extentBytesExpected", box expectedFieldExtent
                        "isOffsetDynamic", box field.IsOffsetDynamic
                        "isOffsetDynamicExpected", box expectedFieldOffsetDynamic
                        "isDynamic", box field.IsDynamic
                        "isDynamicExpected", box expectedFieldDynamic
                        "minimumPayloadBytes", box field.MinimumPayloadBytes
                        "minimumPayloadBytesExpected", box expectedFieldMinimumPayload
                        "minimumExtentBytes", box field.MinimumExtentBytes
                        "minimumExtentBytesExpected", box expectedFieldMinimumExtent ]))
                    if not fieldPassed then failures.Add($"Sum oracle record field layout does not match {typeName}.{fieldName}.")
    let summary =
        layouts
        |> List.filter (fun layout ->
            let mutable ignored = Unchecked.defaultof<JsonElement>
            oracle.TryGetProperty(layout.TypeName, &ignored))
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "isDynamic", box layout.IsDynamic
                "minimumPayloadBytes", box layout.MinimumPayloadBytes
                "minimumExtentBytes", box layout.MinimumExtentBytes
                "cases", box (layout.Cases |> List.map (fun item -> jsonObject [ "name", box item.CaseName; "tag", box item.Tag; "typeName", box (item.PayloadType |> Option.map typeNameFor |> Option.defaultValue ""); "offsetBytes", box item.OffsetBytes; "payloadBytes", box item.PayloadBytes; "extentBytes", box item.ExtentBytes ]))
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes; "isOffsetDynamic", box field.IsOffsetDynamic ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private validateDynamicTypeLayouts (typeNames: string list) (fixture: JsonElement) (layouts: OwningStackTypeLayout list) =
    let oracle = fixture.GetProperty("stringWorkload").GetProperty("dynamicLayoutOracle")
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    for typeName in typeNames do
        let expectedPayload = oracle.GetProperty("minimumPayloadBytes").GetProperty(typeName).GetInt32()
        let expectedExtent = oracle.GetProperty("minimumExtentBytes").GetProperty(typeName).GetInt32()
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None ->
            checks.Add(box (jsonObject [ "type", box typeName; "present", box false ]))
            failures.Add($"Missing dynamic type layout for {typeName}.")
        | Some layout ->
            let dynamic = Convert.ToBoolean(getProperty (box layout) "IsDynamic", CultureInfo.InvariantCulture)
            let minimumPayload = int64Property (box layout) "MinimumPayloadBytes"
            let minimumExtent = int64Property (box layout) "MinimumExtentBytes"
            let payload = int64 layout.PayloadBytes
            let extent = int64 layout.ExtentBytes
            let passed = dynamic && payload = -1L && extent = -1L && minimumPayload = int64 expectedPayload && minimumExtent = int64 expectedExtent
            checks.Add(box (jsonObject [
                "type", box typeName
                "isDynamic", box dynamic
                "payloadBytes", box payload
                "extentBytes", box extent
                "expectedMinimumPayloadBytes", box expectedPayload
                "actualMinimumPayloadBytes", box minimumPayload
                "expectedMinimumExtentBytes", box expectedExtent
                "actualMinimumExtentBytes", box minimumExtent ]))
            if not passed then failures.Add($"{typeName} must expose dynamic sizes (-1) and minimum payload/extent {expectedPayload}/{expectedExtent}.")
    let expectedFields =
        oracle.GetProperty("fields").EnumerateObject()
        |> Seq.filter (fun property ->
            let separator = property.Name.IndexOf('.')
            separator > 0 && List.contains (property.Name.Substring(0, separator)) typeNames)
    for property in expectedFields do
        let key = property.Name
        let separator = key.IndexOf('.')
        let typeName = key.Substring(0, separator)
        let fieldName = key.Substring(separator + 1)
        let expected = property.Value
        match layouts |> List.tryFind (fun layout -> layout.TypeName = typeName) with
        | None -> failures.Add($"Field oracle owner {typeName} has no type layout.")
        | Some owner ->
            match owner.Fields |> List.tryFind (fun field -> field.FieldName = fieldName) with
            | None -> failures.Add($"Missing dynamic field layout for {key}.")
            | Some field ->
                let expectedDynamic = expected.GetProperty("isDynamic").GetBoolean()
                let expectedOffsetDynamic = expected.GetProperty("isOffsetDynamic").GetBoolean()
                let expectedMinimumOffset =
                    if expectedOffsetDynamic then expected.GetProperty("minimumOffsetBytes").GetInt32()
                    else
                        let mutable minimumOffset = Unchecked.defaultof<JsonElement>
                        if expected.TryGetProperty("minimumOffsetBytes", &minimumOffset) then minimumOffset.GetInt32()
                        else expected.GetProperty("offsetBytes").GetInt32()
                let expectedOffset = if expectedOffsetDynamic then -1 else expectedMinimumOffset
                let expectedMinimumPayload =
                    if expectedDynamic then expected.GetProperty("minimumPayloadBytes").GetInt32()
                    else expected.GetProperty("payloadBytes").GetInt32()
                let expectedMinimumExtent =
                    if expectedDynamic then expected.GetProperty("minimumExtentBytes").GetInt32()
                    else expected.GetProperty("extentBytes").GetInt32()
                let dynamic = Convert.ToBoolean(getProperty (box field) "IsDynamic", CultureInfo.InvariantCulture)
                let offsetDynamic = Convert.ToBoolean(getProperty (box field) "IsOffsetDynamic", CultureInfo.InvariantCulture)
                let minimumPayload = int64Property (box field) "MinimumPayloadBytes"
                let minimumExtent = int64Property (box field) "MinimumExtentBytes"
                let expectedPayload = if expectedDynamic then -1 else expected.GetProperty("payloadBytes").GetInt32()
                let expectedExtent = if expectedDynamic then -1 else expected.GetProperty("extentBytes").GetInt32()
                let passed =
                    dynamic = expectedDynamic
                    && offsetDynamic = expectedOffsetDynamic
                    && field.OffsetBytes = expectedOffset
                    && field.PayloadBytes = expectedPayload
                    && field.ExtentBytes = expectedExtent
                    && minimumPayload = int64 expectedMinimumPayload
                    && minimumExtent = int64 expectedMinimumExtent
                checks.Add(box (jsonObject [
                    "field", box key
                    "isDynamic", box dynamic
                    "expectedIsDynamic", box expectedDynamic
                    "isOffsetDynamic", box offsetDynamic
                    "expectedIsOffsetDynamic", box expectedOffsetDynamic
                    "offsetBytes", box field.OffsetBytes
                    "expectedOffsetBytes", box expectedOffset
                    "payloadBytes", box field.PayloadBytes
                    "extentBytes", box field.ExtentBytes
                    "minimumPayloadBytes", box minimumPayload
                    "minimumExtentBytes", box minimumExtent ]))
                if not passed then failures.Add($"{key} dynamic metadata does not match the independent layout oracle.")
    let summary =
        layouts
        |> List.filter (fun layout -> List.contains layout.TypeName typeNames)
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "isDynamic", box (getProperty (box layout) "IsDynamic")
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "minimumPayloadBytes", box (getProperty (box layout) "MinimumPayloadBytes")
                "minimumExtentBytes", box (getProperty (box layout) "MinimumExtentBytes")
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [
                    "name", box field.FieldName
                    "isDynamic", box (getProperty (box field) "IsDynamic")
                    "isOffsetDynamic", box (getProperty (box field) "IsOffsetDynamic")
                    "offsetBytes", box field.OffsetBytes
                    "payloadBytes", box field.PayloadBytes
                    "extentBytes", box field.ExtentBytes
                    "minimumPayloadBytes", box (getProperty (box field) "MinimumPayloadBytes")
                    "minimumExtentBytes", box (getProperty (box field) "MinimumExtentBytes") ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private layoutEventKind (event: OwningStackLayoutEvent) =
    if event.Kind = "duplicate" then 2L
    elif event.Kind = "drop" then 3L
    elif event.Kind = "call-input-move" then 8L
    elif event.Kind = "call-return-move" then 9L
    elif event.Kind = "retained-copy" then 10L
    elif event.Kind = "local-compact" then 14L
    elif event.Kind = "string-concat-left" then 15L
    elif event.Kind = "string-concat-right" then 16L
    elif event.Kind = "descriptor-transfer" then 17L
    elif event.Kind = "arena-rewind" then 18L
    elif event.Kind = "allocate" then 1L
    else -1L

let private layoutEventRanges (events: OwningStackLayoutEvent list) =
    events
    |> List.map (fun event ->
        {| Kind = layoutEventKind event
           Offset = int64 event.OffsetBytes
           Extent = int64 event.ExtentBytes
           Payload = int64 event.PayloadBytes
           SourceOffset = event.SourceOffsetBytes |> Option.map int64 |> Option.defaultValue -1L
           SourceExtent = event.SourceExtentBytes |> Option.map int64 |> Option.defaultValue -1L |})
    |> List.toArray

let private layoutEventDetails (events: OwningStackLayoutEvent list) =
    events
    |> List.map (fun event ->
        jsonObject [
            "kind", box event.Kind
            "kindCode", box (layoutEventKind event)
            "typeId", box event.TypeId
            "offsetBytes", box event.OffsetBytes
            "extentBytes", box event.ExtentBytes
            "payloadBytes", box event.PayloadBytes
            "sourceOffsetBytes", box (event.SourceOffsetBytes |> Option.map box |> Option.defaultValue null)
            "sourceExtentBytes", box (event.SourceExtentBytes |> Option.map box |> Option.defaultValue null)
            "checksum", box (event.Checksum |> Option.map box |> Option.defaultValue null) ])
    |> List.toArray

let private envelopeFromJson (element: JsonElement) =
    let leaf = element.GetProperty("leaf")
    RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", valueInt leaf "value" ]); "tag", valueInt element "tag" ])

let private stateFromJson (element: JsonElement) =
    RecordValue("State", Map.ofList [ "count", valueInt element "count"; "last", envelopeFromJson (element.GetProperty("last")) ])

let private expectedValues (element: JsonElement) =
    let kind = element.GetProperty("type").GetString()
    match kind with
    | "State" -> [ stateFromJson element ]
    | "Envelope" -> [ envelopeFromJson element ]
    | "Unit" -> [ UnitValue ]
    | "Int" -> [ valueInt element "value" ]
    | "Empty" -> [ RecordValue("Empty", Map.empty) ]
    | other -> invalidOp $"Unsupported fixture result type '{other}'."

let private inspectScopeShadow (program: VerifiedIrProgram) (verifiedBody: VerifiedIrBody) =
    let programData = VerifiedIrProgram.inspect program
    let body = VerifiedIrBody.inspect verifiedBody
    let slot =
        body.BodyLocalNames
        |> Map.toSeq
        |> Seq.tryPick (fun (slot, name) -> if name = "shadow" then Some slot else None)
    let typeKey typeName =
        programData.NominalTypesByKey
        |> Map.toSeq
        |> Seq.tryPick (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record when record.TypeName = typeName -> Some key
            | _ -> None)
    let typeName = function
        | Some(IrNominal key) ->
            match programData.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) -> record.TypeName
            | _ -> "<unknown-nominal>"
        | Some other -> IrTypes.format other
        | None -> "<missing>"
    match slot with
    | None -> false, jsonObject [ "slotFound", box false ]
    | Some shadowSlot ->
        let scopeBlock =
            body.BodyBlock.Code
            |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.Scope inner -> Some inner | _ -> None)
        match scopeBlock, typeKey "Envelope", typeKey "Leaf" with
        | Some inner, Some envelopeKey, Some leafKey ->
            let entryType = inner.EntryShape.LocalTypes.TryFind shadowSlot
            let innerType = inner.ExitShape.LocalTypes.TryFind shadowSlot
            let restoredType = body.BodyBlock.ExitShape.LocalTypes.TryFind shadowSlot
            let innerStoreUsesSlot = inner.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let outerStoreUsesSlot = body.BodyBlock.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let passed =
                innerStoreUsesSlot
                && outerStoreUsesSlot
                && entryType = Some(IrNominal envelopeKey)
                && innerType = Some(IrNominal leafKey)
                && restoredType = Some(IrNominal envelopeKey)
            passed, jsonObject [
                "slotFound", box true
                "slot", box (sprintf "%A" shadowSlot)
                "outerStoreUsesSameSlot", box outerStoreUsesSlot
                "innerStoreUsesSameSlot", box innerStoreUsesSlot
                "scopeEntryType", box (typeName entryType)
                "scopeInnerExitType", box (typeName innerType)
                "afterScopeType", box (typeName restoredType) ]
        | _ -> false, jsonObject [ "slotFound", box true; "scopeBlockFound", box scopeBlock.IsSome; "expectedTypeKeysFound", box (typeKey "Envelope" |> Option.isSome && typeKey "Leaf" |> Option.isSome) ]

let private inspectDynamicScopeShadow (program: VerifiedIrProgram) (verifiedBody: VerifiedIrBody) =
    let programData = VerifiedIrProgram.inspect program
    let body = VerifiedIrBody.inspect verifiedBody
    let slot =
        body.BodyLocalNames
        |> Map.toSeq
        |> Seq.tryPick (fun (slot, name) -> if name = "shadow" then Some slot else None)
    let envelopeKey =
        programData.NominalTypesByKey
        |> Map.toSeq
        |> Seq.tryPick (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record when record.TypeName = "TextEnvelope" -> Some key
            | _ -> None)
    match slot, envelopeKey with
    | Some shadowSlot, Some key ->
        let scopeBlock =
            body.BodyBlock.Code
            |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.Scope inner -> Some inner | _ -> None)
        match scopeBlock with
        | Some inner ->
            let entryType = inner.EntryShape.LocalTypes.TryFind shadowSlot
            let innerType = inner.ExitShape.LocalTypes.TryFind shadowSlot
            let restoredType = body.BodyBlock.ExitShape.LocalTypes.TryFind shadowSlot
            let innerStoreUsesSlot = inner.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let outerStoreUsesSlot = body.BodyBlock.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let expectedType = Some(IrNominal key)
            let passed =
                innerStoreUsesSlot
                && outerStoreUsesSlot
                && entryType = expectedType
                && innerType = expectedType
                && restoredType = expectedType
            passed, jsonObject [
                "slotFound", box true
                "slot", box (sprintf "%A" shadowSlot)
                "outerStoreUsesSameSlot", box outerStoreUsesSlot
                "innerStoreUsesSameSlot", box innerStoreUsesSlot
                "scopeEntryType", box "TextEnvelope"
                "scopeInnerExitType", box "TextEnvelope"
                "afterScopeType", box "TextEnvelope"
                "sameLogicalSlot", box (innerStoreUsesSlot && outerStoreUsesSlot)
                "widthsAreRuntimeDependent", box true ]
        | None -> false, jsonObject [ "slotFound", box true; "scopeBlockFound", box false ]
    | _ -> false, jsonObject [ "slotFound", box slot.IsSome; "textEnvelopeTypeFound", box envelopeKey.IsSome ]

let private diagnosticCode (error: exn) =
    match error with
    | LanguageException diagnostic -> diagnostic.Code
    | _ ->
        match getProperty error "Diagnostic" with
        | null -> ""
        | diagnostic -> getProperty diagnostic "Code" |> string

let private exceptionCode (error: exn) =
    let diagnostic = diagnosticCode error
    if not (String.IsNullOrWhiteSpace diagnostic) then diagnostic
    else
        match getProperty error "Code" with
        | null -> ""
        | code -> string code

let private resourceExceptionDetails (error: exn) =
    jsonObject [
        "type", box (error.GetType().Name)
        "code", box (getProperty error "Code" |> string)
        "boundary", box (getProperty error "Boundary" |> string)
        "arena", box (getProperty error "Arena" |> string)
        "requiredBytes", box (getProperty error "RequiredBytes")
        "availableBytes", box (getProperty error "AvailableBytes")
        "requiredNodes", box (getProperty error "RequiredNodes")
        "availableNodes", box (getProperty error "AvailableNodes") ]

let private recordCheck (checks: ResizeArray<obj>) (failures: ResizeArray<string>) name passed details =
    checks.Add(box (jsonObject [ "name", box name; "passed", box passed; "details", box details ]))
    if not passed then failures.Add($"{name}: {details}")

let private addCommonEntryReports (report: Dictionary<string, obj>) sourceHash expandedSource turnHashes =
    report["source"] <- box (jsonObject [
        "flowFixtureSha256", box sourceHash
        "expandedSourceSha256", box (hashText expandedSource)
        "turnBodyHashes", box turnHashes ])

let private runInterpreterChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody) =
    let expectedInitial = expectedValues (fixture.GetProperty("initialState"))
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    use initial = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.initialize" entries.Initialize None []
    recordCheck checks failures $"interpreter/N={repetition}/initialize" (initial.Decode() = expectedInitial) (ValueInspection.toJson entries.Program (initial.Decode()))
    let firstInput = turns[0].GetProperty("input").GetInt64()
    use first = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn" turnBody (Some initial) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ]
    let firstValues = first.Decode()
    let expectedFirst = expectedValues (turns[0].GetProperty("expectedState"))
    recordCheck checks failures $"interpreter/N={repetition}/first-turn" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    let secondInput = turns[1].GetProperty("input").GetInt64()
    use second = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn" turnBody (Some first) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument secondInput ]
    let secondValues = second.Decode()
    let expectedSecond = expectedValues (turns[1].GetProperty("expectedState"))
    recordCheck checks failures $"interpreter/N={repetition}/second-turn" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    for (index, branch) in fixture.GetProperty("branches").EnumerateArray() |> Seq.indexed do
        let input = branch.GetProperty("value").GetInt64()
        let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
        use actual = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.branch" entries.Branch None [ IrEntryArgument.IntArgument input; IrEntryArgument.BoolArgument chooseLeft ]
        let values = actual.Decode()
        let expected = expectedValues (branch.GetProperty("expected"))
        recordCheck checks failures $"interpreter/N={repetition}/branch-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    use direct = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-direct-dup-drop" entries.DirectDupDrop None [ IrEntryArgument.IntArgument 17L; IrEntryArgument.IntArgument 1017L ]
    recordCheck checks failures $"interpreter/N={repetition}/direct-top-dup-drop" (direct.Decode() = directExpected) (ValueInspection.toJson entries.Program (direct.Decode()))
    let failureCode =
        try
            use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.fail-after-allocation" entries.FailAfterAllocation (Some initial) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ]
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures $"interpreter/N={repetition}/error-after-allocation" (failureCode = "RUNTIME_DIVIDE_BY_ZERO" && initial.Decode() = expectedInitial) failureCode
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        use unitResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.unit-value" entries.UnitValue None []
        let unitValues = unitResult.Decode()
        recordCheck checks failures "interpreter/unit-fixed-layout-value" (unitValues = unitExpected) (ValueInspection.toJson entries.Program unitValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitValues) ]))
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        use unitDropResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-unit-drop" entries.UnitDrop None []
        let unitDropValues = unitDropResult.Decode()
        recordCheck checks failures "interpreter/unit-drop-releases-token-and-continues" (unitDropValues = unitDropExpected) (ValueInspection.toJson entries.Program unitDropValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropValues) ]))
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        use emptyResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.empty-value" entries.EmptyValue None []
        let emptyValues = emptyResult.Decode()
        recordCheck checks failures "interpreter/empty-record-zero-field-value" (emptyValues = emptyExpected) (ValueInspection.toJson entries.Program emptyValues)
        use emptyIdentity = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-empty-record-identity" entries.EmptyIdentity (Some emptyResult) [ IrEntryArgument.RetainedRoot 0 ]
        recordCheck checks failures "interpreter/empty-record-zero-token-identity" (emptyIdentity.Decode() = emptyExpected) (ValueInspection.toJson entries.Program (emptyIdentity.Decode()))
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyValues) ]))
        let scopedCases = fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.toArray
        for index, scopedCase in scopedCases |> Array.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            use scoped = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.scoped-width" entries.ScopedWidth None [ IrEntryArgument.IntArgument input ]
            let values = scoped.Decode()
            recordCheck checks failures $"interpreter/scoped-width-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            fixedCaseReports.Add(box (jsonObject [ "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program values) ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        use shadow = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-scope-local-shadow" entries.ScopeShadow None []
        let shadowValues = shadow.Decode()
        recordCheck checks failures "interpreter/scope-shadow-restores-outer-width" (shadowValues = shadowExpected) (ValueInspection.toJson entries.Program shadowValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowValues) ]))
        let depthExpected = [ IntValue 42L ]
        use depth64 = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.depth-64" entries.Depth64 None [ IrEntryArgument.IntArgument 42L ]
        let depth64Values = depth64.Decode()
        recordCheck checks failures "interpreter/call-depth-64-succeeds" (depth64Values = depthExpected) (ValueInspection.toJson entries.Program depth64Values)
        let depth65Code =
            try
                use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.depth-65" entries.Depth65 None [ IrEntryArgument.IntArgument 42L ]
                "unexpected-success"
            with error -> diagnosticCode error
        recordCheck checks failures "interpreter/call-depth-65-diagnostic" (depth65Code = "RUNTIME_CALL_DEPTH") depth65Code
    jsonObject [
        "backend", box "interpreter"
        "repetitions", box repetition
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "failureAfterAllocation", box failureCode
        "fixedLayoutCases", box (fixedCaseReports.ToArray()) ]

let private nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes =
    { NativeExecutionOptions.defaults with
        ScratchByteCapacity = Some scratchBytes
        ScratchNodeCapacity = Some scratchNodes
        RetainedByteCapacity = Some retainedBytes
        RetainedNodeCapacity = Some retainedNodes }

let private controlScratchBytes repetition = 24 * repetition + 80
let private controlScratchNodes repetition = 2 * repetition + 6

let private runNativeChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody) =
    let nativeSources = NativeDiagnosticSources.fromLoweringContext entries.CompilerContext
    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        LlvmAot.compile toolchain optimization (Path.Combine(artifactRoot, "abi3", string optimization, name)) nativeSources body
    use initialProgram = compile "initialize" entries.Initialize
    use turnProgram = compile $"turn-{repetition}" turnBody
    use branchProgram = compile "branch" entries.Branch
    use directProgram = compile "direct-dup-drop" entries.DirectDupDrop
    use failProgram = compile "fail-after-allocation" entries.FailAfterAllocation
    let expectedInitial = expectedValues (fixture.GetProperty("initialState"))
    let scratchBytes = controlScratchBytes repetition
    let scratchNodes = controlScratchNodes repetition
    let retainedBytes = 40
    let retainedNodes = 3
    use initial = initialProgram.ExecuteRetained("mailbox.initialize", options = nativeOptions 40 3 retainedBytes retainedNodes)
    let initialValues = initial.Decode()
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/initialize" (initialValues = expectedInitial) (ValueInspection.toJson entries.Program initialValues)
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let firstInput = turns[0].GetProperty("input").GetInt64()
    use first = turnProgram.ExecuteRetainedWithInputs(
        "mailbox.turn",
        Some initial,
        [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
        options = nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes)
    let firstValues = first.Decode()
    let expectedFirst = expectedValues (turns[0].GetProperty("expectedState"))
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/turn-1" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    let secondInput = turns[1].GetProperty("input").GetInt64()
    use second = turnProgram.ExecuteRetainedWithInputs(
        "mailbox.turn",
        Some first,
        [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument secondInput ],
        options = nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes)
    let secondValues = second.Decode()
    let expectedSecond = expectedValues (turns[1].GetProperty("expectedState"))
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/turn-2" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    let failedScratch =
        try
            use _unexpected = turnProgram.ExecuteRetainedWithInputs(
                "mailbox.turn-capacity",
                Some initial,
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
                options = nativeOptions (scratchBytes - 1) scratchNodes retainedBytes retainedNodes)
            None
        with :? NativeResourceLimitException as error -> Some error
    let priorUnchanged = initial.Decode() = expectedInitial
    match failedScratch with
    | Some error ->
        let capacityPassed = error.Code = "NATIVE_SCRATCH_CAPACITY" && error.Arena = "scratch" && error.RequiredBytes = int64 scratchBytes && error.AvailableBytes = scratchBytes - 1
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-capacity-boundary" capacityPassed (resourceExceptionDetails error)
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-failure-preserves-owner" priorUnchanged (ValueInspection.toJson entries.Program (initial.Decode()))
    | None ->
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-capacity-boundary" false "Scratch capacity one byte below the independent threshold unexpectedly succeeded."
    let failCode =
        try
            use _unexpected = failProgram.ExecuteRetainedWithInputs(
                "mailbox.fail-after-allocation",
                Some initial,
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
                options = nativeOptions 64 5 retainedBytes retainedNodes)
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/error-after-allocation" (failCode = "RUNTIME_DIVIDE_BY_ZERO" && initial.Decode() = expectedInitial) failCode
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    use direct = directProgram.ExecuteRetainedWithInputs(
        "native-value-stack-direct-dup-drop",
        None,
        [ IrEntryArgument.IntArgument 17L; IrEntryArgument.IntArgument 1017L ],
        options = nativeOptions 24 2 24 2)
    let directValues = direct.Decode()
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/direct-top-dup-drop" (directValues = directExpected) (ValueInspection.toJson entries.Program directValues)
    let branchReports =
        fixture.GetProperty("branches").EnumerateArray()
        |> Seq.mapi (fun index branch ->
            let input = branch.GetProperty("value").GetInt64()
            let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
            let expected = expectedValues (branch.GetProperty("expected"))
            use result = branchProgram.ExecuteRetainedWithInputs(
                "mailbox.branch",
                None,
                [ IrEntryArgument.IntArgument input; IrEntryArgument.BoolArgument chooseLeft ],
                options = nativeOptions 24 2 24 2)
            let values = result.Decode()
            recordCheck checks failures $"abi3/{optimization}/N={repetition}/branch-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            box (jsonObject [ "chooseLeft", box chooseLeft; "values", jsonNode options (ValueInspection.toData entries.Program values) ]))
        |> Seq.toArray
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        use unitProgram = compile "unit-value" entries.UnitValue
        use unitDropProgram = compile "unit-drop" entries.UnitDrop
        use emptyProgram = compile "empty-value" entries.EmptyValue
        use emptyIdentityProgram = compile "empty-identity" entries.EmptyIdentity
        use scopedProgram = compile "scoped-width" entries.ScopedWidth
        use scopeShadowProgram = compile "scope-shadow" entries.ScopeShadow
        use depth64Program = compile "depth-64" entries.Depth64
        use depth65Program = compile "depth-65" entries.Depth65
        use unitResult = unitProgram.ExecuteRetained("mailbox.unit-value", options = nativeOptions 8 1 8 1)
        let unitValues = unitResult.Decode()
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/unit-fixed-layout-value" (unitValues = unitExpected) (ValueInspection.toJson entries.Program unitValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitValues); "abi3RetainedBytes", box unitResult.RetainedByteCount; "abi3RetainedNodes", box unitResult.RetainedNodeCount ]))
        use unitDropResult = unitDropProgram.ExecuteRetained("native-value-stack-unit-drop", options = nativeOptions 16 2 8 1)
        let unitDropValues = unitDropResult.Decode()
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/unit-drop-releases-token-and-continues" (unitDropValues = unitDropExpected) (ValueInspection.toJson entries.Program unitDropValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropValues); "abi3RetainedBytes", box unitDropResult.RetainedByteCount; "abi3RetainedNodes", box unitDropResult.RetainedNodeCount ]))
        use emptyResult = emptyProgram.ExecuteRetained("mailbox.empty-value", options = nativeOptions 8 1 8 1)
        let emptyValues = emptyResult.Decode()
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/empty-record-zero-field-value" (emptyValues = emptyExpected) (ValueInspection.toJson entries.Program emptyValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyValues); "abi3RetainedBytes", box emptyResult.RetainedByteCount; "abi3RetainedNodes", box emptyResult.RetainedNodeCount ]))
        use emptyIdentityResult = emptyIdentityProgram.ExecuteRetainedWithInputs("native-value-stack-empty-record-identity", Some emptyResult, [ IrEntryArgument.RetainedRoot 0 ], options = nativeOptions 8 1 8 1)
        let emptyIdentityValues = emptyIdentityResult.Decode()
        let abi3EmptyBytes = fixedCases.GetProperty("emptyRecord").GetProperty("abi3RetainedBytes").GetInt32()
        let abi3EmptyNodes = fixedCases.GetProperty("emptyRecord").GetProperty("abi3RetainedNodes").GetInt32()
        recordCheck checks failures $"abi3/{optimization}/empty-record-zero-width-identity" (emptyIdentityValues = emptyExpected && emptyIdentityResult.RetainedByteCount = abi3EmptyBytes && emptyIdentityResult.RetainedNodeCount = abi3EmptyNodes) (jsonObject [
            "values", box (ValueInspection.toJson entries.Program emptyIdentityValues)
            "retainedBytes", box emptyIdentityResult.RetainedByteCount
            "retainedNodes", box emptyIdentityResult.RetainedNodeCount
            "note", box "ABI3 encodes a zero-width Empty value as one retained node and zero retained bytes; the owning candidate separately uses an eight-byte token extent." ])
        for index, scopedCase in fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            use scopedResult = scopedProgram.ExecuteRetainedWithInputs(
                "mailbox.scoped-width",
                None,
                [ IrEntryArgument.IntArgument input ],
                options = nativeOptions 128 16 24 2)
            let values = scopedResult.Decode()
            recordCheck checks failures $"abi3/{optimization}/scoped-width-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            fixedCaseReports.Add(box (jsonObject [ "case", box "scoped-width"; "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program values); "abi3RetainedBytes", box scopedResult.RetainedByteCount; "abi3RetainedNodes", box scopedResult.RetainedNodeCount ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        use shadowResult = scopeShadowProgram.ExecuteRetained("native-value-stack-scope-local-shadow", options = nativeOptions 256 32 24 2)
        let shadowValues = shadowResult.Decode()
        recordCheck checks failures $"abi3/{optimization}/scope-shadow-restores-outer-width" (shadowValues = shadowExpected) (ValueInspection.toJson entries.Program shadowValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowValues); "abi3RetainedBytes", box shadowResult.RetainedByteCount; "abi3RetainedNodes", box shadowResult.RetainedNodeCount ]))
        use depth64Result = depth64Program.ExecuteRetainedWithInputs("mailbox.depth-64", None, [ IrEntryArgument.IntArgument 42L ], options = nativeOptions 65536 4096 8 1)
        let depth64Values = depth64Result.Decode()
        recordCheck checks failures $"abi3/{optimization}/call-depth-64-succeeds" (depth64Values = [ IntValue 42L ]) (ValueInspection.toJson entries.Program depth64Values)
        let depth65Code =
            try
                use _unexpected = depth65Program.ExecuteRetainedWithInputs("mailbox.depth-65", None, [ IrEntryArgument.IntArgument 42L ], options = nativeOptions 65536 4096 8 1)
                "unexpected-success"
            with error -> diagnosticCode error
        recordCheck checks failures $"abi3/{optimization}/call-depth-65-diagnostic" (depth65Code = "RUNTIME_CALL_DEPTH") depth65Code
    jsonObject [
        "backend", box $"abi3-sharedgraph/{optimization}"
        "repetitions", box repetition
        "minimumScratchBytes", box scratchBytes
        "minimumScratchNodes", box scratchNodes
        "retainedBytesAfterTurn1", box first.RetainedByteCount
        "retainedNodesAfterTurn1", box first.RetainedNodeCount
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "directDupDrop", jsonNode options (ValueInspection.toData entries.Program directValues)
        "branchRuns", box branchReports
        "failureAfterAllocation", box failCode
        "fixedLayoutCases", box (fixedCaseReports.ToArray()) ]

let private stackMetrics (result: obj) = getProperty result "Metrics"

let private metricSummary (metrics: obj) =
    let typed = metrics :?> OwningStackMetrics
    let boolean name =
        let value = getProperty metrics name
        if isNull value then invalidOp $"Expected metric property '{name}'."
        Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    let optionInt = function Some value -> box value | None -> null
    jsonObject [
        "reservedArenaCapacityBytes", box (int64Property metrics "StackCapacityBytes")
        "occupiedCursorPeakBytes", box (int64Property metrics "ReservedStackBytes")
        "occupiedCursorFinalBytes", box (int64Property metrics "FinalCursorBytes")
        "logicalRootBytes", null
        "uniqueLivePayloadBytes", optionInt typed.UniqueLivePayloadBytes
        "deadInteriorBytes", null
        "livePayloadAccounting", box (string (getProperty metrics "LivePayloadAccountingUnavailableReason"))
        "legacyPeakLivePayloadCounterBytes", null
        "legacyPeakLiveLocalPayloadCounterBytes", null
        "reservedLocalBytes", box (int64Property metrics "ReservedLocalBytes")
        "inputBytes", box (int64Property metrics "InputBytes")
        "inputCopyBytes", box (int64Property metrics "InputCopyBytes")
        "hostInputStagingBytes", box (int64Property metrics "HostInputStagingBytes")
        "hostEncodedInputBytes", box (int64Property metrics "HostEncodedInputBytes")
        "hostInputExtentTableBytes", box (int64Property metrics "HostInputExtentTableBytes")
        "retainedCapacityBytes", box (int64Property metrics "RetainedCapacityBytes")
        "hostRetainedStagingBytes", box (int64Property metrics "HostRetainedStagingBytes")
        "hostRetainedCommitBytes", box (int64Property metrics "HostRetainedCommitBytes")
        "deepCopyBytes", box (int64Property metrics "DeepCopyBytes")
        "moveBytes", box (int64Property metrics "MoveBytes")
        "retainedCopyBytes", box (int64Property metrics "RetainedCopyBytes")
        "descriptorTransferCount", optionInt typed.DescriptorTransferCount
        "descriptorTransferBytes", optionInt typed.DescriptorTransferBytes
        "cursorInvariantChecks", box (int64Property metrics "CursorInvariantChecks")
        "frameReturnCount", box (int64Property metrics "FrameReturnCount")
        "duplicateDisjointChecks", box (int64Property metrics "DuplicateDisjointChecks")
        "dropSurvivorChecks", box (int64Property metrics "DropSurvivorChecks")
        "poisonReuseChecks", box (int64Property metrics "PoisonReuseChecks")
        "traceEventCount", box (int64Property metrics "TraceEventCount")
        "traceEventCapacity", box (int64Property metrics "TraceEventCapacity")
        "traceEventTruncated", box (boolean "TraceTruncated")
        "instrumentationReservedBytes", box (int64Property metrics "InstrumentationReservedBytes")
        "backendMetadataPerFrameBytes", box (int64Property metrics "BackendMetadataPerFrameBytes")
        "backendMetadataPeakBoundBytes", box (int64Property metrics "BackendMetadataPeakBoundBytes")
        "runtimeLayoutScannerScratchBytes", box (int64Property metrics "RuntimeLayoutScannerScratchBytes")
        "finalLivePayloadBytes", null
        "finalLivePayloadAccounting", box "unavailable; final cursor reset is the request-completion oracle" ]

let private descriptorTransferMetricsPass (metrics: OwningStackMetrics) (events: OwningStackLayoutEvent list) =
    let transferEvents = events |> List.filter (fun event -> event.Kind = "descriptor-transfer")
    if metrics.TraceTruncated then
        metrics.DescriptorTransferCount.IsNone && metrics.DescriptorTransferBytes.IsNone
    else
        metrics.DescriptorTransferCount = Some transferEvents.Length
        && metrics.DescriptorTransferBytes = Some(transferEvents.Length * 20)
        && (transferEvents |> List.forall (fun event -> event.ExtentBytes = 20))

let private substringCount (source: string) (needle: string) =
    let mutable found = 0
    let mutable offset = 0
    let mutable next = source.IndexOf(needle, offset, StringComparison.Ordinal)
    while next >= 0 do
        found <- found + 1
        offset <- next + needle.Length
        next <- source.IndexOf(needle, offset, StringComparison.Ordinal)
    found

let private generatedStablePolicyPass (program: OwningStackCompiledProgram) expectedRewindSites =
    let ir = program.LlvmIr
    let frameMarker = "define internal i32 @agentlang_entry_frame("
    let frameStart = ir.IndexOf(frameMarker, StringComparison.Ordinal)
    let frameEnd = if frameStart < 0 then -1 else ir.IndexOf("\ndefine internal i32 @", frameStart + frameMarker.Length, StringComparison.Ordinal)
    let entryFrame = if frameStart < 0 then "" elif frameEnd < 0 then ir.Substring(frameStart) else ir.Substring(frameStart, frameEnd - frameStart)
    let rewindSites = substringCount entryFrame "call void @al_owning_record_layout(ptr %ctx, i32 18,"
    let runtimeLivenessQueries =
        [ "@al_owning_can_rewind"; "@al_owning_find_live"; "@al_owning_scan_roots"; "@al_owning_any_live_range" ]
        |> List.filter (fun symbol -> ir.Contains(symbol, StringComparison.Ordinal))
    frameStart >= 0 && rewindSites = expectedRewindSites && runtimeLivenessQueries.IsEmpty, jsonObject [
        "entryFrameFound", box (frameStart >= 0)
        "authorizedRewindEventSites", box rewindSites
        "expectedAuthorizedRewindEventSites", box expectedRewindSites
        "runtimeLivenessQuerySymbols", box runtimeLivenessQueries ]

let private generatedCalledFunctionRewindPass (program: OwningStackCompiledProgram) expectedRewindSites =
    let ir = program.LlvmIr
    let frameMarker = "define internal i32 @agentlang_entry_frame("
    let frameStart = ir.IndexOf(frameMarker, StringComparison.Ordinal)
    let frameEnd = if frameStart < 0 then -1 else ir.IndexOf("\ndefine internal i32 @", frameStart + frameMarker.Length, StringComparison.Ordinal)
    let entryFrame = if frameStart < 0 then "" elif frameEnd < 0 then ir.Substring(frameStart) else ir.Substring(frameStart, frameEnd - frameStart)
    let callPrefix = "call i32 @agentlang_word_"
    let symbols = ResizeArray<string>()
    let mutable offset = 0
    let mutable callStart = entryFrame.IndexOf(callPrefix, offset, StringComparison.Ordinal)
    while callStart >= 0 do
        let symbolStart = callStart + "call i32 ".Length
        let symbolEnd = entryFrame.IndexOf('(', symbolStart)
        if symbolEnd > symbolStart then symbols.Add(entryFrame.Substring(symbolStart, symbolEnd - symbolStart))
        offset <- if symbolEnd < 0 then entryFrame.Length else symbolEnd + 1
        callStart <- entryFrame.IndexOf(callPrefix, offset, StringComparison.Ordinal)
    let calledFunctionTexts =
        symbols
        |> Seq.map (fun symbol ->
            let marker = $"define internal i32 {symbol}("
            let start = ir.IndexOf(marker, StringComparison.Ordinal)
            let finish = if start < 0 then -1 else ir.IndexOf("\ndefine internal i32 @", start + marker.Length, StringComparison.Ordinal)
            if start < 0 then "" elif finish < 0 then ir.Substring(start) else ir.Substring(start, finish - start))
        |> Seq.toList
    let rewindSites = calledFunctionTexts |> List.map (fun text -> substringCount text "call void @al_owning_record_layout(ptr %ctx, i32 18,")
    let runtimeLivenessQueries =
        [ "@al_owning_can_rewind"; "@al_owning_find_live"; "@al_owning_scan_roots"; "@al_owning_any_live_range" ]
        |> List.filter (fun symbol -> ir.Contains(symbol, StringComparison.Ordinal))
    let passed = symbols.Count = 1 && calledFunctionTexts.Length = 1 && rewindSites = [ expectedRewindSites ] && runtimeLivenessQueries.IsEmpty
    passed, jsonObject [
        "calledFunctionSymbols", box (symbols |> Seq.toList)
        "calledFunctionRewindSites", box rewindSites
        "expectedCalledFunctionRewindSites", box expectedRewindSites
        "runtimeLivenessQuerySymbols", box runtimeLivenessQueries ]

let private checkTraceUsable (checks: ResizeArray<obj>) (failures: ResizeArray<string>) name (metrics: obj) (events: OwningStackLayoutEvent list) =
    let eventCount = events.Length
    let traceCount = int64Property metrics "TraceEventCount"
    let traceCapacity = int64Property metrics "TraceEventCapacity"
    let traceTruncated = Convert.ToBoolean(getProperty metrics "TraceTruncated", CultureInfo.InvariantCulture)
    let passed = not traceTruncated && eventCount >= 0 && int64 eventCount = traceCount && traceCount <= traceCapacity
    recordCheck checks failures name passed (jsonObject [
        "eventArrayCount", box eventCount
        "traceEventCount", box traceCount
        "traceEventCapacity", box traceCapacity
        "traceEventTruncated", box traceTruncated ])
    passed

let private stateBytesFromJson (state: JsonElement) =
    let last = state.GetProperty("last")
    let leafValue = last.GetProperty("leaf").GetProperty("value").GetInt64()
    bytesFromInt64s [ state.GetProperty("count").GetInt64(); leafValue; last.GetProperty("tag").GetInt64() ]

let private int64FromBytesLittleEndian (bytes: byte array) offset =
    if offset < 0 || offset + 8 > bytes.Length then invalidArg (nameof bytes) "A fixed scalar read exceeds the retained byte buffer."
    let mutable bits = 0UL
    for index in 0 .. 7 do bits <- bits ||| (uint64 bytes[offset + index] <<< (index * 8))
    int64 bits

let private stateFromRetainedBytes (bytes: byte array) =
    if bytes.Length <> 24 then invalidArg (nameof bytes) "State fixture bytes must have the pinned 24-byte layout."
    RecordValue("State", Map.ofList [
        "count", IntValue(int64FromBytesLittleEndian bytes 0)
        "last", RecordValue("Envelope", Map.ofList [
            "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue(int64FromBytesLittleEndian bytes 8) ])
            "tag", IntValue(int64FromBytesLittleEndian bytes 16) ]) ])

let private envelopeBytesFromJson (envelope: JsonElement) =
    bytesFromInt64s [ envelope.GetProperty("leaf").GetProperty("value").GetInt64(); envelope.GetProperty("tag").GetInt64() ]

let private bufferCheckDetails (expected: byte array) (actual: byte array) =
    jsonObject [ "unchanged", box (expected = actual); "expectedHex", box (bytesHex expected); "actualHex", box (bytesHex actual); "lengthBytes", box actual.Length ]

let private runOwningStackChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody)
    (candidateSummaries: Dictionary<string, obj>) =
    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", string optimization, name)) body
    use initialProgram = compile "initialize" entries.Initialize
    use turnProgram = compile $"turn-{repetition}" turnBody
    use branchProgram = compile "branch" entries.Branch
    use directProgram = compile "direct-dup-drop" entries.DirectDupDrop
    use failProgram = compile "fail-after-allocation" entries.FailAfterAllocation
    let stackCapacity = 512
    let executionStackCapacity = fixture.GetProperty("owningStack").GetProperty("stableArenaExecutionCapacityBytes").GetInt32()
    let stateCapacity = 24
    let initialElement = fixture.GetProperty("initialState")
    let expectedInitial = expectedValues initialElement
    let oldRetainedState = bytesFromHex (fixture.GetProperty("initialRetainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/initial-little-endian-layout-bytes" (stateBytesFromJson initialElement = oldRetainedState) (jsonObject [
        "fixtureHex", box (bytesHex oldRetainedState)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson initialElement)) ])
    if not BitConverter.IsLittleEndian then invalidOp "The candidate ABI fixture currently pins little-endian Int64 bytes."
    let initialOutput = Array.create stateCapacity 0xA5uy
    let initialResult = initialProgram.ExecuteInto([], stackCapacity, initialOutput)
    let initialValues = getProperty (box initialResult) "Values" :?> Value list
    let initialMetrics = stackMetrics (box initialResult)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/initialize" (initialValues = expectedInitial) (ValueInspection.toJson entries.Program initialValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/initialize-retained-bytes" (initialOutput = oldRetainedState) (bufferCheckDetails oldRetainedState initialOutput)
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let firstInput = turns[0].GetProperty("input").GetInt64()
    let firstOutput = Array.create stateCapacity 0xA5uy
    let firstResult = turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], executionStackCapacity, firstOutput)
    let firstValues = getProperty (box firstResult) "Values" :?> Value list
    let firstMetrics = stackMetrics (box firstResult)
    let expectedFirstElement = turns[0].GetProperty("expectedState")
    let expectedFirst = expectedValues expectedFirstElement
    let expectedFirstBytes = bytesFromHex (turns[0].GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/turn-1-little-endian-layout-bytes" (stateBytesFromJson expectedFirstElement = expectedFirstBytes) (jsonObject [
        "fixtureHex", box (bytesHex expectedFirstBytes)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson expectedFirstElement)) ])
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1-retained-bytes" (firstOutput = expectedFirstBytes) (bufferCheckDetails expectedFirstBytes firstOutput)
    let mutable stableArenaCapacityControlReport: obj = null
    if repetition = 1 then
        let control = fixture.GetProperty("owningStack").GetProperty("stableArenaCapacityControl")
        let controlBuffer = Array.copy oldRetainedState
        let controlBefore = Array.copy controlBuffer
        let controlResult =
            try
                Some(turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, controlBuffer))
            with _ -> None
        let expectedPeak = control.GetProperty("sourceDerivedPeakCursorBytes").GetProperty("1").GetInt32()
        let expectedInputBytes =
            control.GetProperty("inputExtentsBytes").EnumerateArray()
            |> Seq.sumBy (fun item -> item.GetInt32())
        match controlResult with
        | Some result ->
            let metrics = result.Metrics
            let controlPassed =
                control.GetProperty("n1FitsLegacyCapacity").GetBoolean()
                && result.Values = firstValues
                && controlBuffer = firstOutput
                && controlBuffer <> controlBefore
                && metrics.StackCapacityBytes = stackCapacity
                && int64Property (box metrics) "ReservedStackBytes" = int64 expectedPeak
                && int64Property (box metrics) "FinalCursorBytes" = 0L
                && int64Property (box metrics) "InputCopyBytes" = int64 expectedInputBytes
                && int64Property (box metrics) "MoveBytes" = 0L
                && descriptorTransferMetricsPass metrics result.LayoutEvents
            recordCheck checks failures $"owning-stack/{optimization}/N=1/stable-arena-512-byte-capacity-control" controlPassed (jsonObject [
                "capacityBytes", box stackCapacity
                "expectedSourceDerivedPeakCursorBytes", box expectedPeak
                "actualPeakCursorBytes", box metrics.ReservedStackBytes
                "resultMatchesSufficientCapacityRun", box (result.Values = firstValues && controlBuffer = firstOutput)
                "metrics", box (metricSummary (box metrics)) ])
            checkTraceUsable checks failures $"owning-stack/{optimization}/N=1/stable-arena-512-byte-capacity-control-trace" metrics result.LayoutEvents |> ignore
            stableArenaCapacityControlReport <- jsonObject [
                "repetitionCount", box repetition
                "capacityBytes", box stackCapacity
                "outcome", box "success"
                "expectedSourceDerivedPeakCursorBytes", box expectedPeak
                "actualPeakCursorBytes", box metrics.ReservedStackBytes
                "callerBufferMatchesSemanticRun", box (controlBuffer = firstOutput)
                "metrics", box (metricSummary (box metrics)) ]
        | None ->
            recordCheck checks failures $"owning-stack/{optimization}/N=1/stable-arena-512-byte-capacity-control" false "The source-derived 184-byte N=1 schedule must fit the preserved 512-byte capacity."
    elif repetition = 8 then
        let control = fixture.GetProperty("owningStack").GetProperty("stableArenaCapacityControl")
        let controlBuffer = Array.copy oldRetainedState
        let controlBefore = Array.copy controlBuffer
        let failure =
            try
                turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, controlBuffer) |> ignore
                None
            with error -> Some error
        match failure with
        | Some error ->
            let metrics = getProperty error "Metrics"
            let required = int64Property error "RequiredBytes"
            let available = int64Property error "AvailableBytes"
            let inputBytes =
                control.GetProperty("inputExtentsBytes").EnumerateArray()
                |> Seq.sumBy (fun item -> item.GetInt32())
            let schedule =
                int64 inputBytes
                + int64 (control.GetProperty("preExerciseAllocationBytes").GetInt32())
                + int64 (control.GetProperty("n8CompletedExercisesBeforeFailure").GetInt32()) * int64 (control.GetProperty("perExerciseAllocationBytes").GetInt32())
                + int64 (control.GetProperty("n8SeventhExercisePrefixBytes").GetInt32())
                + int64 (control.GetProperty("n8FinalIntResultBytes").GetInt32())
            let expectedRequired = int64 (control.GetProperty("sourceDerivedN8FirstRejectedReservationBytes").GetInt32())
            let traceTruncated = Convert.ToBoolean(getProperty metrics "TraceTruncated", CultureInfo.InvariantCulture)
            let controlPassed =
                control.GetProperty("n8ExceedsLegacyCapacity").GetBoolean()
                && schedule = expectedRequired
                && (getProperty error "Code" |> string) = "OWNING_STACK_CAPACITY"
                && (getProperty error "Boundary" |> string) = control.GetProperty("boundary").GetString()
                && available = int64 stackCapacity
                && required = expectedRequired
                && required > available
                && int64Property metrics "FrameReturnCount" > 0L
                && int64Property metrics "DeepCopyBytes" > 0L
                && int64Property metrics "MoveBytes" = 0L
                && int64Property metrics "FinalCursorBytes" = 0L
                && controlBuffer = controlBefore
                && not traceTruncated
            recordCheck checks failures $"owning-stack/{optimization}/N=8/stable-arena-512-byte-capacity-control" controlPassed (jsonObject [
                "capacityBytes", box stackCapacity
                "sourceDerivedFirstRejectedReservationBytes", box schedule
                "actualFirstRejectedReservationBytes", box required
                "availableBytes", box available
                "frameReturnsBeforeFailure", box (int64Property metrics "FrameReturnCount")
                "deepCopyBytesBeforeFailure", box (int64Property metrics "DeepCopyBytes")
                "moveBytesBeforeFailure", box (int64Property metrics "MoveBytes")
                "traceTruncated", box traceTruncated
                "cursorResetToZero", box (int64Property metrics "FinalCursorBytes" = 0L)
                "callerBufferUnchanged", box (controlBuffer = controlBefore)
                "metrics", box (metricSummary metrics) ])
            stableArenaCapacityControlReport <- jsonObject [
                "repetitionCount", box repetition
                "capacityBytes", box stackCapacity
                "outcome", box "capacity-failure"
                "sourceDerivedFirstRejectedReservationBytes", box schedule
                "actualFirstRejectedReservationBytes", box required
                "callerBufferUnchanged", box (controlBuffer = controlBefore)
                "metrics", box (metricSummary metrics) ]
        | None ->
            recordCheck checks failures $"owning-stack/{optimization}/N=8/stable-arena-512-byte-capacity-control" false "The source-derived 632-byte N=8 schedule must exceed the preserved 512-byte capacity."
    let firstPublishedState = stateFromRetainedBytes firstOutput
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1-raw-publication-decodes-to-next-input" (firstValues = [ firstPublishedState ]) (jsonObject [
        "decodedFromRawRetainedHex", box (bytesHex firstOutput)
        "decodedValue", ValueInspection.toJson entries.Program [ firstPublishedState ] ])
    let secondInput = turns[1].GetProperty("input").GetInt64()
    let secondOutput = Array.create stateCapacity 0xA5uy
    let secondResult = turnProgram.ExecuteInto([ firstPublishedState; IntValue secondInput ], executionStackCapacity, secondOutput)
    let secondValues = getProperty (box secondResult) "Values" :?> Value list
    let secondMetrics = stackMetrics (box secondResult)
    let expectedSecondElement = turns[1].GetProperty("expectedState")
    let expectedSecond = expectedValues expectedSecondElement
    let expectedSecondBytes = bytesFromHex (turns[1].GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/turn-2-little-endian-layout-bytes" (stateBytesFromJson expectedSecondElement = expectedSecondBytes) (jsonObject [
        "fixtureHex", box (bytesHex expectedSecondBytes)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson expectedSecondElement)) ])
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-2" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-2-retained-bytes" (secondOutput = expectedSecondBytes) (bufferCheckDetails expectedSecondBytes secondOutput)
    let stableCapacitySchedule = fixture.GetProperty("owningStack").GetProperty("stableArenaCapacityControl")
    let scheduleInputBytes =
        stableCapacitySchedule.GetProperty("inputExtentsBytes").EnumerateArray()
        |> Seq.sumBy (fun item -> item.GetInt32())
    let sourceDerivedPeak =
        scheduleInputBytes
        + stableCapacitySchedule.GetProperty("preExerciseAllocationBytes").GetInt32()
        + stableCapacitySchedule.GetProperty("perExerciseAllocationBytes").GetInt32() * repetition
        + stableCapacitySchedule.GetProperty("postExerciseAllocationBytes").GetInt32()
    let expectedSchedulePeak = stableCapacitySchedule.GetProperty("sourceDerivedPeakCursorBytes").GetProperty(string repetition).GetInt32()
    let cursorSchedulePass =
        stackCapacity = stableCapacitySchedule.GetProperty("legacyCapacityBytes").GetInt32()
        && executionStackCapacity >= expectedSchedulePeak
        && sourceDerivedPeak = expectedSchedulePeak
        && int64Property firstMetrics "ReservedStackBytes" = int64 expectedSchedulePeak
        && int64Property secondMetrics "ReservedStackBytes" = int64 expectedSchedulePeak
        && int64Property firstMetrics "StackCapacityBytes" = int64 executionStackCapacity
        && int64Property secondMetrics "StackCapacityBytes" = int64 executionStackCapacity
        && int64Property firstMetrics "FinalCursorBytes" = 0L
        && int64Property secondMetrics "FinalCursorBytes" = 0L
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/source-derived-stable-cursor-schedule" cursorSchedulePass (jsonObject [
        "inputBytes", box scheduleInputBytes
        "preExerciseAllocationBytes", box (stableCapacitySchedule.GetProperty("preExerciseAllocationBytes").GetInt32())
        "perExerciseAllocationBytes", box (stableCapacitySchedule.GetProperty("perExerciseAllocationBytes").GetInt32())
        "repetitions", box repetition
        "postExerciseAllocationBytes", box (stableCapacitySchedule.GetProperty("postExerciseAllocationBytes").GetInt32())
        "expectedPeakCursorBytes", box expectedSchedulePeak
        "sourceDerivedFormulaPeakCursorBytes", box sourceDerivedPeak
        "turn1ActualPeakCursorBytes", box (int64Property firstMetrics "ReservedStackBytes")
        "turn2ActualPeakCursorBytes", box (int64Property secondMetrics "ReservedStackBytes") ])
    let retainedBefore = Array.copy oldRetainedState
    let retainedBuffer = Array.copy retainedBefore
    let retainedCapacityFailure =
        try
            turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], executionStackCapacity, retainedBuffer, 23) |> ignore
            None
        with error -> Some error
    match retainedCapacityFailure with
    | Some error ->
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let required = int64Property error "RequiredBytes"
        let available = int64Property error "AvailableBytes"
        let metrics = getProperty error "Metrics"
        let finalCursor = int64Property metrics "FinalCursorBytes"
        let unchanged = retainedBuffer = retainedBefore
        let passed = code = "OWNING_RETAINED_CAPACITY" && boundary = "retained-output" && required = 24L && available = 23L && finalCursor = 0L && unchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/retained-capacity-buffer-atomicity" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails retainedBefore retainedBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/retained-capacity-buffer-atomicity" false "Retained output capacity one byte below State's inline payload unexpectedly succeeded."
    let stackFailureBuffer = Array.copy oldRetainedState
    let stackFailureBefore = Array.copy stackFailureBuffer
    let stackCapacityFailure =
        try
            turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], 1, stackFailureBuffer) |> ignore
            None
        with error -> Some error
    match stackCapacityFailure with
    | Some error ->
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let finalCursor = int64Property (getProperty error "Metrics") "FinalCursorBytes"
        let unchanged = stackFailureBuffer = stackFailureBefore
        let required = int64Property error "RequiredBytes"
        let available = int64Property error "AvailableBytes"
        let passed = code = "OWNING_STACK_CAPACITY" && boundary = "host-input-encoding" && required = 32L && available = 1L && finalCursor = 0L && unchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/host-input-encoding-capacity-buffer-atomicity" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "requiredInputBytes", box required
            "availableStackCapacityBytes", box available
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails stackFailureBefore stackFailureBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/host-input-encoding-capacity-buffer-atomicity" false "One-byte stack capacity unexpectedly accepted the 32-byte host input encoding."
    let diagnosticBuffer = Array.copy oldRetainedState
    let diagnosticBefore = Array.copy diagnosticBuffer
    let diagnostic, diagnosticMetrics =
        try
            failProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, diagnosticBuffer) |> ignore
            "unexpected-success", null
        with error -> diagnosticCode error, getProperty error "Metrics"
    let diagnosticFinalCursor = if isNull diagnosticMetrics then -1L else int64Property diagnosticMetrics "FinalCursorBytes"
    let diagnosticBufferUnchanged = diagnosticBuffer = diagnosticBefore
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/error-after-allocation-buffer-atomicity" (diagnostic = "RUNTIME_DIVIDE_BY_ZERO" && diagnosticFinalCursor = 0L && diagnosticBufferUnchanged) (jsonObject [
        "code", box diagnostic
        "finalCursorBytes", box diagnosticFinalCursor
        "callerBuffer", bufferCheckDetails diagnosticBefore diagnosticBuffer ])
    let directOutput = Array.create 16 0xA5uy
    let directResult = directProgram.ExecuteInto([ IntValue 17L; IntValue 1017L ], stackCapacity, directOutput)
    let directValues = getProperty (box directResult) "Values" :?> Value list
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    let directMetrics = stackMetrics (box directResult)
    let directEvents = getProperty (box directResult) "LayoutEvents" :?> OwningStackLayoutEvent list
    let directRanges = layoutEventRanges directEvents
    let duplicateEvents = directRanges |> Array.filter (fun event -> event.Kind = 2L)
    let dropEvents = directRanges |> Array.filter (fun event -> event.Kind = 3L)
    let callReturnEvents = directRanges |> Array.filter (fun event -> event.Kind = 9L)
    let retainedEvents = directRanges |> Array.filter (fun event -> event.Kind = 10L)
    let descriptorTransferEvents = directRanges |> Array.filter (fun event -> event.Kind = 17L)
    let droppedDuplicates =
        duplicateEvents
        |> Array.collect (fun duplicate ->
            dropEvents
            |> Array.filter (fun dropped -> dropped.Offset = duplicate.Offset && dropped.Extent = duplicate.Extent && dropped.Payload = duplicate.Payload))
    let duplicateIndex =
        directRanges |> Array.tryFindIndex (fun event -> event.Kind = 2L) |> Option.defaultValue -1
    let droppedDuplicateIndex =
        directRanges
        |> Array.tryFindIndex (fun event ->
            duplicateEvents.Length = 1
            && event.Kind = 3L
            && event.Offset = duplicateEvents[0].Offset
            && event.Extent = duplicateEvents[0].Extent)
        |> Option.defaultValue -1
    let retainedCopyIndex = directRanges |> Array.tryFindIndex (fun event -> event.Kind = 10L) |> Option.defaultValue -1
    let directDuplicateRangesPass =
        duplicateEvents.Length = 1
        && droppedDuplicates.Length = 1
        && retainedEvents.Length = 1
        && callReturnEvents.Length = 0
        && duplicateIndex >= 0
        && duplicateEvents[0].Extent = 16L
        && duplicateEvents[0].Payload = 16L
        && duplicateEvents[0].SourceExtent = 16L
        && (duplicateEvents[0].Offset + duplicateEvents[0].Extent <= duplicateEvents[0].SourceOffset
            || duplicateEvents[0].SourceOffset + duplicateEvents[0].SourceExtent <= duplicateEvents[0].Offset)
        && droppedDuplicates[0].Offset = duplicateEvents[0].Offset
        && droppedDuplicates[0].Extent = duplicateEvents[0].Extent
        && duplicateIndex < droppedDuplicateIndex
        && retainedCopyIndex > droppedDuplicateIndex
        && retainedEvents[0].SourceOffset = duplicateEvents[0].SourceOffset
        && retainedEvents[0].SourceExtent = duplicateEvents[0].SourceExtent
        && (descriptorTransferEvents |> Array.exists (fun event -> event.Offset = duplicateEvents[0].SourceOffset && event.Payload = 16L))
        && retainedEvents[0].Payload = 16L
    let directLayouts = getProperty (box directResult) "Layouts" :?> OwningStackTypeLayout list
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-top-dup-drop" (directValues = directExpected) (ValueInspection.toJson entries.Program directValues)
    let directExpectedBytes = bytesFromHex (fixture.GetProperty("directTopDupDrop").GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/direct-Envelope-little-endian-layout-bytes" (directExpectedBytes = bytesFromInt64s [ 17L; 1017L ]) (box (bytesHex directExpectedBytes))
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-retained-bytes" (directOutput = directExpectedBytes) (bufferCheckDetails directExpectedBytes directOutput)
    let directTraceComplete = checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/direct-trace-complete" directMetrics directEvents
    let directMetricsTyped = directMetrics :?> OwningStackMetrics
    let directTransferMetricsPass = descriptorTransferMetricsPass directMetricsTyped directEvents
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-dup-drop-keeps-survivor-address" (directDuplicateRangesPass && directTraceComplete && directTransferMetricsPass && int64Property directMetrics "MoveBytes" = 0L && int64Property directMetrics "DeepCopyBytes" = 40L) (jsonObject [
        "duplicateEvents", box duplicateEvents
        "dropEvents", box dropEvents
        "matchingDroppedDuplicateEvents", box droppedDuplicates
        "callReturnMoveEvents", box callReturnEvents
        "descriptorTransferEvents", box descriptorTransferEvents
        "retainedCopyEvents", box retainedEvents
        "expectedDeepCopyBytes", box 40
        "measuredDeepCopyBytes", box (int64Property directMetrics "DeepCopyBytes")
        "transferMetricsMatchCompleteTrace", box directTransferMetricsPass ])
    let allocatedScalars = directRanges |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L && event.Payload = 8L) |> Array.sortBy (fun event -> event.Offset)
    let adjacentScalars = allocatedScalars |> Array.pairwise |> Array.exists (fun (left, right) -> left.Offset + left.Extent = right.Offset)
    let noCompactionEvents = directEvents |> List.forall (fun event -> event.Kind <> "local-compact")
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/descriptor-path-does-not-compact-live-payload" (adjacentScalars && noCompactionEvents && int64Property directMetrics "MoveBytes" = 0L) (jsonObject [
        "adjacentFreshScalarAllocations", box allocatedScalars
        "localCompactEvents", box (directEvents |> List.filter (fun event -> event.Kind = "local-compact") |> layoutEventDetails)
        "moveBytes", box (int64Property directMetrics "MoveBytes") ])
    let directDeepCopyBytes = int64Property directMetrics "DeepCopyBytes"
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/record-construction-and-dup-copy-categories" (directDeepCopyBytes = 40L && int64Property directMetrics "MoveBytes" = 0L && int64Property directMetrics "InputCopyBytes" = 16L && int64Property directMetrics "RetainedCopyBytes" = 16L) (jsonObject [
        "expectedMakeRecordConstructionBytes", box 24
        "expectedExplicitDuplicateBytes", box 16
        "expectedDeepCopyBytes", box 40
        "measuredDeepCopyBytes", box directDeepCopyBytes
        "moveBytes", box (int64Property directMetrics "MoveBytes")
        "inputCopyBytes", box (int64Property directMetrics "InputCopyBytes")
        "retainedCopyBytes", box (int64Property directMetrics "RetainedCopyBytes") ])
    let directReserved = int64Property directMetrics "ReservedStackBytes"
    let directUniqueLive = directMetricsTyped.UniqueLivePayloadBytes
    recordCheck checks failures $"owning-stack/{optimization}/direct-cursor-and-metadata-categories" (directReserved >= 56L && directUniqueLive.IsNone && directTransferMetricsPass) (jsonObject [
        "reservedArenaCapacityBytes", box (int64Property directMetrics "StackCapacityBytes")
        "occupiedCursorPeakBytes", box directReserved
        "logicalRootBytes", null
        "uniqueLivePayloadBytes", box (directUniqueLive |> Option.map box |> Option.defaultValue null)
        "deadInteriorBytes", null
        "backendMetadataPerFrameBytes", box (int64Property directMetrics "BackendMetadataPerFrameBytes")
        "backendMetadataPeakBoundBytes", box (int64Property directMetrics "BackendMetadataPeakBoundBytes")
        "metrics", box (metricSummary directMetrics) ])
    let afterAllocationCapacity = max 0L (directReserved - 1L) |> int
    let afterAllocationBuffer = Array.copy oldRetainedState
    let afterAllocationBefore = Array.copy afterAllocationBuffer
    let afterAllocationFailure =
        try
            directProgram.ExecuteInto([ IntValue 17L; IntValue 1017L ], afterAllocationCapacity, afterAllocationBuffer, stateCapacity) |> ignore
            None
        with error -> Some error
    match afterAllocationFailure with
    | Some error ->
        let metrics = getProperty error "Metrics"
        let frameReturns = int64Property metrics "FrameReturnCount"
        let finalCursor = int64Property metrics "FinalCursorBytes"
        let peakCursor = int64Property metrics "ReservedStackBytes"
        let deepCopyBytes = int64Property metrics "DeepCopyBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let retainedCopyBytes = int64Property metrics "RetainedCopyBytes"
        let bufferUnchanged = afterAllocationBuffer = afterAllocationBefore
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let passed = code = "OWNING_STACK_CAPACITY" && boundary = "program-data-stack" && frameReturns > 0L && peakCursor > 0L && deepCopyBytes > 0L && finalCursor = 0L && inputCopyBytes = 16L && retainedCopyBytes = 0L && bufferUnchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/post-allocation-post-frame-capacity-unwind" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "configuredStackCapacityBytes", box afterAllocationCapacity
            "frameReturnsBeforeFailure", box frameReturns
            "inputCopyBytes", box inputCopyBytes
            "occupiedCursorPeakBytesBeforeFailure", box peakCursor
            "deepCopyBytesBeforeFailure", box deepCopyBytes
            "retainedCopyBytes", box retainedCopyBytes
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails afterAllocationBefore afterAllocationBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/post-allocation-post-frame-capacity-unwind" false "Capacity one byte below the successful program high-water unexpectedly succeeded."
    let directLayoutChecks, directLayoutFailures, directLayoutElement = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] directLayouts
    for details in directLayoutChecks do checks.Add details
    for failure in directLayoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/direct-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-layout-contract" (directLayoutFailures.Length = 0) (jsonNode options directLayoutElement)
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        use unitProgram = compile "unit-value" entries.UnitValue
        use unitDropProgram = compile "unit-drop" entries.UnitDrop
        use emptyProgram = compile "empty-value" entries.EmptyValue
        use emptyIdentityProgram = compile "empty-identity" entries.EmptyIdentity
        use scopedProgram = compile "scoped-width" entries.ScopedWidth
        use scopeShadowProgram = compile "scope-shadow" entries.ScopeShadow
        use depth64Program = compile "depth-64" entries.Depth64
        use depth65Program = compile "depth-65" entries.Depth65
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        let unitExpectedBytes = bytesFromHex (fixedCases.GetProperty("unit").GetProperty("retainedBytesHex").GetString())
        let unitOutput = Array.create unitExpectedBytes.Length 0xA5uy
        let unitResult = unitProgram.ExecuteInto([], stackCapacity, unitOutput)
        let unitLayout = unitResult.Layouts |> List.tryFind (fun layout -> layout.TypeName = "Unit")
        let unitLayoutEvents = layoutEventRanges unitResult.LayoutEvents
        let unitAllocationEvents = unitLayoutEvents |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L)
        let unitLayoutPassed =
            match unitLayout with
            | Some layout ->
                layout.PayloadBytes = fixedCases.GetProperty("unit").GetProperty("payloadBytes").GetInt32()
                && layout.ExtentBytes = fixedCases.GetProperty("unit").GetProperty("stackExtentBytes").GetInt32()
            | None -> false
        let unitAllocationPassed = unitAllocationEvents.Length = 1 && unitAllocationEvents[0].Payload = 8L
        let unitLayoutDetails =
            match unitLayout with
            | Some layout -> jsonObject [ "typeName", box layout.TypeName; "payloadBytes", box layout.PayloadBytes; "extentBytes", box layout.ExtentBytes; "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes ])) ]
            | None -> null
        recordCheck checks failures $"owning-stack/{optimization}/unit-fixed-layout-value" (unitResult.Values = unitExpected) (ValueInspection.toJson entries.Program unitResult.Values)
        recordCheck checks failures $"owning-stack/{optimization}/unit-scalar-layout-and-retained-bytes" (unitLayoutPassed && unitAllocationPassed && unitResult.RetainedBytesWritten = 8 && unitOutput = unitExpectedBytes) (jsonObject [
            "layout", box unitLayoutDetails
            "allocateEvents", box unitAllocationEvents
            "retainedBytesWritten", box unitResult.RetainedBytesWritten
            "retainedBytes", box (bytesHex unitOutput)
            "metrics", jsonNode options (metricSummary (box unitResult.Metrics)) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitResult.Values); "retainedBytes", box (bytesHex unitOutput); "metrics", jsonNode options (metricSummary (box unitResult.Metrics)) ]))
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        let unitDropExpectedBytes = bytesFromHex (fixedCases.GetProperty("unitDrop").GetProperty("retainedBytesHex").GetString())
        let unitDropOutput = Array.create unitDropExpectedBytes.Length 0xA5uy
        let unitDropResult = unitDropProgram.ExecuteInto([], stackCapacity, unitDropOutput)
        let unitDropEvents = unitDropResult.LayoutEvents |> List.filter (fun event -> event.Kind = "drop" && event.PayloadBytes = 8 && event.ExtentBytes = 8)
        let unitDropMetrics = box unitDropResult.Metrics
        let unitDropPassed =
            unitDropResult.Values = unitDropExpected
            && unitDropOutput = unitDropExpectedBytes
            && unitDropResult.RetainedBytesWritten = unitDropExpectedBytes.Length
            && unitDropEvents.Length >= 1
            && int64Property unitDropMetrics "ReservedStackBytes" >= 8L
            && int64Property unitDropMetrics "MoveBytes" = 0L
            && int64Property unitDropMetrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimization}/unit-drop-preserves-semantics-with-stable-cursor" unitDropPassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program unitDropResult.Values)
            "retainedBytes", box (bytesHex unitDropOutput)
            "unitDropEvents", box (layoutEventRanges unitDropEvents)
            "metrics", box (jsonNode options (metricSummary unitDropMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/unit-drop-trace-complete" unitDropResult.Metrics unitDropResult.LayoutEvents |> ignore
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropResult.Values); "retainedBytes", box (bytesHex unitDropOutput); "unitDropEvents", box (layoutEventRanges unitDropEvents); "metrics", jsonNode options (metricSummary unitDropMetrics) ]))
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        let emptyExpectedBytes = bytesFromHex (fixedCases.GetProperty("emptyRecord").GetProperty("retainedBytesHex").GetString())
        let emptyOutput = Array.create emptyExpectedBytes.Length 0xA5uy
        let emptyResult = emptyProgram.ExecuteInto([], stackCapacity, emptyOutput)
        let emptyLayoutChecks, emptyLayoutFailures, emptyLayoutSummary = validateTypeLayouts fixture [ "Empty" ] emptyResult.Layouts
        let emptyAllocationEvents = layoutEventRanges emptyResult.LayoutEvents |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L)
        let emptyTokenAllocationPassed = emptyAllocationEvents.Length = 1 && emptyAllocationEvents[0].Payload = 0L
        for details in emptyLayoutChecks do checks.Add details
        for failure in emptyLayoutFailures do failures.Add($"owning-stack/{optimization}/empty-record-layout: {failure}")
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-zero-field-value" (emptyResult.Values = emptyExpected) (ValueInspection.toJson entries.Program emptyResult.Values)
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-token-extent-and-bytes" (emptyLayoutFailures.Length = 0 && emptyTokenAllocationPassed && emptyResult.RetainedBytesWritten = 8 && emptyOutput = emptyExpectedBytes) (jsonObject [
            "layout", box (jsonNode options emptyLayoutSummary)
            "allocateEvents", box emptyAllocationEvents
            "retainedBytesWritten", box emptyResult.RetainedBytesWritten
            "retainedBytes", box (bytesHex emptyOutput)
            "metrics", box (jsonNode options (metricSummary (box emptyResult.Metrics))) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyResult.Values); "retainedBytes", box (bytesHex emptyOutput); "metrics", jsonNode options (metricSummary (box emptyResult.Metrics)) ]))
        let emptyIdentityOutput = Array.create 8 0xA5uy
        let emptyIdentityResult = emptyIdentityProgram.ExecuteInto([ RecordValue("Empty", Map.empty) ], stackCapacity, emptyIdentityOutput)
        let emptyIdentityMetrics = box emptyIdentityResult.Metrics
        let emptyIdentityPassed =
            emptyIdentityResult.Values = emptyExpected
            && emptyIdentityOutput = emptyExpectedBytes
            && emptyIdentityResult.RetainedBytesWritten = 8
            && int64Property emptyIdentityMetrics "InputBytes" = 8L
            && int64Property emptyIdentityMetrics "InputCopyBytes" = 8L
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-value-input-and-zero-token-decode" emptyIdentityPassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program emptyIdentityResult.Values)
            "inputBytes", box (int64Property emptyIdentityMetrics "InputBytes")
            "inputCopyBytes", box (int64Property emptyIdentityMetrics "InputCopyBytes")
            "retainedBytes", box (bytesHex emptyIdentityOutput)
            "metrics", box (jsonNode options (metricSummary emptyIdentityMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/empty-record-identity-trace-complete" emptyIdentityResult.Metrics emptyIdentityResult.LayoutEvents |> ignore
        checkTraceUsable checks failures $"owning-stack/{optimization}/unit-trace-complete" unitResult.Metrics unitResult.LayoutEvents |> ignore
        checkTraceUsable checks failures $"owning-stack/{optimization}/empty-record-trace-complete" emptyResult.Metrics emptyResult.LayoutEvents |> ignore
        for index, scopedCase in fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            let expectedBytes = bytesFromHex (scopedCase.GetProperty("retainedBytesHex").GetString())
            let output = Array.create expectedBytes.Length 0xA5uy
            let result = scopedProgram.ExecuteInto([ IntValue input ], stackCapacity, output)
            let layoutChecks, layoutFailures, layoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope"; "State" ] result.Layouts
            for details in layoutChecks do checks.Add details
            for failure in layoutFailures do failures.Add($"owning-stack/{optimization}/scoped-width-{index}-layout: {failure}")
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-restores-outer-value" (result.Values = expected) (ValueInspection.toJson entries.Program result.Values)
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-retained-layout-bytes" (layoutFailures.Length = 0 && result.RetainedBytesWritten = expectedBytes.Length && output = expectedBytes) (jsonObject [
                "layout", box (jsonNode options layoutSummary)
                "retainedBytes", box (bytesHex output)
                "metrics", box (jsonNode options (metricSummary (box result.Metrics))) ])
            let cleanupPassed = int64Property (box result.Metrics) "FinalCursorBytes" = 0L
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-local-frame-cleanup" cleanupPassed (jsonNode options (metricSummary (box result.Metrics)))
            checkTraceUsable checks failures $"owning-stack/{optimization}/scoped-width-{index}-trace-complete" result.Metrics result.LayoutEvents |> ignore
            fixedCaseReports.Add(box (jsonObject [ "case", box "scoped-width"; "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program result.Values); "retainedBytes", box (bytesHex output); "metrics", jsonNode options (metricSummary (box result.Metrics)) ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        let shadowExpectedBytes = bytesFromHex (shadowCase.GetProperty("retainedBytesHex").GetString())
        let shadowOutput = Array.create shadowExpectedBytes.Length 0xA5uy
        let shadowResult = scopeShadowProgram.ExecuteInto([], stackCapacity, shadowOutput)
        let shadowLayoutChecks, shadowLayoutFailures, shadowLayoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] shadowResult.Layouts
        for details in shadowLayoutChecks do checks.Add details
        for failure in shadowLayoutFailures do failures.Add($"owning-stack/{optimization}/scope-shadow-layout: {failure}")
        let shadowMetrics = box shadowResult.Metrics
        let shadowEvents = shadowResult.LayoutEvents
        let shadowDescriptorOnly =
            int64Property shadowMetrics "MoveBytes" = 0L
            && (shadowEvents |> List.forall (fun event -> event.Kind <> "call-input-move" && event.Kind <> "call-return-move" && event.Kind <> "local-compact"))
        let shadowScopePassed =
            shadowResult.Values = shadowExpected
            && shadowOutput = shadowExpectedBytes
            && shadowLayoutFailures.Length = 0
            && shadowDescriptorOnly
            && int64Property shadowMetrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimization}/scope-shadow-same-slot-restores-envelope" shadowScopePassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program shadowResult.Values)
            "retainedBytes", box (bytesHex shadowOutput)
            "layout", box (jsonNode options shadowLayoutSummary)
            "descriptorOnlyPathHasNoPayloadMoves", box shadowDescriptorOnly
            "metrics", box (jsonNode options (metricSummary shadowMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/scope-shadow-trace-complete" shadowResult.Metrics shadowResult.LayoutEvents |> ignore
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowResult.Values); "retainedBytes", box (bytesHex shadowOutput); "metrics", jsonNode options (metricSummary shadowMetrics) ]))
        let stableCases = fixture.GetProperty("stableArenaCases")
        let stableDead = stableCases.GetProperty("fixedSixLocalNestedDeadOnly")
        use stableDeadProgram = compile "stable-six-local-dead-only" entries.StableDeadOnlySixLocals
        let stableDeadInputs = stableDead.GetProperty("input").EnumerateArray() |> Seq.map (fun value -> IntValue(value.GetInt64())) |> Seq.toList
        let stableDeadResult = stableDeadProgram.ExecuteInto(stableDeadInputs, stackCapacity, Array.empty)
        let stableDeadMetrics = stableDeadResult.Metrics
        let stableDeadEvents = stableDeadResult.LayoutEvents
        let stableDeadRewinds = stableDeadEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
        let stableDeadIndexedEvents = stableDeadEvents |> List.indexed |> List.toArray
        let stableDeadMark = stableDead.GetProperty("inputCopyBytes").GetInt32()
        let stableDeadRewindPositions =
            stableDeadIndexedEvents
            |> Array.choose (fun (index, event) -> if event.Kind = "arena-rewind" then Some(index, event) else None)
        let stableDeadInnerRewindIndex = stableDeadRewindPositions |> Array.tryItem 0 |> Option.map fst |> Option.defaultValue -1
        let stableDeadReusedAllocation =
            stableDeadIndexedEvents
            |> Array.tryFind (fun (index, event) ->
                index > stableDeadInnerRewindIndex
                && event.Kind = "allocate"
                && event.TypeId = 1u
                && event.OffsetBytes = stableDead.GetProperty("postRewindAllocationOffsetBytes").GetInt32()
                && event.ExtentBytes = stableDead.GetProperty("postRewindAllocationExtentBytes").GetInt32()
                && event.PayloadBytes = 8)
        let stableDeadReusedAllocationIndex = stableDeadReusedAllocation |> Option.map fst |> Option.defaultValue -1
        let stableDeadReusedConstruction =
            stableDeadIndexedEvents
            |> Array.tryFind (fun (index, event) ->
                index > stableDeadReusedAllocationIndex
                && event.Kind = "record-build"
                && event.OffsetBytes = stableDead.GetProperty("postRewindConstructionOffsetBytes").GetInt32()
                && event.ExtentBytes = stableDead.GetProperty("postRewindConstructionExtentBytes").GetInt32()
                && event.PayloadBytes = 8)
        let stableDeadLoadsAfterReuse =
            match stableDeadRewindPositions |> Array.tryItem 1 with
            | None -> [||]
            | Some(outerRewindIndex, _) ->
                stableDeadIndexedEvents
                |> Array.choose (fun (index, event) ->
                    if index > stableDeadReusedAllocationIndex
                       && index < outerRewindIndex
                       && event.Kind = "descriptor-transfer"
                       && event.TypeId = 1u
                       && event.PayloadBytes = 8
                       && event.SourceExtentBytes = Some 8
                       && event.SourceOffsetBytes = Some(event.OffsetBytes + 8) then
                        Some event.OffsetBytes
                    else None)
        let stableDeadAllocationBetweenRewindsPass =
            stableDeadRewindPositions.Length = 3
            && (stableDeadReusedAllocation |> Option.exists (fun (allocationIndex, allocation) ->
                allocationIndex > fst stableDeadRewindPositions[0]
                && allocationIndex < fst stableDeadRewindPositions[1]
                && allocation.OffsetBytes = stableDeadMark))
        let stableDeadConstructionBeforeOuterRewindPass =
            stableDeadRewindPositions.Length = 3
            && (stableDeadReusedConstruction |> Option.exists (fun (index, event) ->
                index < fst stableDeadRewindPositions[1]
                && event.OffsetBytes = stableDead.GetProperty("postRewindConstructionOffsetBytes").GetInt32()
                && event.ExtentBytes = stableDead.GetProperty("postRewindConstructionExtentBytes").GetInt32()
                && event.PayloadBytes = 8))
        let stableDeadInputsRemainAtOriginalOffsetsPass = Array.sort stableDeadLoadsAfterReuse = [| 0; 8; 16; 24; 32; 40 |]
        let stableDeadReusePass =
            stableDeadAllocationBetweenRewindsPass
            && stableDeadConstructionBeforeOuterRewindPass
            && stableDeadInputsRemainAtOriginalOffsetsPass
        let stableDeadRewindRangesPass =
            stableDeadRewinds.Length = stableDead.GetProperty("rewindEventCount").GetInt32()
            && stableDead.GetProperty("nestedScopeCount").GetInt32() + stableDead.GetProperty("functionExitRewindCount").GetInt32() = stableDeadRewinds.Length
            && List.sort (stableDeadRewinds |> List.map (fun event -> event.OffsetBytes, event.ExtentBytes, event.PayloadBytes)) =
               List.sort [
                   stableDeadMark, stableDead.GetProperty("innerRewindExtentBytes").GetInt32(), 0
                   stableDeadMark, stableDead.GetProperty("outerRewindExtentBytes").GetInt32(), 0
                   stableDeadMark, stableDead.GetProperty("functionExitRewindExtentBytes").GetInt32(), 0 ]
        let stableDeadMetricsPass =
            stableDeadResult.Values.IsEmpty
            && stableDeadResult.RetainedBytesWritten = stableDead.GetProperty("outputCount").GetInt32()
            && int64Property (box stableDeadMetrics) "InputCopyBytes" = int64 (stableDead.GetProperty("inputCopyBytes").GetInt32())
            && int64Property (box stableDeadMetrics) "DeepCopyBytes" = int64 (stableDead.GetProperty("deepCopyBytes").GetInt32())
            && int64Property (box stableDeadMetrics) "MoveBytes" = int64 (stableDead.GetProperty("moveBytes").GetInt32())
            && int64Property (box stableDeadMetrics) "ReservedStackBytes" = int64 (stableDead.GetProperty("expectedPeakCursorBytes").GetInt32())
            && int64Property (box stableDeadMetrics) "FinalCursorBytes" = 0L
            && stableDeadRewindRangesPass
            && stableDeadReusePass
            && descriptorTransferMetricsPass stableDeadMetrics stableDeadEvents
        let stableDeadLlvmSites = stableDead.GetProperty("nestedScopeCount").GetInt32() + stableDead.GetProperty("functionExitRewindCount").GetInt32()
        let stableDeadLlvmPass, stableDeadLlvmDetails = generatedStablePolicyPass stableDeadProgram stableDeadLlvmSites
        recordCheck checks failures $"owning-stack/{optimization}/stable-arena/fixed-dead-only-nested-scope-rewinds" (stableDeadMetricsPass && stableDeadLlvmPass && checkTraceUsable checks failures $"owning-stack/{optimization}/stable-arena/fixed-dead-only-trace-complete" stableDeadMetrics stableDeadEvents) (jsonObject [
            "inputCount", box stableDeadInputs.Length
            "expectedInputCopyBytes", box (stableDead.GetProperty("inputCopyBytes").GetInt32())
            "expectedDeepCopyBytes", box (stableDead.GetProperty("deepCopyBytes").GetInt32())
            "scopeRewindSiteCount", box (stableDead.GetProperty("nestedScopeCount").GetInt32())
            "functionExitRewindSiteCount", box (stableDead.GetProperty("functionExitRewindCount").GetInt32())
            "rewindEvents", box (layoutEventDetails stableDeadRewinds)
            "rewindRangesMatchSavedMarks", box stableDeadRewindRangesPass
            "postRewindAllocation", box (stableDeadReusedAllocation |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
            "postRewindConstruction", box (stableDeadReusedConstruction |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
            "postRewindInputDescriptorOffsets", box stableDeadLoadsAfterReuse
            "postRewindEventIndices", box (jsonObject [
                "allocation", box (stableDeadReusedAllocation |> Option.map fst |> Option.defaultValue -1)
                "rewinds", box (stableDeadRewindPositions |> Array.map fst)
                "construction", box (stableDeadReusedConstruction |> Option.map fst |> Option.defaultValue -1) ])
            "postRewindSubchecks", box (jsonObject [
                "allocationBetweenRewinds", box stableDeadAllocationBetweenRewindsPass
                "constructionBeforeOuterRewind", box stableDeadConstructionBeforeOuterRewindPass
                "premarkInputsAtOriginalOffsets", box stableDeadInputsRemainAtOriginalOffsetsPass ])
            "postRewindAllocationReusesSavedMarkWithoutMovingPremarkInputs", box stableDeadReusePass
            "generatedLlvmPolicy", box stableDeadLlvmDetails
            "metrics", box (jsonNode options (metricSummary (box stableDeadMetrics))) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "stable-dead-only-nested-scope"; "rewindEvents", box (layoutEventDetails stableDeadRewinds); "metrics", jsonNode options (metricSummary (box stableDeadMetrics)) ]))

        let stableEscape = stableCases.GetProperty("fixedEscapingScopeResult")
        use stableEscapeProgram = compile "stable-escaping-scope-result" entries.StableEscapingScopeResult
        let stableEscapeInput = stableEscape.GetProperty("input")
        let stableEscapeArgs = [ IntValue(stableEscapeInput.GetProperty("value").GetInt64()); IntValue(stableEscapeInput.GetProperty("tag").GetInt64()) ]
        let stableEscapeExpected = RecordValue("Envelope", Map.ofList [
            "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue(stableEscapeInput.GetProperty("value").GetInt64()) ])
            "tag", IntValue(stableEscapeInput.GetProperty("tag").GetInt64()) ])
        let stableEscapeBytes = bytesFromHex (stableEscape.GetProperty("retainedBytesHex").GetString())
        let stableEscapeOutput = Array.create stableEscapeBytes.Length 0xA5uy
        let stableEscapeResult = stableEscapeProgram.ExecuteInto(stableEscapeArgs, stackCapacity, stableEscapeOutput)
        let stableEscapeMetrics = stableEscapeResult.Metrics
        let stableEscapeRewinds = stableEscapeResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
        let stableEscapeRetention = stableEscapeResult.LayoutEvents |> List.tryFind (fun event -> event.Kind = "retained-copy")
        let stableEscapeMetricsPass =
            stableEscapeResult.Values = [ stableEscapeExpected]
            && stableEscapeOutput = stableEscapeBytes
            && int64Property (box stableEscapeMetrics) "InputCopyBytes" = int64 (stableEscape.GetProperty("inputCopyBytes").GetInt32())
            && int64Property (box stableEscapeMetrics) "DeepCopyBytes" = int64 (stableEscape.GetProperty("deepCopyBytes").GetInt32())
            && int64Property (box stableEscapeMetrics) "MoveBytes" = int64 (stableEscape.GetProperty("moveBytes").GetInt32())
            && int64Property (box stableEscapeMetrics) "ReservedStackBytes" >= int64 (stableEscape.GetProperty("minimumPeakCursorBytes").GetInt32())
            && int64Property (box stableEscapeMetrics) "FinalCursorBytes" = 0L
            && stableEscapeRewinds.Length = stableEscape.GetProperty("scopeRewinds").GetInt32()
            && (stableEscapeRetention |> Option.exists (fun event -> event.SourceOffsetBytes |> Option.exists (fun source -> source > 16)))
            && descriptorTransferMetricsPass stableEscapeMetrics stableEscapeResult.LayoutEvents
        let stableEscapeLlvmPass, stableEscapeLlvmDetails = generatedStablePolicyPass stableEscapeProgram (stableEscape.GetProperty("scopeRewinds").GetInt32())
        let stableEscapeTracePass = checkTraceUsable checks failures $"owning-stack/{optimization}/stable-arena/fixed-escape-trace-complete" stableEscapeMetrics stableEscapeResult.LayoutEvents
        recordCheck checks failures $"owning-stack/{optimization}/stable-arena/fixed-escape-keeps-temp-under-result" (stableEscapeMetricsPass && stableEscapeLlvmPass && stableEscapeTracePass) (jsonObject [
            "values", box (ValueInspection.toJson entries.Program stableEscapeResult.Values)
            "retainedBytes", box (bytesHex stableEscapeOutput)
            "retainedCopySourceOffset", box (stableEscapeRetention |> Option.bind (fun event -> event.SourceOffsetBytes) |> Option.map box |> Option.defaultValue null)
            "rewindEvents", box (layoutEventDetails stableEscapeRewinds)
            "generatedLlvmPolicy", box stableEscapeLlvmDetails
            "metrics", box (jsonNode options (metricSummary (box stableEscapeMetrics))) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "stable-escaping-scope-result"; "retainedBytes", box (bytesHex stableEscapeOutput); "metrics", jsonNode options (metricSummary (box stableEscapeMetrics)) ]))

        let branchEscape = stableCases.GetProperty("fixedBranchEscapingScopeResult")
        use stableBranchProgram = compile "stable-branch-escaping-scope-result" entries.StableBranchEscapingScopeResult
        let branchLlvmPass, branchLlvmDetails = generatedStablePolicyPass stableBranchProgram (branchEscape.GetProperty("scopeRewinds").GetInt32())
        for chooseLeft, tagField, bytesField in [ true, "leftTag", "leftRetainedBytesHex"; false, "rightTag", "rightRetainedBytesHex" ] do
            let branchBytes = bytesFromHex (branchEscape.GetProperty(bytesField).GetString())
            let branchOutput = Array.create branchBytes.Length 0xA5uy
            let branchResult = stableBranchProgram.ExecuteInto([ IntValue(branchEscape.GetProperty("inputValue").GetInt64()); BoolValue chooseLeft ], stackCapacity, branchOutput)
            let branchMetrics = branchResult.Metrics
            let tag = branchEscape.GetProperty(tagField).GetInt64()
            let branchExpected = RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue(branchEscape.GetProperty("inputValue").GetInt64()) ]); "tag", IntValue tag ])
            let branchRewinds = branchResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
            let branchPass = branchResult.Values = [ branchExpected ] && branchOutput = branchBytes && int64Property (box branchMetrics) "MoveBytes" = int64 (branchEscape.GetProperty("moveBytes").GetInt32()) && branchRewinds.Length = branchEscape.GetProperty("scopeRewinds").GetInt32() && int64Property (box branchMetrics) "FinalCursorBytes" = 0L && descriptorTransferMetricsPass branchMetrics branchResult.LayoutEvents
            recordCheck checks failures $"owning-stack/{optimization}/stable-arena/fixed-branch-result-blocks-rewind/{chooseLeft}" (branchPass && branchLlvmPass && checkTraceUsable checks failures $"owning-stack/{optimization}/stable-arena/fixed-branch-trace/{chooseLeft}" branchMetrics branchResult.LayoutEvents) (jsonObject [
                "chooseLeft", box chooseLeft
                "expectedBytes", box (bytesHex branchBytes)
                "actualBytes", box (bytesHex branchOutput)
                "rewindEvents", box (layoutEventDetails branchRewinds)
                "generatedLlvmPolicy", box branchLlvmDetails
                "metrics", box (jsonNode options (metricSummary (box branchMetrics))) ])
        let depthOutput = Array.create 8 0xA5uy
        let depthResult = depth64Program.ExecuteInto([ IntValue 42L ], 32768, depthOutput)
        recordCheck checks failures $"owning-stack/{optimization}/call-depth-64-succeeds" (depthResult.Values = [ IntValue 42L ] && depthOutput = bytesFromInt64s [ 42L ]) (jsonObject [
            "values", box (ValueInspection.toJson entries.Program depthResult.Values)
            "retainedBytes", box (bytesHex depthOutput)
            "metrics", box (jsonNode options (metricSummary (box depthResult.Metrics))) ])
        let depthFailureBuffer = Array.create 8 0xA5uy
        let depthFailureBefore = Array.copy depthFailureBuffer
        let depthFailure, depthFailureMetrics =
            try
                depth65Program.ExecuteInto([ IntValue 42L ], 32768, depthFailureBuffer) |> ignore
                "unexpected-success", null
            with error -> diagnosticCode error, getProperty error "Metrics"
        let depthFinalCursor = if isNull depthFailureMetrics then -1L else int64Property depthFailureMetrics "FinalCursorBytes"
        recordCheck checks failures $"owning-stack/{optimization}/call-depth-65-diagnostic-unwinds" (depthFailure = "RUNTIME_CALL_DEPTH" && depthFinalCursor = 0L && depthFailureBuffer = depthFailureBefore) (jsonObject [
            "diagnosticCode", box depthFailure
            "finalCursorBytes", box depthFinalCursor
            "callerBuffer", box (bufferCheckDetails depthFailureBefore depthFailureBuffer)
            "metrics", (if isNull depthFailureMetrics then null else jsonNode options (metricSummary depthFailureMetrics)) ])
    let branches = fixture.GetProperty("branches").EnumerateArray() |> Seq.toArray
    let branchReports =
        branches
        |> Array.mapi (fun index branch ->
            let value = branch.GetProperty("value").GetInt64()
            let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
            let expectedElement = branch.GetProperty("expected")
            let expected = expectedValues expectedElement
            let branchOutput = Array.create 16 0xA5uy
            let branchResult = branchProgram.ExecuteInto([ IntValue value; BoolValue chooseLeft ], stackCapacity, branchOutput)
            let actual = getProperty (box branchResult) "Values" :?> Value list
            let branchMetrics = stackMetrics (box branchResult)
            let branchLayouts = getProperty (box branchResult) "Layouts" :?> OwningStackTypeLayout list
            let layoutChecks, layoutFailures, layoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] branchLayouts
            for details in layoutChecks do checks.Add details
            for failure in layoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/branch-{index}-layout: {failure}")
            let expectedBytes = bytesFromHex (branch.GetProperty("retainedBytesHex").GetString())
            recordCheck checks failures $"fixture/N={repetition}/branch-{index}-little-endian-layout-bytes" (envelopeBytesFromJson expectedElement = expectedBytes) (jsonObject [
                "fixtureHex", box (bytesHex expectedBytes)
                "independentValueEncodingHex", box (bytesHex (envelopeBytesFromJson expectedElement)) ])
            recordCheck checks failures $"owning-stack/{optimization}/branch-{index}" (actual = expected) (ValueInspection.toJson entries.Program actual)
            recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/branch-{index}-retained-bytes" (branchOutput = expectedBytes) (bufferCheckDetails expectedBytes branchOutput)
            box (jsonObject [
                "chooseLeft", box chooseLeft
                "values", jsonNode options (ValueInspection.toData entries.Program actual)
                "retainedBytes", box (bytesHex branchOutput)
                "metrics", jsonNode options (metricSummary branchMetrics)
                "typeLayouts", jsonNode options layoutSummary
                "layoutEvents", jsonNode options (layoutEventRanges (getProperty (box branchResult) "LayoutEvents" :?> OwningStackLayoutEvent list)) ]))
    for (turnName, metrics) in [ "turn1", firstMetrics; "turn2", secondMetrics ] do
        let summary = metricSummary metrics
        candidateSummaries.Add($"{optimization}/N={repetition}/{turnName}", summary)
        let completedResult = if turnName = "turn1" then box firstResult else box secondResult
        let completedEvents = getProperty completedResult "LayoutEvents" :?> OwningStackLayoutEvent list
        let completedCursorReset = int64Property metrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/{turnName}/request-cursor-resets" completedCursorReset (jsonObject [
            "finalCursorBytes", box (int64Property metrics "FinalCursorBytes")
            "livePayloadBytes", null
            "livePayloadAccounting", box (string (getProperty metrics "LivePayloadAccountingUnavailableReason"))
            "descriptorTransferMetricsMatchTrace", box (descriptorTransferMetricsPass (metrics :?> OwningStackMetrics) completedEvents) ])
        let inputBytes = int64Property metrics "InputBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let retainedCopyBytes = int64Property metrics "RetainedCopyBytes"
        let retainedCapacityBytes = int64Property metrics "RetainedCapacityBytes"
        let hostStagingBytes = int64Property metrics "HostRetainedStagingBytes"
        let hostCommitBytes = int64Property metrics "HostRetainedCommitBytes"
        let operationEvents = getProperty (if turnName = "turn1" then box firstResult else box secondResult) "LayoutEvents" :?> OwningStackLayoutEvent list
        let typedMetrics = metrics :?> OwningStackMetrics
        let descriptorMetricsPass = descriptorTransferMetricsPass typedMetrics operationEvents
        let descriptorPathPass =
            int64Property metrics "MoveBytes" = 0L
            && (operationEvents |> List.forall (fun event -> event.Kind <> "call-input-move" && event.Kind <> "call-return-move" && event.Kind <> "local-compact"))
            && descriptorMetricsPass
        let measuredCopySizesPass = inputBytes = 32L && inputCopyBytes = 32L && retainedCopyBytes = 24L && retainedCapacityBytes = 24L && hostStagingBytes = 24L && hostCommitBytes = 24L && descriptorPathPass
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/{turnName}/input-retained-copy-categories" measuredCopySizesPass (jsonObject [
            "expectedInputBytes", box 32
            "actualInputBytes", box inputBytes
            "expectedInputCopyBytes", box 32
            "actualInputCopyBytes", box inputCopyBytes
            "expectedRetainedCopyBytes", box 24
            "actualRetainedCopyBytes", box retainedCopyBytes
            "retainedCapacityBytes", box retainedCapacityBytes
            "hostRetainedStagingBytes", box hostStagingBytes
            "hostRetainedCommitBytes", box hostCommitBytes
            "moveBytes", box (int64Property metrics "MoveBytes")
            "callOrCompactionPayloadEvents", box (operationEvents |> List.filter (fun event -> event.Kind = "call-input-move" || event.Kind = "call-return-move" || event.Kind = "local-compact") |> layoutEventDetails)
            "descriptorTransferMetricsMatchTrace", box descriptorMetricsPass ])
        let reserved = int64Property metrics "ReservedStackBytes"
        let reservedLocal = int64Property metrics "ReservedLocalBytes"
        let uniqueLive = typedMetrics.UniqueLivePayloadBytes
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/{turnName}/cursor-metadata-and-live-metric-categories" (reserved >= 0L && reservedLocal >= 0L && uniqueLive.IsNone) (jsonObject [
            "occupiedCursorPeakBytes", box reserved
            "backendMetadataPerFrameBytes", box (int64Property metrics "BackendMetadataPerFrameBytes")
            "backendMetadataPeakBoundBytes", box (int64Property metrics "BackendMetadataPeakBoundBytes")
            "legacyPeakLivePayloadCounterBytes", null
            "legacyPeakLiveLocalPayloadCounterBytes", null
            "uniqueLivePayloadBytes", box null
            "logicalRootBytes", box null
            "metrics", box summary ])
    let stateLayoutChecks, stateLayoutFailures, stateLayoutElement =
        validateTypeLayouts fixture [ "Leaf"; "Envelope"; "State" ] (getProperty (box firstResult) "Layouts" :?> OwningStackTypeLayout list)
    for details in stateLayoutChecks do checks.Add details
    for failure in stateLayoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/State-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/full-nested-layout-contract" (stateLayoutFailures.Length = 0) (jsonNode options stateLayoutElement)
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/direct-trace-complete" directMetrics directEvents |> ignore
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/turn1-trace-complete" firstMetrics (getProperty (box firstResult) "LayoutEvents" :?> OwningStackLayoutEvent list) |> ignore
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/turn2-trace-complete" secondMetrics (getProperty (box secondResult) "LayoutEvents" :?> OwningStackLayoutEvent list) |> ignore
    jsonObject [
        "backend", box $"inline-owning-stack/{optimization}"
        "repetitions", box repetition
        "legacyStackCapacityControlBytes", box stackCapacity
        "semanticExecutionStackCapacityBytes", box executionStackCapacity
        "stableArenaCapacityControl", stableArenaCapacityControlReport
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "retainedBytesAfterTurn1", box (bytesHex firstOutput)
        "retainedBytesAfterTurn2", box (bytesHex secondOutput)
        "initializeMetrics", jsonNode options (metricSummary initialMetrics)
        "turn1Metrics", jsonNode options (metricSummary firstMetrics)
        "turn2Metrics", jsonNode options (metricSummary secondMetrics)
        "stateLayoutContract", jsonNode options stateLayoutElement
        "directDupDropMetrics", jsonNode options (metricSummary directMetrics)
        "directDupDropLayouts", jsonNode options directLayoutElement
        "directDupDropLayoutContract", jsonNode options directLayoutElement
        "directDupDropLayoutEvents", jsonNode options directRanges
        "fixedLayoutCases", box (fixedCaseReports.ToArray())
        "branchRuns", box branchReports
        "failureAfterAllocation", box diagnostic
        "failureAfterAllocationMetrics", (if isNull diagnosticMetrics then null else jsonNode options (metricSummary diagnosticMetrics)) ]

let private textStateMetadata (value: Value) =
    match value with
    | RecordValue("TextState", state) ->
        match state["count"], state["last"] with
        | IntValue count, RecordValue("TextEnvelope", envelope) ->
            match envelope["tag"], envelope["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> count, tag, codeUnits, text
                | _ -> invalidOp "TextState value contains invalid TextLeaf field values."
            | _ -> invalidOp "TextState value contains an invalid TextEnvelope."
        | _ -> invalidOp "TextState value contains invalid count or last fields."
    | _ -> invalidOp "Expected a TextState value."

let private textEnvelopeFromState (value: Value) =
    match value with
    | RecordValue("TextState", state) -> state["last"]
    | _ -> invalidOp "Expected a TextState value."

let private textEnvelopeBytes (tag: int64) (codeUnits: int64) stringCodeUnitsHex =
    Array.concat [ stringBytesFromCodeUnitsHex stringCodeUnitsHex; bytesFromInt64s [ codeUnits; tag ] ]

let private runStringWorkload
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (entries: StringEntryBodies)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string) =
    let workload = fixture.GetProperty("stringWorkload")
    let eventTypeIdOracle = workload.GetProperty("dynamicLayoutOracle").GetProperty("eventTypeIds")
    let nominalTypeIds =
        VerifiedIrProgram.inspect entries.Program
        |> fun program -> program.NominalTypesByKey
        |> Map.toList
        |> List.mapi (fun index (_, definition) ->
            let typeName =
                match definition with
                | IrRecordDefinition record -> record.TypeName
                | IrScalarDefinition scalar -> scalar.TypeName
                | IrEnumDefinition enum -> enum.TypeName
            typeName, uint32 (index + 4))
        |> Map.ofList
    let stringTypeId = uint32 (nominalTypeIds.Count + 4)
    let textEnvelopeTypeId =
        nominalTypeIds.TryFind "TextEnvelope"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing TextEnvelope's nominal type ID.")
    let emptyTypeId =
        nominalTypeIds.TryFind "Empty"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing Empty's nominal type ID.")
    let textMixedTypeId =
        nominalTypeIds.TryFind "TextMixed"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing TextMixed's nominal type ID.")
    let intTypeId = 1u
    let expectedStringTypeId = eventTypeIdOracle.GetProperty("String").GetUInt32()
    let expectedTextEnvelopeTypeId = eventTypeIdOracle.GetProperty("TextEnvelope").GetUInt32()
    let expectedEmptyTypeId = eventTypeIdOracle.GetProperty("Empty").GetUInt32()
    let expectedTextMixedTypeId = eventTypeIdOracle.GetProperty("TextMixed").GetUInt32()
    let expectedIntTypeId = eventTypeIdOracle.GetProperty("Int").GetUInt32()
    let eventTypeIdsMatch =
        stringTypeId = expectedStringTypeId
        && textEnvelopeTypeId = expectedTextEnvelopeTypeId
        && emptyTypeId = expectedEmptyTypeId
        && textMixedTypeId = expectedTextMixedTypeId
        && intTypeId = expectedIntTypeId
    recordCheck checks failures "oracle/String/runtime-event-type-ids" eventTypeIdsMatch (jsonObject [
        "verifiedProgramNominalTypeCount", box nominalTypeIds.Count
        "expectedStringTypeId", box expectedStringTypeId
        "actualStringTypeId", box stringTypeId
        "expectedTextEnvelopeTypeId", box expectedTextEnvelopeTypeId
        "actualTextEnvelopeTypeId", box textEnvelopeTypeId
        "expectedEmptyTypeId", box expectedEmptyTypeId
        "actualEmptyTypeId", box emptyTypeId
        "expectedTextMixedTypeId", box expectedTextMixedTypeId
        "actualTextMixedTypeId", box textMixedTypeId
        "expectedIntTypeId", box expectedIntTypeId
        "actualIntTypeId", box intTypeId ])
    let mainCases = workload.GetProperty("mainCases").EnumerateArray() |> Seq.toArray
    let edgeCases = workload.GetProperty("nestedEdgeCases").EnumerateArray() |> Seq.toArray
    let cases = Array.append mainCases edgeCases
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    let compiledInputBodies =
        cases
        |> Array.map (fun caseElement ->
            let name = caseElement.GetProperty("name").GetString()
            let inputValue = textStateFromCase caseElement
            name, (inputValue, compileTextStateFactory entries name inputValue))
        |> Map.ofArray
    let interpreterReports = ResizeArray<obj>()
    for caseElement in cases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, inputFactory =
            match compiledInputBodies[name] with
            | value, body -> value, body
        let expectedValues = textExpectedOutputs caseElement
        use inputOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("string-input-" + name) inputFactory None []
        use actual = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn-text" entries.TurnText (Some inputOwner) [ IrEntryArgument.RetainedRoot 0 ]
        let inputCount, inputTag, inputCodeUnits, _ = textStateMetadata inputValue
        let inputHex = caseElement.GetProperty("inputCodeUnitsHex").GetString()
        let output = expectedValues[0]
        let outputCount, outputTag, outputCodeUnits, _ = textStateMetadata output
        let outputHex = caseElement.GetProperty("outputCodeUnitsHex").GetString()
        let inputOracle = textStateBytes inputCount inputTag inputCodeUnits inputHex
        let outputStateOracle = textStateBytes outputCount outputTag outputCodeUnits outputHex
        let expectedOutputLength = match expectedValues[1] with | IntValue value -> value | _ -> invalidOp "String fixture length output must be Int."
        let expectedRetained = Array.concat [ outputStateOracle; bytesFromInt64s [ expectedOutputLength ] ]
        let fixtureInputBytes = bytesFromHex (caseElement.GetProperty("inputStateBytesHex").GetString())
        let fixtureOutputBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let values = actual.Decode()
        recordCheck checks failures $"interpreter/String/{name}/same-verified-body-output" (values = expectedValues) (codeUnitSafeValuesJson values)
        recordCheck checks failures $"oracle/String/{name}/input-inline-UTF16-bytes" (inputOracle = fixtureInputBytes) (jsonObject [
            "oracleHex", box (bytesHex inputOracle)
            "fixtureHex", box (bytesHex fixtureInputBytes)
            "codeUnitCount", box inputCodeUnits ])
        recordCheck checks failures $"oracle/String/{name}/output-inline-UTF16-bytes" (expectedRetained = fixtureOutputBytes) (jsonObject [
            "oracleHex", box (bytesHex expectedRetained)
            "fixtureHex", box (bytesHex fixtureOutputBytes)
            "outputCodeUnitCount", box (caseElement.GetProperty("outputCodeUnitCount").GetInt32()) ])
        interpreterReports.Add(box (jsonObject [
            "case", box name
            "inputValue", jsonNode options (codeUnitSafeValuesData [ inputValue ])
            "values", jsonNode options (codeUnitSafeValuesData values)
            "inputStateBytes", box (bytesHex fixtureInputBytes)
            "retainedBytes", box (bytesHex fixtureOutputBytes) ]))

    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "strings", optimizationName, name)) body
    use turnProgram = compile "turn-text" entries.TurnText
    use identityProgram = compile "identity-string" entries.IdentityString
    use joinProgram = compile "join-strings" entries.JoinStrings
    use directConcatProgram = compile "direct-concat-capacity" entries.DirectConcat
    use mixedConstructProgram = compile "mixed-record-construct" entries.MixedConstruct
    use mixedProjectProgram = compile "mixed-record-project-text-and-sentinel" entries.MixedProjectTextAndSentinel
    use mixedProjectEmptyProgram = compile "mixed-record-project-empty-marker" entries.MixedProjectEmptyMarker
    use dynamicBranchProgram = compile "dynamic-record-branch-join" entries.DynamicBranch
    use duplicateProgram = compile "text-envelope-dup-drop" entries.DirectDupDrop
    use scopeProgram = compile "text-envelope-scope-shadow" entries.ScopeShadow
    use zeroOutputProgram = compile "zero-output-user-call" entries.ZeroOutputUserCall
    use stableDeadProgram = compile "stable-six-string-local-dead-only" entries.StableDeadOnlySixLocals
    use stableEscapeProgram = compile "stable-string-escaping-scope-result" entries.StableEscapingScopeResult
    use stableBindingProgram = compile "stable-string-binding-round-trip" entries.StableBindingRoundTrip
    use stableUncertainCallProgram = compile "stable-string-uncertain-call-scope" entries.StableUncertainCallScope
    use failProgram = compile "fail-after-text-allocation" entries.FailAfterTextAllocation
    let owningReports = ResizeArray<obj>()
    let metricsByCase = Dictionary<string, OwningStackMetrics>(StringComparer.Ordinal)
    let mutable dynamicLayouts = []
    let mutable layoutSchemaVersion = -1L
    let stateCapacity = 4096
    for caseElement in cases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, _ = compiledInputBodies[name]
        let expectedValues = textExpectedOutputs caseElement
        let outputBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let callerOutput = Array.create outputBytes.Length 0xA5uy
        let result = turnProgram.ExecuteInto([ inputValue ], stateCapacity, callerOutput)
        let actualValues = getProperty (box result) "Values" :?> Value list
        let metrics = stackMetrics (box result) :?> OwningStackMetrics
        let events = getProperty (box result) "LayoutEvents" :?> OwningStackLayoutEvent list
        if name = mainCases[0].GetProperty("name").GetString() then
            dynamicLayouts <- getProperty (box result) "Layouts" :?> OwningStackTypeLayout list
            layoutSchemaVersion <- int64Property (box result) "LayoutSchemaVersion"
        metricsByCase.Add(name, metrics)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/same-verified-body-output" (actualValues = expectedValues) (codeUnitSafeValuesJson actualValues)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/caller-retained-bytes" (callerOutput = outputBytes && (getProperty (box result) "RetainedOutputBytes" :?> byte array) = outputBytes) (bufferCheckDetails outputBytes callerOutput)
        let inputStateExtent = caseElement.GetProperty("inputStateBytes").GetInt32()
        let outputStateExtent = outputBytes.Length - 8
        let returnedLength = int64FromBytesLittleEndian outputBytes outputStateExtent
        let expectedLength = match expectedValues[1] with | IntValue value -> value | _ -> invalidOp "String fixture length output must be Int."
        let multiOutputPass = result.RetainedBytesWritten = outputBytes.Length && returnedLength = expectedLength && outputStateExtent + 8 = outputBytes.Length && (name <> "long-bmp-astral" || outputStateExtent > inputStateExtent)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/multi-output-range-and-growth" multiOutputPass (jsonObject [
            "inputStateExtentBytes", box inputStateExtent
            "outputStateExtentBytes", box outputStateExtent
            "retainedBytesWritten", box result.RetainedBytesWritten
            "secondOutputAtByteOffset", box outputStateExtent
            "secondOutputValue", box returnedLength ])
        let inputValueBytes = bytesFromHex (caseElement.GetProperty("inputStateBytesHex").GetString())
        let inputBytes = int64Property metrics "InputBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let hostEncodedInputBytes = int64Property metrics "HostEncodedInputBytes"
        let exactInputCopy = inputBytes = int64 inputValueBytes.Length && inputCopyBytes = int64 inputValueBytes.Length && hostEncodedInputBytes = int64 inputValueBytes.Length
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/dynamic-value-input-marshaling" exactInputCopy (jsonObject [
            "oracleInputBytes", box inputValueBytes.Length
            "inputBytes", box inputBytes
            "inputCopyBytes", box inputCopyBytes
            "hostEncodedInputBytes", box hostEncodedInputBytes ])
        let inputCount = caseElement.GetProperty("inputCodeUnitCount").GetInt32()
        let outputCount = caseElement.GetProperty("outputCodeUnitCount").GetInt32()
        let inputStringExtent = caseElement.GetProperty("inputStringExtentBytes").GetInt32()
        let outputStringExtent = caseElement.GetProperty("outputStringExtentBytes").GetInt32()
        let fieldExtractEvents = events |> List.filter (fun event -> event.Kind = "field-extract")
        let stringFieldExtractEvents = fieldExtractEvents |> List.filter (fun event -> event.TypeId = stringTypeId)
        let inputStringPayload = 8 + 2 * inputCount
        let outputStringPayload = 8 + 2 * outputCount
        let align8 bytes = ((bytes + 7) / 8) * 8
        let inputStringExtentFromOracle = align8 inputStringPayload
        let outputStringExtentFromOracle = align8 outputStringPayload
        let inputStringEvent =
            inputStringExtent = inputStringExtentFromOracle
            && (stringFieldExtractEvents |> List.exists (fun event -> event.PayloadBytes = inputStringPayload && event.ExtentBytes = inputStringExtent))
        let outputStringEvent =
            outputStringExtent = outputStringExtentFromOracle
            && (stringFieldExtractEvents |> List.exists (fun event -> event.PayloadBytes = outputStringPayload && event.ExtentBytes = outputStringExtent))
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-string-field-extents" (inputStringEvent && outputStringEvent) (jsonObject [
            "stringTypeId", box stringTypeId
            "inputStringPayloadBytes", box inputStringPayload
            "inputStringExtentBytes", box inputStringExtent
            "inputStringExtentFromIndependentFormula", box inputStringExtentFromOracle
            "inputInstanceFieldEventFound", box inputStringEvent
            "outputStringPayloadBytes", box outputStringPayload
            "outputStringExtentBytes", box outputStringExtent
            "outputStringExtentFromIndependentFormula", box outputStringExtentFromOracle
            "outputInstanceFieldEventFound", box outputStringEvent
            "stringFieldExtractEvents", box (layoutEventDetails stringFieldExtractEvents) ])
        let concatEventPairs =
            let indexedEvents = events |> List.indexed |> List.toArray
            if indexedEvents.Length < 2 then
                [||]
            else
                [| for index in 0 .. indexedEvents.Length - 2 do
                       let _, left = indexedEvents[index]
                       let _, right = indexedEvents[index + 1]
                       if left.Kind = "string-concat-left"
                          && right.Kind = "string-concat-right"
                          && left.TypeId = stringTypeId
                          && right.TypeId = stringTypeId then
                           yield left, right |]
        let outputStringDataBytes = 2 * outputCount
        let outputConcatSpansMatch =
            concatEventPairs
            |> Array.exists (fun (left, right) ->
                left.OffsetBytes >= 8
                && left.ExtentBytes > 0
                && right.ExtentBytes > 0
                && left.PayloadBytes = left.ExtentBytes
                && right.PayloadBytes = right.ExtentBytes
                && left.SourceExtentBytes = Some left.ExtentBytes
                && right.SourceExtentBytes = Some right.ExtentBytes
                && right.OffsetBytes = left.OffsetBytes + left.ExtentBytes
                && left.ExtentBytes + right.ExtentBytes = outputStringDataBytes)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-string-concat-output-spans" outputConcatSpansMatch (jsonObject [
            "stringTypeId", box stringTypeId
            "expectedOutputCodeUnitDataBytes", box outputStringDataBytes
            "concatSourceSpans", box (concatEventPairs |> Array.collect (fun (left, right) -> [| left; right |]) |> Array.toList |> layoutEventDetails) ])
        let textLeafExtent = inputStringExtent + 8
        let expectedTagOffsetWithinEnvelope = caseElement.GetProperty("inputTagOffsetWithinEnvelopeBytes").GetInt32()
        let tagOffsetOracleMatches = textLeafExtent = expectedTagOffsetWithinEnvelope
        let expectedEnvelopePayload = inputStringPayload + 16
        let expectedEnvelopeExtent = align8 expectedEnvelopePayload
        let indexedEvents = events |> List.indexed |> List.toArray
        let rangeOverlaps leftStart leftExtent rightStart rightExtent =
            leftExtent > 0
            && rightExtent > 0
            && int64 leftStart < int64 rightStart + int64 rightExtent
            && int64 rightStart < int64 leftStart + int64 leftExtent
        let mutationRanges event =
            let destination = [ event.OffsetBytes, event.ExtentBytes ]
            match event.Kind with
            | "allocate" | "duplicate" | "record-build" | "string-concat-left" | "string-concat-right" -> destination
            | _ -> []
        let tagCandidates =
            indexedEvents
            |> Array.choose (fun (tagIndex, event) ->
                let isTagField =
                    event.Kind = "field-extract"
                    && event.TypeId = intTypeId
                    && event.ExtentBytes = 8
                    && event.PayloadBytes = 8
                    && event.SourceExtentBytes = Some 8
                    && event.SourceOffsetBytes = Some(event.OffsetBytes + expectedTagOffsetWithinEnvelope)
                if not isTagField then
                    None
                else
                    let parentTransfers =
                        indexedEvents
                        |> Array.filter (fun (transferIndex, transfer) ->
                            transferIndex < tagIndex
                            && transfer.Kind = "descriptor-transfer"
                            && transfer.TypeId = textEnvelopeTypeId
                            && transfer.OffsetBytes = event.OffsetBytes
                            && transfer.SourceExtentBytes = Some expectedEnvelopeExtent
                            && transfer.PayloadBytes = expectedEnvelopePayload)
                    if parentTransfers.Length = 0 then
                        None
                    else
                        let parentTransferIndex, parentTransfer = parentTransfers[parentTransfers.Length - 1]
                        let invalidatingEvents =
                            indexedEvents
                            |> Array.choose (fun (index, intervening) ->
                                if index <= parentTransferIndex || index >= tagIndex then
                                    None
                                else
                                    let mutatesOwner =
                                        mutationRanges intervening
                                        |> List.exists (fun (offset, extent) -> rangeOverlaps offset extent event.OffsetBytes expectedEnvelopeExtent)
                                    if mutatesOwner then Some(index, intervening) else None)
                        Some(tagIndex, event, parentTransferIndex, parentTransfer, invalidatingEvents))
        let validTagCandidates = tagCandidates |> Array.filter (fun (_, _, _, _, invalidating) -> invalidating.Length = 0)
        let dynamicTagFound = tagOffsetOracleMatches && validTagCandidates.Length > 0
        let tagEvidence =
            validTagCandidates
            |> Array.map (fun (tagIndex, tagEvent, loadIndex, loadEvent, _) ->
                jsonObject [
                    "tagExtractionIndex", box tagIndex
                    "parentDescriptorTransferIndex", box loadIndex
                    "parentDescriptorTransfer", box (layoutEventDetails [ loadEvent ])
                    "tagExtraction", box (layoutEventDetails [ tagEvent ]) ])
        let rejectedTagEvidence =
            tagCandidates
            |> Array.filter (fun (_, _, _, _, invalidating) -> invalidating.Length > 0)
            |> Array.map (fun (_, _, _, _, invalidating) ->
                invalidating
                |> Array.map (fun (index, event) -> jsonObject [ "index", box index; "event", box (layoutEventDetails [ event ]) ]))
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-nested-dynamic-tag-offset" dynamicTagFound (jsonObject [
            "textEnvelopeTypeId", box textEnvelopeTypeId
            "intTypeId", box intTypeId
            "stringExtentBytes", box inputStringExtent
            "expectedTextLeafExtentBytes", box textLeafExtent
            "expectedTagOffsetWithinEnvelopeBytes", box expectedTagOffsetWithinEnvelope
            "tagOffsetOracleMatchesIndependentLiteral", box tagOffsetOracleMatches
            "expectedEnvelopePayloadBytes", box expectedEnvelopePayload
            "expectedEnvelopeExtentBytes", box expectedEnvelopeExtent
            "correlatedParentAndTagEvents", box tagEvidence
            "tagCandidatesRejectedForInterveningOwnerMutation", box rejectedTagEvidence ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{name}/trace-complete" metrics events |> ignore
        owningReports.Add(box (jsonObject [
            "case", box name
            "values", jsonNode options (codeUnitSafeValuesData actualValues)
            "inputBytes", box (bytesHex inputValueBytes)
            "retainedBytes", box (bytesHex outputBytes)
            "metrics", jsonNode options (metricSummary metrics)
            "layoutEvents", jsonNode options (layoutEventDetails events) ]))

    recordCheck checks failures $"owning-stack/{optimizationName}/String/layout-schema-v3" (layoutSchemaVersion = 3L) (jsonObject [ "expected", box 3; "actual", box layoutSchemaVersion ])
    let dynamicLayoutChecks, dynamicLayoutFailures, dynamicLayoutSummary =
        validateDynamicTypeLayouts [ "String"; "TextLeaf"; "TextEnvelope"; "TextState" ] fixture dynamicLayouts
    for details in dynamicLayoutChecks do checks.Add details
    for failure in dynamicLayoutFailures do failures.Add($"owning-stack/{optimizationName}/String/dynamic-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimizationName}/String/dynamic-layout-contract" (dynamicLayoutFailures.Length = 0) (jsonNode options dynamicLayoutSummary)

    let roundTripReports = ResizeArray<obj>()
    for roundTrip in workload.GetProperty("directValueStringRoundTrips").EnumerateArray() do
        let name = roundTrip.GetProperty("name").GetString()
        let codeUnitsHex = roundTrip.GetProperty("inputCodeUnitsHex").GetString()
        let value = StringValue(stringFromCodeUnitsHex codeUnitsHex)
        let expectedBytes = bytesFromHex (roundTrip.GetProperty("stringBytesHex").GetString())
        let independentBytes = stringBytesFromCodeUnitsHex codeUnitsHex
        recordCheck checks failures $"oracle/String/{name}/direct-value-string-buffer" (independentBytes = expectedBytes && expectedBytes.Length = roundTrip.GetProperty("extentBytes").GetInt32()) (jsonObject [
            "codeUnitCount", box (roundTrip.GetProperty("inputCodeUnitCount").GetInt32())
            "oracleHex", box (bytesHex independentBytes)
            "fixtureHex", box (bytesHex expectedBytes)
            "extentBytes", box expectedBytes.Length ])
        let factory = compileStringFactory entries ("roundtrip-" + name) (stringFromCodeUnitsHex codeUnitsHex)
        use interpreterOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("string-roundtrip-input-" + name) factory None []
        use interpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.identity-string" entries.IdentityString (Some interpreterOwner) [ IrEntryArgument.RetainedRoot 0 ]
        let interpreterValues = interpreterResult.Decode()
        recordCheck checks failures $"interpreter/String/{name}/direct-value-roundtrip" (interpreterValues = [ value ]) (codeUnitSafeValuesJson interpreterValues)
        let callerOutput = Array.create expectedBytes.Length 0xA5uy
        let result = identityProgram.ExecuteInto([ value ], stateCapacity, callerOutput)
        let resultValues = getProperty (box result) "Values" :?> Value list
        let metrics = stackMetrics (box result)
        let inputBytes = int64Property metrics "InputBytes"
        let encodedBytes = int64Property metrics "HostEncodedInputBytes"
        let indexedEvents = result.LayoutEvents |> List.indexed |> List.toArray
        let retainedEvent = indexedEvents |> Array.tryFind (fun (_, event) -> event.Kind = "retained-copy")
        let inputStringPayloadBytes = 8 + 2 * (stringFromCodeUnitsHex codeUnitsHex).Length
        let inputAllocation =
            indexedEvents
            |> Array.tryFind (fun (_, event) ->
                event.Kind = "allocate"
                && event.TypeId = stringTypeId
                && event.ExtentBytes = expectedBytes.Length
                && event.PayloadBytes = inputStringPayloadBytes)
        let descriptorTransfers = indexedEvents |> Array.choose (fun (index, event) -> if event.Kind = "descriptor-transfer" then Some(index, event) else None)
        let addressStable =
            match inputAllocation, descriptorTransfers, retainedEvent with
            | Some(inputAllocateIndex, inputAllocate), transfers, Some(retainedIndex, retained) when transfers.Length > 0 ->
                let everyTransferKeepsOriginalAddress =
                    transfers
                    |> Array.forall (fun (index, transfer) ->
                        index > inputAllocateIndex
                        && index < retainedIndex
                        && transfer.TypeId = stringTypeId
                        && transfer.OffsetBytes = inputAllocate.OffsetBytes
                        && transfer.PayloadBytes = inputAllocate.PayloadBytes
                        && transfer.SourceOffsetBytes = Some(inputAllocate.OffsetBytes + inputAllocate.ExtentBytes)
                        && transfer.SourceExtentBytes = Some inputAllocate.ExtentBytes)
                everyTransferKeepsOriginalAddress
                && retained.TypeId = stringTypeId
                && retained.SourceOffsetBytes = Some inputAllocate.OffsetBytes
                && retained.SourceExtentBytes = Some inputAllocate.ExtentBytes
                && retained.ExtentBytes = inputAllocate.ExtentBytes
            | _ -> false
        let passed =
            resultValues = [ value ]
            && callerOutput = expectedBytes
            && (getProperty (box result) "RetainedOutputBytes" :?> byte array) = expectedBytes
            && int64Property metrics "RetainedCopyBytes" = int64 expectedBytes.Length
            && inputBytes = int64 expectedBytes.Length
            && encodedBytes = int64 expectedBytes.Length
            && int64Property metrics "DeepCopyBytes" = 0L
            && int64Property metrics "MoveBytes" = 0L
            && addressStable
            && descriptorTransferMetricsPass (metrics :?> OwningStackMetrics) result.LayoutEvents
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/direct-value-roundtrip" passed (jsonObject [
            "values", box (codeUnitSafeValuesJson resultValues)
            "retainedBytes", box (bytesHex callerOutput)
            "inputBytes", box inputBytes
            "hostEncodedInputBytes", box encodedBytes
            "inputAllocation", box (inputAllocation |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
            "descriptorTransfers", box (descriptorTransfers |> Array.map snd |> Array.toList |> layoutEventDetails)
            "retainedCopy", box (retainedEvent |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
            "addressStable", box addressStable
            "metrics", box (jsonNode options (metricSummary metrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{name}/direct-roundtrip-trace-complete" metrics result.LayoutEvents |> ignore
        roundTripReports.Add(box (jsonObject [ "case", box name; "bytes", box (bytesHex callerOutput); "metrics", box (jsonNode options (metricSummary metrics)) ]))

    let mixedOracle = workload.GetProperty("mixedDynamicRecord")
    let mixedCodeUnitsHex = mixedOracle.GetProperty("inputCodeUnitsHex").GetString()
    let mixedText = stringFromCodeUnitsHex mixedCodeUnitsHex
    let mixedSentinel = mixedOracle.GetProperty("sentinel").GetInt64()
    let mixedEmpty = RecordValue("Empty", Map.empty)
    let mixedValue = RecordValue("TextMixed", Map.ofList [
        "marker", mixedEmpty
        "text", StringValue mixedText
        "sentinel", IntValue mixedSentinel ])
    let mixedCodeUnitCount = mixedOracle.GetProperty("inputCodeUnitCount").GetInt32()
    let mixedStringBytes = stringBytesFromCodeUnitsHex mixedCodeUnitsHex
    let mixedStringPayloadBytes = 8 + 2 * mixedCodeUnitCount
    let mixedStringExtentBytes = ((mixedStringPayloadBytes + 7) / 8) * 8
    let mixedRecordPayloadBytes = mixedStringPayloadBytes + 8
    let mixedExpectedRecordPayloadBytes = 16 + 2 * mixedCodeUnitCount
    let mixedExpectedRecordExtentBytes = mixedStringExtentBytes + 8
    let mixedRecordBytes = Array.concat [ mixedStringBytes; bytesFromInt64s [ mixedSentinel ] ]
    let mixedFixtureStringBytes = bytesFromHex (mixedOracle.GetProperty("inputStringBytesHex").GetString())
    let mixedFixtureRecordBytes = bytesFromHex (mixedOracle.GetProperty("recordBytesHex").GetString())
    let mixedFixtureTextAndSentinelBytes = bytesFromHex (mixedOracle.GetProperty("projectedTextAndSentinelBytesHex").GetString())
    let mixedEmptyTokenBytes = bytesFromHex (mixedOracle.GetProperty("projectedEmptyTokenBytesHex").GetString())
    let mixedOraclePassed =
        mixedStringBytes = mixedFixtureStringBytes
        && mixedRecordBytes = mixedFixtureRecordBytes
        && mixedFixtureRecordBytes = mixedFixtureTextAndSentinelBytes
        && mixedStringBytes.Length = mixedOracle.GetProperty("inputStringExtentBytes").GetInt32()
        && mixedStringExtentBytes = mixedStringBytes.Length
        && mixedRecordPayloadBytes = mixedExpectedRecordPayloadBytes
        && mixedOracle.GetProperty("recordPayloadBytes").GetInt32() = mixedExpectedRecordPayloadBytes
        && mixedRecordBytes.Length = mixedExpectedRecordExtentBytes
        && mixedOracle.GetProperty("recordExtentBytes").GetInt32() = mixedExpectedRecordExtentBytes
        && mixedOracle.GetProperty("textOffsetBytes").GetInt32() = 0
        && mixedOracle.GetProperty("sentinelOffsetBytes").GetInt32() = mixedStringExtentBytes
        && int64 mixedRecordBytes.Length = int64 (mixedOracle.GetProperty("nestedEmptyExtentBytes").GetInt32() + mixedStringBytes.Length + 8)
        && mixedEmptyTokenBytes = Array.zeroCreate<byte> 8
    recordCheck checks failures "oracle/String/mixed-empty-string-int-record/independent-inline-bytes" mixedOraclePassed (jsonObject [
        "inputStringBytes", box (bytesHex mixedStringBytes)
        "fixtureStringBytes", box (bytesHex mixedFixtureStringBytes)
        "nestedEmptyBytes", box (mixedOracle.GetProperty("nestedEmptyExtentBytes").GetInt32())
        "recordPayloadBytes", box mixedExpectedRecordPayloadBytes
        "fixtureRecordPayloadBytes", box (mixedOracle.GetProperty("recordPayloadBytes").GetInt32())
        "recordExtentBytes", box mixedExpectedRecordExtentBytes
        "stringBytes", box mixedStringBytes.Length
        "sentinelOffsetBytes", box mixedStringBytes.Length
        "sentinelBytes", box (bytesHex (bytesFromInt64s [ mixedSentinel ]))
        "recordBytes", box (bytesHex mixedRecordBytes)
        "fixtureRecordBytes", box (bytesHex mixedFixtureRecordBytes)
        "projectedEmptyTokenBytes", box (bytesHex mixedEmptyTokenBytes) ])
    let mixedInputFactory = compileTextMixedInputFactory entries "empty-string-int" mixedText mixedSentinel
    use mixedInterpreterInput = IrInterpreter.executeBodyWithInputs interpreterHost "mixed-empty-string-int-inputs" mixedInputFactory None []
    use mixedInterpreterConstruct =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.make-text-mixed"
            entries.MixedConstruct
            (Some mixedInterpreterInput)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    let mixedConstructInterpreterValues = mixedInterpreterConstruct.Decode()
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/construction" (mixedConstructInterpreterValues = [ mixedValue ]) (codeUnitSafeValuesJson mixedConstructInterpreterValues)
    use mixedInterpreterProjection =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.project-text-mixed"
            entries.MixedProjectTextAndSentinel
            (Some mixedInterpreterConstruct)
            [ IrEntryArgument.RetainedRoot 0 ]
    use mixedInterpreterEmptyProjection =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.project-text-mixed-marker"
            entries.MixedProjectEmptyMarker
            (Some mixedInterpreterConstruct)
            [ IrEntryArgument.RetainedRoot 0 ]
    let mixedProjectionValues = mixedInterpreterProjection.Decode()
    let mixedEmptyProjectionValues = mixedInterpreterEmptyProjection.Decode()
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/projection-order" (mixedProjectionValues = [ StringValue mixedText; IntValue mixedSentinel ]) (codeUnitSafeValuesJson mixedProjectionValues)
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/empty-projection" (mixedEmptyProjectionValues = [ mixedEmpty ]) (codeUnitSafeValuesJson mixedEmptyProjectionValues)

    let mixedRecordOutput = Array.create mixedRecordBytes.Length 0xA5uy
    let mixedConstructResult = mixedConstructProgram.ExecuteInto([ StringValue mixedText; IntValue mixedSentinel ], stateCapacity, mixedRecordOutput)
    let mixedConstructMetrics = stackMetrics (box mixedConstructResult)
    let mixedConstructEvents = mixedConstructResult.LayoutEvents
    let mixedAllocateEvents = mixedConstructEvents |> List.filter (fun event -> event.Kind = "allocate")
    let mixedRecordBuildEvents = mixedConstructEvents |> List.filter (fun event -> event.Kind = "record-build")
    let mixedRecordBuild = mixedRecordBuildEvents |> List.exists (fun event -> event.TypeId = textMixedTypeId && event.PayloadBytes = mixedExpectedRecordPayloadBytes && event.ExtentBytes = mixedExpectedRecordExtentBytes)
    let mixedStringAllocate = mixedAllocateEvents |> List.exists (fun event -> event.TypeId = stringTypeId && event.PayloadBytes = mixedStringPayloadBytes && event.ExtentBytes = mixedStringExtentBytes)
    let mixedEmptyTokenAllocate = mixedAllocateEvents |> List.exists (fun event -> event.TypeId = emptyTypeId && event.PayloadBytes = 0 && event.ExtentBytes = 8)
    let mixedConstructPassed =
        mixedConstructResult.Values = [ mixedValue ]
        && mixedRecordOutput = mixedRecordBytes
        && mixedConstructResult.RetainedBytesWritten = mixedRecordBytes.Length
        && int64Property mixedConstructMetrics "HostEncodedInputBytes" = int64 (mixedStringBytes.Length + 8)
        && mixedRecordBuild
        && mixedStringAllocate
        && mixedEmptyTokenAllocate
        && int64Property mixedConstructMetrics "MoveBytes" = 0L
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/construction-and-inline-nesting" mixedConstructPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedConstructResult.Values)
        "expectedRecordBytes", box (bytesHex mixedRecordBytes)
        "actualRecordBytes", box (bytesHex mixedRecordOutput)
        "retainedBytesWritten", box mixedConstructResult.RetainedBytesWritten
        "expectedRecordPayloadBytes", box mixedExpectedRecordPayloadBytes
        "expectedRecordExtentBytes", box mixedExpectedRecordExtentBytes
        "stringAllocateFound", box mixedStringAllocate
        "mixedRecordBuildFound", box mixedRecordBuild
        "mixedRecordBuildEvents", box (layoutEventDetails mixedRecordBuildEvents)
        "standaloneEmptyTokenAllocateFound", box mixedEmptyTokenAllocate
        "metrics", box (jsonNode options (metricSummary mixedConstructMetrics))
        "events", box (layoutEventDetails mixedConstructEvents) ])

    let mixedLayouts = mixedConstructResult.Layouts
    let mixedDynamicLayoutChecks, mixedDynamicLayoutFailures, mixedDynamicLayoutSummary =
        validateDynamicTypeLayouts [ "TextMixed" ] fixture mixedLayouts
    for details in mixedDynamicLayoutChecks do checks.Add details
    for failure in mixedDynamicLayoutFailures do failures.Add($"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout-contract" (mixedDynamicLayoutFailures.Length = 0) (jsonNode options mixedDynamicLayoutSummary)
    let mixedLayoutCheck =
        match mixedLayouts |> List.tryFind (fun layout -> layout.TypeName = "TextMixed") with
        | None -> false, jsonObject [ "found", box false; "type", box "TextMixed" ]
        | Some layout ->
            let findField name = layout.Fields |> List.tryFind (fun field -> field.FieldName = name)
            let markerField = findField "marker"
            let textField = findField "text"
            let sentinelField = findField "sentinel"
            let emptyLayout = mixedLayouts |> List.tryFind (fun item -> item.TypeName = "Empty")
            let mixedLayoutOracle = workload.GetProperty("dynamicLayoutOracle")
            let passed =
                layout.IsDynamic
                && layout.PayloadBytes = -1
                && layout.ExtentBytes = -1
                && layout.MinimumPayloadBytes = mixedLayoutOracle.GetProperty("minimumPayloadBytes").GetProperty("TextMixed").GetInt32()
                && layout.MinimumExtentBytes = mixedLayoutOracle.GetProperty("minimumExtentBytes").GetProperty("TextMixed").GetInt32()
                && (match markerField with Some field -> not field.IsDynamic && not field.IsOffsetDynamic && field.OffsetBytes = 0 && field.PayloadBytes = 0 && field.ExtentBytes = 0 | None -> false)
                && (match textField with Some field -> field.IsDynamic && not field.IsOffsetDynamic && field.OffsetBytes = 0 && field.MinimumPayloadBytes = 8 && field.MinimumExtentBytes = 8 | None -> false)
                && (match sentinelField with Some field -> not field.IsDynamic && field.IsOffsetDynamic && field.OffsetBytes = -1 && field.MinimumPayloadBytes = 8 && field.MinimumExtentBytes = 8 && field.PayloadBytes = 8 && field.ExtentBytes = 8 | None -> false)
                && (match emptyLayout with Some empty -> not empty.IsDynamic && empty.PayloadBytes = 0 && empty.ExtentBytes = 8 && empty.MinimumPayloadBytes = 0 && empty.MinimumExtentBytes = 8 | None -> false)
            passed, jsonObject [
                "found", box true
                "isDynamic", box layout.IsDynamic
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "minimumPayloadBytes", box layout.MinimumPayloadBytes
                "minimumExtentBytes", box layout.MinimumExtentBytes
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "isOffsetDynamic", box field.IsOffsetDynamic; "isDynamic", box field.IsDynamic; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes; "minimumPayloadBytes", box field.MinimumPayloadBytes; "minimumExtentBytes", box field.MinimumExtentBytes ]))
                "emptyLayout", box (match emptyLayout with Some empty -> jsonObject [ "payloadBytes", box empty.PayloadBytes; "extentBytes", box empty.ExtentBytes ] | None -> null) ]
    let mixedLayoutPassed, mixedLayoutDetails = mixedLayoutCheck
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout-with-zero-width-field" mixedLayoutPassed mixedLayoutDetails

    let mixedProjectionOutput = Array.create mixedFixtureTextAndSentinelBytes.Length 0xA5uy
    let mixedProjectionResult = mixedProjectProgram.ExecuteInto([ mixedValue ], stateCapacity, mixedProjectionOutput)
    let mixedProjectionMetrics = stackMetrics (box mixedProjectionResult)
    let mixedProjectionEvents = mixedProjectionResult.LayoutEvents
    let mixedProjectionIndexedEvents = mixedProjectionEvents |> List.indexed |> List.toArray
    let mixedProjectionParentTransfers =
        mixedProjectionIndexedEvents
        |> Array.choose (fun (index, event) ->
            if event.Kind = "descriptor-transfer" && event.TypeId = textMixedTypeId && event.PayloadBytes = mixedExpectedRecordPayloadBytes then Some(index, event)
            else None)
    let mixedProjectionFieldExtracts = mixedProjectionIndexedEvents |> Array.choose (fun (index, event) -> if event.Kind = "field-extract" then Some(index, event) else None)
    let mixedProjectionTextExtracts = mixedProjectionFieldExtracts |> Array.filter (fun (_, event) -> event.TypeId = stringTypeId && event.ExtentBytes = mixedStringExtentBytes)
    let mixedProjectionSentinelExtracts = mixedProjectionFieldExtracts |> Array.filter (fun (_, event) -> event.TypeId = intTypeId && event.ExtentBytes = 8 && event.SourceOffsetBytes = Some mixedStringExtentBytes)
    let mixedProjectionRetainedIndex =
        mixedProjectionIndexedEvents
        |> Array.tryFind (fun (_, event) -> event.Kind = "retained-copy")
        |> Option.map fst
        |> Option.defaultValue Int32.MaxValue
    let projectedFieldCarriesOwnerEnd parentIndex parent expectedType (fieldIndex, field) =
        fieldIndex > parentIndex
        && field.OffsetBytes = parent.OffsetBytes
        && (mixedProjectionIndexedEvents
            |> Array.exists (fun (transferIndex, transfer) ->
                transferIndex > fieldIndex
                && transferIndex < mixedProjectionRetainedIndex
                && transfer.Kind = "descriptor-transfer"
                && transfer.TypeId = expectedType
                && transfer.OffsetBytes = (Option.defaultValue -1 field.SourceOffsetBytes)
                && transfer.PayloadBytes = field.PayloadBytes
                && transfer.SourceExtentBytes = Some field.ExtentBytes
                && transfer.SourceOffsetBytes = parent.SourceOffsetBytes))
    let mixedProjectionOwnerEndPass =
        mixedProjectionParentTransfers
        |> Array.exists (fun (parentIndex, parent) ->
            let textFieldCarriesOwnerEnd = mixedProjectionTextExtracts |> Array.exists (projectedFieldCarriesOwnerEnd parentIndex parent stringTypeId)
            let sentinelCarriesOwnerEnd = mixedProjectionSentinelExtracts |> Array.exists (projectedFieldCarriesOwnerEnd parentIndex parent intTypeId)
            textFieldCarriesOwnerEnd && sentinelCarriesOwnerEnd)
    let mixedProjectionPassed =
        mixedProjectionResult.Values = [ StringValue mixedText; IntValue mixedSentinel ]
        && mixedProjectionOutput = mixedFixtureTextAndSentinelBytes
        && mixedProjectionResult.RetainedBytesWritten = mixedFixtureTextAndSentinelBytes.Length
        && int64Property mixedProjectionMetrics "DeepCopyBytes" = 0L
        && int64Property mixedProjectionMetrics "MoveBytes" = 0L
        && mixedProjectionOwnerEndPass
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/project-string-and-following-sentinel" mixedProjectionPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedProjectionResult.Values)
        "expectedProjectionBytes", box (bytesHex mixedFixtureTextAndSentinelBytes)
        "actualProjectionBytes", box (bytesHex mixedProjectionOutput)
        "sentinelExpectedOffsetBytes", box (mixedStringBytes.Length)
        "sentinelExpectedValue", box mixedSentinel
        "parentOwnerEndPreservedByProjectedDescriptors", box mixedProjectionOwnerEndPass
        "parentDescriptors", box (mixedProjectionParentTransfers |> Array.map snd |> Array.toList |> layoutEventDetails)
        "projectedFieldExtractions", box (mixedProjectionFieldExtracts |> Array.map snd |> Array.toList |> layoutEventDetails)
        "projectedDescriptorTransfers", box (layoutEventDetails (mixedProjectionEvents |> List.filter (fun event -> event.Kind = "descriptor-transfer" && (event.TypeId = stringTypeId || event.TypeId = intTypeId))))
        "metrics", box (jsonNode options (metricSummary mixedProjectionMetrics))
        "events", box (layoutEventDetails mixedProjectionResult.LayoutEvents) ])
    let mixedEmptyProjectionOutput = Array.create mixedEmptyTokenBytes.Length 0xA5uy
    let mixedEmptyProjectionResult = mixedProjectEmptyProgram.ExecuteInto([ mixedValue ], stateCapacity, mixedEmptyProjectionOutput)
    let mixedEmptyProjectionPassed =
        mixedEmptyProjectionResult.Values = [ mixedEmpty ]
        && mixedEmptyProjectionOutput = mixedEmptyTokenBytes
        && mixedEmptyProjectionResult.RetainedBytesWritten = 8
        && int64Property (box mixedEmptyProjectionResult.Metrics) "DeepCopyBytes" = 0L
        && int64Property (box mixedEmptyProjectionResult.Metrics) "MoveBytes" = 0L
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/project-empty-as-standalone-token" mixedEmptyProjectionPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedEmptyProjectionResult.Values)
        "expectedTokenBytes", box (bytesHex mixedEmptyTokenBytes)
        "actualTokenBytes", box (bytesHex mixedEmptyProjectionOutput)
        "retainedBytesWritten", box mixedEmptyProjectionResult.RetainedBytesWritten
        "metrics", box (jsonNode options (metricSummary (box mixedEmptyProjectionResult.Metrics)))
        "events", box (layoutEventDetails mixedEmptyProjectionResult.LayoutEvents) ])

    let shortBranchState, _ = compiledInputBodies["short-ascii"]
    let longBranchState, _ = compiledInputBodies["long-bmp-astral"]
    let shortBranchEnvelope = textEnvelopeFromState shortBranchState
    let longBranchEnvelope = textEnvelopeFromState longBranchState
    let branchInputFactory = compileTextEnvelopePairFactory entries "dynamic-branch-join" shortBranchEnvelope longBranchEnvelope
    use branchInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "dynamic-branch-join-inputs" branchInputFactory None []
    let dynamicBranchReports = ResizeArray<obj>()
    for branchCase in workload.GetProperty("dynamicBranchCases").EnumerateArray() do
        let branchName = branchCase.GetProperty("name").GetString()
        let chooseLeft = branchCase.GetProperty("chooseLeft").GetBoolean()
        let selectedCaseName = branchCase.GetProperty("selectedMainCase").GetString()
        let selectedEnvelope = if chooseLeft then shortBranchEnvelope else longBranchEnvelope
        let selectedCase = mainCases |> Array.find (fun item -> item.GetProperty("name").GetString() = selectedCaseName)
        let selectedCodeUnitsHex = selectedCase.GetProperty("inputCodeUnitsHex").GetString()
        let selectedCodeUnitCount = selectedCase.GetProperty("inputCodeUnitCount").GetInt32()
        let selectedInput = selectedCase.GetProperty("input")
        let selectedTag = selectedInput.GetProperty("tag").GetInt64()
        let expectedLength = branchCase.GetProperty("expectedLengthOutput").GetInt64()
        let expectedEnvelopeBytes = textEnvelopeBytes selectedTag (int64 selectedCodeUnitCount) selectedCodeUnitsHex
        let expectedBranchBytes = Array.concat [ expectedEnvelopeBytes; bytesFromInt64s [ expectedLength ] ]
        let fixtureBranchBytes = bytesFromHex (branchCase.GetProperty("expectedRetainedBytesHex").GetString())
        let expectedEnvelopeExtent = branchCase.GetProperty("expectedSelectedEnvelopeExtentBytes").GetInt32()
        let fixtureOraclePassed =
            expectedEnvelopeBytes.Length = expectedEnvelopeExtent
            && expectedBranchBytes = fixtureBranchBytes
            && fixtureBranchBytes.Length = branchCase.GetProperty("expectedRetainedBytes").GetInt32()
            && expectedLength = int64 selectedCodeUnitCount
        recordCheck checks failures $"oracle/String/{branchName}/dynamic-branch-retained-bytes" fixtureOraclePassed (jsonObject [
            "selectedEnvelopeBytes", box (bytesHex expectedEnvelopeBytes)
            "selectedEnvelopeExtentBytes", box expectedEnvelopeBytes.Length
            "lengthOutput", box expectedLength
            "oracleRetainedBytes", box (bytesHex expectedBranchBytes)
            "fixtureRetainedBytes", box (bytesHex fixtureBranchBytes) ])
        use branchInterpreterResult =
            IrInterpreter.executeBodyWithInputs
                interpreterHost
                "mailbox.choose-text-envelope"
                entries.DynamicBranch
                (Some branchInputOwner)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.BoolArgument chooseLeft ]
        let branchInterpreterValues = branchInterpreterResult.Decode()
        let expectedBranchValues = [ selectedEnvelope; IntValue expectedLength ]
        recordCheck checks failures $"interpreter/String/{branchName}/phi-value-and-following-read" (branchInterpreterValues = expectedBranchValues) (codeUnitSafeValuesJson branchInterpreterValues)
        let branchOutput = Array.create fixtureBranchBytes.Length 0xA5uy
        let branchResult = dynamicBranchProgram.ExecuteInto([ shortBranchEnvelope; longBranchEnvelope; BoolValue chooseLeft ], stateCapacity, branchOutput)
        let branchValues = branchResult.Values
        let branchMetrics = stackMetrics (box branchResult)
        let returnedSecondOutput = int64FromBytesLittleEndian branchOutput expectedEnvelopeExtent
        let branchPassed =
            branchValues = expectedBranchValues
            && branchOutput = fixtureBranchBytes
            && branchResult.RetainedBytesWritten = fixtureBranchBytes.Length
            && returnedSecondOutput = expectedLength
            && fixtureBranchBytes.Length - expectedEnvelopeExtent = 8
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{branchName}/phi-layout-and-multi-output-return" branchPassed (jsonObject [
            "chooseLeft", box chooseLeft
            "selectedCase", box selectedCaseName
            "values", box (codeUnitSafeValuesJson branchValues)
            "expectedEnvelopeExtentBytes", box expectedEnvelopeExtent
            "secondOutputOffsetBytes", box expectedEnvelopeExtent
            "secondOutputValue", box returnedSecondOutput
            "expectedRetainedBytes", box (bytesHex fixtureBranchBytes)
            "actualRetainedBytes", box (bytesHex branchOutput)
            "retainedBytesWritten", box branchResult.RetainedBytesWritten
            "metrics", box (jsonNode options (metricSummary branchMetrics))
            "events", box (layoutEventDetails branchResult.LayoutEvents) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{branchName}/trace-complete" branchMetrics branchResult.LayoutEvents |> ignore
        dynamicBranchReports.Add(box (jsonObject [
            "case", box branchName
            "selectedCase", box selectedCaseName
            "expectedBytes", box (bytesHex fixtureBranchBytes)
            "actualBytes", box (bytesHex branchOutput)
            "metrics", box (jsonNode options (metricSummary branchMetrics))
            "events", box (layoutEventDetails branchResult.LayoutEvents) ]))

    let joined = workload.GetProperty("joinedSurrogatePair")
    let leftHex = joined.GetProperty("leftCodeUnitHex").GetString()
    let rightHex = joined.GetProperty("rightCodeUnitHex").GetString()
    let joinedHex = joined.GetProperty("expectedCodeUnitsHex").GetString()
    let leftValue = stringFromCodeUnitsHex leftHex
    let rightValue = stringFromCodeUnitsHex rightHex
    let joinedValue = StringValue(stringFromCodeUnitsHex joinedHex)
    let joinedBytes = bytesFromHex (joined.GetProperty("expectedStringBytesHex").GetString())
    let joinedOracle = stringBytesFromCodeUnitsHex joinedHex
    recordCheck checks failures $"oracle/String/joined-surrogate-pair/raw-code-units" (joinedOracle = joinedBytes && joinedBytes.Length = joined.GetProperty("expectedExtentBytes").GetInt32()) (jsonObject [
        "oracleHex", box (bytesHex joinedOracle)
        "fixtureHex", box (bytesHex joinedBytes)
        "codeUnitCount", box (joined.GetProperty("expectedCodeUnitCount").GetInt32()) ])
    let pairFactory = compileStringPairFactory entries "joined-surrogate-pair" leftValue rightValue
    use pairOwner = IrInterpreter.executeBodyWithInputs interpreterHost "joined-surrogate-pair-input" pairFactory None []
    use joinedInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.join-strings" entries.JoinStrings (Some pairOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use directConcatInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-direct-string-concat-capacity" entries.DirectConcat (Some pairOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    let joinedInterpreterValues = joinedInterpreterResult.Decode()
    let directConcatInterpreterValues = directConcatInterpreterResult.Decode()
    recordCheck checks failures $"interpreter/String/joined-surrogate-pair/concat-preserves-code-units" (joinedInterpreterValues = [ joinedValue ]) (codeUnitSafeValuesJson joinedInterpreterValues)
    recordCheck checks failures $"interpreter/String/direct-concat-capacity/same-verified-body" (directConcatInterpreterValues = [ joinedValue ]) (codeUnitSafeValuesJson directConcatInterpreterValues)
    let joinedOutput = Array.create joinedBytes.Length 0xA5uy
    let joinedResult = joinProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], stateCapacity, joinedOutput)
    let joinedValues = getProperty (box joinedResult) "Values" :?> Value list
    let joinedMetrics = stackMetrics (box joinedResult)
    recordCheck checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/two-dynamic-inputs" (joinedValues = [ joinedValue ] && joinedOutput = joinedBytes && (getProperty (box joinedResult) "RetainedOutputBytes" :?> byte array) = joinedBytes && int64Property joinedMetrics "InputBytes" = 32L) (jsonObject [
        "values", box (codeUnitSafeValuesJson joinedValues)
        "leftCodeUnitHex", box leftHex
        "rightCodeUnitHex", box rightHex
        "resultBytes", box (bytesHex joinedOutput)
        "metrics", box (jsonNode options (metricSummary joinedMetrics))
        "layoutEvents", box (layoutEventDetails joinedResult.LayoutEvents) ])
    let concatEvents = joinedResult.LayoutEvents |> List.filter (fun event -> event.Kind = "string-concat-left" || event.Kind = "string-concat-right")
    let concatRangesPass =
        concatEvents.Length = 2
        && (concatEvents |> List.forall (fun event -> event.ExtentBytes = 2 && event.PayloadBytes = 2 && event.SourceExtentBytes = Some 2))
        && (concatEvents |> List.map (fun event -> event.OffsetBytes) |> Set.ofList |> Set.count = 2)
    recordCheck checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/concat-source-ranges" concatRangesPass (jsonObject [
        "expectedCopiedCodeUnitBytesPerInput", box 2
        "events", box (layoutEventDetails concatEvents) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/trace-complete" joinedMetrics joinedResult.LayoutEvents |> ignore

    let independentCapacityOracle = workload.GetProperty("capacityCases").GetProperty("programStack").GetProperty("independentDynamicConcat")
    let inputExtents = independentCapacityOracle.GetProperty("inputStringExtentsBytes").EnumerateArray() |> Seq.map (fun item -> item.GetInt32()) |> Seq.toList
    let inputStackBytes = independentCapacityOracle.GetProperty("inputStackBytes").GetInt32()
    let concatOutputExtent = independentCapacityOracle.GetProperty("concatOutputExtentBytes").GetInt32()
    let concatDeepCopyBytes = independentCapacityOracle.GetProperty("deepCopyBytes").GetInt32()
    let requiredStackBytes = independentCapacityOracle.GetProperty("requiredStackCapacityBytes").GetInt32()
    let oneByteBelowCapacity = independentCapacityOracle.GetProperty("oneByteBelowAvailableBytes").GetInt32()
    let expectedOutputCodeUnits = independentCapacityOracle.GetProperty("concatOutputCodeUnitsHex").GetString()
    let expectedOutputBytes = stringBytesFromCodeUnitsHex expectedOutputCodeUnits
    let joinedTransferMetricsPass = descriptorTransferMetricsPass (joinedMetrics :?> OwningStackMetrics) joinedResult.LayoutEvents
    let wrappedJoinObservedPeakBytes = int64Property joinedMetrics "ReservedStackBytes"
    let independentSchedulePass =
        inputExtents = [ 16; 16 ]
        && inputStackBytes = List.sum inputExtents
        && requiredStackBytes = inputStackBytes + concatOutputExtent
        && oneByteBelowCapacity = requiredStackBytes - 1
        && joinedBytes = expectedOutputBytes
        && joinedBytes.Length = concatOutputExtent
        && int64Property joinedMetrics "InputBytes" = int64 inputStackBytes
        && int64Property joinedMetrics "InputCopyBytes" = int64 inputStackBytes
        && concatDeepCopyBytes = concatOutputExtent
        && int64Property joinedMetrics "DeepCopyBytes" = int64 concatDeepCopyBytes
        && int64Property joinedMetrics "MoveBytes" = 0L
        && int64Property joinedMetrics "ReservedStackBytes" = int64 requiredStackBytes
        && joinedTransferMetricsPass
    recordCheck checks failures $"owning-stack/{optimizationName}/String/descriptor-call-concat-cursor-capacity" independentSchedulePass (jsonObject [
        "inputExtentsBytes", box inputExtents
        "inputCopyBytes", box inputStackBytes
        "concatResultExtentBytes", box concatOutputExtent
        "independentlyRequiredCursorBytes", box requiredStackBytes
        "oneByteBelowAvailableBytes", box oneByteBelowCapacity
        "observedPeakCursorBytes", box wrappedJoinObservedPeakBytes
        "deepCopyBytes", box (int64Property joinedMetrics "DeepCopyBytes")
        "moveBytes", box (int64Property joinedMetrics "MoveBytes")
        "descriptorTransferMetricsMatchTrace", box joinedTransferMetricsPass
        "outputBytes", box (bytesHex joinedBytes) ])
    let exactCapacityFailureOutput = Array.create joinedBytes.Length 0xA5uy
    let exactCapacityFailureBefore = Array.copy exactCapacityFailureOutput
    let exactCapacityFailureCode, exactCapacityFailureMetrics, exactCapacityFailureRequired, exactCapacityFailureAvailable, exactCapacityFailureBoundary =
        try
            directConcatProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], oneByteBelowCapacity, exactCapacityFailureOutput) |> ignore
            "unexpected-success", null, -1L, -1L, ""
        with error ->
            exceptionCode error,
            getProperty error "Metrics",
            optionalInt64 error "RequiredBytes",
            optionalInt64 error "AvailableBytes",
            (getProperty error "Boundary" |> string)
    let exactCapacityFailurePassed =
        exactCapacityFailureCode = "OWNING_STACK_CAPACITY"
        && exactCapacityFailureBoundary = "program-data-stack"
        && exactCapacityFailureRequired = int64 requiredStackBytes
        && exactCapacityFailureAvailable = int64 oneByteBelowCapacity
        && not (isNull exactCapacityFailureMetrics)
        && int64Property exactCapacityFailureMetrics "InputBytes" = int64 inputStackBytes
        && int64Property exactCapacityFailureMetrics "InputCopyBytes" = int64 inputStackBytes
        && int64Property exactCapacityFailureMetrics "TraceEventCount" > 0L
        && not (Convert.ToBoolean(getProperty exactCapacityFailureMetrics "TraceTruncated", CultureInfo.InvariantCulture))
        && int64Property exactCapacityFailureMetrics "FinalCursorBytes" = 0L
        && exactCapacityFailureOutput = exactCapacityFailureBefore
    recordCheck checks failures $"owning-stack/{optimizationName}/String/independent-concat-capacity-one-byte-short" exactCapacityFailurePassed (jsonObject [
        "code", box exactCapacityFailureCode
        "boundary", box exactCapacityFailureBoundary
        "independentlyExpectedRequiredBytes", box requiredStackBytes
        "actualRequiredBytes", box exactCapacityFailureRequired
        "independentlyExpectedAvailableBytes", box oneByteBelowCapacity
        "actualAvailableBytes", box exactCapacityFailureAvailable
        "inputCopyBytesBeforeFailure", box (if isNull exactCapacityFailureMetrics then -1L else int64Property exactCapacityFailureMetrics "InputCopyBytes")
        "callerBufferUnchanged", box (bufferCheckDetails exactCapacityFailureBefore exactCapacityFailureOutput)
        "metrics", if isNull exactCapacityFailureMetrics then null else jsonNode options (metricSummary exactCapacityFailureMetrics) ])
    let exactCapacityOutput = Array.create joinedBytes.Length 0xA5uy
    let exactCapacityResult = directConcatProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], requiredStackBytes, exactCapacityOutput)
    let exactCapacityResultMetrics = stackMetrics (box exactCapacityResult)
    let exactCapacitySuccessPassed =
        exactCapacityResult.Values = [ joinedValue ]
        && exactCapacityOutput = joinedBytes
        && exactCapacityResult.RetainedBytesWritten = joinedBytes.Length
        && int64Property exactCapacityResultMetrics "InputCopyBytes" = int64 inputStackBytes
        && int64Property exactCapacityResultMetrics "ReservedStackBytes" = int64 requiredStackBytes
    recordCheck checks failures $"owning-stack/{optimizationName}/String/independent-concat-capacity-exact-boundary-succeeds" exactCapacitySuccessPassed (jsonObject [
        "capacityBytes", box requiredStackBytes
        "outputBytes", box (bytesHex exactCapacityOutput)
        "reservedStackBytes", box (int64Property exactCapacityResultMetrics "ReservedStackBytes")
        "metrics", jsonNode options (metricSummary exactCapacityResultMetrics) ])

    let shortCase = mainCases |> Array.find (fun element -> element.GetProperty("name").GetString() = "short-ascii")
    let longCase = mainCases |> Array.find (fun element -> element.GetProperty("name").GetString() = "long-bmp-astral")
    let shortState, _ = compiledInputBodies["short-ascii"]
    let longState, _ = compiledInputBodies["long-bmp-astral"]
    let shortEnvelope = textEnvelopeFromState shortState
    let longEnvelope = textEnvelopeFromState longState
    let shortEnvelopeHex = shortCase.GetProperty("inputCodeUnitsHex").GetString()
    let longEnvelopeHex = longCase.GetProperty("inputCodeUnitsHex").GetString()
    let envelopeParts = function
        | RecordValue("TextEnvelope", fields) ->
            match fields["tag"], fields["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> tag, codeUnits, text
                | _ -> invalidOp "TextEnvelope requires StringValue/IntValue leaf fields."
            | _ -> invalidOp "TextEnvelope requires an Int tag and TextLeaf."
        | _ -> invalidOp "Expected TextEnvelope."
    let shortTag, shortCodeUnits, shortText = envelopeParts shortEnvelope
    let longTag, longCodeUnits, longText = envelopeParts longEnvelope
    let scopePairs = [
        "short-outer-long-inner", shortEnvelope, longEnvelope, shortEnvelopeHex, shortTag, shortCodeUnits, shortText
        "long-outer-short-inner", longEnvelope, shortEnvelope, longEnvelopeHex, longTag, longCodeUnits, longText ]
    let scopeReports = ResizeArray<obj>()
    for caseName, outerValue, innerValue, outerHex, outerTag, outerCodeUnits, outerText in scopePairs do
        let pairInputFactory = compileTextEnvelopePairFactory entries caseName outerValue innerValue
        use scopeInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("scope-input-" + caseName) pairInputFactory None []
        use scopeInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-scope-shadow" entries.ScopeShadow (Some scopeInputOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
        recordCheck checks failures $"interpreter/String/scope-shadow/{caseName}" (scopeInterpreterResult.Decode() = [ outerValue ]) (codeUnitSafeValuesJson (scopeInterpreterResult.Decode()))
        let expectedEnvelopeBytes = textEnvelopeBytes outerTag outerCodeUnits outerHex
        let scopeOutput = Array.create expectedEnvelopeBytes.Length 0xA5uy
        let scopeResult = scopeProgram.ExecuteInto([ outerValue; innerValue ], stateCapacity, scopeOutput)
        let scopeMetrics = stackMetrics (box scopeResult)
        let scopeValuePass = scopeResult.Values = [ outerValue ] && scopeOutput = expectedEnvelopeBytes
        let cleanupPass = int64Property scopeMetrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/outer-restore" scopeValuePass (jsonObject [
            "values", box (codeUnitSafeValuesJson scopeResult.Values)
            "expectedOuterBytes", box (bytesHex expectedEnvelopeBytes)
            "actualOuterBytes", box (bytesHex scopeOutput) ])
        let descriptorOnly = int64Property scopeMetrics "MoveBytes" = 0L && int64Property scopeMetrics "DeepCopyBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/cleanup-and-local-categories" (cleanupPass && descriptorOnly) (jsonObject [
            "reservedLocalBytes", box (int64Property scopeMetrics "ReservedLocalBytes")
            "backendMetadataPerFrameBytes", box (int64Property scopeMetrics "BackendMetadataPerFrameBytes")
            "backendMetadataPeakBoundBytes", box (int64Property scopeMetrics "BackendMetadataPeakBoundBytes")
            "descriptorOnlyPathHasNoPayloadCopiesOrMoves", box descriptorOnly
            "finalCursorBytes", box (int64Property scopeMetrics "FinalCursorBytes")
            "metrics", box (jsonNode options (metricSummary scopeMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/trace-complete" scopeMetrics scopeResult.LayoutEvents |> ignore
        scopeReports.Add(box (jsonObject [
            "case", box caseName
            "outerCodeUnits", box (outerText.Length)
            "outerValueBytes", box (bytesHex expectedEnvelopeBytes)
            "metrics", box (jsonNode options (metricSummary scopeMetrics))
            "events", box (layoutEventDetails scopeResult.LayoutEvents) ]))

    let stableCases = fixture.GetProperty("stableArenaCases")
    let stableDead = stableCases.GetProperty("stringSixLocalNestedDeadOnly")
    let stableDeadInputs = stableDead.GetProperty("inputs").EnumerateArray() |> Seq.map (fun item -> StringValue(item.GetString())) |> Seq.toList
    let stableDeadResult = stableDeadProgram.ExecuteInto(stableDeadInputs, stateCapacity, Array.empty)
    let stableDeadMetrics = stableDeadResult.Metrics
    let stableDeadRewinds = stableDeadResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
    let stableDeadIndexedEvents = stableDeadResult.LayoutEvents |> List.indexed |> List.toArray
    let stableDeadMark = stableDead.GetProperty("inputCopyBytes").GetInt32()
    let stableDeadRewindPositions =
        stableDeadIndexedEvents
        |> Array.choose (fun (index, event) -> if event.Kind = "arena-rewind" then Some(index, event) else None)
    let stableDeadInnerRewindIndex = stableDeadRewindPositions |> Array.tryItem 0 |> Option.map fst |> Option.defaultValue -1
    let stableDeadReusedAllocation =
        stableDeadIndexedEvents
        |> Array.tryFind (fun (index, event) ->
            index > stableDeadInnerRewindIndex
            && event.Kind = "allocate"
            && event.TypeId = stringTypeId
            && event.OffsetBytes = stableDead.GetProperty("postRewindAllocationOffsetBytes").GetInt32()
            && event.ExtentBytes = stableDead.GetProperty("postRewindAllocationExtentBytes").GetInt32()
            && event.PayloadBytes = stableDead.GetProperty("postRewindAllocationPayloadBytes").GetInt32())
    let stableDeadReusedAllocationIndex = stableDeadReusedAllocation |> Option.map fst |> Option.defaultValue -1
    let stableDeadLoadsAfterReuse =
        match stableDeadRewindPositions |> Array.tryItem 1 with
        | None -> [||]
        | Some(outerRewindIndex, _) ->
            stableDeadIndexedEvents
            |> Array.choose (fun (index, event) ->
                if index > stableDeadReusedAllocationIndex
                   && index < outerRewindIndex
                   && event.Kind = "descriptor-transfer"
                   && event.TypeId = stringTypeId
                   && event.PayloadBytes = 10
                   && event.SourceExtentBytes = Some 16
                   && event.SourceOffsetBytes = Some(event.OffsetBytes + 16) then
                    Some event.OffsetBytes
                else None)
    let stableDeadReusePass =
        stableDeadRewindPositions.Length = 3
        && (stableDeadReusedAllocation |> Option.exists (fun (allocationIndex, allocation) ->
            allocationIndex > fst stableDeadRewindPositions[0]
            && allocationIndex < fst stableDeadRewindPositions[1]
            && allocation.OffsetBytes = stableDeadMark))
        && Array.sort stableDeadLoadsAfterReuse = [| 0; 16; 32; 48; 64; 80 |]
    let stableDeadRewindPass =
        stableDeadRewinds.Length = stableDead.GetProperty("rewindEventCount").GetInt32()
        && stableDead.GetProperty("nestedScopeCount").GetInt32() + stableDead.GetProperty("functionExitRewindCount").GetInt32() = stableDeadRewinds.Length
        && List.sort (stableDeadRewinds |> List.map (fun event -> event.OffsetBytes, event.ExtentBytes, event.PayloadBytes)) =
           List.sort [
               stableDeadMark, stableDead.GetProperty("innerRewindExtentBytes").GetInt32(), 0
               stableDeadMark, stableDead.GetProperty("outerRewindExtentBytes").GetInt32(), 0
               stableDeadMark, stableDead.GetProperty("functionExitRewindExtentBytes").GetInt32(), 0 ]
    let stableDeadMetricsPass =
        stableDeadResult.Values.IsEmpty
        && stableDeadResult.RetainedBytesWritten = stableDead.GetProperty("outputCount").GetInt32()
        && int64Property (box stableDeadMetrics) "InputCopyBytes" = int64 (stableDead.GetProperty("inputCopyBytes").GetInt32())
        && int64Property (box stableDeadMetrics) "DeepCopyBytes" = int64 (stableDead.GetProperty("deepCopyBytes").GetInt32())
        && int64Property (box stableDeadMetrics) "MoveBytes" = int64 (stableDead.GetProperty("moveBytes").GetInt32())
        && int64Property (box stableDeadMetrics) "ReservedStackBytes" = int64 (stableDead.GetProperty("expectedPeakCursorBytes").GetInt32())
        && int64Property (box stableDeadMetrics) "FinalCursorBytes" = 0L
        && stableDeadRewindPass
        && stableDeadReusePass
        && descriptorTransferMetricsPass stableDeadMetrics stableDeadResult.LayoutEvents
    let stableDeadLlvmSites = stableDead.GetProperty("nestedScopeCount").GetInt32() + stableDead.GetProperty("functionExitRewindCount").GetInt32()
    let stableDeadLlvmPass, stableDeadLlvmDetails = generatedStablePolicyPass stableDeadProgram stableDeadLlvmSites
    let stableDeadTracePass = checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/stable-arena/dead-only-trace-complete" stableDeadMetrics stableDeadResult.LayoutEvents
    recordCheck checks failures $"owning-stack/{optimizationName}/String/stable-arena/dead-only-nested-scopes-rewind" (stableDeadMetricsPass && stableDeadLlvmPass && stableDeadTracePass) (jsonObject [
        "inputCount", box stableDeadInputs.Length
        "scopeRewindSiteCount", box (stableDead.GetProperty("nestedScopeCount").GetInt32())
        "functionExitRewindSiteCount", box (stableDead.GetProperty("functionExitRewindCount").GetInt32())
        "rewindEvents", box (layoutEventDetails stableDeadRewinds)
        "rewindRangesMatchSavedMarks", box stableDeadRewindPass
        "postRewindAllocation", box (stableDeadReusedAllocation |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
        "postRewindInputDescriptorOffsets", box stableDeadLoadsAfterReuse
        "postRewindAllocationReusesSavedMarkWithoutMovingPremarkInputs", box stableDeadReusePass
        "generatedLlvmPolicy", box stableDeadLlvmDetails
        "metrics", box (jsonNode options (metricSummary (box stableDeadMetrics))) ])

    let stableEscape = stableCases.GetProperty("stringEscapingScopeResult")
    let stableEscapeBytes = bytesFromHex (stableEscape.GetProperty("retainedBytesHex").GetString())
    let stableEscapeOutput = Array.create stableEscapeBytes.Length 0xA5uy
    let stableEscapeResult = stableEscapeProgram.ExecuteInto([], stateCapacity, stableEscapeOutput)
    let stableEscapeMetrics = stableEscapeResult.Metrics
    let stableEscapeRewinds = stableEscapeResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
    let stableEscapeRetention = stableEscapeResult.LayoutEvents |> List.tryFind (fun event -> event.Kind = "retained-copy")
    let stableEscapeConstructionCopyBytes =
        (stableEscape.GetProperty("literalExtentsBytes").EnumerateArray() |> Seq.sumBy (fun item -> item.GetInt32()))
        + stableEscape.GetProperty("resultExtentBytes").GetInt32()
    let stableEscapeMetricsPass =
        stableEscapeResult.Values = [ StringValue(stableEscape.GetProperty("expected").GetString()) ]
        && stableEscapeOutput = stableEscapeBytes
        && stableEscapeConstructionCopyBytes = stableEscape.GetProperty("deepCopyBytes").GetInt32()
        && int64Property (box stableEscapeMetrics) "DeepCopyBytes" = int64 stableEscapeConstructionCopyBytes
        && int64Property (box stableEscapeMetrics) "MoveBytes" = int64 (stableEscape.GetProperty("moveBytes").GetInt32())
        && int64Property (box stableEscapeMetrics) "ReservedStackBytes" >= int64 (stableEscape.GetProperty("minimumPeakCursorBytes").GetInt32())
        && int64Property (box stableEscapeMetrics) "FinalCursorBytes" = 0L
        && stableEscapeRewinds.Length = stableEscape.GetProperty("scopeRewinds").GetInt32()
        && (stableEscapeRetention |> Option.exists (fun event -> event.SourceOffsetBytes |> Option.exists (fun source -> source > 16)))
        && descriptorTransferMetricsPass stableEscapeMetrics stableEscapeResult.LayoutEvents
    let stableEscapeLlvmPass, stableEscapeLlvmDetails = generatedStablePolicyPass stableEscapeProgram (stableEscape.GetProperty("scopeRewinds").GetInt32())
    let stableEscapeTracePass = checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/stable-arena/escape-trace-complete" stableEscapeMetrics stableEscapeResult.LayoutEvents
    recordCheck checks failures $"owning-stack/{optimizationName}/String/stable-arena/escaping-result-keeps-temporary-below-result" (stableEscapeMetricsPass && stableEscapeLlvmPass && stableEscapeTracePass) (jsonObject [
        "values", box (codeUnitSafeValuesJson stableEscapeResult.Values)
        "expectedBytes", box (bytesHex stableEscapeBytes)
        "actualBytes", box (bytesHex stableEscapeOutput)
        "retainedCopySourceOffset", box (stableEscapeRetention |> Option.bind (fun event -> event.SourceOffsetBytes) |> Option.map box |> Option.defaultValue null)
        "rewindEvents", box (layoutEventDetails stableEscapeRewinds)
        "generatedLlvmPolicy", box stableEscapeLlvmDetails
        "metrics", box (jsonNode options (metricSummary (box stableEscapeMetrics))) ])

    let stableBinding = stableCases.GetProperty("stringBindingRoundTrip")
    let stableBindingBytes = bytesFromHex (stableBinding.GetProperty("retainedBytesHex").GetString())
    let stableBindingOutput = Array.create stableBindingBytes.Length 0xA5uy
    let stableBindingResult = stableBindingProgram.ExecuteInto([], stateCapacity, stableBindingOutput)
    let stableBindingMetrics = stableBindingResult.Metrics
    let stableBindingIndexedEvents = stableBindingResult.LayoutEvents |> List.indexed |> List.toArray
    let stableBindingTransfers = stableBindingIndexedEvents |> Array.choose (fun (index, event) -> if event.Kind = "descriptor-transfer" then Some(index, event) else None)
    let stableBindingRewinds = stableBindingResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
    let stableBindingExpectedLiteralBytes = stableBinding.GetProperty("literalExtentBytes").GetInt32()
    let stableBindingExpectedLiteralPayloadBytes = 8 + 2 * stableBinding.GetProperty("value").GetString().Length
    let stableBindingLiteral =
        stableBindingIndexedEvents
        |> Array.tryFind (fun (_, event) ->
            event.Kind = "allocate"
            && event.TypeId = stringTypeId
            && event.ExtentBytes = stableBindingExpectedLiteralBytes
            && event.PayloadBytes = stableBindingExpectedLiteralPayloadBytes)
    let stableBindingRetention =
        stableBindingIndexedEvents
        |> Array.tryFind (fun (_, event) -> event.Kind = "retained-copy")
    let stableBindingTransferMatchesLiteral (literal: OwningStackLayoutEvent) (transfer: OwningStackLayoutEvent) =
        transfer.TypeId = stringTypeId
        && transfer.OffsetBytes = literal.OffsetBytes
        && transfer.PayloadBytes = literal.PayloadBytes
        && transfer.SourceOffsetBytes = Some(literal.OffsetBytes + literal.ExtentBytes)
        && transfer.SourceExtentBytes = Some literal.ExtentBytes
    let stableBindingAddressStable =
        match stableBindingLiteral, stableBindingTransfers |> Array.toList, stableBindingRetention with
        | Some(literalIndex, literal), [ (storeIndex, store); (loadIndex, load); (resultIndex, resultTransfer) ], Some(retainedIndex, retained) ->
            literalIndex < storeIndex
            && storeIndex < loadIndex
            && loadIndex < resultIndex
            && resultIndex < retainedIndex
            && ([ store; load; resultTransfer ] |> List.forall (stableBindingTransferMatchesLiteral literal))
            && retained.TypeId = stringTypeId
            && retained.SourceOffsetBytes = Some literal.OffsetBytes
            && retained.SourceExtentBytes = Some literal.ExtentBytes
            && retained.ExtentBytes = literal.ExtentBytes
        | _ -> false
    let stableBindingPass =
        stableBindingResult.Values = [ StringValue(stableBinding.GetProperty("value").GetString()) ]
        && stableBindingOutput = stableBindingBytes
        && stableBindingExpectedLiteralBytes = stableBindingBytes.Length
        && stableBindingExpectedLiteralPayloadBytes = stableBinding.GetProperty("literalPayloadBytes").GetInt32()
        && stableBinding.GetProperty("deepCopyBytes").GetInt32() = stableBindingExpectedLiteralBytes
        && int64Property (box stableBindingMetrics) "DeepCopyBytes" = int64 (stableBinding.GetProperty("deepCopyBytes").GetInt32())
        && int64Property (box stableBindingMetrics) "MoveBytes" = int64 (stableBinding.GetProperty("moveBytes").GetInt32())
        && stableBindingMetrics.DescriptorTransferCount = Some(stableBinding.GetProperty("descriptorTransferCount").GetInt32())
        && stableBindingMetrics.DescriptorTransferBytes = Some(stableBinding.GetProperty("descriptorTransferBytes").GetInt32())
        && int64Property (box stableBindingMetrics) "RetainedCopyBytes" = int64 stableBindingBytes.Length
        && int64Property (box stableBindingMetrics) "FinalCursorBytes" = 0L
        && stableBindingRewinds.Length = stableBinding.GetProperty("scopeRewinds").GetInt32()
        && stableBindingAddressStable
        && descriptorTransferMetricsPass stableBindingMetrics stableBindingResult.LayoutEvents
    let stableBindingLlvmPass, stableBindingLlvmDetails = generatedStablePolicyPass stableBindingProgram (stableBinding.GetProperty("scopeRewinds").GetInt32())
    let stableBindingTracePass = checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/stable-arena/binding-trace-complete" stableBindingMetrics stableBindingResult.LayoutEvents
    recordCheck checks failures $"owning-stack/{optimizationName}/String/stable-arena/local-store-load-preserves-result-address" (stableBindingPass && stableBindingLlvmPass && stableBindingTracePass) (jsonObject [
        "values", box (codeUnitSafeValuesJson stableBindingResult.Values)
        "expectedBytes", box (bytesHex stableBindingBytes)
        "actualBytes", box (bytesHex stableBindingOutput)
        "literalAllocation", box (stableBindingLiteral |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
        "descriptorTransferIndices", box (stableBindingTransfers |> Array.map fst)
        "expectedDescriptorTransferRoles", box [| "local-store"; "local-load"; "frame-result" |]
        "descriptorTransfersInStoreLoadReturnOrder", box (stableBindingTransfers |> Array.map (fun (_, event) -> event) |> Array.toList |> layoutEventDetails)
        "retainedCopy", box (stableBindingRetention |> Option.map (fun (_, event) -> layoutEventDetails [ event ]) |> Option.defaultValue [||])
        "addressStable", box stableBindingAddressStable
        "generatedLlvmPolicy", box stableBindingLlvmDetails
        "metrics", box (jsonNode options (metricSummary (box stableBindingMetrics))) ])

    let stableUncertain = stableCases.GetProperty("stringUncertainCallScope")
    let stableUncertainValue = StringValue(stableUncertain.GetProperty("input").GetString())
    let stableUncertainBytes = bytesFromHex (stableUncertain.GetProperty("retainedBytesHex").GetString())
    let stableUncertainOutput = Array.create stableUncertainBytes.Length 0xA5uy
    let stableUncertainResult = stableUncertainCallProgram.ExecuteInto([ stableUncertainValue ], stateCapacity, stableUncertainOutput)
    let stableUncertainMetrics = stableUncertainResult.Metrics
    let stableUncertainRewinds = stableUncertainResult.LayoutEvents |> List.filter (fun event -> event.Kind = "arena-rewind")
    let stableUncertainPass =
        stableUncertainResult.Values = [ stableUncertainValue ]
        && stableUncertainOutput = stableUncertainBytes
        && int64Property (box stableUncertainMetrics) "InputCopyBytes" = int64 stableUncertainBytes.Length
        && int64Property (box stableUncertainMetrics) "DeepCopyBytes" = 0L
        && int64Property (box stableUncertainMetrics) "MoveBytes" = int64 (stableUncertain.GetProperty("moveBytes").GetInt32())
        && int64Property (box stableUncertainMetrics) "FinalCursorBytes" = 0L
        && stableUncertainRewinds.Length = stableUncertain.GetProperty("calledFunctionRewindEventCount").GetInt32()
        && (stableUncertainRewinds |> List.forall (fun event -> event.OffsetBytes = stableUncertainBytes.Length && event.ExtentBytes = stableUncertain.GetProperty("calledFunctionRewindExtentBytes").GetInt32() && event.PayloadBytes = 0))
        && descriptorTransferMetricsPass stableUncertainMetrics stableUncertainResult.LayoutEvents
    let stableUncertainLlvmPass, stableUncertainLlvmDetails = generatedStablePolicyPass stableUncertainCallProgram (stableUncertain.GetProperty("scopeRewinds").GetInt32())
    let stableUncertainCalledFunctionPass, stableUncertainCalledFunctionDetails = generatedCalledFunctionRewindPass stableUncertainCallProgram (stableUncertain.GetProperty("calledFunctionRewindSites").GetInt32())
    let stableUncertainTracePass = checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/stable-arena/uncertain-call-trace-complete" stableUncertainMetrics stableUncertainResult.LayoutEvents
    recordCheck checks failures $"owning-stack/{optimizationName}/String/stable-arena/uncertain-call-output-blocks-rewind" (stableUncertainPass && stableUncertainLlvmPass && stableUncertainCalledFunctionPass && stableUncertainTracePass) (jsonObject [
        "inputBytes", box (bytesHex stableUncertainBytes)
        "outputBytes", box (bytesHex stableUncertainOutput)
        "rewindEvents", box (layoutEventDetails stableUncertainRewinds)
        "generatedLlvmPolicy", box stableUncertainLlvmDetails
        "calledFunctionGeneratedLlvmPolicy", box stableUncertainCalledFunctionDetails
        "metrics", box (jsonNode options (metricSummary (box stableUncertainMetrics))) ])

    let duplicateInputFactory = compileTextEnvelopePairFactory entries "duplicate-long" longEnvelope longEnvelope
    use duplicateInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "duplicate-long-input" duplicateInputFactory None []
    use duplicateInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-direct-dup-drop" entries.DirectDupDrop (Some duplicateInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
    recordCheck checks failures "interpreter/String/direct-dup-drop-preserves-survivor" (duplicateInterpreterResult.Decode() = [ longEnvelope ]) (codeUnitSafeValuesJson (duplicateInterpreterResult.Decode()))
    let duplicateExpectedBytes = textEnvelopeBytes longTag longCodeUnits longEnvelopeHex
    let duplicateOutput = Array.create duplicateExpectedBytes.Length 0xA5uy
    let duplicateResult = duplicateProgram.ExecuteInto([ longEnvelope ], stateCapacity, duplicateOutput)
    let duplicateEvents = duplicateResult.LayoutEvents
    let duplicateCopyEvents = duplicateEvents |> List.filter (fun event -> event.Kind = "duplicate")
    let duplicateDropEvents = duplicateEvents |> List.filter (fun event -> event.Kind = "drop")
    let duplicatePhysicalPass =
        duplicateCopyEvents.Length = 1
        && (duplicateCopyEvents |> List.forall (fun event ->
            match event.SourceOffsetBytes with
            | Some source ->
                let sourceExtent = defaultArg event.SourceExtentBytes event.ExtentBytes
                let destinationStart, destinationEnd = int64 event.OffsetBytes, int64 event.OffsetBytes + int64 event.ExtentBytes
                let sourceStart, sourceEnd = int64 source, int64 source + int64 sourceExtent
                destinationEnd <= sourceStart || sourceEnd <= destinationStart
            | None -> false))
        && (duplicateDropEvents |> List.exists (fun drop -> duplicateCopyEvents |> List.exists (fun duplicate -> drop.OffsetBytes = duplicate.OffsetBytes && drop.ExtentBytes = duplicate.ExtentBytes)))
        && duplicateResult.Values = [ longEnvelope ]
        && int64Property (box duplicateResult.Metrics) "MoveBytes" = 0L
        && int64Property (box duplicateResult.Metrics) "DeepCopyBytes" = int64 duplicateCopyEvents[0].ExtentBytes
        && (duplicateEvents |> List.forall (fun event -> event.Kind <> "call-input-move" && event.Kind <> "call-return-move" && event.Kind <> "local-compact"))
        && duplicateOutput = duplicateExpectedBytes
    recordCheck checks failures $"owning-stack/{optimizationName}/String/direct-dup-drop-disjoint-and-survivor" duplicatePhysicalPass (jsonObject [
        "expectedEnvelopeBytes", box (bytesHex duplicateExpectedBytes)
        "actualEnvelopeBytes", box (bytesHex duplicateOutput)
        "copyEvents", box (layoutEventDetails duplicateCopyEvents)
        "dropEvents", box (layoutEventDetails duplicateDropEvents)
        "metrics", box (jsonNode options (metricSummary (box duplicateResult.Metrics))) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/direct-dup-drop/trace-complete" duplicateResult.Metrics duplicateEvents |> ignore

    let zeroEnvelope = shortEnvelope
    let zeroFactory = compileTextEnvelopePairFactory entries "zero-output" zeroEnvelope zeroEnvelope
    use zeroInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "zero-output-input" zeroFactory None []
    use zeroInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-zero-output-user-call" entries.ZeroOutputUserCall (Some zeroInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
    recordCheck checks failures "interpreter/String/zero-output-user-call" (zeroInterpreterResult.Decode() = []) (codeUnitSafeValuesJson (zeroInterpreterResult.Decode()))
    let zeroBuffer = Array.create 32 0xA5uy
    let zeroBefore = Array.copy zeroBuffer
    let zeroResult = zeroOutputProgram.ExecuteInto([ zeroEnvelope ], stateCapacity, zeroBuffer)
    let zeroMetrics = box zeroResult.Metrics
    let zeroPassed = zeroResult.Values = [] && zeroResult.RetainedBytesWritten = 0 && zeroBuffer = zeroBefore && int64Property zeroMetrics "FinalCursorBytes" = 0L
    recordCheck checks failures $"owning-stack/{optimizationName}/String/zero-output-user-call-return" zeroPassed (jsonObject [
        "values", box []
        "retainedBytesWritten", box zeroResult.RetainedBytesWritten
        "callerBufferUnchanged", box (bufferCheckDetails zeroBefore zeroBuffer)
        "metrics", box (jsonNode options (metricSummary zeroMetrics))
        "events", box (layoutEventDetails zeroResult.LayoutEvents) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/zero-output-user-call/trace-complete" zeroResult.Metrics zeroResult.LayoutEvents |> ignore

    let inputOwnerForFailure = compileTextStateFactory entries "failure-after-allocation" longState
    use failureInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "text-failure-input" inputOwnerForFailure None []
    let interpreterFailureCode =
        try
            use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.fail-after-text-allocation" entries.FailAfterTextAllocation (Some failureInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures "interpreter/String/failure-after-concat" (interpreterFailureCode = "RUNTIME_DIVIDE_BY_ZERO" && failureInputOwner.Decode() = [ longState ]) interpreterFailureCode
    let failureBuffer = Array.create 80 0xA5uy
    let failureBefore = Array.copy failureBuffer
    let nativeFailure, nativeFailureMetrics =
        try
            failProgram.ExecuteInto([ longState ], stateCapacity, failureBuffer) |> ignore
            "unexpected-success", null
        with error -> diagnosticCode error, getProperty error "Metrics"
    let nativeFailurePassed =
        nativeFailure = "RUNTIME_DIVIDE_BY_ZERO"
        && not (isNull nativeFailureMetrics)
        && int64Property nativeFailureMetrics "DeepCopyBytes" > 0L
        && int64Property nativeFailureMetrics "FinalCursorBytes" = 0L
        && failureBuffer = failureBefore
    recordCheck checks failures $"owning-stack/{optimizationName}/String/failure-after-concat-unwinds-and-does-not-publish" nativeFailurePassed (jsonObject [
        "diagnosticCode", box nativeFailure
        "callerBufferUnchanged", box (bufferCheckDetails failureBefore failureBuffer)
        "metrics", if isNull nativeFailureMetrics then null else jsonNode options (metricSummary nativeFailureMetrics) ])

    let retainedFailureReports = ResizeArray<obj>()
    for caseElement in mainCases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, _ = compiledInputBodies[name]
        let expectedBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let sentinel = Array.create expectedBytes.Length 0xA5uy
        let old = Array.copy sentinel
        let available = expectedBytes.Length - 1
        let failureCode, failureMetrics, required, actualAvailable, boundary =
            try
                turnProgram.ExecuteInto([ inputValue ], stateCapacity, sentinel, available) |> ignore
                "unexpected-success", null, -1L, -1L, ""
            with error ->
                exceptionCode error,
                getProperty error "Metrics",
                optionalInt64 error "RequiredBytes",
                optionalInt64 error "AvailableBytes",
                (getProperty error "Boundary" |> string)
        let passed = failureCode = "OWNING_RETAINED_CAPACITY" && boundary = "retained-output" && required = int64 expectedBytes.Length && actualAvailable = int64 available && sentinel = old && not (isNull failureMetrics) && int64Property failureMetrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/retained-capacity-atomicity" passed (jsonObject [
            "code", box failureCode
            "boundary", box boundary
            "requiredBytes", box required
            "availableBytes", box actualAvailable
            "oldCallerBytesUnchanged", box (sentinel = old)
            "metrics", if isNull failureMetrics then null else jsonNode options (metricSummary failureMetrics) ])
        retainedFailureReports.Add(box (jsonObject [ "case", box name; "requiredBytes", box required; "availableBytes", box actualAvailable; "bufferUnchanged", box (sentinel = old) ]))

    let longMetrics = metricsByCase["long-bmp-astral"]
    let highWater = int64Property (box longMetrics) "ReservedStackBytes" + int64Property (box longMetrics) "ReservedLocalBytes"
    let upperCapacity = int (max 0L (highWater - 1L))
    let mutable selectedStackFailure = None
    for available in upperCapacity .. -1 .. 0 do
        if selectedStackFailure.IsNone then
            let attemptOutput = Array.create 80 0xA5uy
            let attemptBefore = Array.copy attemptOutput
            try
                turnProgram.ExecuteInto([ longState ], available, attemptOutput) |> ignore
            with error ->
                if exceptionCode error = "OWNING_STACK_CAPACITY" then
                    let metrics = getProperty error "Metrics"
                    let deepCopy = if isNull metrics then 0L else int64Property metrics "DeepCopyBytes"
                    let frameReturns = if isNull metrics then 0L else int64Property metrics "FrameReturnCount"
                    let traceCount = if isNull metrics then 0L else int64Property metrics "TraceEventCount"
                    if deepCopy > 0L && frameReturns > 0L && traceCount > 0L then
                        selectedStackFailure <- Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore)
    match selectedStackFailure with
    | None -> recordCheck checks failures $"owning-stack/{optimizationName}/String/program-stack-capacity-after-concat" false (jsonObject [
        "searchUpperCapacityBytes", box upperCapacity
        "successfulStackAndLocalHighWaterBytes", box highWater
        "reason", box "No one-byte-under or lower stack capacity reached a capacity error after concat/copy events." ])
    | Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore) ->
        let capacityPassed =
            exceptionCode error = "OWNING_STACK_CAPACITY"
            && (getProperty error "Boundary" |> string) = "program-data-stack"
            && deepCopy > 0L
            && frameReturns > 0L
            && traceCount > 0L
            && int64Property metrics "FinalCursorBytes" = 0L
            && attemptOutput = attemptBefore
        recordCheck checks failures $"owning-stack/{optimizationName}/String/program-stack-capacity-after-concat" capacityPassed (jsonObject [
            "capacityBytes", box available
            "requiredBytes", box (getProperty error "RequiredBytes")
            "availableBytes", box (getProperty error "AvailableBytes")
            "deepCopyBytesBeforeFailure", box deepCopy
            "helperFrameReturnsBeforeFailure", box frameReturns
            "traceEventCountBeforeFailure", box traceCount
            "callerBufferUnchanged", box (bufferCheckDetails attemptBefore attemptOutput)
            "metrics", box (jsonNode options (metricSummary metrics)) ])

    let stackFailureSummary: obj =
        match selectedStackFailure with
        | Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore) ->
            box (jsonObject [
                "availableBytes", box available
                "requiredBytes", box (getProperty error "RequiredBytes")
                "deepCopyBytesBeforeFailure", box deepCopy
                "helperFrameReturnsBeforeFailure", box frameReturns
                "traceEventCountBeforeFailure", box traceCount
                "callerBufferUnchanged", box (attemptOutput = attemptBefore)
                "metrics", jsonNode options (metricSummary metrics) ])
        | None -> null
    jsonObject [
        "backend", box $"inline-owning-stack-strings/{optimizationName}"
        "sameVerifiedProgramInstance", box true
        "layoutSchemaVersion", box layoutSchemaVersion
        "dynamicTypeLayouts", jsonNode options dynamicLayoutSummary
        "interpreterCases", box (interpreterReports.ToArray())
        "owningCases", box (owningReports.ToArray())
        "directValueStringRoundTrips", box (roundTripReports.ToArray())
        "joinedSurrogatePair", jsonObject [
            "expectedBytes", box (bytesHex joinedBytes)
            "actualBytes", box (bytesHex joinedOutput)
            "metrics", jsonNode options (metricSummary joinedMetrics)
            "concatSourceEvents", jsonNode options (layoutEventDetails concatEvents) ]
        "mixedEmptyStringIntRecord", jsonObject [
            "expectedRecordBytes", box (bytesHex mixedRecordBytes)
            "actualRecordBytes", box (bytesHex mixedRecordOutput)
            "projectedStringAndSentinelBytes", box (bytesHex mixedProjectionOutput)
            "projectedEmptyTokenBytes", box (bytesHex mixedEmptyProjectionOutput)
            "layout", box mixedLayoutDetails
            "constructMetrics", box (jsonNode options (metricSummary mixedConstructMetrics))
            "projectionMetrics", box (jsonNode options (metricSummary mixedProjectionMetrics))
            "emptyProjectionMetrics", box (jsonNode options (metricSummary (box mixedEmptyProjectionResult.Metrics)))
            "constructEvents", box (jsonNode options (layoutEventDetails mixedConstructEvents))
            "projectionEvents", box (jsonNode options (layoutEventDetails mixedProjectionResult.LayoutEvents))
            "emptyProjectionEvents", box (jsonNode options (layoutEventDetails mixedEmptyProjectionResult.LayoutEvents)) ]
        "dynamicBranchJoins", box (dynamicBranchReports.ToArray())
        "independentDynamicConcatCapacity", jsonObject [
            "requiredStackCapacityBytes", box requiredStackBytes
            "oneByteBelowAvailableBytes", box oneByteBelowCapacity
            "failureCode", box exactCapacityFailureCode
            "failureRequiredBytes", box exactCapacityFailureRequired
            "failureAvailableBytes", box exactCapacityFailureAvailable
            "failureCallerBufferUnchanged", box (exactCapacityFailureOutput = exactCapacityFailureBefore)
            "exactBoundarySucceeded", box exactCapacitySuccessPassed
            "exactBoundaryOutput", box (bytesHex exactCapacityOutput)
            "failureMetrics", if isNull exactCapacityFailureMetrics then null else jsonNode options (metricSummary exactCapacityFailureMetrics)
            "successMetrics", jsonNode options (metricSummary exactCapacityResultMetrics) ]
        "directDupDrop", jsonObject [
            "values", box (jsonNode options (codeUnitSafeValuesData duplicateResult.Values))
            "bytes", box (bytesHex duplicateOutput)
            "copyEvents", box (jsonNode options (layoutEventDetails duplicateCopyEvents))
            "dropEvents", box (jsonNode options (layoutEventDetails duplicateDropEvents))
            "metrics", box (jsonNode options (metricSummary (box duplicateResult.Metrics))) ]
        "scopeShadowCases", box (scopeReports.ToArray())
        "zeroOutputUserCall", jsonObject [
            "values", box (codeUnitSafeValuesJson zeroResult.Values)
            "retainedBytesWritten", box zeroResult.RetainedBytesWritten
            "metrics", jsonNode options (metricSummary zeroMetrics) ]
        "failureAfterConcat", jsonObject [
            "interpreterDiagnostic", box interpreterFailureCode
            "owningDiagnostic", box nativeFailure
            "metrics", if isNull nativeFailureMetrics then null else jsonNode options (metricSummary nativeFailureMetrics) ]
        "retainedCapacityFailures", box (retainedFailureReports.ToArray())
        "programStackCapacityFailure", stackFailureSummary ]

let private runLayoutDepthCases
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string) =
    let boundary = fixture.GetProperty("stringWorkload").GetProperty("layoutDepthBoundary")
    let acceptedLayoutLevels = boundary.GetProperty("maximumAcceptedLayoutLevels").GetInt32()
    let acceptedRecordLevels = boundary.GetProperty("acceptedAcyclicRecordLevels").GetInt32()
    let acceptedTopType = boundary.GetProperty("acceptedTopType").GetString()
    let rejectedLayoutLevels = boundary.GetProperty("rejectedLayoutLevels").GetInt32()
    let rejectedRecordLevels = boundary.GetProperty("rejectedAcyclicRecordLevels").GetInt32()
    let rejectedTopType = boundary.GetProperty("rejectedTopType").GetString()
    let expectedDiagnostic = boundary.GetProperty("diagnosticCode").GetString()
    let supported = compileLayoutDepthCase acceptedRecordLevels
    let tooDeep = compileLayoutDepthCase rejectedRecordLevels
    let fixtureGeometryPassed =
        acceptedLayoutLevels = supported.LayoutLevels
        && rejectedLayoutLevels = tooDeep.LayoutLevels
        && supported.TopTypeName = acceptedTopType
        && tooDeep.TopTypeName = rejectedTopType
        && boundary.GetProperty("rootLevelIs").GetInt32() = 1
    let toolchain = LlvmToolchain.discover ()
    use supportedProgram = OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "layout-depth", optimizationName, $"layout-{acceptedLayoutLevels}")) supported.Body
    let inputValue = layoutDepthValue supported
    let output = Array.create 8 0xA5uy
    let result = supportedProgram.ExecuteInto([ inputValue ], 4096, output)
    let expected = bytesFromInt64s [ 42L ]
    let layoutCount = result.Layouts |> List.length
    let supportedPassed = fixtureGeometryPassed && result.Values = [ inputValue ] && output = expected && result.RetainedOutputBytes = expected
    recordCheck checks failures $"owning-stack/{optimizationName}/layout-depth/{acceptedLayoutLevels}-layout-levels-accepted" supportedPassed (jsonObject [
        "rootLevelIsOne", box (boundary.GetProperty("rootLevelIs").GetInt32() = 1)
        "recordLevels", box supported.RecordLevels
        "layoutLevelsIncludingScalarLeaf", box supported.LayoutLevels
        "topType", box supported.TopTypeName
        "typeLayoutCount", box layoutCount
        "fixtureGeometryPassed", box fixtureGeometryPassed
        "outputValueCount", box result.Values.Length
        "outputMatchesDeepInput", box (result.Values = [ inputValue ])
        "expectedRawBytes", box (bytesHex expected)
        "actualRawBytes", box (bytesHex output)
        "metrics", jsonNode options (metricSummary (box result.Metrics)) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/layout-depth/{acceptedLayoutLevels}-level-trace-complete" result.Metrics result.LayoutEvents |> ignore
    let rejectedCode, rejectedException =
        try
            use _unexpected = OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "layout-depth", optimizationName, $"layout-{rejectedLayoutLevels}")) tooDeep.Body
            "unexpected-success", null
        with error -> diagnosticCode error, error
    let rejectedPassed = fixtureGeometryPassed && rejectedCode = expectedDiagnostic
    recordCheck checks failures $"owning-stack/{optimizationName}/layout-depth/{rejectedLayoutLevels}-layout-levels-rejected-before-native-entry" rejectedPassed (jsonObject [
        "recordLevels", box tooDeep.RecordLevels
        "layoutLevelsIncludingScalarLeaf", box tooDeep.LayoutLevels
        "topType", box tooDeep.TopTypeName
        "diagnosticCode", box rejectedCode
        "expectedDiagnosticCode", box expectedDiagnostic
        "fixtureGeometryPassed", box fixtureGeometryPassed
        "diagnostic", if isNull rejectedException then null else box (rejectedException.ToString()) ])
    jsonObject [
        "optimization", box optimizationName
        "acceptedLayoutLevels", box acceptedLayoutLevels
        "acceptedRecordLevels", box acceptedRecordLevels
        "acceptedTypeLayoutCount", box layoutCount
        "acceptedMetrics", jsonNode options (metricSummary (box result.Metrics))
        "rejectedLayoutLevels", box rejectedLayoutLevels
        "rejectedRecordLevels", box rejectedRecordLevels
        "rejectedDiagnosticCode", box rejectedCode ]

let private runEnumConformance
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (fixture: JsonElement)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string)
    (entries: EnumEntryBodies) =
    let oracle = fixture.GetProperty("enumConformance")
    let contextAbiOracle = fixture.GetProperty("storageRuntimeTestOracle").GetProperty("abi")
    let enumType = oracle.GetProperty("enumType").GetString()
    let fixedRecordType = oracle.GetProperty("fixedRecordType").GetString()
    let dynamicRecordType = oracle.GetProperty("dynamicRecordType").GetString()
    let caseElements = oracle.GetProperty("cases").EnumerateArray() |> Seq.toArray
    let caseNames = caseElements |> Array.map (fun item -> item.GetProperty("name").GetString()) |> Array.toList
    recordCheck checks failures $"enum/{optimizationName}/verified-case-order" (entries.Cases = caseNames) (jsonObject [
        "fixtureCaseOrder", box caseNames
        "verifiedCaseOrder", box entries.Cases ])
    let layoutChecked = ref false
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    let toolchain = LlvmToolchain.discover ()
    let compile (name: string) (body: VerifiedIrBody) =
        OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "enums", optimizationName, name)) body
    let valueForCase caseName = EnumValue(enumType, caseName)
    let sequence = oracle.GetProperty("fixedRecord").GetProperty("sequence").GetInt64()
    let textOracle = oracle.GetProperty("dynamicRecord")
    let text = textOracle.GetProperty("note").GetString()
    let dynamicSequence = textOracle.GetProperty("sequence").GetInt64()
    let fixedRecord caseValue =
        RecordValue(fixedRecordType, Map.ofList [ "signal", caseValue; "sequence", IntValue sequence ])
    let dynamicRecord caseValue =
        RecordValue(dynamicRecordType, Map.ofList [ "note", StringValue text; "signal", caseValue; "sequence", IntValue dynamicSequence ])
    let interpreterBody (body: VerifiedIrBody) =
        IrInterpreter.executeBody interpreterHost (VerifiedIrBody.inspect body).BodyName body
    let interpreterWithRoot (body: VerifiedIrBody) (root: IrInterpreterResult option) (arguments: IrEntryArgument list) =
        use result = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect body).BodyName body root arguments
        result.Decode()
    let outputBuffer bytes = Array.create bytes 0xA5uy
    let getCaseElement caseName =
        caseElements
        |> Array.find (fun item -> String.Equals(item.GetProperty("name").GetString(), caseName, StringComparison.Ordinal))
    for caseIndex, caseName in caseNames |> List.indexed do
        let caseOracle = getCaseElement caseName
        let ordinal = caseOracle.GetProperty("ordinal").GetInt64()
        let expectedValue = valueForCase caseName
        let expectedBytes = bytesFromHex (caseOracle.GetProperty("retainedBytesHex").GetString())
        let ordinalBytesMatch = bytesFromInt64s [ ordinal ] = expectedBytes
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/fixture-ordinal-bytes" ordinalBytesMatch (jsonObject [
            "caseOrdinal", box ordinal
            "expectedLiteralBytes", box (bytesHex expectedBytes)
            "bytesFromPinnedOrdinal", box (bytesHex (bytesFromInt64s [ ordinal ])) ])

        use constructorProgram = compile ("construct-" + caseName) entries.Constructors[caseName]
        let constructorInterpreter = interpreterBody entries.Constructors[caseName]
        let constructorOutput = outputBuffer expectedBytes.Length
        let constructorResult = constructorProgram.ExecuteInto([], 128, constructorOutput)
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/constructor-interpreter-owning-and-retained-bytes"
            (constructorInterpreter = [ expectedValue ]
             && constructorResult.Values = constructorInterpreter
             && constructorOutput = expectedBytes
             && constructorResult.RetainedOutputBytes = expectedBytes
             && constructorResult.RetainedBytesWritten = expectedBytes.Length)
            (jsonObject [
                "interpreterValues", box (ValueInspection.toJson entries.Program constructorInterpreter)
                "owningValues", box (ValueInspection.toJson entries.Program constructorResult.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex constructorOutput) ])

        use matchProgram = compile ("match-" + caseName) entries.Matches[caseName]
        let matchInterpreter = interpreterBody entries.Matches[caseName]
        let matchValue = caseOracle.GetProperty("matchResult").GetInt64()
        let matchExpected = [ IntValue matchValue ]
        let matchBytes = bytesFromHex (caseOracle.GetProperty("matchRetainedBytesHex").GetString())
        let matchBytesMatchOracle = bytesFromInt64s [ matchValue ] = matchBytes
        let matchOutput = outputBuffer matchBytes.Length
        let matchResult = matchProgram.ExecuteInto([], 128, matchOutput)
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/exhaustive-match-interpreter-owning"
            (matchBytesMatchOracle
             && matchInterpreter = matchExpected
             && matchResult.Values = matchInterpreter
             && matchOutput = matchBytes)
            (jsonObject [
                "expectedMatchValue", box matchValue
                "interpreterValues", box (ValueInspection.toJson entries.Program matchInterpreter)
                "owningValues", box (ValueInspection.toJson entries.Program matchResult.Values)
                "fixtureRetainedBytesMatchLiteralMatchValue", box matchBytesMatchOracle
                "expectedRetainedBytes", box (bytesHex matchBytes)
                "actualRetainedBytes", box (bytesHex matchOutput) ])

        use localCallProgram = compile ("local-call-" + caseName) entries.LocalCallRoundTrip
        use enumRoot = IrInterpreter.executeBodyWithInputs interpreterHost ("enum-root-" + caseName) entries.Constructors[caseName] None []
        let localCallInterpreter = interpreterWithRoot entries.LocalCallRoundTrip (Some enumRoot) [ IrEntryArgument.RetainedRoot 0 ]
        let localCallOutput = outputBuffer expectedBytes.Length
        let localCallResult = localCallProgram.ExecuteInto([ expectedValue ], 128, localCallOutput)
        let localCallMetrics = box localCallResult.Metrics
        let localCallNoMoves = int64Property localCallMetrics "MoveBytes" = 0L
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/local-call-round-trip-stays-in-place"
            (localCallInterpreter = [ expectedValue ]
             && localCallResult.Values = localCallInterpreter
             && localCallOutput = expectedBytes
             && localCallNoMoves)
            (jsonObject [
                "interpreterValues", box (ValueInspection.toJson entries.Program localCallInterpreter)
                "owningValues", box (ValueInspection.toJson entries.Program localCallResult.Values)
                "retainedBytes", box (bytesHex localCallOutput)
                "moveBytes", box (int64Property localCallMetrics "MoveBytes")
                "descriptorTransferCount", box localCallResult.Metrics.DescriptorTransferCount ])

        use fixedProgram = compile ("fixed-record-" + caseName) entries.MakeFixedRecord
        let fixedInputArguments = [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument sequence ]
        use fixedInterpreterResult =
            IrInterpreter.executeBodyWithInputs interpreterHost ("enum-fixed-record-" + caseName) entries.MakeFixedRecord (Some enumRoot) fixedInputArguments
        let fixedInterpreterValues = fixedInterpreterResult.Decode()
        let fixedExpectedValue = fixedRecord expectedValue
        let fixedExpectedBytes = bytesFromHex (oracle.GetProperty("fixedRecord").GetProperty("retainedBytesByCase").GetProperty(caseName).GetString())
        let fixedOutput = outputBuffer fixedExpectedBytes.Length
        let fixedResult = fixedProgram.ExecuteInto([ expectedValue; IntValue sequence ], 256, fixedOutput)
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/fixed-record-layout-and-construction"
            (fixedInterpreterValues = [ fixedExpectedValue ]
             && fixedResult.Values = fixedInterpreterValues
             && fixedOutput = fixedExpectedBytes
            && fixedResult.RetainedBytesWritten = fixedExpectedBytes.Length)
            (jsonObject [
                "expectedFieldOffsets", box (oracle.GetProperty("fixedRecord").GetProperty("fieldOffsetsBytes").ToString())
                "expectedValue", box (ValueInspection.toJson entries.Program [ fixedExpectedValue ])
                "interpreterValues", box (ValueInspection.toJson entries.Program fixedInterpreterValues)
                "owningValues", box (ValueInspection.toJson entries.Program fixedResult.Values)
                "expectedRetainedBytes", box (bytesHex fixedExpectedBytes)
                "actualRetainedBytes", box (bytesHex fixedOutput) ])

        use fixedProjectionProgram = compile ("fixed-projection-" + caseName) entries.ProjectFixedRecord
        let fixedProjectionInterpreter = interpreterWithRoot entries.ProjectFixedRecord (Some fixedInterpreterResult) [ IrEntryArgument.RetainedRoot 0 ]
        let fixedProjectionOutput = outputBuffer expectedBytes.Length
        let fixedProjectionResult = fixedProjectionProgram.ExecuteInto([ fixedExpectedValue ], 256, fixedProjectionOutput)
        let fixedProjectionHasFieldEvent = fixedProjectionResult.LayoutEvents |> List.exists (fun event -> event.Kind = "field-extract")
        let fixedProjectionNoMoves = fixedProjectionResult.Metrics.MoveBytes = 0UL
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/fixed-record-enum-projection-no-payload-move"
            (fixedProjectionInterpreter = [ expectedValue ]
             && fixedProjectionResult.Values = fixedProjectionInterpreter
             && fixedProjectionOutput = expectedBytes
             && fixedProjectionHasFieldEvent
             && fixedProjectionNoMoves)
            (jsonObject [
                "interpreterValues", box (ValueInspection.toJson entries.Program fixedProjectionInterpreter)
                "owningValues", box (ValueInspection.toJson entries.Program fixedProjectionResult.Values)
                "retainedBytes", box (bytesHex fixedProjectionOutput)
                "fieldExtractEventFound", box fixedProjectionHasFieldEvent
                "moveBytes", box fixedProjectionResult.Metrics.MoveBytes
                "events", box (layoutEventDetails fixedProjectionResult.LayoutEvents) ])

        use dynamicProgram = compile ("dynamic-record-" + caseName) entries.MakeDynamicRecords[caseName]
        use dynamicInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost ("enum-dynamic-record-" + caseName) entries.MakeDynamicRecords[caseName] None []
        let dynamicInterpreterValues = dynamicInterpreterResult.Decode()
        let dynamicExpectedValue = dynamicRecord expectedValue
        let dynamicExpectedBytes = bytesFromHex (textOracle.GetProperty("retainedBytesByCase").GetProperty(caseName).GetString())
        let dynamicRawInputBytes = bytesFromHex (textOracle.GetProperty("rawInputBytesByCase").GetProperty(caseName).GetString())
        let dynamicOutput = outputBuffer dynamicExpectedBytes.Length
        let dynamicResult = dynamicProgram.ExecuteInto([], 256, dynamicOutput)
        let dynamicFixtureGeometry =
            let stringBytes = stringBytesFromCodeUnitsHex (codeUnitsHexFromString text)
            let offsets = textOracle.GetProperty("fieldOffsetsBytesForNoteX")
            stringBytes.Length = offsets.GetProperty("signal").GetInt32()
            && offsets.GetProperty("note").GetInt32() = 0
            && offsets.GetProperty("sequence").GetInt32() = offsets.GetProperty("signal").GetInt32() + 8
            && bytesFromInt64s [ ordinal ] = Array.sub dynamicExpectedBytes (offsets.GetProperty("signal").GetInt32()) 8
            && dynamicRawInputBytes = dynamicExpectedBytes
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/dynamic-record-enum-offset-and-construction"
            (dynamicFixtureGeometry
             && dynamicInterpreterValues = [ dynamicExpectedValue ]
             && dynamicResult.Values = dynamicInterpreterValues
             && dynamicOutput = dynamicExpectedBytes
             && dynamicResult.RetainedBytesWritten = dynamicExpectedBytes.Length)
            (jsonObject [
                "expectedFieldOffsets", box (textOracle.GetProperty("fieldOffsetsBytesForNoteX").ToString())
                "expectedValue", box (ValueInspection.toJson entries.Program [ dynamicExpectedValue ])
                "interpreterValues", box (ValueInspection.toJson entries.Program dynamicInterpreterValues)
                "owningValues", box (ValueInspection.toJson entries.Program dynamicResult.Values)
                "fixtureGeometryMatchesLiteralOffsets", box dynamicFixtureGeometry
                "expectedRetainedBytes", box (bytesHex dynamicExpectedBytes)
                "actualRetainedBytes", box (bytesHex dynamicOutput) ])

        use dynamicProjectionProgram = compile ("dynamic-projection-" + caseName) entries.ProjectDynamicRecord
        let dynamicProjectionInterpreter = interpreterWithRoot entries.ProjectDynamicRecord (Some dynamicInterpreterResult) [ IrEntryArgument.RetainedRoot 0 ]
        let dynamicProjectionOutput = outputBuffer expectedBytes.Length
        let dynamicProjectionResult = dynamicProjectionProgram.ExecuteInto([ dynamicExpectedValue ], 256, dynamicProjectionOutput)
        let dynamicProjectionHasFieldEvent = dynamicProjectionResult.LayoutEvents |> List.exists (fun event -> event.Kind = "field-extract")
        let dynamicProjectionNoMoves = dynamicProjectionResult.Metrics.MoveBytes = 0UL
        recordCheck checks failures $"enum/{optimizationName}/{caseName}/dynamic-record-enum-projection-no-payload-move"
            (dynamicProjectionInterpreter = [ expectedValue ]
             && dynamicProjectionResult.Values = dynamicProjectionInterpreter
             && dynamicProjectionOutput = expectedBytes
             && dynamicProjectionHasFieldEvent
             && dynamicProjectionNoMoves)
            (jsonObject [
                "interpreterValues", box (ValueInspection.toJson entries.Program dynamicProjectionInterpreter)
                "owningValues", box (ValueInspection.toJson entries.Program dynamicProjectionResult.Values)
                "retainedBytes", box (bytesHex dynamicProjectionOutput)
                "fieldExtractEventFound", box dynamicProjectionHasFieldEvent
                "moveBytes", box dynamicProjectionResult.Metrics.MoveBytes
                "events", box (layoutEventDetails dynamicProjectionResult.LayoutEvents) ])

        if caseIndex = 0 && not !layoutChecked then
            let layouts =
                constructorProgram.Layouts @ fixedProgram.Layouts @ dynamicProgram.Layouts
                |> List.distinctBy (fun layout -> layout.TypeName)
            let layoutChecks, layoutFailures, layoutSummary = validateEnumTypeLayouts fixture layouts
            recordCheck checks failures $"enum/{optimizationName}/independent-layout-oracle" (layoutFailures.Length = 0) (jsonObject [
                "checks", box layoutChecks
                "failures", box layoutFailures
                "layouts", box layoutSummary ])
            layoutChecked := true

        if caseIndex = 0 then
            let sentinel = bytesFromHex (oracle.GetProperty("rawNativeEntry").GetProperty("sentinelBytesHex").GetString())
            let checkHostRejected label invalidValue =
                let output = Array.copy sentinel
                let before = Array.copy output
                let mutable rejected = false
                let mutable errorCode = ""
                try localCallProgram.ExecuteInto([ invalidValue ], 128, output) |> ignore
                with error ->
                    rejected <- true
                    errorCode <- diagnosticCode error
                recordCheck checks failures $"enum/{optimizationName}/host-rejects-{label}-without-publishing"
                    (rejected && output = before)
                    (jsonObject [
                        "rejected", box rejected
                        "diagnosticCode", box errorCode
                        "retainedBufferUnchanged", box (output = before)
                        "retainedBytes", box (bytesHex output) ])
            checkHostRejected "mismatched-nominal-type" (EnumValue("OtherSignal", "off"))
            checkHostRejected "unknown-case" (EnumValue(enumType, "unknown"))

    for equalityCase in oracle.GetProperty("equalityCases").EnumerateArray() do
        let left = equalityCase.GetProperty("left").GetString()
        let right = equalityCase.GetProperty("right").GetString()
        let expected = equalityCase.GetProperty("expected").GetBoolean()
        let expectedValues = [ BoolValue expected ]
        let expectedBytes = bytesFromHex (equalityCase.GetProperty("retainedBytesHex").GetString())
        let equalityBytesMatchOracle = bytesFromInt64s [ if expected then 1L else 0L ] = expectedBytes
        let body = entries.Equalities[(left, right)]
        use equalityProgram = compile ($"equals-{left}-{right}") body
        let interpreted = interpreterBody body
        let output = outputBuffer expectedBytes.Length
        let native = equalityProgram.ExecuteInto([], 128, output)
        recordCheck checks failures $"enum/{optimizationName}/equals-{left}-{right}-interpreter-owning"
            (equalityBytesMatchOracle && interpreted = expectedValues && native.Values = interpreted && output = expectedBytes)
            (jsonObject [
                "expected", box expected
                "fixtureRetainedBytesMatchLiteralBoolean", box equalityBytesMatchOracle
                "interpreterValues", box (ValueInspection.toJson entries.Program interpreted)
                "owningValues", box (ValueInspection.toJson entries.Program native.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex output) ])

    use rawEnumProgram = compile "raw-unused-enum-input" entries.IgnoreEnum
    use rawFixedProgram = compile "raw-unused-fixed-record-input" entries.IgnoreFixedRecord
    use rawDynamicProgram = compile "raw-unused-dynamic-record-input" entries.IgnoreDynamicRecord
    let rawOracle = oracle.GetProperty("rawNativeEntry")
    let contextAbiPassed, contextAbiDetails = inspectRawOwningContextAbi contextAbiOracle
    recordCheck checks failures $"enum/{optimizationName}/raw-context-mirror-matches-independent-abi-oracle"
        contextAbiPassed contextAbiDetails
    if not contextAbiPassed then invalidOp "Raw native-entry checks cannot run because the context mirror differs from the independent ABI oracle."
    let expectedInvalidStatus = uint32 (rawOracle.GetProperty("expectedInvalidRequestStatus").GetInt32())
    let sentinel = bytesFromHex (rawOracle.GetProperty("sentinelBytesHex").GetString())
    let rawProgramForBody = function
        | "enum" -> rawEnumProgram
        | "fixedRecord" -> rawFixedProgram
        | "dynamicRecord" -> rawDynamicProgram
        | other -> invalidOp $"Unknown raw enum conformance body '{other}'."
    let dynamicOffBytes = bytesFromHex (textOracle.GetProperty("rawInputBytesByCase").GetProperty("off").GetString())
    let dynamicEnumOffset = textOracle.GetProperty("fieldOffsetsBytesForNoteX").GetProperty("signal").GetInt32()
    let rawBytes kind ordinal =
        match kind with
        | "enum" -> bytesFromInt64s [ ordinal ]
        | "fixedRecord" -> bytesFromInt64s [ ordinal; sequence ]
        | "dynamicRecord" ->
            let bytes = Array.copy dynamicOffBytes
            Array.Copy(BitConverter.GetBytes(ordinal), 0, bytes, dynamicEnumOffset, 8)
            bytes
        | other -> invalidOp $"Unknown raw enum input shape '{other}'."
    let checkRawRejected name program bytes extents count =
        let result = invokeRawOwningEntry program contextAbiOracle bytes extents count sentinel
        let unchanged = result.RetainedOutput = sentinel
        let passed = result.NativeStatus = int32 expectedInvalidStatus && result.ContextStatus = expectedInvalidStatus && unchanged
        recordCheck checks failures $"enum/{optimizationName}/raw-native-entry/{name}" passed (jsonObject [
            "expectedInvalidRequestStatus", box expectedInvalidStatus
            "nativeStatusExpected", box (int32 expectedInvalidStatus)
            "nativeStatus", box result.NativeStatus
            "contextStatus", box result.ContextStatus
            "inputBytes", box (bytesHex bytes)
            "inputExtentsBytes", box extents
            "inputCount", box count
            "retainedOutputUnchanged", box unchanged
            "retainedOutputBeforeAndAfterHex", box (bytesHex result.RetainedOutput) ])
    for invalidOrdinal in rawOracle.GetProperty("invalidOrdinals").EnumerateArray() do
        let scenarioName = invalidOrdinal.GetProperty("name").GetString()
        let ordinal = invalidOrdinal.GetProperty("value").GetInt64()
        for inputKind, bodyName in [ "enum", "enum"; "fixedRecord", "fixed-record"; "dynamicRecord", "dynamic-record" ] do
            let bytes = rawBytes inputKind ordinal
            checkRawRejected ($"{scenarioName}-{bodyName}-unused-input") (rawProgramForBody inputKind) bytes [| uint32 bytes.Length |] 1u
    for malformed in rawOracle.GetProperty("malformedInputs").EnumerateArray() do
        let name = malformed.GetProperty("name").GetString()
        let bodyName = malformed.GetProperty("body").GetString()
        let inputBytes = bytesFromHex (malformed.GetProperty("inputBytesHex").GetString())
        let inputExtents = malformed.GetProperty("inputExtentsBytes").EnumerateArray() |> Seq.map (fun item -> uint32 (item.GetInt32())) |> Seq.toArray
        let inputCount = uint32 (malformed.GetProperty("inputCount").GetInt32())
        checkRawRejected ("malformed-" + name) (rawProgramForBody bodyName) inputBytes inputExtents inputCount

    jsonObject [
        "optimization", box optimizationName
        "caseCount", box caseNames.Length
        "equalityCaseCount", box (oracle.GetProperty("equalityCases").GetArrayLength())
        "invalidOrdinalCount", box (rawOracle.GetProperty("invalidOrdinals").GetArrayLength())
        "malformedInputCount", box (rawOracle.GetProperty("malformedInputs").GetArrayLength()) ]

let private runSumConformance
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (fixture: JsonElement)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string)
    (entries: SumEntryBodies) =
    let oracle = fixture.GetProperty("sumConformance")
    let contextAbiOracle = fixture.GetProperty("storageRuntimeTestOracle").GetProperty("abi")
    let caseElements = oracle.GetProperty("cases").EnumerateArray() |> Seq.toArray
    let caseValues =
        caseElements
        |> Array.map (fun item -> item.GetProperty("name").GetString(), parseSumFixtureValue (item.GetProperty("expected")))
        |> Map.ofArray
    let caseByName name =
        caseElements
        |> Array.find (fun item -> String.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal))
    let caseNames = caseElements |> Array.map (fun item -> item.GetProperty("name").GetString())
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    let toolchain = LlvmToolchain.discover ()
    let observedLayouts = ResizeArray<OwningStackTypeLayout>()
    let compile (name: string) (body: VerifiedIrBody) =
        let program = OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "sums", optimizationName, name)) body
        observedLayouts.AddRange program.Layouts
        program
    let interpreterBody (body: VerifiedIrBody) =
        use result = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect body).BodyName body None []
        result.Decode()
    let executeInterpreterWithCaseRoot (body: VerifiedIrBody) (inputCaseName: string) =
        use root = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect entries.Cases[inputCaseName]).BodyName entries.Cases[inputCaseName] None []
        use result = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect body).BodyName body (Some root) [ IrEntryArgument.RetainedRoot 0 ]
        result.Decode()
    let outputBuffer bytes = Array.create bytes 0xA5uy
    let sumConstructionCount =
        let rec count value =
            match value with
            | OptionValue(_, None) -> 1
            | OptionValue(_, Some nested) -> 1 + count nested
            | ResultValue(_, _, Ok nested)
            | ResultValue(_, _, Error nested) -> 1 + count nested
            | RecordValue(_, fields) -> fields |> Map.toList |> List.sumBy (snd >> count)
            | _ -> 0
        count
    let noPayloadRelocation (result: OwningStackExecutionResult) =
        result.Metrics.MoveBytes = 0UL
        && (result.LayoutEvents |> List.forall (fun event -> event.Kind <> "local-compact" && event.Kind <> "call-input-move" && event.Kind <> "call-return-move"))
    let expectedLayoutAbi = oracle.GetProperty("layoutAbiVersion").GetInt32()
    let nativeLayoutAbi = contextAbiOracle.GetProperty("layoutAbiVersion").GetInt32()
    recordCheck checks failures $"sum/{optimizationName}/owning-layout-abi-v3"
        (expectedLayoutAbi = 3 && nativeLayoutAbi = expectedLayoutAbi)
        (jsonObject [ "fixtureLayoutAbiVersion", box expectedLayoutAbi; "nativeLayoutAbiVersion", box nativeLayoutAbi ])
    let mutable schemaChecked = false
    for caseName in caseNames do
        let caseOracle = caseByName caseName
        let expectedValue = caseValues[caseName]
        let expectedBytes = bytesFromHex (caseOracle.GetProperty("retainedBytesHex").GetString())
        let constructorBody = entries.Cases[caseName]
        use constructorProgram = compile ("construct-" + caseName) constructorBody
        let interpretedConstructor = interpreterBody constructorBody
        let constructorOutput = outputBuffer expectedBytes.Length
        let constructorResult = constructorProgram.ExecuteInto([], 4096, constructorOutput)
        let constructorNoMoves = noPayloadRelocation constructorResult
        let recordBuildEvents = constructorResult.LayoutEvents |> List.filter (fun event -> event.Kind = "record-build")
        let sumLayoutEvents =
            recordBuildEvents
            |> List.filter (fun event ->
                match event.SourceExtentBytes with
                | Some sourceExtent -> event.ExtentBytes = sourceExtent + 8
                | None -> event.ExtentBytes = 8 && event.PayloadBytes = 8)
        let expectedSumNodes = sumConstructionCount expectedValue
        let sumCopiesPairedExactlyOnce =
            sumLayoutEvents
            |> List.forall (fun sumEvent ->
                match sumEvent.SourceOffsetBytes, sumEvent.SourceExtentBytes with
                | Some sourceOffset, Some sourceExtent ->
                    recordBuildEvents
                    |> List.filter (fun copyEvent ->
                        copyEvent.SourceOffsetBytes = Some sourceOffset
                        && copyEvent.SourceExtentBytes = Some sourceExtent
                        && copyEvent.ExtentBytes = sourceExtent)
                    |> List.length = 1
                | None, None -> sumEvent.ExtentBytes = 8 && sumEvent.PayloadBytes = 8
                | _ -> false)
        let sumConstructionEventsExactlyMatchValue = sumLayoutEvents.Length = expectedSumNodes
        let constructionPassed =
            interpretedConstructor = [ expectedValue ]
            && constructorResult.Values = interpretedConstructor
            && constructorOutput = expectedBytes
            && constructorResult.RetainedOutputBytes = expectedBytes
            && constructorResult.RetainedBytesWritten = expectedBytes.Length
            && constructorNoMoves
            && sumConstructionEventsExactlyMatchValue
            && sumCopiesPairedExactlyOnce
        recordCheck checks failures $"sum/{optimizationName}/{caseName}/same-verified-body-literal-bytes-and-single-inline-copies"
            constructionPassed (jsonObject [
                "expected", box (codeUnitSafeValuesData [ expectedValue ])
                "interpreterValues", box (codeUnitSafeValuesData interpretedConstructor)
                "owningValues", box (codeUnitSafeValuesData constructorResult.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex constructorOutput)
                "sumConstructionCountExpected", box expectedSumNodes
                "sumConstructionEventCount", box sumLayoutEvents.Length
                "sumActivePayloadCopyPairedExactlyOnce", box sumCopiesPairedExactlyOnce
                "moveBytes", box constructorResult.Metrics.MoveBytes
                "layoutEvents", box (layoutEventDetails constructorResult.LayoutEvents) ])
        if not schemaChecked then
            let actualSchema = constructorResult.LayoutSchemaVersion
            let expectedSchema = oracle.GetProperty("layoutSchemaVersion").GetInt32()
            recordCheck checks failures $"sum/{optimizationName}/independent-layout-oracle-and-schema-v3"
                (expectedSchema = 3 && actualSchema = expectedSchema)
                (jsonObject [
                    "expectedSchemaVersion", box expectedSchema
                    "actualSchemaVersion", box actualSchema ])
            schemaChecked <- true

        let identityName = caseOracle.GetProperty("identityBody").GetString()
        let identityBody = entries.IdentityBodies[identityName]
        let localCallBody = entries.LocalCallBodies[identityName]
        use identityProgram = compile ("host-round-trip-" + caseName) identityBody
        use localCallProgram = compile ("local-call-round-trip-" + caseName) localCallBody
        use interpreterRoot = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect constructorBody).BodyName constructorBody None []
        use interpretedIdentity = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect identityBody).BodyName identityBody (Some interpreterRoot) [ IrEntryArgument.RetainedRoot 0 ]
        let identityInterpreterValues = interpretedIdentity.Decode()
        let identityOutput = outputBuffer expectedBytes.Length
        let identityResult = identityProgram.ExecuteInto([ expectedValue ], 4096, identityOutput)
        let identityInputMetrics = identityResult.Metrics
        let identityPassed =
            identityInterpreterValues = [ expectedValue ]
            && identityResult.Values = identityInterpreterValues
            && identityOutput = expectedBytes
            && identityResult.RetainedOutputBytes = expectedBytes
            && identityResult.RetainedBytesWritten = expectedBytes.Length
            && int64 identityInputMetrics.InputBytes = int64 expectedBytes.Length
            && int64 identityInputMetrics.InputCopyBytes = int64 expectedBytes.Length
            && int64 identityInputMetrics.RetainedCopyBytes = int64 expectedBytes.Length
            && identityInputMetrics.DeepCopyBytes = 0UL
            && noPayloadRelocation identityResult
        recordCheck checks failures $"sum/{optimizationName}/{caseName}/host-input-decode-and-user-call-return"
            identityPassed (jsonObject [
                "interpreterValues", box (codeUnitSafeValuesData identityInterpreterValues)
                "owningValues", box (codeUnitSafeValuesData identityResult.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex identityOutput)
                "inputBytes", box identityInputMetrics.InputBytes
                "inputCopyBytes", box identityInputMetrics.InputCopyBytes
                "deepCopyBytes", box identityInputMetrics.DeepCopyBytes
                "moveBytes", box identityInputMetrics.MoveBytes
                "retainedCopyBytes", box identityInputMetrics.RetainedCopyBytes ])
        use interpretedLocalCall = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect localCallBody).BodyName localCallBody (Some interpreterRoot) [ IrEntryArgument.RetainedRoot 0 ]
        let localCallInterpreterValues = interpretedLocalCall.Decode()
        let localCallOutput = outputBuffer expectedBytes.Length
        let localCallResult = localCallProgram.ExecuteInto([ expectedValue ], 4096, localCallOutput)
        let localCallPassed =
            localCallInterpreterValues = [ expectedValue ]
            && localCallResult.Values = localCallInterpreterValues
            && localCallOutput = expectedBytes
            && localCallResult.RetainedOutputBytes = expectedBytes
            && localCallResult.RetainedBytesWritten = expectedBytes.Length
            && localCallResult.Metrics.DeepCopyBytes = 0UL
            && noPayloadRelocation localCallResult
        recordCheck checks failures $"sum/{optimizationName}/{caseName}/local-store-load-call-return-descriptor-transfer"
            localCallPassed (jsonObject [
                "interpreterValues", box (codeUnitSafeValuesData localCallInterpreterValues)
                "owningValues", box (codeUnitSafeValuesData localCallResult.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex localCallOutput)
                "deepCopyBytes", box localCallResult.Metrics.DeepCopyBytes
                "moveBytes", box localCallResult.Metrics.MoveBytes
                "layoutEvents", box (layoutEventDetails localCallResult.LayoutEvents) ])

        let shortOutput = outputBuffer expectedBytes.Length
        let shortOutputBefore = Array.copy shortOutput
        let shortCapacityError =
            try
                identityProgram.ExecuteInto([ expectedValue ], 4096, shortOutput, expectedBytes.Length - 1) |> ignore
                None
            with error -> Some error
        match shortCapacityError with
        | Some error ->
            let errorMetrics = getProperty error "Metrics"
            let requiredBytes = int64Property error "RequiredBytes"
            let availableBytes = int64Property error "AvailableBytes"
            let finalCursorBytes = int64Property errorMetrics "FinalCursorBytes"
            let shortPassed =
                (getProperty error "Code" |> string) = "OWNING_RETAINED_CAPACITY"
                && (getProperty error "Boundary" |> string) = "retained-output"
                && requiredBytes = int64 expectedBytes.Length
                && availableBytes = int64 (expectedBytes.Length - 1)
                && finalCursorBytes = 0L
                && shortOutput = shortOutputBefore
            recordCheck checks failures $"sum/{optimizationName}/{caseName}/retained-output-one-byte-short-is-atomic" shortPassed (jsonObject [
                "exception", box (resourceExceptionDetails error)
                "requiredBytes", box requiredBytes
                "availableBytes", box availableBytes
                "finalCursorBytes", box finalCursorBytes
                "retainedOutputUnchanged", box (shortOutput = shortOutputBefore) ])
        | None ->
            recordCheck checks failures $"sum/{optimizationName}/{caseName}/retained-output-one-byte-short-is-atomic" false "One byte below the exact retained-output extent unexpectedly succeeded."

    for equalityIndex, equalityCase in oracle.GetProperty("equalityCases").EnumerateArray() |> Seq.indexed do
        let leftName = equalityCase.GetProperty("left").GetString()
        let rightName = equalityCase.GetProperty("right").GetString()
        let expected = equalityCase.GetProperty("expected").GetBoolean()
        let expectedBytes = bytesFromHex (equalityCase.GetProperty("retainedBytesHex").GetString())
        let body = entries.Equalities[equalityIndex]
        use equalityProgram = compile ($"equals-{equalityIndex}-{leftName}-{rightName}") body
        let interpreted = interpreterBody body
        let output = outputBuffer expectedBytes.Length
        let native = equalityProgram.ExecuteInto([], 4096, output)
        let booleanBytes = bytesFromInt64s [ if expected then 1L else 0L ]
        recordCheck checks failures $"sum/{optimizationName}/equals-{leftName}-{rightName}-active-tag-payload"
            (expectedBytes = booleanBytes && interpreted = [ BoolValue expected ] && native.Values = interpreted && output = expectedBytes && noPayloadRelocation native)
            (jsonObject [
                "expected", box expected
                "interpreterValues", box (codeUnitSafeValuesData interpreted)
                "owningValues", box (codeUnitSafeValuesData native.Values)
                "fixtureBooleanBytesMatch", box (expectedBytes = booleanBytes)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex output)
                "deepCopyBytes", box native.Metrics.DeepCopyBytes
                "moveBytes", box native.Metrics.MoveBytes ])

    let matchBodies =
        [ "optionIntSome", entries.OptionIntMatch
          "optionIntNone", entries.OptionIntMatch
          "resultOk", entries.ResultIntStringMatch
          "resultError", entries.ResultIntStringMatch
          "resultStringError", entries.ResultStringIntMatch
          "resultErrorInt", entries.ResultStringIntErrorMatch
          "optionStringNone", entries.OptionStringMatch
          "optionStringEmbeddedNul", entries.OptionStringMatch
          "nestedError", entries.NestedMatch
          "nestedOk", entries.NestedMatch
          "nestedNone", entries.NestedMatch
          "nestedErrorInt", entries.NestedErrorIntMatch ] |> Map.ofList
    for matchOracle in oracle.GetProperty("matches").EnumerateObject() do
        let matchName = matchOracle.Name
        let details = matchOracle.Value
        let inputCaseName = details.GetProperty("inputCase").GetString()
        let expectedValue = parseSumFixtureValue (details.GetProperty("expected"))
        let expectedValues = [ expectedValue ]
        let expectedBytes = bytesFromHex (details.GetProperty("retainedBytesHex").GetString())
        let body = matchBodies[matchName]
        use matchProgram = compile ("match-" + matchName) body
        let interpreted = executeInterpreterWithCaseRoot body inputCaseName
        let output = outputBuffer expectedBytes.Length
        let native = matchProgram.ExecuteInto([ caseValues[inputCaseName] ], 4096, output)
        let expectedDeepCopyBytes = uint64 (details.GetProperty("expectedDeepCopyBytes").GetInt32())
        let mutable literalCodeUnitsElement = Unchecked.defaultof<JsonElement>
        let mutable expectedLiteralPayloadBytes = 0
        let mutable expectedLiteralExtentBytes = 0
        let mutable literalStoreCallFound = false
        let expectedLiteralCopyMatchesExtent =
            if details.TryGetProperty("expectedLiteralCodeUnitsHex", &literalCodeUnitsElement) then
                let literalCodeUnitsHex = literalCodeUnitsElement.GetString()
                let literalCodeUnitBytes = bytesFromHex literalCodeUnitsHex
                let literalBytes = stringBytesFromCodeUnitsHex literalCodeUnitsHex
                expectedLiteralPayloadBytes <- 8 + literalCodeUnitBytes.Length
                expectedLiteralExtentBytes <- literalBytes.Length
                let llvmLines = matchProgram.LlvmIr.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                let encodedLiteralBytes =
                    literalBytes
                    |> Array.map (fun value -> "\\" + value.ToString("X2", CultureInfo.InvariantCulture))
                    |> String.concat ""
                let expectedGlobalDeclaration =
                    $"private unnamed_addr constant [{expectedLiteralExtentBytes} x i8] c\"{encodedLiteralBytes}\", align 8"
                let literalGlobal =
                    llvmLines
                    |> Array.tryFind (fun line -> line.Contains(expectedGlobalDeclaration, StringComparison.Ordinal))
                literalStoreCallFound <-
                    match literalGlobal with
                    | Some globalLine ->
                        let nameEnd = globalLine.IndexOf(" = ", StringComparison.Ordinal)
                        if nameEnd <= 0 then false
                        else
                            let globalName = globalLine.Substring(0, nameEnd)
                            let expectedStoreArguments =
                                $"ptr getelementptr inbounds ([{expectedLiteralExtentBytes} x i8], ptr {globalName}, i64 0, i64 0), i32 {expectedLiteralExtentBytes}, i32 {expectedLiteralPayloadBytes}, i32 {expectedLiteralExtentBytes}"
                            llvmLines
                            |> Array.exists (fun line ->
                                line.Contains("@al_owning_copy_constant(", StringComparison.Ordinal)
                                && line.Contains(expectedStoreArguments, StringComparison.Ordinal))
                    | None -> false
                expectedValue = StringValue(stringFromCodeUnitsHex literalCodeUnitsHex)
                && expectedDeepCopyBytes = uint64 expectedLiteralExtentBytes
                && literalStoreCallFound
            else expectedDeepCopyBytes = 0UL
        let payloadExtractionCase =
            [ "optionIntSome"; "resultError"; "optionStringEmbeddedNul"; "nestedOk"; "resultErrorInt"; "nestedErrorInt" ]
            |> List.contains matchName
        let noMatchCopy =
            expectedLiteralCopyMatchesExtent
            && native.Metrics.DeepCopyBytes = expectedDeepCopyBytes
            && (not payloadExtractionCase || expectedDeepCopyBytes = 0UL)
        recordCheck checks failures $"sum/{optimizationName}/match-{matchName}-both-arms-and-payload-binding"
            (interpreted = expectedValues && native.Values = interpreted && output = expectedBytes && native.RetainedOutputBytes = expectedBytes && native.RetainedBytesWritten = expectedBytes.Length && noPayloadRelocation native && noMatchCopy)
            (jsonObject [
                "inputCase", box inputCaseName
                "interpreterValues", box (codeUnitSafeValuesData interpreted)
                "owningValues", box (codeUnitSafeValuesData native.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex output)
                "payloadExtractionRequiresNoDeepCopy", box (not payloadExtractionCase || native.Metrics.DeepCopyBytes = 0UL)
                "expectedDeepCopyBytes", box expectedDeepCopyBytes
                "literalStoreMatchesIndependentCodeUnitsAndExtent", box expectedLiteralCopyMatchesExtent
                "literalStoreCallFound", box literalStoreCallFound
                "expectedLiteralPayloadBytes", box expectedLiteralPayloadBytes
                "expectedLiteralExtentBytes", box expectedLiteralExtentBytes
                "deepCopyBytes", box native.Metrics.DeepCopyBytes
                "moveBytes", box native.Metrics.MoveBytes
                "layoutEvents", box (layoutEventDetails native.LayoutEvents) ])

    let branchOracle = oracle.GetProperty("branchJoinCases").EnumerateArray() |> Seq.toArray
    for branchCase in branchOracle do
        let name = branchCase.GetProperty("name").GetString()
        let chooseSome = branchCase.GetProperty("chooseSome").GetBoolean()
        let expectedValue = parseSumFixtureValue (branchCase.GetProperty("expected"))
        let expectedBytes = bytesFromHex (branchCase.GetProperty("retainedBytesHex").GetString())
        let body = entries.BranchJoin
        use branchProgram = compile ("branch-join-" + name) body
        use interpretedResult = IrInterpreter.executeBodyWithInputs interpreterHost (VerifiedIrBody.inspect body).BodyName body None [ IrEntryArgument.BoolArgument chooseSome ]
        let interpreted = interpretedResult.Decode()
        let output = outputBuffer expectedBytes.Length
        let native = branchProgram.ExecuteInto([ BoolValue chooseSome ], 4096, output)
        recordCheck checks failures $"sum/{optimizationName}/branch-join-{name}-interpreter-owning"
            (interpreted = [ expectedValue ] && native.Values = interpreted && output = expectedBytes && native.RetainedOutputBytes = expectedBytes && noPayloadRelocation native)
            (jsonObject [
                "chooseSome", box chooseSome
                "interpreterValues", box (codeUnitSafeValuesData interpreted)
                "owningValues", box (codeUnitSafeValuesData native.Values)
                "expectedRetainedBytes", box (bytesHex expectedBytes)
                "actualRetainedBytes", box (bytesHex output)
                "moveBytes", box native.Metrics.MoveBytes ])

    let duplicateOracle = oracle.GetProperty("dupDrop")
    let duplicateInputCase = duplicateOracle.GetProperty("inputCase").GetString()
    let duplicateExpected = caseValues[duplicateInputCase]
    let duplicateExpectedBytes = bytesFromHex (duplicateOracle.GetProperty("retainedBytesHex").GetString())
    use duplicateProgram = compile "dup-drop-option-string" entries.DuplicateDrop
    let duplicateInterpreted = executeInterpreterWithCaseRoot entries.DuplicateDrop duplicateInputCase
    let duplicateOutput = outputBuffer duplicateExpectedBytes.Length
    let duplicateResult = duplicateProgram.ExecuteInto([ duplicateExpected ], 4096, duplicateOutput)
    let duplicateEvent = duplicateResult.LayoutEvents |> List.filter (fun event -> event.Kind = "duplicate")
    let duplicateDropEvent = duplicateResult.LayoutEvents |> List.filter (fun event -> event.Kind = "drop")
    let duplicateRangesDisjoint =
        match duplicateEvent with
        | [ duplicate ] ->
            match duplicate.SourceOffsetBytes, duplicate.SourceExtentBytes with
            | Some sourceOffset, Some sourceExtent ->
                duplicate.ExtentBytes = duplicateOracle.GetProperty("duplicateExtentBytes").GetInt32()
                && (duplicate.OffsetBytes + duplicate.ExtentBytes <= sourceOffset || sourceOffset + sourceExtent <= duplicate.OffsetBytes)
                && (duplicateDropEvent |> List.exists (fun dropped -> dropped.OffsetBytes = duplicate.OffsetBytes && dropped.ExtentBytes = duplicate.ExtentBytes))
            | _ -> false
        | _ -> false
    recordCheck checks failures $"sum/{optimizationName}/dup-drop-preserves-survivor-with-one-explicit-copy"
        (duplicateInterpreted = [ duplicateExpected ] && duplicateResult.Values = duplicateInterpreted && duplicateOutput = duplicateExpectedBytes && duplicateResult.Metrics.DeepCopyBytes = uint64 (duplicateOracle.GetProperty("duplicateExtentBytes").GetInt32()) && duplicateResult.Metrics.MoveBytes = 0UL && duplicateRangesDisjoint)
        (jsonObject [
            "interpreterValues", box (codeUnitSafeValuesData duplicateInterpreted)
            "owningValues", box (codeUnitSafeValuesData duplicateResult.Values)
            "expectedRetainedBytes", box (bytesHex duplicateExpectedBytes)
            "actualRetainedBytes", box (bytesHex duplicateOutput)
            "duplicateExtentBytes", box (duplicateOracle.GetProperty("duplicateExtentBytes").GetInt32())
            "deepCopyBytes", box duplicateResult.Metrics.DeepCopyBytes
            "moveBytes", box duplicateResult.Metrics.MoveBytes
            "duplicateRangesDisjoint", box duplicateRangesDisjoint
            "events", box (layoutEventDetails duplicateResult.LayoutEvents) ])

    let tailCase = caseByName "sum-text-tail"
    let tailValue = caseValues["sum-text-tail"]
    let tailExpectedChoice = parseSumFixtureValue (tailCase.GetProperty("expected").GetProperty("fields").GetProperty("choice"))
    let tailExpectedChoiceBytes = bytesFromHex (tailCase.GetProperty("choiceRetainedBytesHex").GetString())
    use tailChoiceProgram = compile "project-dynamic-option-before-tail-choice" entries.TailChoice
    let tailChoiceInterpreter = executeInterpreterWithCaseRoot entries.TailChoice "sum-text-tail"
    let tailChoiceOutput = outputBuffer tailExpectedChoiceBytes.Length
    let tailChoiceNative = tailChoiceProgram.ExecuteInto([ tailValue ], 4096, tailChoiceOutput)
    let tailChoiceFieldEvents = tailChoiceNative.LayoutEvents |> List.filter (fun event -> event.Kind = "field-extract")
    recordCheck checks failures $"sum/{optimizationName}/dynamic-record-sum-field-extract-before-tail"
        (tailChoiceInterpreter = [ tailExpectedChoice ] && tailChoiceNative.Values = tailChoiceInterpreter && tailChoiceOutput = tailExpectedChoiceBytes && tailChoiceNative.RetainedOutputBytes = tailExpectedChoiceBytes && tailChoiceFieldEvents.Length = 1 && noPayloadRelocation tailChoiceNative)
        (jsonObject [
            "expectedFieldOffsetBytes", box (tailCase.GetProperty("fieldOffsetsBytes").GetProperty("choice").GetInt32())
            "interpreterValues", box (codeUnitSafeValuesData tailChoiceInterpreter)
            "owningValues", box (codeUnitSafeValuesData tailChoiceNative.Values)
            "expectedRetainedBytes", box (bytesHex tailExpectedChoiceBytes)
            "actualRetainedBytes", box (bytesHex tailChoiceOutput)
            "moveBytes", box tailChoiceNative.Metrics.MoveBytes
            "fieldEvents", box (layoutEventDetails tailChoiceFieldEvents) ])
    let tailExpectedValue = parseSumFixtureValue (tailCase.GetProperty("expected").GetProperty("fields").GetProperty("tail"))
    let tailExpectedBytes = bytesFromHex (tailCase.GetProperty("tailRetainedBytesHex").GetString())
    use tailProjectionProgram = compile "project-dynamic-int-tail-after-option-string" entries.TailProjection
    let tailInterpreter = executeInterpreterWithCaseRoot entries.TailProjection "sum-text-tail"
    let tailOutput = outputBuffer tailExpectedBytes.Length
    let tailNative = tailProjectionProgram.ExecuteInto([ tailValue ], 4096, tailOutput)
    let tailFieldEvents = tailNative.LayoutEvents |> List.filter (fun event -> event.Kind = "field-extract")
    let expectedTailOffset = tailCase.GetProperty("fieldOffsetsBytes").GetProperty("tail").GetInt32()
    let actualTailOffset = tailFieldEvents |> List.tryHead |> Option.map (fun event -> event.SourceOffsetBytes |> Option.defaultValue -1) |> Option.defaultValue -1
    recordCheck checks failures $"sum/{optimizationName}/dynamic-record-fixed-tail-offset-after-variable-sum"
        (tailInterpreter = [ tailExpectedValue ] && tailNative.Values = tailInterpreter && tailOutput = tailExpectedBytes && tailNative.RetainedOutputBytes = tailExpectedBytes && tailFieldEvents.Length = 1 && actualTailOffset = expectedTailOffset && noPayloadRelocation tailNative)
        (jsonObject [
            "expectedDynamicTailOffsetBytes", box expectedTailOffset
            "actualDynamicTailOffsetBytes", box actualTailOffset
            "interpreterValues", box (codeUnitSafeValuesData tailInterpreter)
            "owningValues", box (codeUnitSafeValuesData tailNative.Values)
            "expectedRetainedBytes", box (bytesHex tailExpectedBytes)
            "actualRetainedBytes", box (bytesHex tailOutput)
            "moveBytes", box tailNative.Metrics.MoveBytes
            "fieldEvents", box (layoutEventDetails tailFieldEvents) ])

    let hostRejectedOracle = oracle.GetProperty("hostValueRejections")
    let mismatchSentinel = bytesFromHex (oracle.GetProperty("rawNativeEntry").GetProperty("sentinelBytesHex").GetString())
    use mismatchProgram = compile "reject-mismatched-option-host-value" entries.IdentityBodies["sums.option-int-identity"]
    let mismatchValue = OptionValue(TString, Some(StringValue "wrong"))
    let mismatchOutput = Array.copy mismatchSentinel
    let mismatchOutputBefore = Array.copy mismatchOutput
    let mutable mismatchRejected = false
    let mutable mismatchCode = ""
    try mismatchProgram.ExecuteInto([ mismatchValue ], 4096, mismatchOutput) |> ignore
    with error ->
        mismatchRejected <- true
        mismatchCode <- exceptionCode error
    let mismatchCase = hostRejectedOracle.GetProperty("mismatchedOptionType")
    let expectedMismatch = mismatchCase.GetProperty("expectedRejected").GetBoolean()
    let expectedMismatchOutputUnchanged = mismatchCase.GetProperty("expectedRetainedBytesUnchanged").GetBoolean()
    recordCheck checks failures $"sum/{optimizationName}/host-rejects-mismatched-option-input-without-publishing"
        (mismatchRejected = expectedMismatch
         && expectedMismatchOutputUnchanged
         && mismatchOutput = mismatchOutputBefore)
        (jsonObject [
            "rejected", box mismatchRejected
            "expectedRetainedOutputUnchanged", box expectedMismatchOutputUnchanged
            "diagnosticCode", box mismatchCode
            "retainedOutputUnchanged", box (mismatchOutput = mismatchOutputBefore)
            "retainedOutputHex", box (bytesHex mismatchOutput) ])

    let rawOracle = oracle.GetProperty("rawNativeEntry")
    let contextAbiPassed, contextAbiDetails = inspectRawOwningContextAbi contextAbiOracle
    recordCheck checks failures $"sum/{optimizationName}/raw-context-mirror-matches-independent-abi-oracle" contextAbiPassed contextAbiDetails
    if not contextAbiPassed then invalidOp "Raw sum native-entry checks require the independent context ABI oracle."
    let expectedInvalidStatus = uint32 (rawOracle.GetProperty("expectedInvalidRequestStatus").GetInt32())
    let rawSentinel = bytesFromHex (rawOracle.GetProperty("sentinelBytesHex").GetString())
    let rawProgramForBody = function
        | "optionInt" -> compile "raw-ignore-option-int" entries.RawIgnoreBodies["optionInt"]
        | "resultIntString" -> compile "raw-ignore-result-int-string" entries.RawIgnoreBodies["resultIntString"]
        | "nestedOptionResult" -> compile "raw-ignore-nested-option-result" entries.RawIgnoreBodies["nestedOptionResult"]
        | other -> invalidOp $"Unknown raw sum conformance body '{other}'."
    let checkRawRejected name (program: OwningStackCompiledProgram) bytes extents count =
        let result = invokeRawOwningEntry program contextAbiOracle bytes extents count rawSentinel
        let unchanged = result.RetainedOutput = rawSentinel
        let passed = result.NativeStatus = int32 expectedInvalidStatus && result.ContextStatus = expectedInvalidStatus && unchanged
        recordCheck checks failures $"sum/{optimizationName}/raw-native-entry/{name}" passed (jsonObject [
            "expectedInvalidRequestStatus", box expectedInvalidStatus
            "nativeStatus", box result.NativeStatus
            "contextStatus", box result.ContextStatus
            "inputBytes", box (bytesHex bytes)
            "inputExtentsBytes", box extents
            "inputCount", box count
            "retainedOutputUnchanged", box unchanged
            "retainedOutputBeforeAndAfterHex", box (bytesHex result.RetainedOutput) ])
    let rawInvalidTags = rawOracle.GetProperty("invalidTags").EnumerateArray() |> Seq.toArray
    let rawMalformedInputs = rawOracle.GetProperty("malformedInputs").EnumerateArray() |> Seq.toArray
    for invalidTag in rawInvalidTags do
        let name = invalidTag.GetProperty("name").GetString()
        let bodyName = invalidTag.GetProperty("body").GetString()
        let bytes = bytesFromHex (invalidTag.GetProperty("inputBytesHex").GetString())
        let extents = invalidTag.GetProperty("inputExtentsBytes").EnumerateArray() |> Seq.map (fun item -> uint32 (item.GetInt32())) |> Seq.toArray
        let inputCount = invalidTag.GetProperty("inputCount").GetInt32()
        let count = uint32 inputCount
        use rawProgram = rawProgramForBody bodyName
        checkRawRejected ("invalid-tag-" + name) rawProgram bytes extents count
    for malformed in rawMalformedInputs do
        let name = malformed.GetProperty("name").GetString()
        let bodyName = malformed.GetProperty("body").GetString()
        let bytes = bytesFromHex (malformed.GetProperty("inputBytesHex").GetString())
        let extents = malformed.GetProperty("inputExtentsBytes").EnumerateArray() |> Seq.map (fun item -> uint32 (item.GetInt32())) |> Seq.toArray
        let count = uint32 (malformed.GetProperty("inputCount").GetInt32())
        use rawProgram = rawProgramForBody bodyName
        checkRawRejected ("malformed-" + name) rawProgram bytes extents count

    let stackOracle = oracle.GetProperty("capacityOracle")
    let stackCaseName = stackOracle.GetProperty("stackConstructionCase").GetString()
    let stackCaseValue = caseValues[stackCaseName]
    let stackExpectedBytes = bytesFromHex ((caseByName stackCaseName).GetProperty("retainedBytesHex").GetString())
    let stackExactCapacity = stackOracle.GetProperty("stackExactBytes").GetInt32()
    let stackShortCapacity = stackOracle.GetProperty("stackOneByteShortBytes").GetInt32()
    use stackCapacityProgram = compile "sum-stack-capacity-boundary" entries.Cases[stackCaseName]
    let exactStackOutput = outputBuffer stackExpectedBytes.Length
    let exactStackResult = stackCapacityProgram.ExecuteInto([], stackExactCapacity, exactStackOutput)
    let stackShortOutput = outputBuffer stackExpectedBytes.Length
    let stackShortOutputBefore = Array.copy stackShortOutput
    let stackShortError =
        try
            stackCapacityProgram.ExecuteInto([], stackShortCapacity, stackShortOutput) |> ignore
            None
        with error -> Some error
    match stackShortError with
    | Some error ->
        let metrics = getProperty error "Metrics"
        let finalCursor = int64Property metrics "FinalCursorBytes"
        let stackPassed =
            stackExactCapacity = stackShortCapacity + 1
            && exactStackResult.Values = [ stackCaseValue ]
            && exactStackOutput = stackExpectedBytes
            && exactStackResult.Metrics.StackCapacityBytes = stackExactCapacity
            && (getProperty error "Code" |> string) = "OWNING_STACK_CAPACITY"
            && (getProperty error "Boundary" |> string) = "program-data-stack"
            && int64Property error "RequiredBytes" = int64 stackExactCapacity
            && int64Property error "AvailableBytes" = int64 stackShortCapacity
            && finalCursor = 0L
            && stackShortOutput = stackShortOutputBefore
        recordCheck checks failures $"sum/{optimizationName}/stack-exact-and-one-byte-short-unwinds"
            stackPassed (jsonObject [
                "stackConstructionCase", box stackCaseName
                "exactCapacityBytes", box stackExactCapacity
                "oneByteShortCapacityBytes", box stackShortCapacity
                "exactResultBytes", box (bytesHex exactStackOutput)
                "shortFailure", box (resourceExceptionDetails error)
                "finalCursorBytes", box finalCursor
                "retainedOutputUnchanged", box (stackShortOutput = stackShortOutputBefore) ])
    | None ->
        recordCheck checks failures $"sum/{optimizationName}/stack-exact-and-one-byte-short-unwinds" false "One byte below the pinned sum construction stack capacity unexpectedly succeeded."

    use failProgram = compile "sum-failure-after-payload-allocation" entries.FailAfterSumAllocation
    let interpretedFailure =
        try
            interpreterBody entries.FailAfterSumAllocation |> ignore
            "unexpected-success"
        with error -> diagnosticCode error
    let failureSentinel = Array.copy rawSentinel
    let failureBefore = Array.copy failureSentinel
    let mutable nativeFailureCode = ""
    let mutable nativeFailureMetrics: obj = null
    try failProgram.ExecuteInto([], 4096, failureSentinel) |> ignore
    with error ->
        nativeFailureCode <- diagnosticCode error
        nativeFailureMetrics <- getProperty error "Metrics"
    let failureFinalCursor = if isNull nativeFailureMetrics then -1L else int64Property nativeFailureMetrics "FinalCursorBytes"
    let failureDeepCopy = if isNull nativeFailureMetrics then -1L else int64Property nativeFailureMetrics "DeepCopyBytes"
    let failureMoves = if isNull nativeFailureMetrics then -1L else int64Property nativeFailureMetrics "MoveBytes"
    let failurePassed =
        interpretedFailure = nativeFailureCode
        && nativeFailureCode = "RUNTIME_DIVIDE_BY_ZERO"
        && failureFinalCursor = 0L
        && failureDeepCopy > 0L
        && failureMoves = 0L
        && failureSentinel = failureBefore
    recordCheck checks failures $"sum/{optimizationName}/failure-after-inline-payload-allocation-unwinds-without-publishing" failurePassed (jsonObject [
        "interpreterDiagnosticCode", box interpretedFailure
        "owningDiagnosticCode", box nativeFailureCode
        "deepCopyBytesBeforeFailure", box failureDeepCopy
        "moveBytesBeforeFailure", box failureMoves
        "finalCursorBytes", box failureFinalCursor
        "retainedOutputUnchanged", box (failureSentinel = failureBefore) ])

    let layouts = observedLayouts |> Seq.toList |> List.distinctBy (fun layout -> layout.TypeName)
    let layoutChecks, layoutFailures, layoutSummary = validateSumTypeLayouts fixture layouts
    recordCheck checks failures $"sum/{optimizationName}/independent-case-and-field-layout-oracle"
        (layoutFailures.Length = 0)
        (jsonObject [ "checks", box layoutChecks; "failures", box layoutFailures; "layouts", box layoutSummary ])

    jsonObject [
        "optimization", box optimizationName
        "caseCount", box caseNames.Length
        "equalityCaseCount", box (oracle.GetProperty("equalityCases").GetArrayLength())
        "matchCaseCount", box (oracle.GetProperty("matches").EnumerateObject() |> Seq.length)
        "invalidTagCount", box rawInvalidTags.Length
        "malformedInputCount", box rawMalformedInputs.Length
        "rawInputCount", box (rawInvalidTags.Length + rawMalformedInputs.Length)
        "hostInputCount", box caseNames.Length
        "localCallCount", box caseNames.Length
        "retainedShortCount", box caseNames.Length
        "branchJoinCaseCount", box branchOracle.Length
        "stackCapacityCaseCount", box 1
        "unwindCaseCount", box 1
        "descriptorGraphRejectionCount", box (oracle.GetProperty("descriptorGraphRejections").GetArrayLength()) ]

[<EntryPoint>]
let main argv =
    let reportPath =
        if argv.Length >= 3 then Path.GetFullPath argv[2]
        else Path.Combine(Environment.CurrentDirectory, "native-value-stack-evidence.json")
    let artifactsRoot = if argv.Length >= 2 then Path.GetFullPath argv[1] else Path.Combine(Environment.CurrentDirectory, "native-value-stack-artifacts")
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let options = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let report = Dictionary<string, obj>(StringComparer.Ordinal)
    let mutable exceptionText = ""
    try
        if argv.Length <> 3 then invalidArg "arguments" "Usage: AgentLang.NativeValueStack <fixture.json> <artifact-directory> <evidence.json>"
        let fixturePath = Path.GetFullPath argv[0]
        let fixturePathFromRepo = fixturePath
        let sourcePath = Path.Combine(AppContext.BaseDirectory, "value-stack.flow")
        let source = File.ReadAllText sourcePath
        let sourceHash = hashFile sourcePath
        use fixtureDocument = JsonDocument.Parse(File.ReadAllText fixturePath)
        let fixture = fixtureDocument.RootElement
        if fixture.GetProperty("schemaVersion").GetInt32() <> 3 then
            invalidOp "The current native-value-stack fixture must use schemaVersion 3."
        let expectedSourceHash = fixture.GetProperty("source").GetProperty("sha256").GetString()
        recordCheck checks failures "fixture/source-hash" (String.Equals(sourceHash, expectedSourceHash, StringComparison.Ordinal)) (jsonObject [ "expected", box expectedSourceHash; "actual", box sourceHash ])
        let repetitions = fixture.GetProperty("repetitions").EnumerateArray() |> Seq.map (fun item -> item.GetInt32()) |> Seq.toArray
        if repetitions <> [| 1; 8 |] then invalidOp "The independent fixture must retain repetition counts 1 and 8."
        let generatedTurns = repetitions |> Array.map turnSource
        let depthSource = depthChainSource 65
        let stringRecordMarker = "record TextLeaf {"
        let stringRecordStart = source.IndexOf(stringRecordMarker, StringComparison.Ordinal)
        if stringRecordStart < 0 then invalidOp "The source fixture no longer contains the String workload boundary marker."
        let fixedSource = source.Substring(0, stringRecordStart)
        let expandedFixedSource = String.concat "\n\n" [ fixedSource; String.concat "\n\n" generatedTurns; depthSource ]
        let expandedStringSource = String.concat "\n\n" [ source; String.concat "\n\n" generatedTurns; depthSource ]
        Directory.CreateDirectory(Path.GetDirectoryName reportPath) |> ignore
        Directory.CreateDirectory artifactsRoot |> ignore
        File.WriteAllText(Path.Combine(Path.GetDirectoryName reportPath, "expanded-fixed-source.flow"), expandedFixedSource, UTF8Encoding(false))
        File.WriteAllText(Path.Combine(Path.GetDirectoryName reportPath, "expanded-string-source.flow"), expandedStringSource, UTF8Encoding(false))
        let turnHashes =
            repetitions
            |> Array.mapi (fun index count -> box (jsonObject [ "repetitions", box count; "sha256", box (hashText generatedTurns[index]) ]))
        addCommonEntryReports report sourceHash expandedFixedSource turnHashes
        report["stringExpandedSourceSha256"] <- box (hashText expandedStringSource)
        report["depthChain"] <- box (jsonObject [ "maxDepth", box 65; "sourceSha256", box (hashText depthSource); "shape", box "Static acyclic function chain; chain-64 enters 65 user bodies (depth 0 through 64), chain-65 enters 66 and crosses the language boundary." ])
        report["fixturePath"] <- box fixturePathFromRepo
        let entries = compileEntries expandedFixedSource
        let scopeShadowPassed, scopeShadowDetails = inspectScopeShadow entries.Program entries.ScopeShadow
        recordCheck checks failures "verified-ir/scope-shadow-same-local-slot-width-restore" scopeShadowPassed scopeShadowDetails
        report["scopeShadowIr"] <- scopeShadowDetails
        report["sameVerifiedProgramInstance"] <- box true
        report["fixedControlVerifiedProgramInstance"] <- box true
        report["backendScope"] <- box "Within fixed controls, the same VerifiedIrBody objects are executed by interpreter, existing ABI3 LLVM sharedgraph, and inline owning-stack LLVM. ABI3 receives only the fixed nominal types."
        report["turnBoundaryScope"] <- box "Each owning-stack invocation publishes fixed State bytes into a caller-owned output buffer. Turn 2 is fed from an independent decode of turn 1's raw retained bytes through the Value input API; this validates publication and round-trip semantics across the host boundary, not a persistent native controller."
        report["limitations"] <- fixture.GetProperty("limits").EnumerateArray() |> Seq.map (fun value -> box (value.GetString())) |> Seq.toArray
        let optimizationPairs = [| LlvmOptimization.O0, "O0"; LlvmOptimization.O2, "O2" |]
        let runReports = ResizeArray<obj>()
        let candidateSummaries = Dictionary<string, obj>(StringComparer.Ordinal)
        for repetition in repetitions do
            let body = if repetition = 1 then entries.TurnOne else entries.TurnEight
            runReports.Add(box (runInterpreterChain checks failures options fixture entries repetition body))
            for optimization, _ in optimizationPairs do
                runReports.Add(box (runNativeChain checks failures options artifactsRoot optimization fixture entries repetition body))
                runReports.Add(box (runOwningStackChain checks failures options artifactsRoot optimization fixture entries repetition body candidateSummaries))
        let readSummary (key: string) : Dictionary<string, obj> = candidateSummaries[key] :?> Dictionary<string, obj>
        let metricInt (summary: Dictionary<string, obj>) (name: string) = Convert.ToInt64(summary[name], CultureInfo.InvariantCulture)
        let repetitionComparisons = ResizeArray<obj>()
        for (_, optimizationName) in optimizationPairs do
            for turnName in [ "turn1"; "turn2" ] do
                let n1 = readSummary $"{optimizationName}/N=1/{turnName}"
                let n8 = readSummary $"{optimizationName}/N=8/{turnName}"
                let invariantFields = [ "inputBytes"; "inputCopyBytes"; "retainedCopyBytes"; "hostRetainedStagingBytes"; "hostRetainedCommitBytes" ]
                let invariantResults =
                    invariantFields
                    |> List.map (fun field -> field, metricInt n1 field, metricInt n8 field)
                let invariantPassed = invariantResults |> List.forall (fun (_, left, right) -> left = right)
                recordCheck checks failures $"owning-stack/{optimizationName}/{turnName}/same-boundary-copy-bytes-for-N1-and-N8" invariantPassed (jsonObject [
                    "independentExpectation", box "Input and retained-output boundary bytes depend on the request shape, while the stable arena cursor may retain dead interior allocations across helper calls."
                    "categories", box (invariantResults |> List.map (fun (name, n1Value, n8Value) -> jsonObject [ "category", box name; "N1", box n1Value; "N8", box n8Value ])) ])
                let copyDelta = metricInt n8 "deepCopyBytes" - metricInt n1 "deepCopyBytes"
                let frameDelta = metricInt n8 "frameReturnCount" - metricInt n1 "frameReturnCount"
                let repetitionPassed = copyDelta >= 7L * 16L && frameDelta >= 7L
                recordCheck checks failures $"owning-stack/{optimizationName}/{turnName}/repetition-copy-and-frame-growth" repetitionPassed (jsonObject [
                    "minimumExplicitDuplicateGrowthBytes", box (7 * 16)
                    "actualDeepCopyGrowthBytes", box copyDelta
                    "minimumAdditionalHelperReturns", box 7
                    "actualFrameReturnGrowth", box frameDelta ])
                repetitionComparisons.Add(box (jsonObject [
                    "optimization", box optimizationName
                    "turn", box turnName
                    "categories", box (invariantResults |> List.map (fun (name, n1Value, n8Value) -> jsonObject [ "category", box name; "N1", box n1Value; "N8", box n8Value ]))
                    "occupiedCursorPeakBytesN1", box (metricInt n1 "occupiedCursorPeakBytes")
                    "occupiedCursorPeakBytesN8", box (metricInt n8 "occupiedCursorPeakBytes")
                    "deepCopyGrowthBytes", box copyDelta
                    "frameReturnGrowth", box frameDelta ]))
        report["runs"] <- runReports.ToArray()
        report["candidateRepetitionComparisons"] <- repetitionComparisons.ToArray()
        let stringEntries = compileStringEntries expandedStringSource
        let dynamicScopeShadowPassed, dynamicScopeShadowDetails = inspectDynamicScopeShadow stringEntries.Program stringEntries.ScopeShadow
        recordCheck checks failures "verified-ir/dynamic-scope-shadow-same-local-slot" dynamicScopeShadowPassed dynamicScopeShadowDetails
        report["stringScopeShadowIr"] <- dynamicScopeShadowDetails
        report["stringVerifiedProgramInstance"] <- box true
        report["stringBackendScope"] <- box "String cases share one compiler-authorized VerifiedIrProgram and exact VerifiedIrBody between interpreter and owning LLVM O0/O2. The older LlvmAot sharedgraph ABI3 String parity is unavailable because its closed native type registry rejects String and string.concat; fixed controls remain the three-way comparison. Interpreter fixture inputs are produced by compiler-verified literal factory bodies because its entry API accepts only Int/Bool/Unit arguments, while owning execution receives each varying nested TextState as a runtime Value input."
        let stringRuns = ResizeArray<obj>()
        let layoutDepthRuns = ResizeArray<obj>()
        for optimization, optimizationName in optimizationPairs do
            stringRuns.Add(box (runStringWorkload checks failures options fixture stringEntries artifactsRoot optimization optimizationName))
            layoutDepthRuns.Add(box (runLayoutDepthCases checks failures options fixture artifactsRoot optimization optimizationName))
        report["stringRuns"] <- stringRuns.ToArray()
        report["layoutDepthRuns"] <- layoutDepthRuns.ToArray()
        let enumEntries = compileEnumEntries ()
        report["enumVerifiedProgramInstance"] <- box true
        report["enumBackendScope"] <- box "The same verified enum program and bodies run through the interpreter and owning O0/O2. Closed enums use literal ordinal bytes in the independent fixture; fixed and String-bearing records pin inline field placement."
        let enumRuns = ResizeArray<obj>()
        for optimization, optimizationName in optimizationPairs do
            enumRuns.Add(box (runEnumConformance checks failures fixture artifactsRoot optimization optimizationName enumEntries))
        report["enumRuns"] <- enumRuns.ToArray()
        let sumEntries = compileSumEntries fixture
        report["sumVerifiedProgramInstance"] <- box true
        report["sumBackendScope"] <- box "The same compiler-authorized VerifiedIrProgram and exact VerifiedIrBody instances run through the interpreter and owning-stack LLVM at O0/O2, including runtime Value input encoding and output decoding. Layout ABI3 here is the owning-stack physical layout contract. The older LlvmAot sharedgraph ABI3 path remains a separate fixed-only control and does not claim Option/Result support."
        let sumRuns = ResizeArray<obj>()
        for optimization, optimizationName in optimizationPairs do
            sumRuns.Add(box (runSumConformance checks failures fixture artifactsRoot optimization optimizationName sumEntries))
        report["sumRuns"] <- sumRuns.ToArray()
    with error ->
        let exceptionDetails =
            [ "Diagnostic"; "Metrics"; "RequiredBytes"; "AvailableBytes"; "Boundary" ]
            |> List.choose (fun name ->
                let value = getProperty error name
                if isNull value then None else Some($"{name}: {value}"))
            |> String.concat "\n"
        exceptionText <-
            if String.IsNullOrWhiteSpace exceptionDetails then error.ToString()
            else error.ToString() + "\n" + exceptionDetails
        failures.Add(exceptionText)
    report["passed"] <- box (String.IsNullOrEmpty exceptionText && failures.Count = 0 && checks.Count > 0)
    report["checks"] <- checks.ToArray()
    report["failure"] <- box exceptionText
    report["failureCount"] <- box failures.Count
    report["failureDetails"] <- box (failures.ToArray())
    Directory.CreateDirectory(Path.GetDirectoryName reportPath) |> ignore
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options), UTF8Encoding(false))
    let outcome = if failures.Count = 0 then "passed" else "failed"
    Console.WriteLine($"Native owning-value-stack experiment: {outcome}; evidence={reportPath}")
    if failures.Count = 0 then 0 else 1
