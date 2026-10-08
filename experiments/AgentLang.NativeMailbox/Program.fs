module AgentLang.NativeMailbox.Program

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open AgentLang
open AgentLang.Llvm
open AgentLang.NativeMailbox

type private HandlerBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      Initialize: VerifiedIrBody
      Begin: VerifiedIrBody
      Resume: VerifiedIrBody }

type private ScenarioResult =
    { Backend: string
      Optimization: string
      AAfterOverlap: int64
      BAfterOverlap: int64
      AFinal: int64
      LanguageFailureCode: string
      CapacityFailureCode: string
      CapacityRequiredBytes: int64
      CapacityRequiredNodes: int64
      CapacityAvailableBytes: int
      RetainedMaximumBytes: int
      RetainedMaximumNodes: int
      ScratchArenaOwnersCreated: int
      ScratchArenaByteCapacity: int
      ScratchArenaNodeCapacity: int
      ScratchLeaseAcquisitions: int
      ScratchLeaseReturns: int
      ScratchPoisonPasses: int
      SuspensionLeaseCounts: int array
      HandlerInvocations: int
      DecodeCalls: int
      CreatedOwners: int
      DisposedOwners: int }

type private CheckResult =
    { Name: string
      Passed: bool
      Detail: string }

type private BackendResources<'Owner> =
    { Backend: MailboxBackend<'Owner>
      CreatedOwners: unit -> int
      DisposedOwners: unit -> int }

let private sourceSpan file column =
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
              Span = sourceSpan "<generated-mailbox-record>" 1 }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let constructor = entry (RecordConstructor name) (prefix + ".new") (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ]
        let accessors =
            record.Fields
            |> List.map (fun field ->
                entry (RecordAccessor(name, field.Name)) (prefix + "." + field.Name) [ TNamed name ] [ field.Type ])
        constructor :: accessors)

let private compileHandlers (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 "mailbox.flow" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records = document.Records |> List.map (fun record -> record.Name, record) |> Map.ofList
    let words =
        (generatedRecordEntries records)
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
          Enums = Map.empty
          WordIds = wordIds }
    let context: FlowLowering.Context =
        { CompilerContext = compilerContext
          ParameterNames = Map.empty
          SourceOrigins = Map.empty }
    let changes: FlowLowering.FlowWordChange list =
        document.Words
        |> List.map (fun definition ->
            { FlowLowering.FlowWordChange.Definition = definition
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId("mailbox-source-" + definition.Name), 1) })
    let compiled = FlowLowering.compileBatchWords context changes
    let entrySpan name = sourceSpan ("<" + name + ">") 1
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, entrySpan entryName) ]
            compiled.Context.SourceOrigins
    let handlers =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          Initialize = compileEntry "mailbox.initialize" "mailbox.initialize" [ TInt ]
          Begin = compileEntry "mailbox.begin" "mailbox.begin" [ TNamed "State"; TInt ]
          Resume = compileEntry "mailbox.resume" "mailbox.resume" [ TNamed "State"; TNamed "Continuation"; TInt ] }
    let allBodies = [ handlers.Initialize; handlers.Begin; handlers.Resume ]
    if allBodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, handlers.Program))) then
        invalidOp "Mailbox entry wrappers did not retain one VerifiedIrProgram instance."
    handlers

let private interpreterBackend (handlers: HandlerBodies) =
    let wordSpans = handlers.CompilerContext.Words |> Map.map (fun _ entry -> entry.Definition.Span)
    let interpreterHost: IrInterpreterHost =
        { PreflightEffects = fun _ _ _ -> ()
          ChargeInstruction = fun _ _ -> ()
          RecordBranchOutcome = fun _ _ _ -> ()
          RecordUse = ignore
          InvokeEffect = fun _ -> EffectUnit
          WordDefinitionSpan = fun word -> wordSpans.TryFind word
          PrimitiveDefinitionSpan = fun word -> wordSpans.TryFind word }
    let execute executionName body owner arguments =
        IrInterpreter.executeBodyWithInputs interpreterHost executionName body owner arguments
    let mutable createdOwners = 0
    let mutable disposedOwners = 0
    let create executionName body owner arguments =
        let result = execute executionName body owner arguments
        createdOwners <- createdOwners + 1
        result
    { Backend =
        { Initialize = fun seed -> create "mailbox.initialize" handlers.Initialize None [ IrEntryArgument.IntArgument seed ]
          Begin = fun owner delta -> create "mailbox.begin" handlers.Begin (Some owner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument delta ]
          Resume = fun owner divisor _ ->
              create "mailbox.resume" handlers.Resume (Some owner)
                  [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.IntArgument divisor ]
          Decode = fun owner -> owner.Decode()
          Usage = fun _ -> None
          DisposeOwner = fun owner -> disposedOwners <- disposedOwners + 1; owner.Dispose()
          EnterScratchLease = ignore
          LeaveScratchLease = ignore
          OutstandingScratchLeases = fun () -> 0
          PoisonScratch = ignore
          ScratchArenaOwnersCreated = 0
          ScratchArenaByteCapacity = 0
          ScratchArenaNodeCapacity = 0
          ScratchLeaseAcquisitions = fun () -> 0
          ScratchLeaseReturns = fun () -> 0
          ScratchPoisonPasses = fun () -> 0
          DisposeBackend = ignore }
      CreatedOwners = fun () -> createdOwners
      DisposedOwners = fun () -> disposedOwners }

let private nativeBackend (handlers: HandlerBodies) (artifacts: string) optimization =
    let name = string optimization
    let outputDirectory entryName = Path.Combine(artifacts, "native", name, entryName)
    let toolchain = LlvmToolchain.discover ()
    let diagnosticSources = NativeDiagnosticSources.fromLoweringContext handlers.CompilerContext
    let compile entryName body = LlvmAot.compile toolchain optimization (outputDirectory entryName) diagnosticSources body
    let compiledPrograms = ResizeArray<NativeCompiledProgram>()
    let mutable scratchOwner: NativeScratchArena option = None
    try
        let initialize = compile "initialize" handlers.Initialize
        compiledPrograms.Add initialize
        let beginHandler = compile "begin" handlers.Begin
        compiledPrograms.Add beginHandler
        let resumeHandler = compile "resume" handlers.Resume
        compiledPrograms.Add resumeHandler
        let scratch = new NativeScratchArena(24, 3)
        scratchOwner <- Some scratch
        let mutable activeLeases = 0
        let mutable leaseAcquisitions = 0
        let mutable leaseReturns = 0
        let mutable poisonPasses = 0
        let mutable createdOwners = 0
        let mutable disposedOwners = 0
        let mutable disposedBackend = false
        let enterLease () =
            if activeLeases <> 0 then invalidOp "The shared mailbox scratch arena received overlapping host leases."
            activeLeases <- 1
            leaseAcquisitions <- leaseAcquisitions + 1
        let leaveLease () =
            if activeLeases <> 1 then invalidOp "Mailbox scratch lease accounting is unbalanced."
            activeLeases <- 0
            leaseReturns <- leaseReturns + 1
        let options retainedByteCapacity =
            { NativeExecutionOptions.defaults with
                ScratchArena = Some scratch
                RetainedByteCapacity = Some(defaultArg retainedByteCapacity 16)
                RetainedNodeCapacity = Some 2 }
        let invoke (handler: NativeCompiledProgram) executionName owner arguments retainedByteCapacity =
            let result = handler.ExecuteRetainedWithInputs(executionName, owner, arguments, options = options retainedByteCapacity)
            createdOwners <- createdOwners + 1
            result
        let usage (owner: NativeRetainedResult) =
            Some { Bytes = owner.RetainedByteCount; Nodes = owner.RetainedNodeCount }
        let disposeOwner (owner: NativeRetainedResult) =
            disposedOwners <- disposedOwners + 1
            (owner :> IDisposable).Dispose()
        let disposeBackend () =
            if not disposedBackend then
                disposedBackend <- true
                (scratch :> IDisposable).Dispose()
                for native in compiledPrograms do (native :> IDisposable).Dispose()
        { Backend =
            { Initialize = fun seed -> invoke initialize "mailbox.initialize" None [ IrEntryArgument.IntArgument seed ] None
              Begin = fun owner delta -> invoke beginHandler "mailbox.begin" (Some owner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument delta ] None
              Resume = fun owner divisor retainedByteCapacity ->
                  invoke resumeHandler "mailbox.resume" (Some owner)
                      [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.IntArgument divisor ] retainedByteCapacity
              Decode = fun owner -> owner.Decode()
              Usage = usage
              DisposeOwner = disposeOwner
              EnterScratchLease = enterLease
              LeaveScratchLease = leaveLease
              OutstandingScratchLeases = fun () -> activeLeases
              PoisonScratch = fun () -> scratch.PoisonAndClear(); poisonPasses <- poisonPasses + 1
              ScratchArenaOwnersCreated = 1
              ScratchArenaByteCapacity = 24
              ScratchArenaNodeCapacity = 3
              ScratchLeaseAcquisitions = fun () -> leaseAcquisitions
              ScratchLeaseReturns = fun () -> leaseReturns
              ScratchPoisonPasses = fun () -> poisonPasses
              DisposeBackend = disposeBackend }
          CreatedOwners = fun () -> createdOwners
          DisposedOwners = fun () -> disposedOwners }
    with _ ->
      match scratchOwner with
      | Some scratch -> (scratch :> IDisposable).Dispose()
      | None -> ()
      for native in compiledPrograms do (native :> IDisposable).Dispose()
      reraise ()

let private readStateTotal (values: Value list) =
    match values with
    | [ RecordValue("State", fields) ] ->
        match fields.TryFind "total" with
        | Some(IntValue total) -> total
        | other -> failwithf "Expected State.total to be Int, got %A." other
    | other -> failwithf "Expected one State root, got %A." other

let private runScenario
    (checks: ResizeArray<CheckResult>)
    (backendName: string)
    (optimization: string)
    (resources: BackendResources<'Owner>)
    testNativeCapacityFailure =
    let backend = resources.Backend
    use host = new MailboxHost<'Owner>([ "A"; "B" ], backend)
    let check name passed detail =
        checks.Add({ Name = backendName + "/" + name; Passed = passed; Detail = detail })
        if not passed then invalidOp $"{backendName}/{name}: {detail}"
    let usage name expectedBytes expectedNodes =
        match host.CurrentUsage name with
        | Some actual ->
            check (name + " retained capacity") (actual.Bytes = expectedBytes && actual.Nodes = expectedNodes)
                $"expected {expectedBytes} bytes/{expectedNodes} nodes, observed {actual.Bytes}/{actual.Nodes}"
            actual
        | None -> { Bytes = 0; Nodes = 0 }
    let rejectBeforeHandler name expectedCode action =
        let before = host.HandlerInvocations
        let mutable actualCode = "no rejection"
        try action ()
        with MailboxProtocolException(code, _) -> actualCode <- code
        check (name + " code") (actualCode = expectedCode) $"expected {expectedCode}, observed {actualCode}"
        check (name + " before handler") (host.HandlerInvocations = before) "invalid protocol request did not invoke a handler"
    let captureLanguageError action =
        try
            action ()
            None
        with LanguageException diagnostic -> Some diagnostic
    let mutable maxRetainedBytes = 0
    let mutable maxRetainedNodes = 0
    let captureUsage mailboxId =
        match host.CurrentUsage mailboxId with
        | Some actual ->
            maxRetainedBytes <- max maxRetainedBytes actual.Bytes
            maxRetainedNodes <- max maxRetainedNodes actual.Nodes
            actual
        | None -> { Bytes = 0; Nodes = 0 }

    host.Initialize("A", 10L)
    host.Initialize("B", 100L)
    let initialA = captureUsage "A"
    let initialB = captureUsage "B"
    if initialA.Bytes > 0 then check "init payload formula" (initialA.Bytes = 8 && initialA.Nodes = 1) "State stores one Int slot (8 bytes) in one node."
    if initialB.Bytes > 0 then check "second init payload formula" (initialB.Bytes = 8 && initialB.Nodes = 1) "Each initialized mailbox owns a separate one-node State graph."

    let initialAReference = host.CurrentReference "A"
    let tokenA1 = host.Begin("A", 7L)
    usage "A" 16 2 |> ignore
    captureUsage "A" |> ignore
    let suspendedA = host.Suspensions |> List.last
    check "A suspends without scratch lease" (suspendedA.OutstandingScratchLeases = 0) "A retains promoted State and Continuation roots only."
    check "A initialization owner is replaced only after successful begin" (not (Object.ReferenceEquals(initialAReference, host.CurrentReference "A"))) "Begin produced a new retained owner."
    let beforeBusyOwner = host.CurrentReference "A"
    let beforeBusyToken = host.PendingToken "A"
    rejectBeforeHandler "busy admission" "MAILBOX_BUSY" (fun () -> host.Begin("A", 999L) |> ignore)
    check "busy admission leaves owner and token unchanged"
        (Object.ReferenceEquals(beforeBusyOwner, host.CurrentReference "A") && beforeBusyToken = host.PendingToken "A")
        "A's suspended retained owner and completion token were preserved."

    let foreignCount = host.HandlerInvocations
    let mutable foreignError: exn option = None
    let foreignThread =
        Thread(ThreadStart(fun () ->
            try host.Resume("A", tokenA1, 1L)
            with error -> foreignError <- Some error))
    foreignThread.IsBackground <- true
    foreignThread.Start()
    if not (foreignThread.Join(TimeSpan.FromSeconds 2.0)) then invalidOp "Foreign-thread rejection check timed out."
    let foreignCode =
        match foreignError with
        | Some(MailboxProtocolException(code, _)) -> code
        | Some error -> error.GetType().Name
        | None -> "no rejection"
    check "foreign-thread operation rejected" (foreignCode = "MAILBOX_FOREIGN_THREAD") $"observed {foreignCode}"
    check "foreign-thread operation rejected before handler" (host.HandlerInvocations = foreignCount) "foreign thread did not enter a handler."
    check "foreign-thread rejection preserves A token" (host.PendingToken "A" = Some tokenA1) "A's pending completion remains unchanged."

    let tokenB = host.Begin("B", 3L)
    usage "B" 16 2 |> ignore
    captureUsage "B" |> ignore
    let suspendedB = host.Suspensions |> List.last
    check "B suspends without scratch lease" (suspendedB.OutstandingScratchLeases = 0) "B retains promoted State and Continuation roots only."
    let beforeWrongOwner = host.CurrentReference "B"
    let beforeWrongToken = host.PendingToken "B"
    rejectBeforeHandler "wrong-mailbox token" "MAILBOX_WRONG_OWNER" (fun () -> host.Resume("B", tokenA1, 1L))
    check "wrong-mailbox rejection preserves B owner and token"
        (Object.ReferenceEquals(beforeWrongOwner, host.CurrentReference "B") && beforeWrongToken = host.PendingToken "B")
        "B's state and own token remain unchanged."

    host.Resume("B", tokenB, 1L)
    usage "B" 8 1 |> ignore
    captureUsage "B" |> ignore
    let beforeDuplicate = host.HandlerInvocations
    rejectBeforeHandler "duplicate completion" "MAILBOX_DUPLICATE_TOKEN" (fun () -> host.Resume("B", tokenB, 1L))
    check "duplicate completion does not change invocation count" (host.HandlerInvocations = beforeDuplicate) "Duplicate delivery was rejected before handler execution."

    host.PoisonScratch()
    check "A remains opaque through scratch reuse" (host.DecodeCalls = 0) "No retained graph was decoded between mailbox calls."
    let aBeforeFailure = host.CurrentReference "A"
    let aTokenBeforeFailure = host.PendingToken "A"
    let languageFailure = captureLanguageError (fun () -> host.Resume("A", tokenA1, 0L))
    let languageFailureCode = languageFailure |> Option.map (fun diagnostic -> diagnostic.Code) |> Option.defaultValue "no language failure"
    check "failed handler reports divide-by-zero" (languageFailureCode = "RUNTIME_DIVIDE_BY_ZERO") $"observed {languageFailureCode}"
    check "failed handler preserves old owner and completion token"
        (Object.ReferenceEquals(aBeforeFailure, host.CurrentReference "A") && aTokenBeforeFailure = host.PendingToken "A")
        "A can retry the same token because neither owner nor token was published on failure."
    check "failed handler leaves no scratch lease" (host.OutstandingScratchLeases = 0) "The failed call returned its scratch lease."
    host.Resume("A", tokenA1, 1L)
    usage "A" 8 1 |> ignore
    captureUsage "A" |> ignore

    let aAfterOverlap = readStateTotal (host.DecodeCurrent "A")
    let bAfterOverlapValue = readStateTotal (host.DecodeCurrent "B")
    check "independent overlap results" (aAfterOverlap = 17L && bAfterOverlapValue = 103L)
        $"expected A=17 and B=103, observed A={aAfterOverlap} and B={bAfterOverlapValue}"
    check "decoding starts after the overlap sequence" (host.DecodeCalls = 2) "Only final observations after A.resume decoded values."

    let tokenA2 = host.Begin("A", 5L)
    usage "A" 16 2 |> ignore
    captureUsage "A" |> ignore
    rejectBeforeHandler "stale completion" "MAILBOX_STALE_TOKEN" (fun () -> host.Resume("A", tokenA1, 2L))
    let oldOwner = host.CurrentReference "A"
    let oldToken = host.PendingToken "A"
    let mutable capacityCode = "not-tested"
    let mutable capacityRequiredBytes = 0L
    let mutable capacityRequiredNodes = 0L
    let mutable capacityAvailableBytes = 0
    if testNativeCapacityFailure then
        try
            host.Resume("A", tokenA2, 2L, retainedByteCapacity = 7)
            check "short retained capacity must fail" false "Expected NATIVE_RETAINED_CAPACITY."
        with :? NativeResourceLimitException as error ->
            capacityCode <- error.Code
            capacityRequiredBytes <- error.RequiredBytes
            capacityRequiredNodes <- error.RequiredNodes
            capacityAvailableBytes <- error.AvailableBytes
            check "retained capacity failure totals" (error.Code = "NATIVE_RETAINED_CAPACITY" && error.RequiredBytes = 8L && error.RequiredNodes = 1L && error.AvailableBytes = 7)
                $"observed {error.Code}: required {error.RequiredBytes}/{error.RequiredNodes}, available {error.AvailableBytes}/{error.AvailableNodes}"
        check "capacity failure preserves old owner and completion token"
            (Object.ReferenceEquals(oldOwner, host.CurrentReference "A") && oldToken = host.PendingToken "A")
            "No new result or token was published after retained promotion failed."
        check "capacity failure returns scratch lease" (host.OutstandingScratchLeases = 0) "The failed promotion returned the shared scratch lease."
    else
        capacityCode <- "not-applicable"
    host.Resume("A", tokenA2, 2L)
    usage "A" 8 1 |> ignore
    captureUsage "A" |> ignore
    let aFinal = readStateTotal (host.DecodeCurrent "A")
    check "independent truncating result" (aFinal = 19L) $"expected A=19 after 5/2 truncates to 2, observed {aFinal}"
    if backendName.StartsWith("native", StringComparison.Ordinal) then
        check "exact retained graph maxima" (maxRetainedBytes = 16 && maxRetainedNodes = 2)
            $"expected max 16 bytes/2 nodes, observed {maxRetainedBytes}/{maxRetainedNodes}"
    check "scratch leases balance" (host.OutstandingScratchLeases = 0 && host.ScratchLeaseAcquisitions = host.ScratchLeaseReturns)
        $"acquired={host.ScratchLeaseAcquisitions}; returned={host.ScratchLeaseReturns}; outstanding={host.OutstandingScratchLeases}"
    if backendName.StartsWith("native", StringComparison.Ordinal) then
        check "one shared scratch arena owner" (host.ScratchArenaOwnersCreated = 1) $"observed {host.ScratchArenaOwnersCreated} NativeScratchArena owner(s)"
        check "scratch arena capacity follows independent formula" (host.ScratchArenaByteCapacity = 24 && host.ScratchArenaNodeCapacity = 3)
            $"expected 24 bytes/3 nodes, observed {host.ScratchArenaByteCapacity}/{host.ScratchArenaNodeCapacity}"
        check "scratch was poisoned between mailboxes" (host.ScratchPoisonPasses = 1) $"observed {host.ScratchPoisonPasses} poison pass(es)"
        check "suspended calls carry no lease" (host.Suspensions |> List.forall (fun observation -> observation.OutstandingScratchLeases = 0)) "Every Begin returned at zero outstanding leases."
    host.Dispose()
    let disposedAfterCleanup = resources.DisposedOwners ()
    let createdAfterCleanup = resources.CreatedOwners ()
    check "cleanup releases every successful retained owner" (disposedAfterCleanup = createdAfterCleanup)
        $"created={createdAfterCleanup}; disposed={disposedAfterCleanup}"
    host.Dispose()
    check "double dispose is idempotent" (resources.DisposedOwners () = disposedAfterCleanup)
        "A second host disposal did not dispose any owner twice."
    let invocationsBeforeClosedCall = host.HandlerInvocations
    let mutable closedCallRejected = false
    try host.Begin("A", 0L) |> ignore
    with :? ObjectDisposedException -> closedCallRejected <- true
    check "disposed host rejects before handler" (closedCallRejected && host.HandlerInvocations = invocationsBeforeClosedCall)
        "Cleanup prevents subsequent handler invocation."
    { Backend = backendName
      Optimization = optimization
      AAfterOverlap = aAfterOverlap
      BAfterOverlap = bAfterOverlapValue
      AFinal = aFinal
      LanguageFailureCode = languageFailureCode
      CapacityFailureCode = capacityCode
      CapacityRequiredBytes = capacityRequiredBytes
      CapacityRequiredNodes = capacityRequiredNodes
      CapacityAvailableBytes = capacityAvailableBytes
      RetainedMaximumBytes = maxRetainedBytes
      RetainedMaximumNodes = maxRetainedNodes
      ScratchArenaOwnersCreated = host.ScratchArenaOwnersCreated
      ScratchArenaByteCapacity = host.ScratchArenaByteCapacity
      ScratchArenaNodeCapacity = host.ScratchArenaNodeCapacity
      ScratchLeaseAcquisitions = host.ScratchLeaseAcquisitions
      ScratchLeaseReturns = host.ScratchLeaseReturns
      ScratchPoisonPasses = host.ScratchPoisonPasses
      SuspensionLeaseCounts = host.Suspensions |> List.map (fun observation -> observation.OutstandingScratchLeases) |> List.toArray
      HandlerInvocations = host.HandlerInvocations
      DecodeCalls = host.DecodeCalls
      CreatedOwners = resources.CreatedOwners ()
      DisposedOwners = resources.DisposedOwners () }

let private addProperties (target: Dictionary<string, obj>) (properties: (string * obj) list) =
    for key, value in properties do target.Add(key, value)
    target

let private toJsonValue (run: ScenarioResult) =
    let value = Dictionary<string, obj>()
    addProperties value [
        "backend", box run.Backend
        "optimization", box run.Optimization
        "aAfterOverlap", box run.AAfterOverlap
        "bAfterOverlap", box run.BAfterOverlap
        "aFinal", box run.AFinal
        "languageFailureCode", box run.LanguageFailureCode
        "capacityFailureCode", box run.CapacityFailureCode
        "capacityRequiredBytes", box run.CapacityRequiredBytes
        "capacityRequiredNodes", box run.CapacityRequiredNodes
        "capacityAvailableBytes", box run.CapacityAvailableBytes
        "retainedMaximumBytes", box run.RetainedMaximumBytes
        "retainedMaximumNodes", box run.RetainedMaximumNodes
        "scratchArenaOwnersCreated", box run.ScratchArenaOwnersCreated
        "scratchArenaByteCapacity", box run.ScratchArenaByteCapacity
        "scratchArenaNodeCapacity", box run.ScratchArenaNodeCapacity
        "scratchLeaseAcquisitions", box run.ScratchLeaseAcquisitions
        "scratchLeaseReturns", box run.ScratchLeaseReturns
        "scratchPoisonPasses", box run.ScratchPoisonPasses
        "suspensionLeaseCounts", box run.SuspensionLeaseCounts
        "handlerInvocations", box run.HandlerInvocations
        "decodeCalls", box run.DecodeCalls
        "createdOwners", box run.CreatedOwners
        "disposedOwners", box run.DisposedOwners ]

[<EntryPoint>]
let main arguments =
    let getOption name defaultValue =
        match arguments |> Array.tryFindIndex ((=) name) with
        | None -> defaultValue
        | Some index when index + 1 < arguments.Length -> arguments[index + 1]
        | Some _ -> invalidArg name $"Option {name} requires a value."
    let outputPath = Path.GetFullPath(getOption "--output" (Path.Combine(".agentlang", "native-mailbox", "result.json")))
    let artifacts = Path.GetFullPath(getOption "--artifacts" (Path.Combine(".agentlang", "native-mailbox", "artifacts")))
    Directory.CreateDirectory(Path.GetDirectoryName outputPath) |> ignore
    Directory.CreateDirectory artifacts |> ignore
    let checks = ResizeArray<CheckResult>()
    let runs = ResizeArray<ScenarioResult>()
    let mutable failure = ""
    let mutable sourceHash = ""
    let mutable sharedProgramIdentity = false
    let sourceFile = Path.Combine(AppContext.BaseDirectory, "mailbox.flow")
    try
        let source = File.ReadAllText sourceFile
        sourceHash <- SHA256.HashData(Encoding.UTF8.GetBytes source) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        File.Copy(sourceFile, Path.Combine(artifacts, "mailbox.flow"), true)
        let handlers = compileHandlers source
        sharedProgramIdentity <- true
        let coreResources = interpreterBackend handlers
        runs.Add(runScenario checks "interpreter" "Core" coreResources false)
        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            let resources = nativeBackend handlers artifacts optimization
            let result = runScenario checks "native" (string optimization) resources true
            runs.Add result
        for result in runs do
            let parity =
                result.AAfterOverlap = 17L
                && result.BAfterOverlap = 103L
                && result.AFinal = 19L
                && result.LanguageFailureCode = "RUNTIME_DIVIDE_BY_ZERO"
            checks.Add({ Name = result.Backend + "/" + result.Optimization + "/interpreter-independent parity"; Passed = parity; Detail = "Expected A=17, B=103, final A=19, and a retryable divide-by-zero failure." })
            if not parity then invalidOp $"{result.Backend}/{result.Optimization} diverged from the independent outcome."
        let nativeRuns = runs |> Seq.filter (fun result -> result.Backend = "native") |> Seq.toList
        let modeParity =
            nativeRuns.Length = 2
            && nativeRuns |> List.forall (fun result -> result.AAfterOverlap = 17L && result.BAfterOverlap = 103L && result.AFinal = 19L)
        checks.Add({ Name = "O0/O2 native parity"; Passed = modeParity; Detail = "O0 and O2 produce the same independent numeric outcomes." })
        if not modeParity then invalidOp "Native O0 and O2 results differ."
    with error ->
        failure <- error.ToString()
    let allPassed = String.IsNullOrEmpty failure && checks.Count > 0 && (checks |> Seq.forall (fun result -> result.Passed))
    let checkValues =
        checks
        |> Seq.map (fun result ->
            let item = Dictionary<string, obj>()
            addProperties item [ "name", box result.Name; "passed", box result.Passed; "detail", box result.Detail ])
        |> Seq.toArray
    let report = Dictionary<string, obj>()
    addProperties report [
        "passed", box allPassed
        "experiment", box "Native handler mailbox suspension"
        "handlerSource", box "mailbox.flow"
        "handlerSourceSha256", box sourceHash
        "sameVerifiedProgramInstance", box sharedProgramIdentity
        "resultRootShape", box "State + Continuation; resume returns State"
        "independentExpected", box (Dictionary<string, obj>(dict [ "aAfterOverlap", box 17L; "bAfterOverlap", box 103L; "aFinal", box 19L ]))
        "checks", box checkValues
        "runs", box (runs |> Seq.map toJsonValue |> Seq.toArray)
        "failure", box failure ] |> ignore
    let options = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options), UTF8Encoding(false))
    let outcome = if allPassed then "passed" else "failed"
    Console.WriteLine($"Native mailbox experiment: {outcome}; report={outputPath}")
    if allPassed then 0 else 1
