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

    let private templateHoleCount (parts: HtmlTemplatePart list) =
        parts |> List.sumBy (function HtmlTemplatePart.Text _ -> 0 | _ -> 1)

    let private owningPrimitiveSet =
        set [ "add"; "subtract"; "multiply"; "divide"; "int.remainder"
              "int.to-string"
              "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
              "equals"; "bool.and"; "bool.or"; "bool.not"
              "dup"; "drop"; "swap"; "string.concat"; "string.replace"; "string.length"; "string.byte-length"; "string.char-length"
              "string.join"; "string.html-escape"; "string.html-fragment?"
              "string.contains"; "string.starts-with"; "string.ends-with"
              "list.append"; "list.concat"
              "bytes.from-utf8"; "bytes.to-utf8"; "bytes.length"; "bytes.concat"; "bytes.slice"; "bytes.xor"; "bytes.equal-constant-time?"; "base64.encode"; "base64.decode"; "base64.url-encode"; "base64.url-decode"; "hex.encode"; "crypto.sha256"; "crypto.hmac-sha256"; "crypto.pbkdf2-sha256"; "string.split"; "string.index-of"; "string.slice"; "string.byte-at"; "string.byte-at-or"; "int.parse"; "string.to-lower-ascii"; "crypto.random-bytes"
              // Slice 2 (report 274).
              "crypto.hmac-sha1" ]

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

    /// Outer locals a nested block reassigns (Flow vars) gain the origins they
    /// may hold afterwards; every other outer local keeps its own origins.
    let private carryAssigned (block: IrBlock) (outer: Map<LocalSlot, Set<ArenaRegion>>) (inner: Map<LocalSlot, Set<ArenaRegion>>) =
        let assigned = IrLoops.assignedSlots block
        outer
        |> Map.map (fun slot origins ->
            if assigned.Contains slot then Set.union origins (inner.TryFind slot |> Option.defaultValue Set.empty)
            else origins)

    let private analyze (owner: AnalysisOwner) (inputTypes: IrType list) (outputTypes: IrType list) (rootBlock: IrBlock) =
        let decisions = ResizeArray<SourceSiteId * ArenaRewindDecision>()
        // States reaching break / continue in each enclosing loop body.
        let loopExits = System.Collections.Generic.Stack<ResizeArray<AbstractState> * ResizeArray<AbstractState>>()

        let rec analyzeBlock (scopePath: SourceSiteId list) (block: IrBlock) (state: AbstractState) =
            block.Code |> List.fold (analyzeInstruction scopePath) state

        and analyzeInstruction (scopePath: SourceSiteId list) (state: AbstractState) (instruction: IrInstruction) =
            let currentRegions = activeRegions owner scopePath
            let currentRegion = regionFor owner scopePath
            let unmodeled () = analyzeUnmodeled scopePath state instruction
            match instruction.Operation with
            | IrOperation.Constant _ ->
                { state with Stack = state.Stack @ [ Set.singleton currentRegion ] }
            | IrOperation.StoreLocal slot
            | IrOperation.AssignLocal slot ->
                let prefix, values = pop 1 state.Stack
                { state with Stack = prefix; Locals = Map.add slot values.Head state.Locals }
            | IrOperation.Break ->
                if loopExits.Count > 0 then (fst (loopExits.Peek())).Add state
                state
            | IrOperation.Continue ->
                if loopExits.Count > 0 then (snd (loopExits.Peek())).Add state
                state
            | IrOperation.ForRange(_, body) | IrOperation.ForList(_, _, _, body) ->
                analyzeLoop scopePath state instruction body
            | IrOperation.LoadLocal slot ->
                match state.Locals.TryFind slot with
                | Some origins -> { state with Stack = state.Stack @ [ origins ] }
                | None ->
                    let retainedState, possible = addRetainedUncertainty owner scopePath state
                    { retainedState with Stack = retainedState.Stack @ [ possible ] }
            | IrOperation.Scope innerBlock ->
                let innerPath = scopePath @ [ instruction.Site ]
                let innerState = analyzeBlock innerPath innerBlock state
                let restoredState = { innerState with Locals = carryAssigned innerBlock state.Locals innerState.Locals }
                let scopeDecision =
                    decision owner (Some instruction.Site) restoredState.Stack (restoredState.Locals |> Map.toList |> List.map snd) restoredState.Escaped
                decisions.Add(instruction.Site, scopeDecision)
                restoredState
            | IrOperation.Compact innerBlock ->
                // compact { }: the block's single result is relocated to the block's
                // mark, so it is exempt from the outgoing-value check; everything
                // else (prefix values, surviving locals, retained effects) must not
                // refer to the block's region.
                let innerPath = scopePath @ [ instruction.Site ]
                let innerState = analyzeBlock innerPath innerBlock state
                let restoredState = { innerState with Locals = carryAssigned innerBlock state.Locals innerState.Locals }
                let prefix, results = pop 1 restoredState.Stack
                let compactDecision =
                    decision owner (Some instruction.Site) prefix (restoredState.Locals |> Map.toList |> List.map snd) restoredState.Escaped
                decisions.Add(instruction.Site, compactDecision)
                // After relocation the result lives in the enclosing region.
                let insideBlock, outside =
                    results.Head |> Set.partition (fun region -> region.Owner = owner && List.contains instruction.Site region.ScopePath)
                let relocated = if Set.isEmpty insideBlock then outside else Set.add currentRegion outside
                { restoredState with Stack = prefix @ [ relocated ] }
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
            | IrOperation.ListEmpty _
            | IrOperation.OptionNone _ ->
                // Empty Lists and None sums allocate their inline root in the
                // current arena region, even when they have no child payload.
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
            | IrOperation.WrapScalar(call, _, validator) ->
                // Wrapping writes a new scalar in the current region; a pure
                // validator allocates only in the active regions.
                let validatorEffects = validator |> Option.exists callEffects
                if callEffects call || validatorEffects then unmodeled ()
                else
                    let prefix, inputs = pop 1 state.Stack
                    { state with Stack = prefix @ [ Set.union inputs.Head currentRegions ] }
            | IrOperation.UnwrapScalar(call, _) ->
                // Unwrapping projects the stored base value.
                if callEffects call then unmodeled ()
                else
                    let prefix, inputs = pop 1 state.Stack
                    { state with Stack = prefix @ [ inputs.Head ] }
            | IrOperation.MakeEnumCase(call, _, _) ->
                if callEffects call then unmodeled ()
                else { state with Stack = state.Stack @ [ Set.singleton currentRegion ] }
            | IrOperation.HtmlTemplate(_, parts) ->
                // A new String owner in the current region; every hole is copied
                // (escaped or as-is), never aliased.
                let prefix, _ = pop (templateHoleCount parts) state.Stack
                { state with Stack = prefix @ [ Set.singleton currentRegion ] }
            | IrOperation.MatchEnum(_, caseBlocks) ->
                let prefix, _ = pop 1 state.Stack
                let entry = { state with Stack = prefix }
                caseBlocks
                |> List.map (snd >> fun block -> analyzeBlock scopePath block entry)
                |> List.reduce combineStates
            | IrOperation.ListSingleton _ ->
                // A new list owner in the current region holding a copy of the item.
                let prefix, items = pop 1 state.Stack
                { state with Stack = prefix @ [ Set.add currentRegion items.Head ] }
            | IrOperation.ListOf(_, count) ->
                // A new list owner in the current region holding a copy of each item.
                let prefix, items = pop count state.Stack
                { state with Stack = prefix @ [ Set.add currentRegion (unionOrigins items) ] }
            | IrOperation.ListMap(call, _, _)
            | IrOperation.ListFilter(call, _)
            | IrOperation.ListEach(call, _)
            | IrOperation.ListFold(call, _, _)
            | IrOperation.ListMapWithContext(call, _, _, _) ->
                // Each element is a user-word call: model it like Call, joining the
                // list (and fold seed or map context) origins with the active regions.
                let inputCount = match instruction.Operation with IrOperation.ListFold _ | IrOperation.ListMapWithContext _ -> 2 | _ -> 1
                let prefix, inputs = pop inputCount state.Stack
                let inputOrigins = unionOrigins inputs
                let outputOrigins = Set.union inputOrigins currentRegions
                let retained =
                    if callEffects call then Set.union state.Escaped currentRegions |> Set.union inputOrigins
                    else state.Escaped
                { state with Stack = prefix @ [ outputOrigins ]; Escaped = retained }

        /// A loop body is its own region, reclaimed at every back edge. Vars the
        /// body assigns are relocated to the loop's mark (the enclosing region)
        /// first, so they are exempt from the rewind proof like a compact result.
        /// Other outer locals, the stack prefix and retained effects must not
        /// refer to the body region. Origins grow monotonically to a fixpoint.
        and analyzeLoop (scopePath: SourceSiteId list) (state: AbstractState) (instruction: IrInstruction) (body: IrBlock) =
            let site = instruction.Site
            let currentRegion = regionFor owner scopePath
            let loopPath = scopePath @ [ site ]
            let loopRegion = regionFor owner loopPath
            let inLoop region = region.Owner = owner && List.contains site region.ScopePath
            let relocate (origins: Set<ArenaRegion>) =
                if origins |> Set.exists inLoop then origins |> Set.filter (inLoop >> not) |> Set.add currentRegion
                else origins
            let prefix, inputs, bound =
                match instruction.Operation with
                | IrOperation.ForRange(indexLocal, _) ->
                    let prefix, inputs = pop 2 state.Stack
                    prefix, inputs, [ indexLocal, Set.singleton loopRegion ]
                | IrOperation.ForList(_, itemLocal, indexLocal, _) ->
                    let prefix, inputs = pop 1 state.Stack
                    // An item is a view into the list, which predates the loop mark.
                    prefix, inputs, (itemLocal, inputs.Head) :: (indexLocal |> Option.map (fun slot -> slot, Set.singleton loopRegion) |> Option.toList)
                | _ -> invalidOp "analyzeLoop requires a loop operation."
            ignore inputs
            let outer = state.Locals
            let assigned = IrLoops.assignedSlots body |> Set.filter outer.ContainsKey
            let mutable head = { state with Stack = prefix }
            let mutable latches: AbstractState list = []
            let mutable breaks: AbstractState list = []
            let mutable stable = false
            let mutable rounds = 0
            while not stable do
                rounds <- rounds + 1
                if rounds > 64 then invalidOp "Arena lifetime loop analysis did not reach a fixpoint."
                let entry = { head with Locals = bound |> List.fold (fun locals (slot, origins) -> Map.add slot origins locals) head.Locals }
                let breakStates = ResizeArray<AbstractState>()
                let continueStates = ResizeArray<AbstractState>()
                loopExits.Push((breakStates, continueStates))
                let exit =
                    try analyzeBlock loopPath body entry
                    finally loopExits.Pop() |> ignore
                latches <- exit :: List.ofSeq continueStates
                breaks <- List.ofSeq breakStates
                let nextLocals =
                    head.Locals
                    |> Map.map (fun slot origins ->
                        if assigned.Contains slot then
                            latches |> List.fold (fun found latch -> Set.union found (relocate (latch.Locals.TryFind slot |> Option.defaultValue emptyOrigins))) origins
                        else origins)
                let nextEscaped = latches |> List.fold (fun found latch -> Set.union found latch.Escaped) head.Escaped
                let next = { head with Locals = nextLocals; Escaped = nextEscaped }
                stable <- (next = head)
                head <- next
            let survivors =
                latches
                |> List.collect (fun latch ->
                    outer |> Map.toList |> List.filter (fun (slot, _) -> not (assigned.Contains slot)) |> List.map (fun (slot, _) -> latch.Locals.TryFind slot |> Option.defaultValue emptyOrigins))
            let escaped = latches |> List.fold (fun found latch -> Set.union found latch.Escaped) head.Escaped
            decisions.Add(site, decision owner (Some site) prefix survivors escaped)
            // Leaving the loop (header test or break) keeps the last iteration's
            // allocations; they now belong to the enclosing region.
            let exitStates = head :: breaks
            let exitLocals =
                outer
                |> Map.map (fun slot origins ->
                    exitStates |> List.fold (fun found exitState -> Set.union found (relocate (exitState.Locals.TryFind slot |> Option.defaultValue emptyOrigins))) origins)
            let exitEscaped = exitStates |> List.fold (fun found exitState -> Set.union found (relocate exitState.Escaped)) emptyOrigins
            { Stack = prefix; Locals = exitLocals; Escaped = exitEscaped }

        and analyzeUnmodeled (scopePath: SourceSiteId list) (state: AbstractState) (instruction: IrInstruction) =
            let retainedState, possible = addRetainedUncertainty owner scopePath state
            let unknownOutputOrigins = Set.union possible (liveOrigins state)
            let finishUnknown prefix inputs outputCount =
                let taint = Set.union unknownOutputOrigins (unionOrigins inputs)
                makeOutputs outputCount taint prefix retainedState
            match instruction.Operation with
            | IrOperation.ListEmpty _ ->
                invalidOp "Arena lifetime analysis routed a modeled ListEmpty through the unmodeled fallback."
            | IrOperation.OptionNone _ ->
                finishUnknown state.Stack [] 1
            | IrOperation.ListSingleton _ | IrOperation.OptionSome _
            | IrOperation.ResultOk _ | IrOperation.ResultError _ ->
                let prefix, inputs = pop 1 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.ListOf(_, count) ->
                let prefix, inputs = pop count state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.ListMap _ | IrOperation.ListFilter _ | IrOperation.ListEach _ ->
                let prefix, inputs = pop 1 state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.ListFold _ | IrOperation.ListMapWithContext _ ->
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
            | IrOperation.HtmlTemplate(_, parts) ->
                let prefix, inputs = pop (templateHoleCount parts) state.Stack
                finishUnknown prefix inputs 1
            | IrOperation.Constant _ | IrOperation.StoreLocal _ | IrOperation.LoadLocal _
            | IrOperation.Scope _ | IrOperation.Compact _ | IrOperation.If _
            | IrOperation.AssignLocal _ | IrOperation.Break | IrOperation.Continue
            | IrOperation.ForRange _ | IrOperation.ForList _ ->
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
