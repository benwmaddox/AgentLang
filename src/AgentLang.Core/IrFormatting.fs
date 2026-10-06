namespace AgentLang

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
type IrFormatTarget =
    | UserWordName of string
    | UserWordId of WordId
    | GeneratedWordName of string
    | GeneratedWordId of WordId
    | PrimitiveContract of PrimitiveId

/// Stable JSON DTOs used by the verified IR inspection surface.
module IrFormatting =
    type TypeDto =
        { Kind: string
          Display: string
          Name: string
          HasName: bool
          NominalKey: int
          HasNominalKey: bool
          Arguments: TypeDto list }

    type SourceSiteDto =
        { OwnerWordId: string
          HasOwnerWordId: bool
          Ordinal: int }

    type SourceSpanDto =
        { File: string
          Line: int
          Column: int
          Length: int }

    type SourceDto =
        { Site: SourceSiteDto
          SourceKind: string
          Span: SourceSpanDto }

    type LiteralDto =
        { Kind: string
          Display: string
          IntValue: int64
          FloatValue: double
          BoolValue: bool
          StringValue: string }

    type TargetDto =
        { Kind: string
          Id: string
          Revision: int
          HasRevision: bool }

    type ResolvedCallDto =
        { Name: string
          Target: TargetDto
          Inputs: TypeDto list
          Outputs: TypeDto list
          DeclaredEffects: string list
          Effects: string list }

    type LocalRefDto =
        { Slot: int
          Name: string }

    type LocalDto =
        { Slot: int
          Name: string
          Type: TypeDto }

    type ShapeDto =
        { Stack: TypeDto list
          Locals: LocalDto list }

    type InstructionDto =
        { Source: SourceDto
          Operation: JsonObject }

    type BlockDto =
        { Entry: ShapeDto
          Exit: ShapeDto
          Instructions: InstructionDto list }

    type CoverageBranchDto =
        { Site: SourceSiteDto
          Outcomes: string list }

    type CoverageDto =
        { Meaning: string
          CoveredSites: SourceSiteDto list
          BranchOutcomes: CoverageBranchDto list }

    type RecordFieldDto =
        { Index: int
          Name: string
          Type: TypeDto }

    type NominalTypeDto =
        { Kind: string
          Name: string
          TypeKey: int
          Fields: RecordFieldDto list
          BaseType: TypeDto
          HasBaseType: bool
          Validator: ResolvedCallDto
          HasValidator: bool
          GeneratedSources: SourceDto list }

    type FunctionDocumentDto =
        { FormatVersion: int
          Kind: string
          Name: string
          WordId: string
          Revision: int
          Inputs: TypeDto list
          Outputs: TypeDto list
          DeclaredEffects: string list
          InferredEffects: string list
          Locals: LocalRefDto list
          Body: BlockDto
          SourceSites: SourceDto list
          CoverageObligations: CoverageDto
          NominalTypes: NominalTypeDto list }

    type GeneratedOperationDto =
        { Kind: string
          TypeKey: int
          HasTypeKey: bool
          FieldIndex: int
          HasFieldIndex: bool }

    type GeneratedDocumentDto =
        { FormatVersion: int
          Kind: string
          Name: string
          WordId: string
          Revision: int
          Operation: GeneratedOperationDto
          Inputs: TypeDto list
          Outputs: TypeDto list
          DeclaredEffects: string list
          Effects: string list
          Source: SourceDto
          NominalTypes: NominalTypeDto list }

    type TypePatternDto =
        { Kind: string
          VariableIndex: int
          HasVariableIndex: bool
          Arguments: TypePatternDto list }

    type PrimitiveContractDocumentDto =
        { FormatVersion: int
          Kind: string
          PrimitiveId: string
          Aliases: string list
          Inputs: TypePatternDto list
          Outputs: TypePatternDto list
          Effects: string list }

    type private Document =
        | FunctionDocument of FunctionDocumentDto
        | GeneratedDocument of GeneratedDocumentDto
        | PrimitiveContractDocument of PrimitiveContractDocumentDto

    // Version 3 adds the typed ListFold operation. Emit one global
    // version so consumers never misread an extended document as version 2.
    let private formatVersion = 3
    // The formatter rejects deep documents before JsonSerializer or the outer
    // Protocol response can throw. This conservative estimate reserves ten
    // JSON levels for the response envelope and fixed DTO nesting.
    let private maxProtocolJsonDepth = 64

    let private jsonOptions =
        let options = JsonSerializerOptions(WriteIndented = true)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options

    let private toNode<'T> (value: 'T) = JsonSerializer.SerializeToNode(value, jsonOptions)

    let private wrapWordId (WordId value) = value
    let private wrapPrimitiveId (PrimitiveId value) = value
    let private wrapTypeKey (ProgramTypeKey value) = value
    let private wrapLocalSlot (LocalSlot value) = value

    let private fail code message requested expected actual =
        Diagnostics.raiseError code message (Some requested) None expected actual

    let private sourceSiteDto (SourceSiteId(owner, ordinal)) =
        match owner with
        | Some id ->
            { OwnerWordId = wrapWordId id
              HasOwnerWordId = true
              Ordinal = ordinal }
        | None ->
            { OwnerWordId = ""
              HasOwnerWordId = false
              Ordinal = ordinal }

    let private sourceDto (program: IrProgram) site =
        match program.SourceMap.TryFind site with
        | None -> fail "IR_FORMAT_SOURCE_MISSING" "Verified IR refers to a source site absent from its source map." (sprintf "%A" site) [ "verified source site" ] []
        | Some source ->
            { Site = sourceSiteDto site
              SourceKind = source.SourceKind
              Span =
                { File = source.SiteSpan.File
                  Line = source.SiteSpan.Line
                  Column = source.SiteSpan.Column
                  Length = source.SiteSpan.Length } }

    let private nominalName (program: IrProgram) key =
        match program.NominalTypesByKey.TryFind key with
        | Some(IrRecordDefinition record) -> record.TypeName
        | Some(IrScalarDefinition scalar) -> scalar.TypeName
        | None -> fail "IR_FORMAT_TYPE_MISSING" "Verified IR nominal key is absent from its type table." (string (wrapTypeKey key)) [ "known nominal type key" ] []

    let rec private typeDto program = function
        | IrInt ->
            { Kind = "int"; Display = "Int"; Name = "Int"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [] }
        | IrFloat ->
            { Kind = "float"; Display = "Float"; Name = "Float"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [] }
        | IrBool ->
            { Kind = "bool"; Display = "Bool"; Name = "Bool"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [] }
        | IrString ->
            { Kind = "string"; Display = "String"; Name = "String"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [] }
        | IrUnit ->
            { Kind = "unit"; Display = "Unit"; Name = "Unit"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [] }
        | IrList item ->
            let itemDto = typeDto program item
            { Kind = "list"; Display = $"List<{itemDto.Display}>"; Name = "List"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [ itemDto ] }
        | IrOption item ->
            let itemDto = typeDto program item
            { Kind = "option"; Display = $"Option<{itemDto.Display}>"; Name = "Option"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [ itemDto ] }
        | IrResult(ok, error) ->
            let okDto = typeDto program ok
            let errorDto = typeDto program error
            { Kind = "result"; Display = $"Result<{okDto.Display}, {errorDto.Display}>"; Name = "Result"; HasName = true; NominalKey = 0; HasNominalKey = false; Arguments = [ okDto; errorDto ] }
        | IrNominal key ->
            let name = nominalName program key
            { Kind = "nominal"; Display = name; Name = name; HasName = true; NominalKey = wrapTypeKey key; HasNominalKey = true; Arguments = [] }

    let private literalDto = function
        | LInt value ->
            { Kind = "int"
              Display = string value
              IntValue = value
              FloatValue = 0.0
              BoolValue = false
              StringValue = "" }
        | LFloat value ->
            { Kind = "float"
              Display = value.ToString("R", CultureInfo.InvariantCulture)
              IntValue = 0L
              FloatValue = value
              BoolValue = false
              StringValue = "" }
        | LBool value ->
            let display = if value then "true" else "false"
            { Kind = "bool"
              Display = display
              IntValue = 0L
              FloatValue = 0.0
              BoolValue = value
              StringValue = "" }
        | LString value ->
            { Kind = "string"
              Display = JsonSerializer.Serialize(value)
              IntValue = 0L
              FloatValue = 0.0
              BoolValue = false
              StringValue = value }
        | LUnit ->
            { Kind = "unit"
              Display = "unit"
              IntValue = 0L
              FloatValue = 0.0
              BoolValue = false
              StringValue = "" }

    let private targetDto = function
        | UserWordTarget(id, revision) ->
            { Kind = "user-word"; Id = wrapWordId id; Revision = revision; HasRevision = true }
        | PrimitiveTarget id ->
            { Kind = "primitive"; Id = wrapPrimitiveId id; Revision = 0; HasRevision = false }
        | GeneratedWordTarget(id, revision) ->
            { Kind = "generated-word"; Id = wrapWordId id; Revision = revision; HasRevision = true }

    let private callDto program (call: IrResolvedCall) =
        { Name = call.ResolvedName
          Target = targetDto call.ResolvedTarget
          Inputs = call.InputTypes |> List.map (typeDto program)
          Outputs = call.OutputTypes |> List.map (typeDto program)
          DeclaredEffects = IrEffects.names call.ResolvedDeclaredEffects
          Effects = IrEffects.names call.ResolvedEffects }

    let private localRefDto (names: Map<LocalSlot, string>) (slot: LocalSlot) =
        let (LocalSlot number) = slot
        match names.TryFind slot with
        | Some name -> { Slot = number; Name = name }
        | None -> fail "IR_FORMAT_LOCAL_NAME_MISSING" "Verified local slot has no display name." (string number) [ "named local slot" ] []

    let private shapeDto program names (shape: IrShape) =
        { Stack = shape.StackTypes |> List.map (typeDto program)
          Locals =
            shape.LocalTypes
            |> Map.toList
            |> List.map (fun (slot, ty) ->
                let local = localRefDto names slot
                { Slot = local.Slot; Name = local.Name; Type = typeDto program ty }) }

    let private addString (target: JsonObject) (key: string) (value: string) = target[key] <- JsonValue.Create(value)
    let private addInt (target: JsonObject) (key: string) (value: int) = target[key] <- JsonValue.Create(value)
    let private addNode (target: JsonObject) (key: string) (value: JsonNode) = target[key] <- value

    let private callNode program call = toNode (callDto program call)

    let rec private operationNode program names operation =
        let node = JsonObject()
        let addType key ty = addNode node key (toNode (typeDto program ty))
        let addCall key call = addNode node key (callNode program call)
        let addLocal key slot = addNode node key (toNode (localRefDto names slot))
        let addKey key typeKey = addInt node key (wrapTypeKey typeKey)
        match operation with
        | IrOperation.Constant(literal, ty) ->
            addString node "kind" "constant"
            addNode node "literal" (toNode (literalDto literal))
            addType "type" ty
        | IrOperation.Call call ->
            addString node "kind" "call"
            addCall "call" call
        | IrOperation.ListEmpty item ->
            addString node "kind" "list-empty"
            addType "elementType" item
        | IrOperation.ListSingleton item ->
            addString node "kind" "list-singleton"
            addType "elementType" item
        | IrOperation.OptionNone item ->
            addString node "kind" "option-none"
            addType "elementType" item
        | IrOperation.OptionSome item ->
            addString node "kind" "option-some"
            addType "elementType" item
        | IrOperation.ResultOk(ok, error) ->
            addString node "kind" "result-ok"
            addType "okType" ok
            addType "errorType" error
        | IrOperation.ResultError(ok, error) ->
            addString node "kind" "result-error"
            addType "okType" ok
            addType "errorType" error
        | IrOperation.ListMap(call, item, output) ->
            addString node "kind" "list-map"
            addCall "callback" call
            addType "elementType" item
            addType "outputType" output
            addType "resultType" (IrList output)
        | IrOperation.ListFilter(call, item) ->
            addString node "kind" "list-filter"
            addCall "callback" call
            addType "elementType" item
            addType "resultType" (IrList item)
        | IrOperation.ListEach(call, item) ->
            addString node "kind" "list-each"
            addCall "callback" call
            addType "elementType" item
            addType "resultType" IrUnit
        | IrOperation.ListFold(call, item, accumulator) ->
            addString node "kind" "list-fold"
            addCall "callback" call
            addType "elementType" item
            addType "accumulatorType" accumulator
            addType "resultType" accumulator
        | IrOperation.StoreLocal slot ->
            addString node "kind" "store-local"
            addLocal "local" slot
        | IrOperation.LoadLocal slot ->
            addString node "kind" "load-local"
            addLocal "local" slot
        | IrOperation.Scope innerBlock ->
            addString node "kind" "scope"
            addNode node "body" (toNode (blockDto program names innerBlock))
        | IrOperation.If(thenBlock, elseBlock) ->
            addString node "kind" "if"
            addNode node "then" (toNode (blockDto program names thenBlock))
            addNode node "else" (toNode (blockDto program names elseBlock))
        | IrOperation.MatchOption(someLocal, someBlock, noneBlock) ->
            addString node "kind" "match-option"
            addLocal "someLocal" someLocal
            addNode node "some" (toNode (blockDto program names someBlock))
            addNode node "none" (toNode (blockDto program names noneBlock))
        | IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) ->
            addString node "kind" "match-result"
            addLocal "okLocal" okLocal
            addLocal "errorLocal" errorLocal
            addNode node "ok" (toNode (blockDto program names okBlock))
            addNode node "error" (toNode (blockDto program names errorBlock))
        | IrOperation.MakeRecord(call, key) ->
            addString node "kind" "make-record"
            addCall "call" call
            addKey "typeKey" key
        | IrOperation.GetRecordField(call, key, fieldIndex) ->
            addString node "kind" "get-record-field"
            addCall "call" call
            addKey "typeKey" key
            addInt node "fieldIndex" fieldIndex
        | IrOperation.WrapScalar(call, key, validator) ->
            addString node "kind" "wrap-scalar"
            addCall "call" call
            addKey "typeKey" key
            match validator with
            | Some value -> addCall "validator" value
            | None -> ()
        | IrOperation.UnwrapScalar(call, key) ->
            addString node "kind" "unwrap-scalar"
            addCall "call" call
            addKey "typeKey" key
        node

    and private instructionDto program names (instruction: IrInstruction) =
        { Source = sourceDto program instruction.Site
          Operation = operationNode program names instruction.Operation }

    and private blockDto program names (block: IrBlock) =
        { Entry = shapeDto program names block.EntryShape
          Exit = shapeDto program names block.ExitShape
          Instructions = block.Code |> List.map (instructionDto program names) }

    let private sourceSitesInBlock program (block: IrBlock) =
        let rec gather (instructions: IrInstruction list) =
            instructions
            |> List.collect (fun instruction ->
                let nested =
                    match instruction.Operation with
                    | IrOperation.If(left, right) -> gather left.Code @ gather right.Code
                    | IrOperation.Scope innerBlock -> gather innerBlock.Code
                    | IrOperation.MatchOption(_, someBlock, noneBlock) -> gather someBlock.Code @ gather noneBlock.Code
                    | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> gather okBlock.Code @ gather errorBlock.Code
                    | _ -> []
                instruction.Site :: nested)
        gather block.Code
        |> Set.ofList
        |> Set.toList
        |> List.map (sourceDto program)

    let private coverageDto (coverage: IrCoverageObligations) =
        { Meaning = "obligations; outcomes are required cases, not observed execution traces"
          CoveredSites = coverage.CoveredSites |> Set.toList |> List.map sourceSiteDto
          BranchOutcomes =
            coverage.BranchOutcomes
            |> Map.toList
            |> List.map (fun (site, outcomes) -> { Site = sourceSiteDto site; Outcomes = outcomes }) }

    let rec private collectTypeKeys acc = function
        | IrNominal key -> Set.add key acc
        | IrList item | IrOption item -> collectTypeKeys acc item
        | IrResult(ok, error) -> collectTypeKeys (collectTypeKeys acc ok) error
        | _ -> acc

    let private collectCallTypeKeys acc (call: IrResolvedCall) =
        (call.InputTypes @ call.OutputTypes) |> List.fold collectTypeKeys acc

    let private collectShapeTypeKeys acc (shape: IrShape) =
        (shape.StackTypes @ (shape.LocalTypes |> Map.toList |> List.map snd)) |> List.fold collectTypeKeys acc

    let private collectShapeTypes (shape: IrShape) =
        shape.StackTypes @ (shape.LocalTypes |> Map.toList |> List.map snd)

    let private operationTypes = function
        | IrOperation.Constant(_, ty)
        | IrOperation.ListEmpty ty | IrOperation.ListSingleton ty
        | IrOperation.OptionNone ty | IrOperation.OptionSome ty -> [ ty ]
        | IrOperation.ResultOk(ok, error) | IrOperation.ResultError(ok, error) -> [ ok; error ]
        | IrOperation.Call call -> call.InputTypes @ call.OutputTypes
        | IrOperation.ListMap(call, item, output) -> call.InputTypes @ call.OutputTypes @ [ item; output ]
        | IrOperation.ListFilter(call, item) | IrOperation.ListEach(call, item) -> call.InputTypes @ call.OutputTypes @ [ item ]
        | IrOperation.ListFold(call, item, accumulator) -> call.InputTypes @ call.OutputTypes @ [ item; accumulator ]
        | IrOperation.MakeRecord(call, _) | IrOperation.GetRecordField(call, _, _) | IrOperation.UnwrapScalar(call, _) -> call.InputTypes @ call.OutputTypes
        | IrOperation.WrapScalar(call, _, validator) ->
            call.InputTypes @ call.OutputTypes @ (validator |> Option.map (fun item -> item.InputTypes @ item.OutputTypes) |> Option.defaultValue [])
        | IrOperation.If _ | IrOperation.MatchOption _ | IrOperation.MatchResult _
        | IrOperation.StoreLocal _ | IrOperation.LoadLocal _ | IrOperation.Scope _ -> []

    let rec private collectBlockTypes (block: IrBlock) =
        let nested (instruction: IrInstruction) =
            match instruction.Operation with
            | IrOperation.If(left, right) -> collectBlockTypes left @ collectBlockTypes right
            | IrOperation.Scope innerBlock -> collectBlockTypes innerBlock
            | IrOperation.MatchOption(_, someBlock, noneBlock) -> collectBlockTypes someBlock @ collectBlockTypes noneBlock
            | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collectBlockTypes okBlock @ collectBlockTypes errorBlock
            | _ -> []
        collectShapeTypes block.EntryShape
        @ collectShapeTypes block.ExitShape
        @ (block.Code |> List.collect (fun (instruction: IrInstruction) -> operationTypes instruction.Operation @ nested instruction))

    let private nominalDefinitionTypes program keys =
        keys
        |> Set.toList
        |> List.collect (fun key ->
            match program.NominalTypesByKey[key] with
            | IrRecordDefinition record -> record.RecordFields |> List.map (fun field -> field.FieldType)
            | IrScalarDefinition scalar ->
                scalar.BaseType :: (scalar.ValidatorCall |> Option.map (fun call -> call.InputTypes @ call.OutputTypes) |> Option.defaultValue []))

    let rec private irTypeDepth = function
        | IrInt | IrFloat | IrBool | IrString | IrUnit | IrNominal _ -> 1
        | IrList item | IrOption item -> 2 + irTypeDepth item
        | IrResult(ok, error) -> 2 + max (irTypeDepth ok) (irTypeDepth error)

    let rec private blockBranchDepth (block: IrBlock) =
        block.Code
        |> List.map (fun instruction ->
            match instruction.Operation with
            | IrOperation.If(left, right) -> 1 + max (blockBranchDepth left) (blockBranchDepth right)
            | IrOperation.Scope innerBlock -> 1 + blockBranchDepth innerBlock
            | IrOperation.MatchOption(_, someBlock, noneBlock) -> 1 + max (blockBranchDepth someBlock) (blockBranchDepth noneBlock)
            | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> 1 + max (blockBranchDepth okBlock) (blockBranchDepth errorBlock)
            | _ -> 0)
        |> List.fold max 0

    let private ensureProjectedDepth requested typeDepth branchDepth =
        // Each nested block adds the instruction array, instruction, operation,
        // and child block; recursive type DTOs add an argument array per layer.
        let projectedDepth = 10 + (2 * typeDepth) + (4 * branchDepth)
        if projectedDepth > maxProtocolJsonDepth then
            fail "IR_FORMAT_LIMIT_EXCEEDED" "Verified IR document exceeds the conservative nesting budget for the default JSON protocol envelope." requested [ $"maximum JSON depth {maxProtocolJsonDepth}" ] [ $"conservative depth estimate {projectedDepth}" ]

    let private ensureFormatDepth requested types branchDepth =
        let typeDepth = types |> List.map irTypeDepth |> List.fold max 1
        ensureProjectedDepth requested typeDepth branchDepth

    let rec private patternDepth = function
        | PatternInt | PatternFloat | PatternBool | PatternString | PatternUnit | PatternVariable _ -> 1
        | PatternList item | PatternOption item -> 2 + patternDepth item
        | PatternResult(ok, error) -> 2 + max (patternDepth ok) (patternDepth error)

    let rec private collectBlockTypeKeys acc (block: IrBlock) =
        let initial = collectShapeTypeKeys (collectShapeTypeKeys acc block.EntryShape) block.ExitShape
        block.Code
        |> List.fold (fun found instruction ->
            let found =
                match instruction.Operation with
                | IrOperation.Constant(_, ty)
                | IrOperation.ListEmpty ty | IrOperation.ListSingleton ty
                | IrOperation.OptionNone ty | IrOperation.OptionSome ty -> collectTypeKeys found ty
                | IrOperation.ResultOk(ok, error) | IrOperation.ResultError(ok, error) -> collectTypeKeys (collectTypeKeys found ok) error
                | IrOperation.Call call -> collectCallTypeKeys found call
                | IrOperation.ListMap(call, item, output) -> collectTypeKeys (collectTypeKeys (collectCallTypeKeys found call) item) output
                | IrOperation.ListFilter(call, item) | IrOperation.ListEach(call, item) -> collectTypeKeys (collectCallTypeKeys found call) item
                | IrOperation.ListFold(call, item, accumulator) -> collectTypeKeys (collectTypeKeys (collectCallTypeKeys found call) item) accumulator
                | IrOperation.MakeRecord(call, key) -> collectCallTypeKeys (Set.add key found) call
                | IrOperation.GetRecordField(call, key, _) -> collectCallTypeKeys (Set.add key found) call
                | IrOperation.WrapScalar(call, key, validator) ->
                    let withCall = collectCallTypeKeys (Set.add key found) call
                    validator |> Option.map (collectCallTypeKeys withCall) |> Option.defaultValue withCall
                | IrOperation.UnwrapScalar(call, key) -> collectCallTypeKeys (Set.add key found) call
                | IrOperation.If _ | IrOperation.MatchOption _ | IrOperation.MatchResult _
                | IrOperation.StoreLocal _ | IrOperation.LoadLocal _ | IrOperation.Scope _ -> found
            match instruction.Operation with
            | IrOperation.If(left, right) -> collectBlockTypeKeys (collectBlockTypeKeys found left) right
            | IrOperation.Scope innerBlock -> collectBlockTypeKeys found innerBlock
            | IrOperation.MatchOption(_, someBlock, noneBlock) -> collectBlockTypeKeys (collectBlockTypeKeys found someBlock) noneBlock
            | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collectBlockTypeKeys (collectBlockTypeKeys found okBlock) errorBlock
            | _ -> found) initial

    let private targetTypeKeys (target: IrGeneratedTarget) =
        let withSignature = (target.InputTypes @ target.OutputTypes) |> List.fold collectTypeKeys Set.empty
        match target.Operation with
        | MakeRecordOperation key | WrapScalarOperation key | UnwrapScalarOperation key -> Set.add key withSignature
        | GetRecordFieldOperation(key, _) -> Set.add key withSignature

    let private nominalClosure (program: IrProgram) (seed: Set<ProgramTypeKey>) =
        let rec expand (pending: Set<ProgramTypeKey>) (included: Set<ProgramTypeKey>) =
            if Set.isEmpty pending then included
            else
                let key = Set.minElement pending
                let rest = Set.remove key pending
                if included.Contains key then expand rest included
                else
                    let definition =
                        match program.NominalTypesByKey.TryFind key with
                        | Some value -> value
                        | None -> fail "IR_FORMAT_TYPE_MISSING" "Verified IR nominal key is absent from its type table." (string (wrapTypeKey key)) [ "known nominal type key" ] []
                    let dependencies =
                        match definition with
                        | IrRecordDefinition record -> record.RecordFields |> List.fold (fun found field -> collectTypeKeys found field.FieldType) Set.empty
                        | IrScalarDefinition scalar ->
                            let withBase = collectTypeKeys Set.empty scalar.BaseType
                            scalar.ValidatorCall |> Option.map (collectCallTypeKeys withBase) |> Option.defaultValue withBase
                    expand (Set.union rest dependencies) (Set.add key included)
        expand seed Set.empty

    let private generatedSourceDtos program keys =
        program.GeneratedTargetsById
        |> Map.toList
        |> List.choose (fun (_, target) ->
            if not (Set.isEmpty (Set.intersect keys (targetTypeKeys target))) then
                target.SourceSite |> Option.map (sourceDto program)
            else None)
        |> List.sortBy (fun source -> source.Span.File, source.Span.Line, source.Span.Column, source.Site.OwnerWordId)

    let private nominalTypeDtos program keys =
        keys
        |> Set.toList
        |> List.map (fun key ->
            let generatedSources = generatedSourceDtos program (Set.singleton key)
            match program.NominalTypesByKey[key] with
            | IrRecordDefinition record ->
                { Kind = "record"
                  Name = record.TypeName
                  TypeKey = wrapTypeKey key
                  Fields = record.RecordFields |> List.map (fun field -> { Index = field.FieldIndex; Name = field.FieldName; Type = typeDto program field.FieldType })
                  BaseType = typeDto program IrUnit
                  HasBaseType = false
                  Validator = Unchecked.defaultof<ResolvedCallDto>
                  HasValidator = false
                  GeneratedSources = generatedSources }
            | IrScalarDefinition scalar ->
                { Kind = "scalar"
                  Name = scalar.TypeName
                  TypeKey = wrapTypeKey key
                  Fields = []
                  BaseType = typeDto program scalar.BaseType
                  HasBaseType = true
                  Validator = scalar.ValidatorCall |> Option.map (callDto program) |> Option.defaultValue Unchecked.defaultof<ResolvedCallDto>
                  HasValidator = scalar.ValidatorCall.IsSome
                  GeneratedSources = generatedSources })

    let private nominalTypeKeysForFunction program (fn: IrFunction) =
        let signatureKeys = (fn.InputTypes @ fn.OutputTypes) |> List.fold collectTypeKeys Set.empty
        let bodyKeys = collectBlockTypeKeys signatureKeys fn.FunctionBody
        nominalClosure program bodyKeys

    let private functionDocument program (fn: IrFunction) =
        let nominalKeys = nominalTypeKeysForFunction program fn
        { FormatVersion = formatVersion
          Kind = "function"
          Name = fn.FunctionName
          WordId = wrapWordId fn.FunctionId
          Revision = fn.FunctionRevision
          Inputs = fn.InputTypes |> List.map (typeDto program)
          Outputs = fn.OutputTypes |> List.map (typeDto program)
          DeclaredEffects = IrEffects.names fn.FunctionDeclaredEffects
          InferredEffects = IrEffects.names fn.FunctionInferredEffects
          Locals = fn.LocalNames |> Map.toList |> List.map (fun (slot, name) -> { Slot = wrapLocalSlot slot; Name = name })
          Body = blockDto program fn.LocalNames fn.FunctionBody
          SourceSites = sourceSitesInBlock program fn.FunctionBody
          CoverageObligations = program.CoverageByWord[fn.FunctionId] |> coverageDto
          NominalTypes = nominalTypeDtos program nominalKeys }

    let private generatedOperationDto = function
        | MakeRecordOperation key -> { Kind = "make-record"; TypeKey = wrapTypeKey key; HasTypeKey = true; FieldIndex = 0; HasFieldIndex = false }
        | GetRecordFieldOperation(key, index) -> { Kind = "get-record-field"; TypeKey = wrapTypeKey key; HasTypeKey = true; FieldIndex = index; HasFieldIndex = true }
        | WrapScalarOperation key -> { Kind = "wrap-scalar"; TypeKey = wrapTypeKey key; HasTypeKey = true; FieldIndex = 0; HasFieldIndex = false }
        | UnwrapScalarOperation key -> { Kind = "unwrap-scalar"; TypeKey = wrapTypeKey key; HasTypeKey = true; FieldIndex = 0; HasFieldIndex = false }

    let private generatedDocument program (target: IrGeneratedTarget) =
        let sourceSite = target.SourceSite |> Option.defaultWith (fun () -> fail "IR_FORMAT_SOURCE_MISSING" "Verified generated target has no declaration source site." target.TargetName [ "generated source site" ] [])
        let nominalKeys =
            let signatureKeys = (target.InputTypes @ target.OutputTypes) |> List.fold collectTypeKeys Set.empty
            nominalClosure program (Set.union signatureKeys (targetTypeKeys target))
        { FormatVersion = formatVersion
          Kind = "generated-word"
          Name = target.TargetName
          WordId = wrapWordId target.TargetId
          Revision = target.TargetRevision
          Operation = generatedOperationDto target.Operation
          Inputs = target.InputTypes |> List.map (typeDto program)
          Outputs = target.OutputTypes |> List.map (typeDto program)
          DeclaredEffects = IrEffects.names target.TargetDeclaredEffects
          Effects = IrEffects.names target.TargetEffects
          Source = sourceDto program sourceSite
          NominalTypes = nominalTypeDtos program nominalKeys }

    let rec private patternDto = function
        | PatternInt -> { Kind = "int"; VariableIndex = 0; HasVariableIndex = false; Arguments = [] }
        | PatternFloat -> { Kind = "float"; VariableIndex = 0; HasVariableIndex = false; Arguments = [] }
        | PatternBool -> { Kind = "bool"; VariableIndex = 0; HasVariableIndex = false; Arguments = [] }
        | PatternString -> { Kind = "string"; VariableIndex = 0; HasVariableIndex = false; Arguments = [] }
        | PatternUnit -> { Kind = "unit"; VariableIndex = 0; HasVariableIndex = false; Arguments = [] }
        | PatternList item -> { Kind = "list"; VariableIndex = 0; HasVariableIndex = false; Arguments = [ patternDto item ] }
        | PatternOption item -> { Kind = "option"; VariableIndex = 0; HasVariableIndex = false; Arguments = [ patternDto item ] }
        | PatternResult(ok, error) -> { Kind = "result"; VariableIndex = 0; HasVariableIndex = false; Arguments = [ patternDto ok; patternDto error ] }
        | PatternVariable index -> { Kind = "variable"; VariableIndex = index; HasVariableIndex = true; Arguments = [] }

    let private primitiveAliases (id: PrimitiveId) =
        let (PrimitiveId operation) = id
        Compiler.primitives
        |> Map.toList
        |> List.choose (fun (_, entry) ->
            match entry.Builtin with
            | Some(BuiltinOp candidate) when candidate = operation -> Some entry.Definition.Name
            | _ -> None)
        |> List.sort

    let private primitiveDocument (id: PrimitiveId) (contract: IrPrimitiveContract) =
        { FormatVersion = formatVersion
          Kind = "primitive-contract"
          PrimitiveId = wrapPrimitiveId id
          Aliases = primitiveAliases id
          Inputs = contract.InputPatterns |> List.map patternDto
          Outputs = contract.OutputPatterns |> List.map patternDto
          Effects = IrEffects.names contract.PrimitiveEffects }

    let private findByName requested source actual =
        match actual with
        | [] -> fail "IR_FORMAT_TARGET_NOT_FOUND" $"No verified {source} named '{requested}' exists." requested [ source ] [ requested ]
        | [ (_, value) ] -> value
        | _ -> fail "IR_FORMAT_TARGET_AMBIGUOUS" $"The {source} name '{requested}' is ambiguous in this verified program." requested [ "unique name" ] (actual |> List.map fst)

    let private resolve verified request =
        VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verified
        let program = VerifiedIrProgram.inspect verified
        let userDocument (fn: IrFunction) =
            let nominalKeys = nominalTypeKeysForFunction program fn
            let types = fn.InputTypes @ fn.OutputTypes @ collectBlockTypes fn.FunctionBody @ nominalDefinitionTypes program nominalKeys
            ensureFormatDepth fn.FunctionName types (blockBranchDepth fn.FunctionBody)
            FunctionDocument(functionDocument program fn)
        let generatedDocumentSafe (target: IrGeneratedTarget) =
            let nominalKeys = nominalClosure program (targetTypeKeys target)
            let types = target.InputTypes @ target.OutputTypes @ nominalDefinitionTypes program nominalKeys
            ensureFormatDepth target.TargetName types 0
            GeneratedDocument(generatedDocument program target)
        match request with
        | IrFormatTarget.UserWordId id ->
            match program.FunctionsById.TryFind id with
            | Some fn -> userDocument fn
            | None -> fail "IR_FORMAT_TARGET_NOT_FOUND" "No verified user function has the requested WordId." (wrapWordId id) [ "user WordId" ] []
        | IrFormatTarget.UserWordName name ->
            program.FunctionsById
            |> Map.toList
            |> List.choose (fun (_, fn) -> if fn.FunctionName = name then Some(wrapWordId fn.FunctionId, fn) else None)
            |> findByName name "user word"
            |> userDocument
        | IrFormatTarget.GeneratedWordId id ->
            match program.GeneratedTargetsById.TryFind id with
            | Some target -> generatedDocumentSafe target
            | None -> fail "IR_FORMAT_TARGET_NOT_FOUND" "No verified generated target has the requested WordId." (wrapWordId id) [ "generated WordId" ] []
        | IrFormatTarget.GeneratedWordName name ->
            program.GeneratedTargetsById
            |> Map.toList
            |> List.choose (fun (_, target) -> if target.TargetName = name then Some(wrapWordId target.TargetId, target) else None)
            |> findByName name "generated word"
            |> generatedDocumentSafe
        | IrFormatTarget.PrimitiveContract id ->
            match Compiler.primitiveIrCatalog.TryFind id with
            | Some contract ->
                let patternDepths = contract.InputPatterns @ contract.OutputPatterns |> List.map patternDepth
                ensureProjectedDepth (wrapPrimitiveId id) (patternDepths |> List.fold max 1) 0
                PrimitiveContractDocument(primitiveDocument id contract)
            | None -> fail "IR_FORMAT_PRIMITIVE_NOT_FOUND" "No canonical primitive contract has the requested PrimitiveId." (wrapPrimitiveId id) [ "canonical primitive contract" ] []

    let toData (verified: VerifiedIrProgram) request : JsonNode =
        match resolve verified request with
        | FunctionDocument document -> toNode document
        | GeneratedDocument document -> toNode document
        | PrimitiveContractDocument document -> toNode document

    let toJson verified request =
        toData verified request |> fun node -> node.ToJsonString(jsonOptions)

    let private formatTypeList (types: TypeDto list) =
        if List.isEmpty types then "[]" else types |> List.map (fun ty -> ty.Display) |> String.concat " "

    let private formatEffects effects =
        if List.isEmpty effects then "none" else String.concat ", " effects

    let private formatLocal (local: LocalRefDto) = $"s{local.Slot} {local.Name}"

    let private formatTarget target =
        if target.HasRevision then $"{target.Kind}:{target.Id}@{target.Revision}" else $"{target.Kind}:{target.Id}"

    let private formatCall (call: ResolvedCallDto) =
        $"{call.Name} [{formatTarget call.Target}] : {formatTypeList call.Inputs} -> {formatTypeList call.Outputs} effects={formatEffects call.Effects}"

    let private sourceLabel (source: SourceDto) =
        let owner = if source.Site.HasOwnerWordId then source.Site.OwnerWordId else "<detached>"
        $"{source.Span.File}:{source.Span.Line}:{source.Span.Column} ({owner}#{source.Site.Ordinal})"

    let private operationHeader (operation: JsonObject) =
        let kind = operation["kind"].GetValue<string>()
        let getString (key: string) =
            match operation[key] with
            | null -> ""
            | value -> value.ToString()
        let getType (key: string) =
            match operation[key] with
            | :? JsonObject as value -> value["display"].GetValue<string>()
            | _ -> ""
        let getLocal (key: string) =
            match operation[key] with
            | :? JsonObject as local ->
                let slot = local["slot"].ToString()
                let name = local["name"].GetValue<string>()
                $"s{slot} {name}"
            | _ -> ""
        let callText (key: string) =
            match operation[key] with
            | :? JsonObject as call ->
                let name = call["name"].GetValue<string>()
                let target = call["target"].AsObject()
                let targetKind = target["kind"].GetValue<string>()
                let targetId = target["id"].GetValue<string>()
                let hasRevision = target["hasRevision"].GetValue<bool>()
                let revision = if hasRevision then "@" + target["revision"].ToString() else ""
                $"{name} [{targetKind}:{targetId}{revision}]"
            | _ -> ""
        match kind with
        | "constant" ->
            let literalNode = operation["literal"].AsObject()
            let literal = literalNode["display"].GetValue<string>()
            let ty = getType "type"
            $"constant {literal} : {ty}"
        | "call" -> "call " + callText "call"
        | "list-empty" -> "list.empty<" + getType "elementType" + ">"
        | "list-singleton" -> "list.singleton<" + getType "elementType" + ">"
        | "option-none" -> "option.none<" + getType "elementType" + ">"
        | "option-some" -> "option.some<" + getType "elementType" + ">"
        | "result-ok" -> "result.ok<" + getType "okType" + ", " + getType "errorType" + ">"
        | "result-error" -> "result.error<" + getType "okType" + ", " + getType "errorType" + ">"
        | "list-map" -> "list.map callback=" + callText "callback" + " : " + getType "elementType" + " -> " + getType "outputType"
        | "list-filter" -> "list.filter callback=" + callText "callback" + " : " + getType "elementType" + " -> Bool"
        | "list-each" -> "list.each callback=" + callText "callback" + " : " + getType "elementType" + " -> Unit"
        | "list-fold" -> "list.fold callback=" + callText "callback" + " : " + getType "accumulatorType" + " " + getType "elementType" + " -> " + getType "accumulatorType"
        | "store-local" -> "store local " + getLocal "local"
        | "load-local" -> "load local " + getLocal "local"
        | "if" -> "if"
        | "match-option" -> "match option some=" + getLocal "someLocal"
        | "match-result" -> "match result ok=" + getLocal "okLocal" + " error=" + getLocal "errorLocal"
        | "make-record" -> "record.make " + callText "call" + " key=" + getString "typeKey"
        | "get-record-field" -> "record.get " + callText "call" + " key=" + getString "typeKey" + " field=" + getString "fieldIndex"
        | "wrap-scalar" -> "scalar.wrap " + callText "call" + " key=" + getString "typeKey"
        | "unwrap-scalar" -> "scalar.unwrap " + callText "call" + " key=" + getString "typeKey"
        | other -> "operation " + other

    let private shapeText (shape: ShapeDto) =
        let stack = formatTypeList shape.Stack
        let locals =
            shape.Locals
            |> List.map (fun local -> $"s{local.Slot} {local.Name}:{local.Type.Display}")
            |> String.concat ", "
        $"stack [{stack}] locals {{{locals}}}"

    let rec private renderBlock indent (block: BlockDto) =
        let pad = String.replicate indent " "
        let lines = ResizeArray<string>()
        lines.Add($"{pad}entry {shapeText block.Entry}")
        for instruction in block.Instructions do
            lines.Add($"{pad}{sourceLabel instruction.Source}: {operationHeader instruction.Operation}")
            match instruction.Operation["then"], instruction.Operation["else"] with
            | (:? JsonObject as thenNode), (:? JsonObject as elseNode) ->
                lines.Add($"{pad}  then:")
                lines.AddRange(renderBlock (indent + 4) (JsonSerializer.Deserialize<BlockDto>(thenNode.ToJsonString(), jsonOptions)))
                lines.Add($"{pad}  else:")
                lines.AddRange(renderBlock (indent + 4) (JsonSerializer.Deserialize<BlockDto>(elseNode.ToJsonString(), jsonOptions)))
            | _ -> ()
            for caseName in [ "some"; "none"; "ok"; "error" ] do
                match instruction.Operation[caseName] with
                | :? JsonObject as caseNode ->
                    lines.Add($"{pad}  {caseName}:")
                    lines.AddRange(renderBlock (indent + 4) (JsonSerializer.Deserialize<BlockDto>(caseNode.ToJsonString(), jsonOptions)))
                | _ -> ()
        lines.Add($"{pad}exit  {shapeText block.Exit}")
        List.ofSeq lines

    let private formatNominal (nominal: NominalTypeDto) =
        match nominal.Kind with
        | "record" ->
            let fields = nominal.Fields |> List.map (fun field -> $"{field.Name}:{field.Type.Display}") |> String.concat ", "
            $"record {nominal.Name} @type{nominal.TypeKey} {{ {fields} }}"
        | _ ->
            let validator = if nominal.HasValidator then "; validator=" + formatCall nominal.Validator else ""
            $"scalar {nominal.Name} @type{nominal.TypeKey} = {nominal.BaseType.Display}{validator}"

    let toText verified request =
        match resolve verified request with
        | FunctionDocument document ->
            [ $"word {document.Name} [{document.WordId}@{document.Revision}]"
              $"signature: {formatTypeList document.Inputs} -> {formatTypeList document.Outputs}"
              $"declared effects: {formatEffects document.DeclaredEffects}"
              $"inferred effects: {formatEffects document.InferredEffects}"
              "local slots: " + (document.Locals |> List.map formatLocal |> String.concat ", ")
              "nominal types:"
              yield! (document.NominalTypes |> List.map (fun nominal -> "  " + formatNominal nominal))
              "source sites:"
              yield! (document.SourceSites |> List.map (fun source -> "  " + sourceLabel source + " " + source.SourceKind))
              "coverage obligations:"
              yield! (document.CoverageObligations.BranchOutcomes |> List.map (fun branch ->
                  let outcomeText = String.concat ", " branch.Outcomes
                  $"  {branch.Site.OwnerWordId}#{branch.Site.Ordinal}: {outcomeText}"))
              "body:"
              yield! renderBlock 2 document.Body ]
            |> String.concat Environment.NewLine
        | GeneratedDocument document ->
            [ $"generated word {document.Name} [{document.WordId}@{document.Revision}]"
              $"operation: {document.Operation.Kind}"
              $"signature: {formatTypeList document.Inputs} -> {formatTypeList document.Outputs}"
              $"declared effects: {formatEffects document.DeclaredEffects}"
              $"effects: {formatEffects document.Effects}"
              "source: " + sourceLabel document.Source
              "nominal types:"
              yield! (document.NominalTypes |> List.map (fun nominal -> "  " + formatNominal nominal)) ]
            |> String.concat Environment.NewLine
        | PrimitiveContractDocument document ->
            let patternText pattern =
                let rec render = function
                    | { Kind = "variable"; VariableIndex = index } -> "T" + string index
                    | { Kind = "list"; Arguments = [ item ] } -> $"List<{render item}>"
                    | { Kind = "option"; Arguments = [ item ] } -> $"Option<{render item}>"
                    | { Kind = "result"; Arguments = [ ok; error ] } -> $"Result<{render ok}, {render error}>"
                    | item -> item.Kind
                render pattern
            let renderPatterns patterns = if List.isEmpty patterns then "[]" else patterns |> List.map patternText |> String.concat " "
            [ $"primitive contract {document.PrimitiveId}"
              "aliases: " + String.concat ", " document.Aliases
              $"signature pattern: {renderPatterns document.Inputs} -> {renderPatterns document.Outputs}"
              $"effects: {formatEffects document.Effects}"
              "kind: generic contract; no executable function body" ]
            |> String.concat Environment.NewLine
