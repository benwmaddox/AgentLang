namespace AgentLang.Llvm

open AgentLang

/// A static answer for one compiler-known arena cleanup boundary.
type internal ArenaRewindDecision =
    { AllowRewind: bool
      Reason: string }

/// Rewind answers for one detached body or one verified function.
type internal ArenaLifetimeBodyAnalysis =
    { ScopeExits: Map<SourceSiteId, ArenaRewindDecision>
      FunctionExit: ArenaRewindDecision }

[<RequireQualifiedAccess>]
module internal ArenaLifetime =
    type private AnalysisOwner =
        | FunctionOwner of WordId * int
        | BodyOwner of string

    type private ArenaRegion =
        { Owner: AnalysisOwner
          ScopePath: SourceSiteId list }

    type private AbstractState =
        { Stack: Set<ArenaRegion> list
          Locals: Map<LocalSlot, Set<ArenaRegion>>
          Escaped: Set<ArenaRegion> }

    let private emptyOrigins = Set.empty<ArenaRegion>

    let private unionOrigins (origins: seq<Set<ArenaRegion>>) =
        origins |> Seq.fold Set.union emptyOrigins

    let private liveOrigins state =
        seq {
            yield! state.Stack
            yield! state.Locals |> Map.toSeq |> Seq.map snd
        }
        |> unionOrigins

    let private pop (count: int) (stack: 'value list) : 'value list * 'value list =
        let prefixLength = stack.Length - count
        stack |> List.take prefixLength, stack |> List.skip prefixLength

    let private callEffects (call: IrResolvedCall) = not (Set.isEmpty call.ResolvedEffects)

    let private owningPrimitiveSet =
        set [ "add"; "subtract"; "multiply"; "divide"
              "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
              "equals"; "bool.and"; "bool.or"; "bool.not"
              "dup"; "drop"; "swap"; "string.concat"; "string.length" ]

    let private combineStates (left: AbstractState) (right: AbstractState) =
        let stack = List.map2 Set.union left.Stack right.Stack
        let localKeys locals = locals |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let keys = Set.union (localKeys left.Locals) (localKeys right.Locals)
        let locals =
            keys
            |> Seq.map (fun slot ->
                let leftOrigins = left.Locals.TryFind slot |> Option.defaultValue emptyOrigins
                let rightOrigins = right.Locals.TryFind slot |> Option.defaultValue emptyOrigins
                slot, Set.union leftOrigins rightOrigins)
            |> Map.ofSeq
        { Stack = stack
          Locals = locals
          Escaped = Set.union left.Escaped right.Escaped }

    let private regionFor (owner: AnalysisOwner) (scopePath: SourceSiteId list) =
        { Owner = owner
          ScopePath = scopePath }

    let private activeRegions (owner: AnalysisOwner) (scopePath: SourceSiteId list) =
        [ 0 .. scopePath.Length ]
        |> List.map (fun depth -> regionFor owner (scopePath |> List.take depth))
        |> Set.ofList

    let private relevantForBoundary (owner: AnalysisOwner) (scopeSite: SourceSiteId option) (regions: Set<ArenaRegion>) =
        regions
        |> Set.filter (fun region ->
            region.Owner = owner
            && (scopeSite |> Option.forall (fun site -> List.contains site region.ScopePath)))

    let private decision
        (owner: AnalysisOwner)
        (scopeSite: SourceSiteId option)
        (stackSurvivors: Set<ArenaRegion> list)
        (localSurvivors: Set<ArenaRegion> list)
        (escaped: Set<ArenaRegion>) =
        let stackOrigins = unionOrigins stackSurvivors |> relevantForBoundary owner scopeSite
        let localOrigins = unionOrigins localSurvivors |> relevantForBoundary owner scopeSite
        let escapedOrigins = relevantForBoundary owner scopeSite escaped
        if not (Set.isEmpty escapedOrigins) then
            { AllowRewind = false
              Reason = "An effectful or unmodeled operation may retain an allocation from this boundary." }
        elif not (Set.isEmpty stackOrigins) then
            { AllowRewind = false
              Reason = "An outgoing stack value may refer to an allocation made after this mark." }
        elif not (Set.isEmpty localOrigins) then
            { AllowRewind = false
              Reason = "A surviving local may refer to an allocation made after this mark." }
        else
            { AllowRewind = true
              Reason = "No outgoing value or retained effect can refer to an allocation made after this mark." }

    /// A duplicated scope-site key must represent every boundary it names. A
    /// false result at any occurrence therefore keeps the shared lookup false.
    let mergeDuplicateScopeDecision (left: ArenaRewindDecision) (right: ArenaRewindDecision) =
        let canRewind = left.AllowRewind && right.AllowRewind
        { AllowRewind = canRewind
          Reason =
            if canRewind then
                "Every scope sharing this source-site key proves its suffix dead."
            else
                "At least one scope sharing this source-site key cannot prove its suffix dead." }

    let private unknownReasonRegions (owner: AnalysisOwner) (scopePath: SourceSiteId list) (state: AbstractState) =
        Set.union (activeRegions owner scopePath) (liveOrigins state)

    let private addRetainedUncertainty (owner: AnalysisOwner) (scopePath: SourceSiteId list) (state: AbstractState) =
        let possible = unknownReasonRegions owner scopePath state
        { state with Escaped = Set.union state.Escaped possible }, possible

    let private makeOutputs (count: int) (origins: Set<ArenaRegion>) (prefix: Set<ArenaRegion> list) (state: AbstractState) =
        { state with Stack = prefix @ List.replicate count origins }

    let private analyze (owner: AnalysisOwner) (inputTypes: IrType list) (outputTypes: IrType list) (rootBlock: IrBlock) =
        let decisions = ResizeArray<SourceSiteId * ArenaRewindDecision>()

        let rec analyzeBlock (scopePath: SourceSiteId list) (block: IrBlock) (state: AbstractState) =
            block.Code |> List.fold (analyzeInstruction scopePath) state

        and analyzeInstruction (scopePath: SourceSiteId list) (state: AbstractState) (instruction: IrInstruction) =
            let currentRegions = activeRegions owner scopePath
            let currentRegion = regionFor owner scopePath
            let unmodeled () = analyzeUnmodeled scopePath state instruction
            match instruction.Operation with
            | IrOperation.Constant _ ->
                { state with Stack = state.Stack @ [ Set.singleton currentRegion ] }
            | IrOperation.StoreLocal slot ->
                let prefix, values = pop 1 state.Stack
                { state with Stack = prefix; Locals = Map.add slot values.Head state.Locals }
            | IrOperation.LoadLocal slot ->
                match state.Locals.TryFind slot with
                | Some origins -> { state with Stack = state.Stack @ [ origins ] }
                | None ->
                    let retainedState, possible = addRetainedUncertainty owner scopePath state
                    { retainedState with Stack = retainedState.Stack @ [ possible ] }
            | IrOperation.Scope innerBlock ->
                let innerPath = scopePath @ [ instruction.Site ]
                let innerState = analyzeBlock innerPath innerBlock state
                let restoredState = { innerState with Locals = state.Locals }
                let scopeDecision =
                    decision owner (Some instruction.Site) restoredState.Stack (restoredState.Locals |> Map.toList |> List.map snd) restoredState.Escaped
                decisions.Add(instruction.Site, scopeDecision)
                restoredState
            | IrOperation.If(thenBlock, elseBlock) ->
                let prefix, _ = pop 1 state.Stack
                let branchEntry = { state with Stack = prefix }
                let thenState = analyzeBlock scopePath thenBlock branchEntry
                let elseState = analyzeBlock scopePath elseBlock branchEntry
                combineStates thenState elseState
            | IrOperation.Call call ->
                let prefix, inputs = pop call.InputTypes.Length state.Stack
                let inputOrigins = unionOrigins inputs
                match call.ResolvedTarget with
                | PrimitiveTarget(PrimitiveId "drop") when call.OutputTypes.IsEmpty && not (callEffects call) ->
                    { state with Stack = prefix }
                | PrimitiveTarget(PrimitiveId "swap") when inputs.Length = 2 && not (callEffects call) ->
                    { state with Stack = prefix @ [ inputs[1]; inputs[0] ] }
                | PrimitiveTarget(PrimitiveId "dup") when inputs.Length = 1 && call.OutputTypes.Length = 2 && not (callEffects call) ->
                    let duplicate = Set.add currentRegion inputs.Head
                    { state with Stack = prefix @ [ inputs.Head; duplicate ] }
                | PrimitiveTarget(PrimitiveId operation) when not (owningPrimitiveSet.Contains operation) ->
                    let retainedState, possible = addRetainedUncertainty owner scopePath { state with Stack = prefix }
                    makeOutputs call.OutputTypes.Length (Set.union inputOrigins possible) prefix retainedState
                | GeneratedWordTarget _ ->
                    let retainedState, possible = addRetainedUncertainty owner scopePath { state with Stack = prefix }
                    makeOutputs call.OutputTypes.Length (Set.union inputOrigins possible) prefix retainedState
                | _ ->
                    let outputOrigins = Set.union inputOrigins currentRegions
                    let retained =
                        if callEffects call then Set.union state.Escaped currentRegions |> Set.union inputOrigins
                        else state.Escaped
                    { state with Stack = prefix @ List.replicate call.OutputTypes.Length outputOrigins
                                 Escaped = retained }
            | IrOperation.MakeRecord(call, _, validator) ->
                if callEffects call || validator.IsSome then unmodeled ()
                else
                    let prefix, _ = pop call.InputTypes.Length state.Stack
                    { state with Stack = prefix @ [ Set.singleton currentRegion ] }
            | IrOperation.GetRecordField(call, _, _) ->
                if callEffects call then unmodeled ()
                else
                    let prefix, inputs = pop 1 state.Stack
                    { state with Stack = prefix @ [ inputs.Head ] }
            | IrOperation.OptionNone _ ->
                // A sum is stored inline in the current arena region, even
                // when its active case has no payload.
                { state with Stack = state.Stack @ [ Set.singleton currentRegion ] }
            | IrOperation.OptionSome _
            | IrOperation.ResultOk _
            | IrOperation.ResultError _ ->
                let prefix, payloads = pop 1 state.Stack
                // Construction creates one inline sum owner and copies the
                // payload into it. Keep the payload's origins as well so this
                // proof remains conservative for nested/aliased values.
                let sumOrigins = Set.add currentRegion payloads.Head
                { state with Stack = prefix @ [ sumOrigins ] }
            | IrOperation.MatchOption(someLocal, someBlock, noneBlock) ->
                let prefix, sums = pop 1 state.Stack
                let sumOrigins = sums.Head
                // The selected payload is a view into the inline sum owner;
                // keeping the whole origin set alive also keeps any payload
                // storage represented by the sum alive.
                let someEntry =
                    { state with
                        Stack = prefix
                        Locals = Map.add someLocal sumOrigins state.Locals }
                let noneEntry = { state with Stack = prefix }
                let someExit =
                    analyzeBlock scopePath someBlock someEntry
                    |> fun exit -> { exit with Locals = Map.remove someLocal exit.Locals }
                let noneExit = analyzeBlock scopePath noneBlock noneEntry
                // Verified match arms have matching stack and outer-local
                // shapes after the case-local is removed.
                combineStates someExit noneExit
            | IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) ->
                let prefix, sums = pop 1 state.Stack
                let sumOrigins = sums.Head
                let okEntry =
                    { state with
                        Stack = prefix
                        Locals = Map.add okLocal sumOrigins state.Locals }
                let errorEntry =
                    { state with
                        Stack = prefix
                        Locals = Map.add errorLocal sumOrigins state.Locals }
                let okExit =
                    analyzeBlock scopePath okBlock okEntry
                    |> fun exit -> { exit with Locals = Map.remove okLocal exit.Locals }
                let errorExit =
                    analyzeBlock scopePath errorBlock errorEntry
                    |> fun exit -> { exit with Locals = Map.remove errorLocal exit.Locals }
                combineStates okExit errorExit
            | IrOperation.WrapScalar _
            | IrOperation.UnwrapScalar _
            | IrOperation.MakeEnumCase _ -> unmodeled ()
            | IrOperation.ListEmpty _
            | IrOperation.ListSingleton _
            | IrOperation.ListMap _
            | IrOperation.ListFilter _
            | IrOperation.ListEach _
            | IrOperation.ListFold _
            | IrOperation.MatchEnum _ -> unmodeled ()

        and analyzeUnmodeled (scopePath: SourceSiteId list) (state: AbstractState) (instruction: IrInstruction) =
            let retainedState, possible = addRetainedUncertainty owner scopePath state
            let unknownOutputOrigins = Set.union possible (liveOrigins state)
            let finishUnknown prefix inputs outputCount =
                let taint = Set.union unknownOutputOrigins (unionOrigins inputs)
                makeOutputs outputCount taint prefix retainedState
            match instruction.Operation with
            | IrOperation.ListEmpty _ | IrOperation.OptionNone _ ->
                finishUnknown state.Stack [] 1
            | IrOperation.ListSingleton _ | IrOperation.OptionSome _
            | IrOperation.ResultOk _ | IrOperation.ResultError _ ->
                let prefix, inputs = pop 1 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.ListMap _ | IrOperation.ListFilter _ | IrOperation.ListEach _ ->
                let prefix, inputs = pop 1 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.ListFold _ ->
                let prefix, inputs = pop 2 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.MatchOption(someLocal, someBlock, noneBlock) ->
                let prefix, _ = pop 1 state.Stack
                let someEntry = { retainedState with Stack = prefix; Locals = Map.add someLocal unknownOutputOrigins retainedState.Locals }
                let noneEntry = { retainedState with Stack = prefix }
                let someExit = analyzeBlock scopePath someBlock someEntry
                let noneExit = analyzeBlock scopePath noneBlock noneEntry
                // The verifier disallows case-local slots from shadowing an
                // outer slot, so removing this binding restores the outer map.
                let someExit = { someExit with Locals = Map.remove someLocal someExit.Locals }
                let merged = combineStates someExit noneExit
                { merged with Stack = merged.Stack |> List.map (Set.union unknownOutputOrigins)
                              Escaped = Set.union merged.Escaped possible }
            | IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) ->
                let prefix, _ = pop 1 state.Stack
                let okEntry = { retainedState with Stack = prefix; Locals = Map.add okLocal unknownOutputOrigins retainedState.Locals }
                let errorEntry = { retainedState with Stack = prefix; Locals = Map.add errorLocal unknownOutputOrigins retainedState.Locals }
                let okExit = analyzeBlock scopePath okBlock okEntry |> fun value -> { value with Locals = Map.remove okLocal value.Locals }
                let errorExit = analyzeBlock scopePath errorBlock errorEntry |> fun value -> { value with Locals = Map.remove errorLocal value.Locals }
                let merged = combineStates okExit errorExit
                { merged with Stack = merged.Stack |> List.map (Set.union unknownOutputOrigins)
                              Escaped = Set.union merged.Escaped possible }
            | IrOperation.MatchEnum(_, caseBlocks) ->
                let prefix, _ = pop 1 state.Stack
                let entry = { retainedState with Stack = prefix }
                let exits = caseBlocks |> List.map (snd >> fun block -> analyzeBlock scopePath block entry)
                let merged = exits |> List.reduce combineStates
                { merged with Stack = merged.Stack |> List.map (Set.union unknownOutputOrigins)
                              Escaped = Set.union merged.Escaped possible }
            | IrOperation.Call call ->
                let prefix, inputs = pop call.InputTypes.Length state.Stack
                finishUnknown prefix inputs call.OutputTypes.Length
            | IrOperation.MakeRecord(call, _, _) ->
                let prefix, inputs = pop call.InputTypes.Length state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.GetRecordField _ | IrOperation.WrapScalar _ | IrOperation.UnwrapScalar _ ->
                let prefix, inputs = pop 1 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.MakeEnumCase(call, _, _) ->
                finishUnknown state.Stack [] call.OutputTypes.Length
            | IrOperation.Constant _ | IrOperation.StoreLocal _ | IrOperation.LoadLocal _
            | IrOperation.Scope _ | IrOperation.If _ ->
                // These operations are modeled above; retain all visible roots
                // if a future verified shape reaches this fallback.
                { retainedState with Stack = state.Stack |> List.map (Set.union unknownOutputOrigins) }

        let initialState =
            { Stack = inputTypes |> List.map (fun _ -> emptyOrigins)
              Locals = Map.empty
              Escaped = emptyOrigins }
        let finalState = analyzeBlock [] rootBlock initialState
        let scopeExits: Map<SourceSiteId, ArenaRewindDecision> =
            decisions
            |> Seq.fold (fun found (site, result) ->
                match found.TryFind site with
                | None -> Map.add site result found
                | Some previous -> Map.add site (mergeDuplicateScopeDecision previous result) found) Map.empty
        let exitDecision = decision owner None finalState.Stack [] finalState.Escaped
        { ScopeExits = scopeExits
          FunctionExit = exitDecision }

    /// Analyze a compiler-verified detached body. Its inputs predate the body
    /// frame mark and therefore carry no allocation origin in this analysis.
    let analyzeBody (verifiedBody: VerifiedIrBody) =
        let body = VerifiedIrBody.inspect verifiedBody
        let owner = BodyOwner body.BodyName
        analyze owner body.BodyInputTypes body.BodyOutputTypes body.BodyBlock

    /// Analyze every function in a verified program. Each function receives a
    /// distinct owner key, including its revision, so source-site ordinals from
    /// separate functions can never alias.
    let analyzeProgram (verifiedProgram: VerifiedIrProgram) =
        VerifiedIrProgram.inspect verifiedProgram
        |> fun program ->
            program.FunctionsById
            |> Map.map (fun wordId functionValue ->
                analyze (FunctionOwner(wordId, functionValue.FunctionRevision))
                    functionValue.InputTypes functionValue.OutputTypes functionValue.FunctionBody)
