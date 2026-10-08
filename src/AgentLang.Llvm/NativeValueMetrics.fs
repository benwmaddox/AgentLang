namespace AgentLang.Llvm

open System.Collections.Generic
open AgentLang

[<Struct>]
type internal NativeValueShapeMetrics =
    { ExpandedNodes: int64
      Depth: int
      EstimatedOutputBytes: int64 }

type internal NativeProgramMetadata =
    { TypeIdsByIrType: Map<IrType, uint32>
      Types: NativeValueTypeMetadata array }

type internal NativeMetricFailure =
    { Dimension: string
      Expected: string
      Actual: string }

type internal NativeMetricResult = Result<NativeValueShapeMetrics, NativeMetricFailure>

[<Sealed>]
type internal NativeValueMetrics(metadata: NativeProgramMetadata) =
    let maxDepth = 256
    let maxNodes = 100000L
    let maxOutputBytes = 8000000L
    let metricCache = Dictionary<IrType, NativeMetricResult>()

    let saturatingAdd limit left right =
        if left > limit || right > limit || left > limit - right then limit + 1L
        else left + right

    let typeName (definition: NativeTypeDefinitionMetadata option) =
        match definition with
        | Some(NativeScalarMetadata(name, _))
        | Some(NativeRecordMetadata(name, _)) -> name
        | None -> invalidOp "A nominal native type has no immutable metadata."

    let measure (rootType: IrType) : NativeMetricResult =
        match metricCache.TryGetValue rootType with
        | true, result -> result
        | false, _ ->
            let frames = Stack<IrType * IrType list * int * int64 * int * int64>()
            let active = HashSet<IrType>()
            let mutable propagatedFailure: NativeMetricFailure option = None

            let nodeParts (currentType: IrType) =
                let typeDescriptionBytes =
                    match currentType with
                    | IrInt | IrBool | IrUnit -> 32L
                    | IrNominal _ ->
                        let typeId =
                            metadata.TypeIdsByIrType.TryFind currentType
                            |> Option.defaultWith (fun () -> invalidOp "A nominal native type has no type ID.")
                        let name = metadata.Types[int typeId].Definition |> typeName
                        saturatingAdd maxOutputBytes 32L (int64 name.Length * 6L)
                    | unsupported -> invalidOp $"Unsupported type reached native metric calculation: {IrTypes.format unsupported}."
                if typeDescriptionBytes > maxOutputBytes then
                    Error
                        { Dimension = "type-description output"
                          Expected = $"estimated UTF-8 output bytes <= {maxOutputBytes}"
                          Actual = string typeDescriptionBytes }
                else
                    let valueBytes, childTypes =
                        match currentType with
                        | IrInt | IrBool | IrUnit -> 64L, []
                        | IrNominal _ ->
                            let typeId =
                                metadata.TypeIdsByIrType.TryFind currentType
                                |> Option.defaultWith (fun () -> invalidOp "A nominal native type has no type ID.")
                            let valueType = metadata.Types[int typeId]
                            match valueType.Definition with
                            | Some(NativeScalarMetadata(name, _)) ->
                                saturatingAdd maxOutputBytes 64L (int64 name.Length * 6L), valueType.Children
                            | Some(NativeRecordMetadata(name, fields)) ->
                                let fieldNameBytes =
                                    fields
                                    |> List.fold (fun total (fieldName, _) ->
                                        saturatingAdd maxOutputBytes total (int64 fieldName.Length * 6L)) 0L
                                let ownBytes =
                                    saturatingAdd maxOutputBytes 64L
                                        (saturatingAdd maxOutputBytes (int64 name.Length * 6L) fieldNameBytes)
                                ownBytes, valueType.Children
                            | None -> invalidOp "A nominal value has no scalar or record metadata."
                        | unsupported -> invalidOp $"Unsupported type reached native metric calculation: {IrTypes.format unsupported}."
                    let ownBytes = saturatingAdd maxOutputBytes valueBytes typeDescriptionBytes
                    Ok(ownBytes, childTypes)

            let checkMetrics nodes depth outputBytes =
                if depth > maxDepth then
                    Error
                        { Dimension = "depth"
                          Expected = $"depth <= {maxDepth}"
                          Actual = string depth }
                elif nodes > maxNodes then
                    Error
                        { Dimension = "expanded-node"
                          Expected = $"expanded nodes <= {maxNodes}"
                          Actual = string nodes }
                elif outputBytes > maxOutputBytes then
                    Error
                        { Dimension = "output-size"
                          Expected = $"estimated UTF-8 output bytes <= {maxOutputBytes}"
                          Actual = string outputBytes }
                else
                    Ok
                        { ExpandedNodes = nodes
                          Depth = depth
                          EstimatedOutputBytes = outputBytes }

            let schedule currentType =
                match nodeParts currentType with
                | Error failure ->
                    metricCache[currentType] <- Error failure
                    propagatedFailure <- Some failure
                | Ok(ownBytes, childTypes) when List.isEmpty childTypes ->
                    let result = checkMetrics 1L 1 ownBytes
                    metricCache[currentType] <- result
                    match result with
                    | Error failure -> propagatedFailure <- Some failure
                    | Ok _ -> ()
                | Ok(ownBytes, childTypes) ->
                    if not (active.Add currentType) then
                        invalidOp "Recursive record metadata reached native metric calculation before cycle validation."
                    // Match Core's stack walk: record children are evaluated in
                    // reverse field order. Frames keep this traversal iterative
                    // even for unusually deep but finite type graphs.
                    frames.Push(currentType, List.rev childTypes, 0, 1L, 1, ownBytes)

            schedule rootType
            while frames.Count > 0 && propagatedFailure.IsNone do
                let currentType, children, nextChild, nodes, depth, outputBytes = frames.Pop()
                if nextChild >= children.Length then
                    active.Remove currentType |> ignore
                    let result = checkMetrics nodes depth outputBytes
                    metricCache[currentType] <- result
                    match result with
                    | Error failure -> propagatedFailure <- Some failure
                    | Ok _ -> ()
                else
                    let childType = children[nextChild]
                    match metricCache.TryGetValue childType with
                    | true, Error failure -> propagatedFailure <- Some failure
                    | true, Ok childMetrics ->
                        frames.Push(
                            currentType, children, nextChild + 1,
                            saturatingAdd maxNodes nodes childMetrics.ExpandedNodes,
                            max depth (childMetrics.Depth + 1),
                            saturatingAdd maxOutputBytes outputBytes childMetrics.EstimatedOutputBytes)
                    | false, _ ->
                        frames.Push(currentType, children, nextChild, nodes, depth, outputBytes)
                        schedule childType

            match propagatedFailure with
            | Some failure ->
                while frames.Count > 0 do
                    let currentType, _, _, _, _, _ = frames.Pop()
                    active.Remove currentType |> ignore
                    metricCache[currentType] <- Error failure
            | None -> ()

            metricCache.TryGetValue rootType
            |> function
                | true, result -> result
                | false, _ -> invalidOp "Native value metrics were not produced for a type root."

    member _.CheckRoots(rootTypes: IrType list) =
        let mutable nodes = 0L
        let mutable depth = 0
        let mutable outputBytes = 0L
        let mutable failure = None
        for ty in rootTypes do
            if failure.IsNone then
                match measure ty with
                | Error value -> failure <- Some value
                | Ok metrics ->
                    nodes <- saturatingAdd maxNodes nodes metrics.ExpandedNodes
                    depth <- max depth metrics.Depth
                    outputBytes <- saturatingAdd maxOutputBytes outputBytes metrics.EstimatedOutputBytes
                    if depth > maxDepth then
                        failure <- Some
                            { Dimension = "depth"
                              Expected = $"depth <= {maxDepth}"
                              Actual = string depth }
                    elif nodes > maxNodes then
                        failure <- Some
                            { Dimension = "expanded-node"
                              Expected = $"expanded nodes <= {maxNodes}"
                              Actual = string nodes }
                    elif outputBytes > maxOutputBytes then
                        failure <- Some
                            { Dimension = "output-size"
                              Expected = $"estimated UTF-8 output bytes <= {maxOutputBytes}"
                              Actual = string outputBytes }
        failure

[<RequireQualifiedAccess>]
module internal NativeProgramMetadata =
    let private primitiveKind = function
        | IrInt -> NativeAbi.TypeKindInt
        | IrBool -> NativeAbi.TypeKindBool
        | IrUnit -> NativeAbi.TypeKindUnit
        | unsupported -> invalidOp $"Unsupported type {IrTypes.format unsupported} cannot have a native descriptor kind."

    let create (program: IrProgram) =
        let nominalTypes = program.NominalTypesByKey |> Map.toList
        let typeIdsByIrType =
            ([ IrInt, NativeAbi.TypeIdInt
               IrBool, NativeAbi.TypeIdBool
               IrUnit, NativeAbi.TypeIdUnit ]
             @ (nominalTypes
                |> List.mapi (fun index (key, _) -> IrNominal key, uint32 (index + 3))))
            |> Map.ofList
        let typeIdFor ty =
            match typeIdsByIrType.TryFind ty with
            | Some typeId -> typeId
            | None -> invalidOp $"Unsupported type {IrTypes.format ty} reached native metadata generation."
        let types = ResizeArray<NativeValueTypeMetadata>()
        types.Add
            { TypeId = NativeAbi.TypeIdInt
              Kind = NativeAbi.TypeKindInt
              ValueType = IrInt
              Children = []
              Definition = None }
        types.Add
            { TypeId = NativeAbi.TypeIdBool
              Kind = NativeAbi.TypeKindBool
              ValueType = IrBool
              Children = []
              Definition = None }
        types.Add
            { TypeId = NativeAbi.TypeIdUnit
              Kind = NativeAbi.TypeKindUnit
              ValueType = IrUnit
              Children = []
              Definition = None }
        for key, definition in nominalTypes do
            let valueType = IrNominal key
            let typeId = typeIdFor valueType
            match definition with
            | IrScalarDefinition scalar ->
                types.Add
                    { TypeId = typeId
                      Kind = primitiveKind scalar.BaseType
                      ValueType = valueType
                      Children = [ scalar.BaseType ]
                      Definition = Some(NativeScalarMetadata(scalar.TypeName, typeIdFor scalar.BaseType)) }
            | IrRecordDefinition record ->
                let declaredFields = record.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                let fields = declaredFields |> List.map (fun field -> field.FieldName, typeIdFor field.FieldType)
                types.Add
                    { TypeId = typeId
                      Kind = NativeAbi.TypeKindRecord
                      ValueType = valueType
                      Children = declaredFields |> List.map (fun field -> field.FieldType)
                      Definition = Some(NativeRecordMetadata(record.TypeName, fields)) }
            | IrEnumDefinition enumDefinition ->
                invalidOp $"Native enum metadata is unsupported for '{enumDefinition.TypeName}'."
        { TypeIdsByIrType = typeIdsByIrType
          Types = types.ToArray() }

    let valueMetrics metadata = NativeValueMetrics(metadata)
