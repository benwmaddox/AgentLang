namespace AgentLang

open System

/// Runtime-independent identity for a source word. IDs are never resolved by name.
[<Struct>]
type WordId = WordId of string

/// A nominal type key is scoped to one immutable compiled program snapshot.
[<Struct>]
type ProgramTypeKey = ProgramTypeKey of int

/// Stable identity of a source expression within its owner (or standalone body).
[<Struct>]
type SourceSiteId = SourceSiteId of WordId option * int

/// Compiler-assigned slot for a named source local.
[<Struct>]
type LocalSlot = LocalSlot of int

/// A standard primitive identity is independent of its per-call type instantiation.
[<Struct>]
type PrimitiveId = PrimitiveId of string

/// Closed, source-independent effect vocabulary used by executable IR.
[<RequireQualifiedAccess>]
type IrEffect =
    | FileRead
    | FileWrite
    | DatabaseRead
    | DatabaseWrite
    | NetworkRead
    | NetworkWrite
    | ProcessExecute
    | ClockRead
    | RandomRead
    | ConsoleWrite

module IrEffects =
    let format = function
        | IrEffect.FileRead -> "fs.read"
        | IrEffect.FileWrite -> "fs.write"
        | IrEffect.DatabaseRead -> "db.read"
        | IrEffect.DatabaseWrite -> "db.write"
        | IrEffect.NetworkRead -> "network.read"
        | IrEffect.NetworkWrite -> "network.write"
        | IrEffect.ProcessExecute -> "process.execute"
        | IrEffect.ClockRead -> "clock.read"
        | IrEffect.RandomRead -> "random.read"
        | IrEffect.ConsoleWrite -> "console.write"

    let ofName = function
        | "fs.read" -> Some IrEffect.FileRead
        | "fs.write" -> Some IrEffect.FileWrite
        | "db.read" -> Some IrEffect.DatabaseRead
        | "db.write" -> Some IrEffect.DatabaseWrite
        | "network.read" -> Some IrEffect.NetworkRead
        | "network.write" -> Some IrEffect.NetworkWrite
        | "process.execute" -> Some IrEffect.ProcessExecute
        | "clock.read" -> Some IrEffect.ClockRead
        | "random.read" -> Some IrEffect.RandomRead
        | "console.write" -> Some IrEffect.ConsoleWrite
        | _ -> None

    let names effects = effects |> Set.toList |> List.map format |> List.sort

/// Closed executable types. Source type names and inference variables are absent.
type IrType =
    | IrInt
    | IrFloat
    | IrBool
    | IrString
    | IrUnit
    | IrList of IrType
    | IrOption of IrType
    | IrResult of IrType * IrType
    | IrNominal of ProgramTypeKey

type IrCallTarget =
    | UserWordTarget of WordId * int
    | PrimitiveTarget of PrimitiveId
    | GeneratedWordTarget of WordId * int

/// A call-site specialization. In particular, multiple calls to one polymorphic
/// primitive carry independent concrete signatures here.
type IrResolvedCall =
    { ResolvedTarget: IrCallTarget
      ResolvedName: string
      InputTypes: IrType list
      OutputTypes: IrType list
      ResolvedDeclaredEffects: Set<IrEffect>
      ResolvedEffects: Set<IrEffect> }

type IrGeneratedOperation =
    | MakeRecordOperation of ProgramTypeKey
    | GetRecordFieldOperation of ProgramTypeKey * int
    | WrapScalarOperation of ProgramTypeKey
    | UnwrapScalarOperation of ProgramTypeKey

type IrRecordField =
    { FieldIndex: int
      FieldName: string
      FieldType: IrType }

type IrRecordDefinitionData =
    { TypeKey: ProgramTypeKey
      TypeName: string
      RecordFields: IrRecordField list }

type IrScalarDefinitionData =
    { TypeKey: ProgramTypeKey
      TypeName: string
      BaseType: IrType
      ValidatorCall: IrResolvedCall option }

type IrNominalDefinition =
    | IrRecordDefinition of IrRecordDefinitionData
    | IrScalarDefinition of IrScalarDefinitionData

type IrGeneratedTarget =
    { TargetId: WordId
      TargetRevision: int
      TargetName: string
      Operation: IrGeneratedOperation
      InputTypes: IrType list
      OutputTypes: IrType list
      TargetDeclaredEffects: Set<IrEffect>
      TargetEffects: Set<IrEffect>
      SourceSite: SourceSiteId option }

type IrShape =
    { StackTypes: IrType list
      LocalTypes: Map<LocalSlot, IrType> }

[<RequireQualifiedAccess>]
type IrOperation =
    | Constant of Literal * IrType
    | Call of IrResolvedCall
    | ListEmpty of IrType
    | ListSingleton of IrType
    | OptionNone of IrType
    | OptionSome of IrType
    | ResultOk of IrType * IrType
    | ResultError of IrType * IrType
    | ListMap of IrResolvedCall * IrType * IrType
    | ListFilter of IrResolvedCall * IrType
    | ListEach of IrResolvedCall * IrType
    | StoreLocal of LocalSlot
    | LoadLocal of LocalSlot
    | If of IrBlock * IrBlock
    | MatchOption of SomeLocal: LocalSlot * SomeBlock: IrBlock * NoneBlock: IrBlock
    | MatchResult of OkLocal: LocalSlot * ErrorLocal: LocalSlot * OkBlock: IrBlock * ErrorBlock: IrBlock
    | MakeRecord of IrResolvedCall * ProgramTypeKey
    | GetRecordField of IrResolvedCall * ProgramTypeKey * int
    | WrapScalar of IrResolvedCall * ProgramTypeKey * IrResolvedCall option
    | UnwrapScalar of IrResolvedCall * ProgramTypeKey

and IrInstruction =
    { Site: SourceSiteId
      Operation: IrOperation }

and IrBlock =
    { EntryShape: IrShape
      ExitShape: IrShape
      Code: IrInstruction list }

type IrSourceSite =
    { SiteOwner: WordId option
      SiteSpan: SourceSpan
      SourceKind: string }

type IrCoverageObligations =
    { CoveredSites: Set<SourceSiteId>
      BranchOutcomes: Map<SourceSiteId, string list> }

type IrFunction =
    { FunctionId: WordId
      FunctionRevision: int
      FunctionName: string
      InputTypes: IrType list
      OutputTypes: IrType list
      FunctionDeclaredEffects: Set<IrEffect>
      FunctionInferredEffects: Set<IrEffect>
      LocalNames: Map<LocalSlot, string>
      FunctionBody: IrBlock }

type IrProgram =
    { NominalTypesByKey: Map<ProgramTypeKey, IrNominalDefinition>
      FunctionsById: Map<WordId, IrFunction>
      GeneratedTargetsById: Map<WordId, IrGeneratedTarget>
      SourceMap: Map<SourceSiteId, IrSourceSite>
      CoverageByWord: Map<WordId, IrCoverageObligations> }

type private IrCallGraphNode =
    | IrFunctionNode of WordId
    | IrGeneratedNode of WordId

/// Generic patterns belong to the compiler's primitive catalog, never to an
/// executable program or a concrete call site.
type IrTypePattern =
    | PatternInt
    | PatternFloat
    | PatternBool
    | PatternString
    | PatternUnit
    | PatternList of IrTypePattern
    | PatternOption of IrTypePattern
    | PatternResult of IrTypePattern * IrTypePattern
    | PatternVariable of int

type IrPrimitiveContract =
    { Primitive: PrimitiveId
      InputPatterns: IrTypePattern list
      OutputPatterns: IrTypePattern list
      PrimitiveEffects: Set<IrEffect> }

type IrPrimitiveCatalog = Map<PrimitiveId, IrPrimitiveContract>

/// Immutable verified-program handle. The constructor is assembly-internal so
/// host backends outside AgentLang.Core cannot forge an executable snapshot.
/// Production code must obtain instances from IrVerifier.verify.
[<Sealed>]
type VerifiedIrProgram internal (program: IrProgram) =
    member internal _.Program = program

module VerifiedIrProgram =
    /// Read-only inspection does not grant execution authority; backends must
    /// still accept a VerifiedIrProgram rather than a raw IrProgram.
    let inspect (verified: VerifiedIrProgram) = verified.Program

module IrTypes =
    let rec format = function
        | IrInt -> "Int"
        | IrFloat -> "Float"
        | IrBool -> "Bool"
        | IrString -> "String"
        | IrUnit -> "Unit"
        | IrList item -> $"List<{format item}>"
        | IrOption item -> $"Option<{format item}>"
        | IrResult(ok, error) -> $"Result<{format ok}, {format error}>"
        | IrNominal(ProgramTypeKey key) -> $"@type{key}"

    let rec hasNominal key = function
        | IrNominal candidate -> candidate = key
        | IrList item | IrOption item -> hasNominal key item
        | IrResult(ok, error) -> hasNominal key ok || hasNominal key error
        | _ -> false

module IrVerifier =
    let private failure code message expected actual =
        Diagnostics.raiseError code message None None expected actual

    let private valueType = function
        | LInt _ -> IrInt
        | LFloat _ -> IrFloat
        | LBool _ -> IrBool
        | LString _ -> IrString
        | LUnit -> IrUnit

    let private sourceSpan (program: IrProgram) site =
        program.SourceMap
        |> Map.tryFind site
        |> Option.map (fun source -> Some source.SiteSpan)
        |> Option.defaultValue None

    let private verifyIrType (program: IrProgram) owner site ty =
        let rec verifyType = function
            | IrInt | IrFloat | IrBool | IrString | IrUnit -> ()
            | IrList item | IrOption item -> verifyType item
            | IrResult(ok, error) -> verifyType ok; verifyType error
            | IrNominal key when program.NominalTypesByKey.ContainsKey key -> ()
            | IrNominal(ProgramTypeKey key) ->
                Diagnostics.raiseError "IR_UNKNOWN_TYPE_KEY" "Executable IR contains a nominal key absent from its snapshot type table." owner (site |> Option.bind (sourceSpan program)) [ "known ProgramTypeKey" ] [ string key ]
        verifyType ty

    let private verifyCatalog (catalog: IrPrimitiveCatalog) =
        let rec patternVariables pattern =
            match pattern with
            | PatternVariable variable -> Set.singleton variable
            | PatternList item | PatternOption item -> patternVariables item
            | PatternResult(ok, error) -> Set.union (patternVariables ok) (patternVariables error)
            | _ -> Set.empty
        for KeyValue(id, contract) in catalog do
            if id <> contract.Primitive then
                failure "IR_PRIMITIVE_ID_MISMATCH" "Primitive catalog key differs from its contract identity." [ sprintf "%A" id ] [ sprintf "%A" contract.Primitive ]
            let inputVariables = contract.InputPatterns |> List.map patternVariables |> Set.unionMany
            let outputVariables = contract.OutputPatterns |> List.map patternVariables |> Set.unionMany
            if outputVariables |> Set.exists (fun variable -> variable < 0 || not (inputVariables.Contains variable)) then
                failure "IR_PRIMITIVE_CONTRACT_VARIABLE" "Primitive output type variables must be nonnegative and constrained by an input." (inputVariables |> Set.toList |> List.map string) (outputVariables |> Set.toList |> List.map string)
            if inputVariables |> Set.exists (fun variable -> variable < 0) then
                failure "IR_PRIMITIVE_CONTRACT_VARIABLE" "Primitive type variables must be nonnegative." [] (inputVariables |> Set.toList |> List.map string)

    let private verifyPattern (contract: IrPrimitiveContract) (call: IrResolvedCall) =
        let substitutions = System.Collections.Generic.Dictionary<int, IrType>()
        let rec unify pattern actual =
            match pattern, actual with
            | PatternInt, IrInt | PatternFloat, IrFloat | PatternBool, IrBool
            | PatternString, IrString | PatternUnit, IrUnit -> true
            | PatternVariable variable, actual ->
                match substitutions.TryGetValue variable with
                | true, previous -> previous = actual
                | _ -> substitutions[variable] <- actual; true
            | PatternList pattern, IrList actual
            | PatternOption pattern, IrOption actual -> unify pattern actual
            | PatternResult(patternOk, patternError), IrResult(actualOk, actualError) ->
                unify patternOk actualOk && unify patternError actualError
            | _ -> false
        contract.InputPatterns.Length = call.InputTypes.Length
        && contract.OutputPatterns.Length = call.OutputTypes.Length
        && (List.zip contract.InputPatterns call.InputTypes |> List.forall (fun (pattern, actual) -> unify pattern actual))
        && (List.zip contract.OutputPatterns call.OutputTypes |> List.forall (fun (pattern, actual) -> unify pattern actual))

    let private verifyCall (program: IrProgram) (catalog: IrPrimitiveCatalog) sourceOwner site (call: IrResolvedCall) =
        let failCall code expected actual =
            Diagnostics.raiseError code $"Resolved call '{call.ResolvedName}' is inconsistent with its target." (Some sourceOwner) (sourceSpan program site) expected actual
        if String.IsNullOrWhiteSpace call.ResolvedName then
            failCall "IR_CALL_NAME_MISSING" [ "nonempty display name" ] [ call.ResolvedName ]
        call.InputTypes @ call.OutputTypes |> List.iter (verifyIrType program (Some sourceOwner) (Some site))
        if call.ResolvedDeclaredEffects <> call.ResolvedEffects then
            failCall "IR_CALL_EFFECT_MISMATCH" (IrEffects.names call.ResolvedDeclaredEffects) (IrEffects.names call.ResolvedEffects)
        match call.ResolvedTarget with
        | UserWordTarget(id, revision) ->
            match program.FunctionsById.TryFind id with
            | None -> failCall "IR_UNKNOWN_WORD_ID" [ "known user WordId" ] [ sprintf "%A" id ]
            | Some target ->
                if target.FunctionRevision <> revision then failCall "IR_WORD_REVISION_MISMATCH" [ string target.FunctionRevision ] [ string revision ]
                if target.InputTypes <> call.InputTypes || target.OutputTypes <> call.OutputTypes then
                    failCall "IR_CALL_SIGNATURE_MISMATCH" (target.InputTypes |> List.map IrTypes.format) (call.InputTypes |> List.map IrTypes.format)
                if target.FunctionDeclaredEffects <> call.ResolvedDeclaredEffects || target.FunctionDeclaredEffects <> call.ResolvedEffects then
                    failCall "IR_CALL_EFFECT_MISMATCH" (IrEffects.names target.FunctionDeclaredEffects) (IrEffects.names call.ResolvedEffects)
        | PrimitiveTarget id ->
            match catalog.TryFind id with
            | None -> failCall "IR_UNKNOWN_PRIMITIVE_ID" [ "catalog primitive" ] [ sprintf "%A" id ]
            | Some contract ->
                if not (verifyPattern contract call) then
                    failCall "IR_CALL_SIGNATURE_MISMATCH" [ "valid concrete primitive instantiation" ] (call.InputTypes |> List.map IrTypes.format)
                if contract.PrimitiveEffects <> call.ResolvedDeclaredEffects || contract.PrimitiveEffects <> call.ResolvedEffects then
                    failCall "IR_CALL_EFFECT_MISMATCH" (IrEffects.names contract.PrimitiveEffects) (IrEffects.names call.ResolvedEffects)
        | GeneratedWordTarget(id, revision) ->
            match program.GeneratedTargetsById.TryFind id with
            | None -> failCall "IR_UNKNOWN_GENERATED_ID" [ "known generated WordId" ] [ sprintf "%A" id ]
            | Some target ->
                if target.TargetRevision <> revision then failCall "IR_WORD_REVISION_MISMATCH" [ string target.TargetRevision ] [ string revision ]
                if target.InputTypes <> call.InputTypes || target.OutputTypes <> call.OutputTypes then
                    failCall "IR_CALL_SIGNATURE_MISMATCH" (target.InputTypes |> List.map IrTypes.format) (call.InputTypes |> List.map IrTypes.format)
                if target.TargetDeclaredEffects <> call.ResolvedDeclaredEffects || target.TargetEffects <> call.ResolvedEffects then
                    failCall "IR_CALL_EFFECT_MISMATCH" (IrEffects.names target.TargetEffects) (IrEffects.names call.ResolvedEffects)

    let private verifyNominalTypes (program: IrProgram) (catalog: IrPrimitiveCatalog) =
        let names = ResizeArray<string>()
        for KeyValue(key, definition) in program.NominalTypesByKey do
            match definition with
            | IrRecordDefinition record ->
                if record.TypeKey <> key then failure "IR_TYPE_KEY_MISMATCH" "Record type table key does not match its definition." [ string (let (ProgramTypeKey value) = key in value) ] [ string (let (ProgramTypeKey value) = record.TypeKey in value) ]
                if key < ProgramTypeKey 0 then failure "IR_TYPE_KEY_INVALID" "ProgramTypeKey must be nonnegative." [ "nonnegative key" ] [ sprintf "%A" key ]
                if String.IsNullOrWhiteSpace record.TypeName then failure "IR_TYPE_NAME_MISSING" "Record type name must be nonempty." [ "nonempty type name" ] [ record.TypeName ]
                names.Add record.TypeName
                let indexes = record.RecordFields |> List.map (fun field -> field.FieldIndex)
                if indexes <> [ 0 .. record.RecordFields.Length - 1 ] then failure "IR_RECORD_FIELD_LAYOUT" $"Record '{record.TypeName}' has a noncanonical field layout." [ "contiguous declared field indexes" ] (indexes |> List.map string)
                let fieldNames = record.RecordFields |> List.map (fun field -> field.FieldName)
                if fieldNames |> List.exists String.IsNullOrWhiteSpace || (Set.ofList fieldNames).Count <> fieldNames.Length then
                    failure "IR_RECORD_FIELD_NAMES" $"Record '{record.TypeName}' has an empty or duplicate field name." [ "unique nonempty field names" ] fieldNames
                for field in record.RecordFields do verifyIrType program (Some record.TypeName) None field.FieldType
            | IrScalarDefinition scalar ->
                if scalar.TypeKey <> key then failure "IR_TYPE_KEY_MISMATCH" "Scalar type table key does not match its definition." [] []
                if key < ProgramTypeKey 0 then failure "IR_TYPE_KEY_INVALID" "ProgramTypeKey must be nonnegative." [ "nonnegative key" ] [ sprintf "%A" key ]
                if String.IsNullOrWhiteSpace scalar.TypeName then failure "IR_TYPE_NAME_MISSING" "Scalar type name must be nonempty." [ "nonempty type name" ] [ scalar.TypeName ]
                names.Add scalar.TypeName
                if IrTypes.hasNominal key scalar.BaseType then
                    failure "IR_SCALAR_CYCLE" $"Scalar '{scalar.TypeName}' cannot use itself as its base type." [ "base type independent of this scalar" ] [ IrTypes.format scalar.BaseType ]
                match scalar.BaseType with
                | IrInt | IrFloat | IrBool | IrString -> ()
                | _ -> failure "IR_SCALAR_BASE_UNSUPPORTED" $"Scalar '{scalar.TypeName}' has a base type unsupported by source validation." [ "Int | Float | Bool | String" ] [ IrTypes.format scalar.BaseType ]
                verifyIrType program (Some scalar.TypeName) None scalar.BaseType
                match scalar.ValidatorCall with
                | Some validator ->
                    if validator.InputTypes <> [ scalar.BaseType ] || validator.OutputTypes <> [ IrBool ] then
                        failure "IR_SCALAR_VALIDATOR_SIGNATURE" $"Scalar '{scalar.TypeName}' validator has a malformed resolved signature." [ IrTypes.format scalar.BaseType + " -> Bool" ] [ String.concat " " (validator.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (validator.OutputTypes |> List.map IrTypes.format) ]
                    if not (Set.isEmpty validator.ResolvedEffects) then
                        failure "IR_SCALAR_VALIDATOR_EFFECT" $"Scalar '{scalar.TypeName}' validator must be pure." [] (IrEffects.names validator.ResolvedEffects)
                    verifyCall program catalog scalar.TypeName (SourceSiteId(None, 0)) validator
                | None -> ()
        if (Set.ofSeq names).Count <> names.Count then
            failure "IR_TYPE_NAME_COLLISION" "Nominal type names must be unique within one executable snapshot." [ "unique record and scalar names" ] (List.ofSeq names)

    let private verifyGeneratedTarget (program: IrProgram) (catalog: IrPrimitiveCatalog) (id: WordId) (target: IrGeneratedTarget) =
        if target.TargetId <> id then failure "IR_GENERATED_ID_MISMATCH" "Generated target map key differs from its identity." [ sprintf "%A" target.TargetId ] [ sprintf "%A" id ]
        let (WordId idText) = id
        if String.IsNullOrWhiteSpace idText || String.IsNullOrWhiteSpace target.TargetName then
            failure "IR_GENERATED_ID_INVALID" "Generated target identity and display name must be nonempty." [ "nonempty identity and name" ] [ idText; target.TargetName ]
        if target.TargetRevision < 0 then failure "IR_INVALID_REVISION" "Generated target revision cannot be negative." [ "nonnegative revision" ] [ string target.TargetRevision ]
        let sourceSite =
            match target.SourceSite with
            | None -> failure "IR_GENERATED_SOURCE_SITE_MISSING" "Generated operations must retain the type or field declaration source site." [ "mapped declaration source site" ] [ target.TargetName ]
            | Some site -> site
        match program.SourceMap.TryFind sourceSite with
        | Some source when source.SiteOwner = Some id -> ()
        | Some source -> Diagnostics.raiseError "IR_SOURCE_OWNER_MISMATCH" "Generated operation source map belongs to a different identity." (Some target.TargetName) (Some source.SiteSpan) [ sprintf "%A" id ] [ sprintf "%A" source.SiteOwner ]
        | None -> failure "IR_SOURCE_SITE_MISSING" "Generated operation source site is absent from the program source map." [ sprintf "%A" sourceSite ] []
        match target.Operation with
        | MakeRecordOperation key ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) when target.InputTypes = (record.RecordFields |> List.map (fun field -> field.FieldType)) && target.OutputTypes = [ IrNominal key ] -> ()
            | _ -> failure "IR_GENERATED_SIGNATURE_MISMATCH" $"Generated record constructor '{target.TargetName}' disagrees with its type layout." [ "record fields -> record" ] (target.InputTypes |> List.map IrTypes.format)
        | GetRecordFieldOperation(key, fieldIndex) ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) when fieldIndex >= 0 && fieldIndex < record.RecordFields.Length && target.InputTypes = [ IrNominal key ] && target.OutputTypes = [ record.RecordFields[fieldIndex].FieldType ] -> ()
            | _ -> failure "IR_GENERATED_SIGNATURE_MISMATCH" $"Generated record accessor '{target.TargetName}' disagrees with its field layout." [ "record -> field type" ] (target.OutputTypes |> List.map IrTypes.format)
        | WrapScalarOperation key ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrScalarDefinition scalar) when target.InputTypes = [ scalar.BaseType ] && target.OutputTypes = [ IrNominal key ] ->
                match scalar.ValidatorCall with
                | Some validator when validator.ResolvedEffects <> target.TargetEffects -> failure "IR_GENERATED_EFFECT_MISMATCH" "Scalar constructor effects do not include its validator effects." (IrEffects.names validator.ResolvedEffects) (IrEffects.names target.TargetEffects)
                | _ -> ()
            | _ -> failure "IR_GENERATED_SIGNATURE_MISMATCH" $"Generated scalar constructor '{target.TargetName}' disagrees with its base type." [ "base type -> nominal type" ] (target.InputTypes |> List.map IrTypes.format)
        | UnwrapScalarOperation key ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrScalarDefinition scalar) when target.InputTypes = [ IrNominal key ] && target.OutputTypes = [ scalar.BaseType ] -> ()
            | _ -> failure "IR_GENERATED_SIGNATURE_MISMATCH" $"Generated scalar accessor '{target.TargetName}' disagrees with its base type." [ "nominal type -> base type" ] (target.OutputTypes |> List.map IrTypes.format)
        let call =
            { ResolvedTarget = GeneratedWordTarget(id, target.TargetRevision)
              ResolvedName = target.TargetName
              InputTypes = target.InputTypes
              OutputTypes = target.OutputTypes
              ResolvedDeclaredEffects = target.TargetDeclaredEffects
              ResolvedEffects = target.TargetEffects }
        verifyCall program catalog target.TargetName sourceSite call

    let private verifyBlock (program: IrProgram) (catalog: IrPrimitiveCatalog) (owner: IrFunction) (block: IrBlock) =
        let verifyShape site shape =
            shape.StackTypes |> List.iter (verifyIrType program (Some owner.FunctionName) (Some site))
            for KeyValue(slot, ty) in shape.LocalTypes do
                verifyIrType program (Some owner.FunctionName) (Some site) ty
                if not (owner.LocalNames.ContainsKey slot) then
                    Diagnostics.raiseError "IR_UNKNOWN_LOCAL_SLOT" "Block shape contains a local slot absent from the function layout." (Some owner.FunctionName) (sourceSpan program site) [] [ sprintf "%A" slot ]
        let rec verifyNested (instructions: IrInstruction list) shape =
            match instructions with
            | first :: _ -> verifyShape first.Site shape
            | [] -> ()
            match instructions with
            | [] -> shape, Set.empty
            | instruction :: rest ->
                let sourceOwner = Some owner.FunctionName
                match program.SourceMap.TryFind instruction.Site with
                | None -> Diagnostics.raiseError "IR_SOURCE_SITE_MISSING" "IR instruction has no source-map entry." sourceOwner None [ sprintf "%A" instruction.Site ] []
                | Some source when source.SiteOwner <> Some owner.FunctionId ->
                    Diagnostics.raiseError "IR_SOURCE_OWNER_MISMATCH" "IR instruction source map belongs to a different word." sourceOwner (Some source.SiteSpan) [ sprintf "%A" owner.FunctionId ] [ sprintf "%A" source.SiteOwner ]
                | _ -> ()
                let at = sourceSpan program instruction.Site
                let operationError code message expected actual = Diagnostics.raiseError code message sourceOwner at expected actual
                let pop count =
                    if shape.StackTypes.Length < count then operationError "IR_STACK_UNDERFLOW" "IR operation input shape underflows its block stack." [ string count ] [ string shape.StackTypes.Length ]
                    shape.StackTypes |> List.take (shape.StackTypes.Length - count), shape.StackTypes |> List.skip (shape.StackTypes.Length - count)
                let requireGeneratedOperation expected call =
                    match call.ResolvedTarget with
                    | GeneratedWordTarget(id, revision) ->
                        match program.GeneratedTargetsById.TryFind id with
                        | Some target when target.TargetRevision = revision && target.Operation = expected -> ()
                        | _ -> operationError "IR_GENERATED_OPERATION_MISMATCH" "Explicit generated operation does not match its linked generated target." [ sprintf "%A" expected ] [ call.ResolvedName ]
                    | _ -> operationError "IR_GENERATED_TARGET_REQUIRED" "Record and scalar operations must resolve to a generated target." [ sprintf "%A" expected ] [ call.ResolvedName ]
                let finish next effects =
                    let finalShape, tailEffects = verifyNested rest next
                    finalShape, Set.union effects tailEffects
                let nextShape, effects =
                    match instruction.Operation with
                    | IrOperation.Constant(literal, ty) ->
                        verifyIrType program sourceOwner (Some instruction.Site) ty
                        if valueType literal <> ty then operationError "IR_CONSTANT_TYPE_MISMATCH" "Constant type does not match its literal." [ IrTypes.format (valueType literal) ] [ IrTypes.format ty ]
                        { shape with StackTypes = shape.StackTypes @ [ ty ] }, Set.empty
                    | IrOperation.Call call ->
                        match call.ResolvedTarget with
                        | GeneratedWordTarget _ -> operationError "IR_GENERATED_OPERATION_REQUIRED" "Generated word calls must lower to their explicit semantic operation." [] [ call.ResolvedName ]
                        | _ -> ()
                        verifyCall program catalog owner.FunctionName instruction.Site call
                        let prefix, actuals = pop call.InputTypes.Length
                        if actuals <> call.InputTypes then operationError "IR_CALL_STACK_MISMATCH" "Resolved call signature does not match its actual stack inputs." (call.InputTypes |> List.map IrTypes.format) (actuals |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ call.OutputTypes }, call.ResolvedEffects
                    | IrOperation.ListEmpty item ->
                        verifyIrType program sourceOwner (Some instruction.Site) item
                        { shape with StackTypes = shape.StackTypes @ [ IrList item ] }, Set.empty
                    | IrOperation.ListSingleton item ->
                        let prefix, actual = pop 1
                        if actual <> [ item ] then operationError "IR_CONTAINER_PAYLOAD" "List singleton payload type does not match its closed element type." [ IrTypes.format item ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrList item ] }, Set.empty
                    | IrOperation.OptionNone item ->
                        verifyIrType program sourceOwner (Some instruction.Site) item
                        { shape with StackTypes = shape.StackTypes @ [ IrOption item ] }, Set.empty
                    | IrOperation.OptionSome item ->
                        let prefix, actual = pop 1
                        if actual <> [ item ] then operationError "IR_CONTAINER_PAYLOAD" "Option Some payload type does not match its closed element type." [ IrTypes.format item ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrOption item ] }, Set.empty
                    | IrOperation.ResultOk(ok, error) ->
                        verifyIrType program sourceOwner (Some instruction.Site) ok
                        verifyIrType program sourceOwner (Some instruction.Site) error
                        let prefix, actual = pop 1
                        if actual <> [ ok ] then operationError "IR_CONTAINER_PAYLOAD" "Result Ok payload type does not match its explicit success type." [ IrTypes.format ok ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrResult(ok, error) ] }, Set.empty
                    | IrOperation.ResultError(ok, error) ->
                        verifyIrType program sourceOwner (Some instruction.Site) ok
                        verifyIrType program sourceOwner (Some instruction.Site) error
                        let prefix, actual = pop 1
                        if actual <> [ error ] then operationError "IR_CONTAINER_PAYLOAD" "Result Error payload type does not match its explicit error type." [ IrTypes.format error ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrResult(ok, error) ] }, Set.empty
                    | IrOperation.ListMap(callback, item, output) ->
                        verifyIrType program sourceOwner (Some instruction.Site) item
                        verifyIrType program sourceOwner (Some instruction.Site) output
                        verifyCall program catalog owner.FunctionName instruction.Site callback
                        if callback.InputTypes <> [ item ] || callback.OutputTypes <> [ output ] then operationError "IR_CALLBACK_SIGNATURE_MISMATCH" "Map callback must have concrete signature T -> U." [ IrTypes.format item + " -> " + IrTypes.format output ] [ String.concat " " (callback.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (callback.OutputTypes |> List.map IrTypes.format) ]
                        let prefix, actual = pop 1
                        if actual <> [ IrList item ] then operationError "IR_LIST_STACK_MISMATCH" "Map input stack must end in List<T>." [ IrTypes.format (IrList item) ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrList output ] }, callback.ResolvedEffects
                    | IrOperation.ListFilter(callback, item) ->
                        verifyIrType program sourceOwner (Some instruction.Site) item
                        verifyCall program catalog owner.FunctionName instruction.Site callback
                        if callback.InputTypes <> [ item ] || callback.OutputTypes <> [ IrBool ] then operationError "IR_CALLBACK_SIGNATURE_MISMATCH" "Filter callback must have concrete signature T -> Bool." [ IrTypes.format item + " -> Bool" ] [ String.concat " " (callback.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (callback.OutputTypes |> List.map IrTypes.format) ]
                        let prefix, actual = pop 1
                        if actual <> [ IrList item ] then operationError "IR_LIST_STACK_MISMATCH" "Filter input stack must end in List<T>." [ IrTypes.format (IrList item) ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrList item ] }, callback.ResolvedEffects
                    | IrOperation.ListEach(callback, item) ->
                        verifyIrType program sourceOwner (Some instruction.Site) item
                        verifyCall program catalog owner.FunctionName instruction.Site callback
                        if callback.InputTypes <> [ item ] || callback.OutputTypes <> [ IrUnit ] then operationError "IR_CALLBACK_SIGNATURE_MISMATCH" "Each callback must have concrete signature T -> Unit." [ IrTypes.format item + " -> Unit" ] [ String.concat " " (callback.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (callback.OutputTypes |> List.map IrTypes.format) ]
                        let prefix, actual = pop 1
                        if actual <> [ IrList item ] then operationError "IR_LIST_STACK_MISMATCH" "Each input stack must end in List<T>." [ IrTypes.format (IrList item) ] (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ [ IrUnit ] }, callback.ResolvedEffects
                    | IrOperation.StoreLocal slot ->
                        if not (owner.LocalNames.ContainsKey slot) then operationError "IR_UNKNOWN_LOCAL_SLOT" "IR local store references a slot absent from the function layout." [] [ sprintf "%A" slot ]
                        let prefix, actual = pop 1
                        let locals = Map.add slot actual.Head shape.LocalTypes
                        { StackTypes = prefix; LocalTypes = locals }, Set.empty
                    | IrOperation.LoadLocal slot ->
                        if not (owner.LocalNames.ContainsKey slot) then operationError "IR_UNKNOWN_LOCAL_SLOT" "IR local load references a slot absent from the function layout." [] [ sprintf "%A" slot ]
                        match shape.LocalTypes.TryFind slot with
                        | None -> operationError "IR_UNKNOWN_LOCAL_SLOT" "IR local load references a slot absent from this block shape." [] [ sprintf "%A" slot ]
                        | Some ty -> { shape with StackTypes = shape.StackTypes @ [ ty ] }, Set.empty
                    | IrOperation.If(thenBlock, elseBlock) ->
                        let prefix, condition = pop 1
                        if condition <> [ IrBool ] then operationError "IR_IF_CONDITION_TYPE" "IR if requires Bool on top of stack." [ "Bool" ] (condition |> List.map IrTypes.format)
                        let expectedEntry = { shape with StackTypes = prefix }
                        let thenShape, thenEffects = verifyBlockNested program catalog owner instruction.Site expectedEntry thenBlock
                        let elseShape, elseEffects = verifyBlockNested program catalog owner instruction.Site expectedEntry elseBlock
                        if thenShape <> elseShape then operationError "IR_BRANCH_JOIN_MISMATCH" "IR if arms do not have the same output stack and local shape." (thenShape.StackTypes |> List.map IrTypes.format) (elseShape.StackTypes |> List.map IrTypes.format)
                        thenShape, Set.union thenEffects elseEffects
                    | IrOperation.MatchOption(someLocal, someBlock, noneBlock) ->
                        if not (owner.LocalNames.ContainsKey someLocal) then operationError "IR_UNKNOWN_LOCAL_SLOT" "Option case local is absent from the function layout." [] [ sprintf "%A" someLocal ]
                        let prefix, input = pop 1
                        let item = match input with | [ IrOption value ] -> value | _ -> operationError "IR_OPTION_MATCH_TYPE" "Option match requires Option<T>." [ "Option<T>" ] (input |> List.map IrTypes.format)
                        if shape.LocalTypes.ContainsKey someLocal then operationError "IR_CASE_LOCAL_SHADOW" "Option match payload slot shadows an outer local." [] [ sprintf "%A" someLocal ]
                        let someEntry = { StackTypes = prefix; LocalTypes = Map.add someLocal item shape.LocalTypes }
                        let noneEntry = { StackTypes = prefix; LocalTypes = shape.LocalTypes }
                        let someShape, someEffects = verifyBlockNested program catalog owner instruction.Site someEntry someBlock
                        let noneShape, noneEffects = verifyBlockNested program catalog owner instruction.Site noneEntry noneBlock
                        if noneShape.LocalTypes.ContainsKey someLocal then operationError "IR_CASE_LOCAL_ESCAPE" "Option payload slot escaped into the None arm." [] [ sprintf "%A" someLocal ]
                        let someJoin = { someShape with LocalTypes = Map.remove someLocal someShape.LocalTypes }
                        if someJoin <> noneShape then operationError "IR_BRANCH_JOIN_MISMATCH" "Option match arms do not have the same output stack and outer-local shape." (someJoin.StackTypes |> List.map IrTypes.format) (noneShape.StackTypes |> List.map IrTypes.format)
                        someJoin, Set.union someEffects noneEffects
                    | IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) ->
                        if not (owner.LocalNames.ContainsKey okLocal && owner.LocalNames.ContainsKey errorLocal) then operationError "IR_UNKNOWN_LOCAL_SLOT" "Result case local is absent from the function layout." [] [ sprintf "%A" (okLocal, errorLocal) ]
                        let prefix, input = pop 1
                        let okType, errorType = match input with | [ IrResult(ok, error) ] -> ok, error | _ -> operationError "IR_RESULT_MATCH_TYPE" "Result match requires Result<T, E>." [ "Result<T, E>" ] (input |> List.map IrTypes.format)
                        if shape.LocalTypes.ContainsKey okLocal || shape.LocalTypes.ContainsKey errorLocal then operationError "IR_CASE_LOCAL_SHADOW" "Result match payload slot shadows an outer local." [] [ sprintf "%A" (okLocal, errorLocal) ]
                        let okEntry = { StackTypes = prefix; LocalTypes = Map.add okLocal okType shape.LocalTypes }
                        let errorEntry = { StackTypes = prefix; LocalTypes = Map.add errorLocal errorType shape.LocalTypes }
                        let okShape, okEffects = verifyBlockNested program catalog owner instruction.Site okEntry okBlock
                        let errorShape, errorEffects = verifyBlockNested program catalog owner instruction.Site errorEntry errorBlock
                        if okShape.LocalTypes.ContainsKey errorLocal || errorShape.LocalTypes.ContainsKey okLocal then operationError "IR_CASE_LOCAL_ESCAPE" "Result case payload escaped into the other case arm." [] [ sprintf "%A" (okLocal, errorLocal) ]
                        let okJoin = { okShape with LocalTypes = Map.remove okLocal okShape.LocalTypes }
                        let errorJoin = { errorShape with LocalTypes = Map.remove errorLocal errorShape.LocalTypes }
                        if okJoin <> errorJoin then operationError "IR_BRANCH_JOIN_MISMATCH" "Result match arms do not have the same output stack and outer-local shape." (okJoin.StackTypes |> List.map IrTypes.format) (errorJoin.StackTypes |> List.map IrTypes.format)
                        okJoin, Set.union okEffects errorEffects
                    | IrOperation.MakeRecord(call, key) ->
                        verifyCall program catalog owner.FunctionName instruction.Site call
                        requireGeneratedOperation (MakeRecordOperation key) call
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrRecordDefinition record) when call.InputTypes = (record.RecordFields |> List.map (fun field -> field.FieldType)) && call.OutputTypes = [ IrNominal key ] -> ()
                        | _ -> operationError "IR_RECORD_CONSTRUCTION_TYPE" "Record construction does not match the snapshot type layout." [ "record fields -> nominal record" ] (call.OutputTypes |> List.map IrTypes.format)
                        let prefix, actual = pop call.InputTypes.Length
                        if actual <> call.InputTypes then operationError "IR_CALL_STACK_MISMATCH" "Record constructor inputs do not match the stack." (call.InputTypes |> List.map IrTypes.format) (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ call.OutputTypes }, call.ResolvedEffects
                    | IrOperation.GetRecordField(call, key, index) ->
                        verifyCall program catalog owner.FunctionName instruction.Site call
                        requireGeneratedOperation (GetRecordFieldOperation(key, index)) call
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrRecordDefinition record) when index >= 0 && index < record.RecordFields.Length && call.InputTypes = [ IrNominal key ] && call.OutputTypes = [ record.RecordFields[index].FieldType ] -> ()
                        | _ -> operationError "IR_RECORD_ACCESS_TYPE" "Record accessor does not match its snapshot type or field index." [ "record -> selected field" ] (call.OutputTypes |> List.map IrTypes.format)
                        let prefix, actual = pop 1
                        if actual <> call.InputTypes then operationError "IR_CALL_STACK_MISMATCH" "Record accessor input does not match the stack." (call.InputTypes |> List.map IrTypes.format) (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ call.OutputTypes }, call.ResolvedEffects
                    | IrOperation.WrapScalar(call, key, validator) ->
                        verifyCall program catalog owner.FunctionName instruction.Site call
                        requireGeneratedOperation (WrapScalarOperation key) call
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrScalarDefinition scalar) when call.InputTypes = [ scalar.BaseType ] && call.OutputTypes = [ IrNominal key ] ->
                            if validator <> scalar.ValidatorCall then operationError "IR_SCALAR_VALIDATOR_MISMATCH" "Scalar wrapping validator differs from the immutable type table." [] []
                            match validator with
                            | Some checkedCall ->
                                verifyCall program catalog owner.FunctionName instruction.Site checkedCall
                                if checkedCall.InputTypes <> [ scalar.BaseType ] || checkedCall.OutputTypes <> [ IrBool ] || not (Set.isEmpty checkedCall.ResolvedEffects) then
                                    operationError "IR_SCALAR_VALIDATOR_INVALID" "Scalar validator must be pure and have signature BaseType -> Bool." [ IrTypes.format scalar.BaseType + " -> Bool" ] [ String.concat " " (checkedCall.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (checkedCall.OutputTypes |> List.map IrTypes.format) ]
                            | None -> ()
                        | _ -> operationError "IR_SCALAR_WRAP_TYPE" "Scalar wrapping does not match its base and nominal types." [ "base -> nominal" ] (call.OutputTypes |> List.map IrTypes.format)
                        let prefix, actual = pop 1
                        if actual <> call.InputTypes then operationError "IR_CALL_STACK_MISMATCH" "Scalar constructor input does not match the stack." (call.InputTypes |> List.map IrTypes.format) (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ call.OutputTypes }, Set.union call.ResolvedEffects (validator |> Option.map (fun value -> value.ResolvedEffects) |> Option.defaultValue Set.empty)
                    | IrOperation.UnwrapScalar(call, key) ->
                        verifyCall program catalog owner.FunctionName instruction.Site call
                        requireGeneratedOperation (UnwrapScalarOperation key) call
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrScalarDefinition scalar) when call.InputTypes = [ IrNominal key ] && call.OutputTypes = [ scalar.BaseType ] -> ()
                        | _ -> operationError "IR_SCALAR_UNWRAP_TYPE" "Scalar unwrapping does not match its nominal and base types." [ "nominal -> base" ] (call.OutputTypes |> List.map IrTypes.format)
                        let prefix, actual = pop 1
                        if actual <> call.InputTypes then operationError "IR_CALL_STACK_MISMATCH" "Scalar accessor input does not match the stack." (call.InputTypes |> List.map IrTypes.format) (actual |> List.map IrTypes.format)
                        { shape with StackTypes = prefix @ call.OutputTypes }, call.ResolvedEffects
                finish nextShape effects
        and verifyBlockNested (program: IrProgram) (catalog: IrPrimitiveCatalog) (owner: IrFunction) (parentSite: SourceSiteId) expectedEntry (block: IrBlock) =
            if block.EntryShape <> expectedEntry then
                Diagnostics.raiseError "IR_BLOCK_ENTRY_MISMATCH" "Structured IR block entry shape does not match its control-flow edge." (Some owner.FunctionName) (sourceSpan program parentSite) (expectedEntry.StackTypes |> List.map IrTypes.format) (block.EntryShape.StackTypes |> List.map IrTypes.format)
            let actualExit, effects = verifyNested block.Code block.EntryShape
            if actualExit <> block.ExitShape then
                Diagnostics.raiseError "IR_BLOCK_EXIT_MISMATCH" "IR block exit annotation disagrees with its verified instructions." (Some owner.FunctionName) (sourceSpan program parentSite) (actualExit.StackTypes |> List.map IrTypes.format) (block.ExitShape.StackTypes |> List.map IrTypes.format)
            actualExit, effects
        let actualExit, effects = verifyNested block.Code block.EntryShape
        if actualExit <> block.ExitShape then
            Diagnostics.raiseError "IR_BLOCK_EXIT_MISMATCH" "Function block exit annotation disagrees with its verified instructions." (Some owner.FunctionName) None (block.ExitShape.StackTypes |> List.map IrTypes.format) (actualExit.StackTypes |> List.map IrTypes.format)
        if effects <> owner.FunctionInferredEffects then
            Diagnostics.raiseError "IR_FUNCTION_EFFECT_MISMATCH" $"Function '{owner.FunctionName}' inferred effects disagree with its body." (Some owner.FunctionName) None (IrEffects.names owner.FunctionInferredEffects) (IrEffects.names effects)
        if not (Set.isSubset effects owner.FunctionDeclaredEffects) then
            Diagnostics.raiseError "IR_UNDECLARED_EFFECT" $"Function '{owner.FunctionName}' uses effects absent from its declaration." (Some owner.FunctionName) None (IrEffects.names owner.FunctionDeclaredEffects) (IrEffects.names effects)

    let private expectedCoverage (block: IrBlock) =
        let rec collectBlock (instructions: IrInstruction list) (sites: Set<SourceSiteId>) (branches: Map<SourceSiteId, string list>) =
            instructions
            |> List.fold (fun (sites, branches) instruction ->
                let sites = Set.add instruction.Site sites
                match instruction.Operation with
                | IrOperation.If(thenBlock, elseBlock) ->
                    let branches = Map.add instruction.Site [ "true"; "false" ] branches
                    let leftSites, leftBranches = collectBlock thenBlock.Code sites branches
                    collectBlock elseBlock.Code leftSites leftBranches
                | IrOperation.MatchOption(_, someBlock, noneBlock) ->
                    let branches = Map.add instruction.Site [ "some"; "none" ] branches
                    let leftSites, leftBranches = collectBlock someBlock.Code sites branches
                    collectBlock noneBlock.Code leftSites leftBranches
                | IrOperation.MatchResult(_, _, okBlock, errorBlock) ->
                    let branches = Map.add instruction.Site [ "ok"; "error" ] branches
                    let leftSites, leftBranches = collectBlock okBlock.Code sites branches
                    collectBlock errorBlock.Code leftSites leftBranches
                | IrOperation.ListMap _ | IrOperation.ListEach _ -> Map.add instruction.Site [ "empty"; "nonempty" ] branches |> fun branches -> sites, branches
                | IrOperation.ListFilter _ -> Map.add instruction.Site [ "empty"; "nonempty"; "keep"; "drop" ] branches |> fun branches -> sites, branches
                | _ -> sites, branches) (sites, branches)
        collectBlock block.Code Set.empty Map.empty

    let private instructionsInBlock (block: IrBlock) =
        let rec collect instructions =
            instructions
            |> List.collect (fun instruction ->
                let nested =
                    match instruction.Operation with
                    | IrOperation.If(thenBlock, elseBlock) -> collect thenBlock.Code @ collect elseBlock.Code
                    | IrOperation.MatchOption(_, someBlock, noneBlock) -> collect someBlock.Code @ collect noneBlock.Code
                    | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collect okBlock.Code @ collect errorBlock.Code
                    | _ -> []
                instruction :: nested)
        collect block.Code

    let private callTargetsInBlock (block: IrBlock) =
        let rec collect instructions =
            instructions
            |> List.collect (fun instruction ->
                let direct =
                    match instruction.Operation with
                    | IrOperation.Call call -> [ call.ResolvedTarget ]
                    | IrOperation.ListMap(call, _, _) | IrOperation.ListFilter(call, _) | IrOperation.ListEach(call, _) -> [ call.ResolvedTarget ]
                    | IrOperation.MakeRecord(call, _) | IrOperation.GetRecordField(call, _, _) | IrOperation.UnwrapScalar(call, _) -> [ call.ResolvedTarget ]
                    | IrOperation.WrapScalar(call, _, validator) -> call.ResolvedTarget :: (validator |> Option.map (fun value -> value.ResolvedTarget) |> Option.toList)
                    | _ -> []
                let nested =
                    match instruction.Operation with
                    | IrOperation.If(thenBlock, elseBlock) -> collect thenBlock.Code @ collect elseBlock.Code
                    | IrOperation.MatchOption(_, someBlock, noneBlock) -> collect someBlock.Code @ collect noneBlock.Code
                    | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collect okBlock.Code @ collect errorBlock.Code
                    | _ -> []
                direct @ nested)
        collect block.Code

    let private targetNode = function
        | UserWordTarget(id, _) -> Some(IrFunctionNode id)
        | GeneratedWordTarget(id, _) -> Some(IrGeneratedNode id)
        | PrimitiveTarget _ -> None

    let private verifyAcyclicCallGraph (program: IrProgram) =
        let functionEdges =
            program.FunctionsById
            |> Map.toList
            |> List.map (fun (id, fn) ->
                IrFunctionNode id,
                (callTargetsInBlock fn.FunctionBody |> List.choose targetNode |> Set.ofList))
        let generatedEdges =
            program.GeneratedTargetsById
            |> Map.toList
            |> List.map (fun (id, target) ->
                let validatorTarget =
                    match target.Operation with
                    | WrapScalarOperation key ->
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrScalarDefinition scalar) -> scalar.ValidatorCall |> Option.bind (fun validator -> targetNode validator.ResolvedTarget) |> Option.toList
                        | _ -> []
                    | _ -> []
                IrGeneratedNode id, (validatorTarget |> Set.ofList))
        let graph = Map.ofList (functionEdges @ generatedEdges)
        let colors = System.Collections.Generic.Dictionary<IrCallGraphNode, int>()
        let path = ResizeArray<IrCallGraphNode>()
        let label = function
            | IrFunctionNode(WordId id) -> "word:" + id
            | IrGeneratedNode(WordId id) -> "generated:" + id
        let rec visit node =
            match colors.TryGetValue node with
            | true, 2 -> ()
            | true, 1 ->
                let currentPath = path |> Seq.map label |> String.concat " -> "
                failure "IR_RECURSIVE_CALL_GRAPH" "Executable user and generated-word references must be acyclic." [ "acyclic call graph" ] [ currentPath + " -> " + label node ]
            | _ ->
                colors[node] <- 1
                path.Add node
                for dependency in (graph.TryFind node |> Option.defaultValue Set.empty) do visit dependency
                path.RemoveAt(path.Count - 1)
                colors[node] <- 2
        for node in graph |> Map.toSeq |> Seq.map fst do visit node

    let private verifyFunction (program: IrProgram) (catalog: IrPrimitiveCatalog) (id: WordId) (fn: IrFunction) =
        if fn.FunctionId <> id then failure "IR_WORD_ID_MISMATCH" "Function map key differs from its stable identity." [ sprintf "%A" fn.FunctionId ] [ sprintf "%A" id ]
        let (WordId idText) = id
        if String.IsNullOrWhiteSpace idText || String.IsNullOrWhiteSpace fn.FunctionName then
            failure "IR_WORD_ID_INVALID" "Function identity and display name must be nonempty." [ "nonempty identity and name" ] [ idText; fn.FunctionName ]
        if fn.FunctionRevision < 0 then failure "IR_INVALID_REVISION" "Function revision cannot be negative." [ "nonnegative revision" ] [ string fn.FunctionRevision ]
        fn.InputTypes @ fn.OutputTypes |> List.iter (verifyIrType program (Some fn.FunctionName) None)
        for KeyValue(slot, name) in fn.LocalNames do
            let (LocalSlot value) = slot
            if value < 0 || String.IsNullOrWhiteSpace name then
                failure "IR_LOCAL_LAYOUT_INVALID" "Function local slots must be nonnegative and have nonempty names." [ "valid local slot/name" ] [ sprintf "%A=%s" slot name ]
        if not (Map.isEmpty fn.FunctionBody.EntryShape.LocalTypes) then
            failure "IR_FUNCTION_ENTRY_LOCALS" $"Function '{fn.FunctionName}' must begin with no initialized locals." [] (fn.FunctionBody.EntryShape.LocalTypes |> Map.toList |> List.map (fun (slot, _) -> sprintf "%A" slot))
        if fn.FunctionBody.EntryShape.StackTypes <> fn.InputTypes || fn.FunctionBody.ExitShape.StackTypes <> fn.OutputTypes then
            failure "IR_FUNCTION_SIGNATURE_MISMATCH" $"Function '{fn.FunctionName}' body stack shape differs from its signature." (fn.InputTypes |> List.map IrTypes.format) (fn.FunctionBody.ExitShape.StackTypes |> List.map IrTypes.format)
        let sites, branches = expectedCoverage fn.FunctionBody
        let coverage = program.CoverageByWord.TryFind id |> Option.defaultValue { CoveredSites = Set.empty; BranchOutcomes = Map.empty }
        if coverage.CoveredSites <> sites || coverage.BranchOutcomes <> branches then
            failure "IR_COVERAGE_MAP_MISMATCH" $"Function '{fn.FunctionName}' coverage obligations do not match its source operations." [ string sites.Count; string branches.Count ] [ string coverage.CoveredSites.Count; string coverage.BranchOutcomes.Count ]
        verifyBlock program catalog fn fn.FunctionBody

    let verify (catalog: IrPrimitiveCatalog) (program: IrProgram) =
        verifyCatalog catalog
        verifyNominalTypes program catalog
        let duplicateIds = Set.intersect (program.FunctionsById |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (program.GeneratedTargetsById |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        if not (Set.isEmpty duplicateIds) then
            failure "IR_WORD_ID_COLLISION" "User and generated targets share stable WordIds." [] (duplicateIds |> Set.toList |> List.map (sprintf "%A"))
        for KeyValue(id, target) in program.GeneratedTargetsById do verifyGeneratedTarget program catalog id target
        for KeyValue(id, fn) in program.FunctionsById do verifyFunction program catalog id fn
        let functionIds = program.FunctionsById |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let coverageIds = program.CoverageByWord |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if functionIds <> coverageIds then
            failure "IR_COVERAGE_OWNER_MISMATCH" "Coverage metadata must contain exactly one entry for each user function." (functionIds |> Set.toList |> List.map (sprintf "%A")) (coverageIds |> Set.toList |> List.map (sprintf "%A"))
        let duplicateSites =
            program.FunctionsById
            |> Map.toList
            |> List.collect (fun (_, fn) -> instructionsInBlock fn.FunctionBody |> List.map (fun instruction -> instruction.Site))
            |> List.countBy id
            |> List.choose (fun (site, count) -> if count > 1 then Some(site, count) else None)
        if not (List.isEmpty duplicateSites) then
            failure "IR_DUPLICATE_SOURCE_SITE" "Each source expression must have one executable instruction identity across all nested blocks." [ "unique source-site ID per executable instruction" ] (duplicateSites |> List.map (fun (site, count) -> sprintf "%A (%d instructions)" site count))
        verifyAcyclicCallGraph program
        for KeyValue(site, source) in program.SourceMap do
            let (SourceSiteId(siteOwner, ordinal)) = site
            if ordinal < 0 then failure "IR_SOURCE_SITE_INVALID" "Source-site ordinals must be nonnegative." [ "nonnegative ordinal" ] [ string ordinal ]
            if String.IsNullOrWhiteSpace source.SourceKind then
                failure "IR_SOURCE_KIND_MISSING" "Source-map entries must identify their source construct." [ "nonempty source kind" ] [ source.SourceKind ]
            if String.IsNullOrWhiteSpace source.SiteSpan.File || source.SiteSpan.Line < 1 || source.SiteSpan.Column < 1 || source.SiteSpan.Length < 0 then
                failure "IR_SOURCE_SPAN_INVALID" "Source-map spans must have a file, positive line/column, and nonnegative length." [ "valid SourceSpan" ] [ sprintf "%A" source.SiteSpan ]
            if siteOwner <> source.SiteOwner then
                failure "IR_SOURCE_SITE_OWNER_MISMATCH" "Source-site ID owner does not match its source-map entry." [ sprintf "%A" siteOwner ] [ sprintf "%A" source.SiteOwner ]
            match source.SiteOwner with
            | Some owner when not (program.FunctionsById.ContainsKey owner || program.GeneratedTargetsById.ContainsKey owner) ->
                failure "IR_SOURCE_OWNER_UNKNOWN" "Source map refers to an unknown owner identity." [] [ sprintf "%A" owner ]
            | _ -> ()
        VerifiedIrProgram(program)
