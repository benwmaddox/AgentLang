namespace AgentLang

open System

/// A deterministic view of finite obligations for one parameter or output
/// position. Open scalar and list payloads do not appear in Required; enclosing
/// Option/Result tags and finite siblings still do.
type FiniteCoveragePosition =
    { Position: int
      TypeName: string
      Required: string list
      Observed: string list
      Missing: string list }

/// Finite coverage is derived from the verified semantic IR and actual values
/// observed at one exact function identity/revision during its attached tests.
type FiniteCoverageReport =
    { Inputs: FiniteCoveragePosition list
      Returns: FiniteCoveragePosition list
      Unsupported: string list }

module FiniteCoverage =
    [<Literal>]
    let maximumDomainValues = 4096

    type private Atom =
        { Key: string
          Label: string }

    type private Shape =
        | Closed of IrType * Atom list
        | OpenScalar of IrType
        | OpenList of IrType
        | OpenOption of IrType * Shape
        | OpenResult of IrType * Shape * Shape
        | OpenRecord of ProgramTypeKey * string * (int * string * IrType * Shape) list
        | OpenNominalScalar of ProgramTypeKey * IrType * Shape
        | Unsupported of string

    let rec private hasFiniteProjection = function
        | Closed(_, values) -> not values.IsEmpty
        | OpenScalar _ | OpenList _ | Unsupported _ -> false
        | OpenOption _ | OpenResult _ -> true
        | OpenRecord(_, _, fields) -> fields |> List.exists (fun (_, _, _, shape) -> hasFiniteProjection shape)
        | OpenNominalScalar(_, _, shape) -> hasFiniteProjection shape

    let private typeName (program: IrProgram) key =
        match program.NominalTypesByKey.TryFind key with
        | Some(IrRecordDefinition definition) -> definition.TypeName
        | Some(IrScalarDefinition definition) -> definition.TypeName
        | Some(IrEnumDefinition definition) -> definition.TypeName
        | None ->
            let keyText = sprintf "%A" key
            $"<unknown:{keyText}>"

    let rec private formatType (program: IrProgram) = function
        | IrInt -> "Int"
        | IrFloat -> "Float"
        | IrBool -> "Bool"
        | IrString -> "String"
        | IrUnit -> "Unit"
        | IrList item -> $"List<{formatType program item}>"
        | IrOption item -> $"Option<{formatType program item}>"
        | IrResult(okType, errorType) -> $"Result<{formatType program okType}, {formatType program errorType}>"
        | IrNominal key -> typeName program key

    let rec private langType (program: IrProgram) = function
        | IrInt -> TInt
        | IrFloat -> TFloat
        | IrBool -> TBool
        | IrString -> TString
        | IrUnit -> TUnit
        | IrList item -> TList(langType program item)
        | IrOption item -> TOption(langType program item)
        | IrResult(okType, errorType) -> TResult(langType program okType, langType program errorType)
        | IrNominal key -> TNamed(typeName program key)

    let private nominalKey (ProgramTypeKey key) = string key

    let rec private cartesian (limit: int) (dimensions: Atom list list) =
        let mutable values: Atom list list = [ [] ]
        for dimension in dimensions do
            if dimension.IsEmpty then values <- []
            elif values.Length > limit / dimension.Length then
                values <- []
                // The caller compares the dimensions' bounded product before
                // asking for a cartesian expansion; this branch is defensive.
                raise (InvalidOperationException("finite coverage product exceeded its bound"))
            else
                values <- [ for prefix in values do for item in dimension do yield prefix @ [ item ] ]
        values

    let private checkedProductCount (counts: int list) =
        let mutable total = 1L
        for count in counts do
            total <- total * int64 count
            if total > int64 maximumDomainValues then
                total <- int64 maximumDomainValues + 1L
        total

    let rec private buildShape (program: IrProgram) (visited: Set<ProgramTypeKey>) (typeValue: IrType) : Shape =
        let closed values = Closed(typeValue, values)
        match typeValue with
        | IrBool ->
            closed [ { Key = "bool:false"; Label = "false" }; { Key = "bool:true"; Label = "true" } ]
        | IrUnit -> closed [ { Key = "unit"; Label = "unit" } ]
        | IrInt | IrFloat | IrString -> OpenScalar typeValue
        | IrList _ -> OpenList typeValue
        | IrOption itemType ->
            match buildShape program visited itemType with
            | Unsupported reason -> Unsupported reason
            | Closed(_, values) ->
                if values.Length + 1 > maximumDomainValues then
                    Unsupported $"expanded domain for {formatType program typeValue} exceeds {maximumDomainValues} values"
                else
                    let none = { Key = "option:none"; Label = "none" }
                    let some = values |> List.map (fun value -> { Key = $"option:some({value.Key})"; Label = $"some({value.Label})" })
                    closed (none :: some)
            | child -> OpenOption(typeValue, child)
        | IrResult(okType, errorType) ->
            match buildShape program visited okType, buildShape program visited errorType with
            | Unsupported reason, _ | _, Unsupported reason -> Unsupported reason
            | Closed(_, okValues), Closed(_, errorValues) ->
                if okValues.Length + errorValues.Length > maximumDomainValues then
                    Unsupported $"expanded domain for {formatType program typeValue} exceeds {maximumDomainValues} values"
                else
                    let oks = okValues |> List.map (fun value -> { Key = $"result:ok({value.Key})"; Label = $"ok({value.Label})" })
                    let errors = errorValues |> List.map (fun value -> { Key = $"result:error({value.Key})"; Label = $"error({value.Label})" })
                    closed (oks @ errors)
            | okShape, errorShape -> OpenResult(typeValue, okShape, errorShape)
        | IrNominal key ->
            if visited.Contains key then
                Unsupported $"recursive nominal type {typeName program key} cannot be exhaustively enumerated"
            else
                let nextVisited = Set.add key visited
                match program.NominalTypesByKey.TryFind key with
                | None ->
                    let keyText = sprintf "%A" key
                    Unsupported $"nominal type key {keyText} is absent from the verified program"
                | Some(IrEnumDefinition definition) ->
                    definition.Cases
                    |> List.mapi (fun index caseName ->
                        { Key = $"enum:{nominalKey key}:{index}"
                          Label = $"{definition.TypeName}.{caseName}" })
                    |> closed
                | Some(IrScalarDefinition (definition: IrScalarDefinitionData)) ->
                    match definition.ValidatorCall with
                    | Some _ ->
                        match buildShape program nextVisited definition.BaseType with
                        | OpenScalar _ as shape -> OpenNominalScalar(key, definition.BaseType, shape)
                        | Unsupported reason -> Unsupported reason
                        | _ -> Unsupported $"refined scalar {definition.TypeName} has no proven complete finite-value domain"
                    | None ->
                        match buildShape program nextVisited definition.BaseType with
                        | Unsupported reason -> Unsupported reason
                        | Closed(_, values) ->
                            values
                            |> List.map (fun value ->
                                { Key = $"scalar:{nominalKey key}({value.Key})"
                                  Label = $"{definition.TypeName}({value.Label})" })
                            |> closed
                        | shape -> OpenNominalScalar(key, definition.BaseType, shape)
                | Some(IrRecordDefinition definition) ->
                    let fields = definition.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                    let shaped =
                        fields
                        |> List.map (fun field -> field.FieldIndex, field.FieldName, field.FieldType, buildShape program nextVisited field.FieldType)
                    match shaped |> List.tryPick (fun (_, _, _, shape) -> match shape with Unsupported reason -> Some reason | _ -> None) with
                    | Some reason -> Unsupported reason
                    | None when definition.ValidatorCall.IsSome && (List.isEmpty shaped || (shaped |> List.exists (fun (_, _, _, shape) -> hasFiniteProjection shape))) ->
                        Unsupported $"validated record {definition.TypeName} has a finite domain or finite projections but no proven complete valid-value domain"
                    | None when definition.ValidatorCall.IsSome -> OpenRecord(key, definition.TypeName, shaped)
                    | None when shaped |> List.forall (fun (_, _, _, shape) -> match shape with Closed _ -> true | _ -> false) ->
                        let dimensions =
                            shaped
                            |> List.map (fun (_, _, _, shape) -> match shape with Closed(_, values) -> values | _ -> [])
                        if checkedProductCount (dimensions |> List.map List.length) > int64 maximumDomainValues then
                            Unsupported $"expanded domain for {definition.TypeName} exceeds {maximumDomainValues} values"
                        else
                            cartesian maximumDomainValues dimensions
                            |> List.map (fun values ->
                                let fields = List.zip shaped values
                                let keyParts = fields |> List.map (fun ((index, _, _, _), value) -> $"{index}={value.Key}")
                                let labelParts = fields |> List.map (fun ((_, name, _, _), value) -> $"{name}={value.Label}")
                                let keyText = String.concat ";" keyParts
                                let labelText = String.concat ", " labelParts
                                { Key = $"record:{nominalKey key}{{{keyText}}}"
                                  Label = $"{definition.TypeName}{{{labelText}}}" })
                            |> closed
                    | None -> OpenRecord(key, definition.TypeName, shaped)

    let private token path key = $"{path}|{key}"

    let private displayPath path = if path = "$" then "" else path + ": "

    let rec private requirements (path: string) (shape: Shape) =
        match shape with
        | Closed(_, values) -> values |> List.map (fun value -> token path value.Key, displayPath path + value.Label)
        | OpenScalar _ | OpenList _ -> []
        | OpenOption(typeValue, child) ->
            let suffix = match typeValue with IrOption _ -> "option" | _ -> "option"
            [ token path (suffix + ":none"), displayPath path + "none"
              token path (suffix + ":some"), displayPath path + "some" ]
            @ requirements (path + ".some") child
        | OpenResult(_, okShape, errorShape) ->
            [ token path "result:ok", displayPath path + "ok"
              token path "result:error", displayPath path + "error" ]
            @ requirements (path + ".ok") okShape
            @ requirements (path + ".error") errorShape
        | OpenRecord(_, _, fields) ->
            fields |> List.collect (fun (_, name, _, child) -> requirements (path + "." + name) child)
        | OpenNominalScalar(_, _, child) -> requirements path child
        | Unsupported _ -> []

    let rec private canonicalValue (program: IrProgram) (typeValue: IrType) (value: Value) : Result<Atom, string> =
        let canonical key label = Ok { Key = key; Label = label }
        match typeValue, value with
        | IrBool, BoolValue flag ->
            let label = if flag then "true" else "false"
            canonical (if flag then "bool:true" else "bool:false") label
        | IrUnit, UnitValue -> canonical "unit" "unit"
        | IrOption itemType, OptionValue(actualType, None) when actualType = langType program itemType -> canonical "option:none" "none"
        | IrOption itemType, OptionValue(actualType, Some item) when actualType = langType program itemType ->
            canonicalValue program itemType item
            |> Result.map (fun nested -> { Key = $"option:some({nested.Key})"; Label = $"some({nested.Label})" })
        | IrResult(okType, errorType), ResultValue(actualOkType, actualErrorType, Ok item)
            when actualOkType = langType program okType && actualErrorType = langType program errorType ->
            canonicalValue program okType item
            |> Result.map (fun nested -> { Key = $"result:ok({nested.Key})"; Label = $"ok({nested.Label})" })
        | IrResult(okType, errorType), ResultValue(actualOkType, actualErrorType, Error item)
            when actualOkType = langType program okType && actualErrorType = langType program errorType ->
            canonicalValue program errorType item
            |> Result.map (fun nested -> { Key = $"result:error({nested.Key})"; Label = $"error({nested.Label})" })
        | IrNominal key, EnumValue(name, caseName) ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrEnumDefinition definition) when definition.TypeName = name ->
                match definition.Cases |> List.tryFindIndex ((=) caseName) with
                | Some index -> canonical $"enum:{nominalKey key}:{index}" $"{name}.{caseName}"
                | None -> Error $"enum value {name}.{caseName} is not in the declared nominal type"
            | _ -> Error $"enum value {name}.{caseName} does not match {formatType program typeValue}"
        | IrNominal key, RecordValue(name, fields) ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition definition) when definition.TypeName = name ->
                let declared = definition.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                let declaredNames = declared |> List.map (fun field -> field.FieldName) |> Set.ofList
                let actualNames = fields |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                let mutable parts: Atom list = []
                let mutable problem: string option =
                    if declaredNames = actualNames then None
                    else Some $"record {name} has a field set different from its declared layout"
                for field in declared do
                    if problem.IsNone then
                        match fields.TryFind field.FieldName with
                        | None -> problem <- Some $"record {name} is missing field {field.FieldName}"
                        | Some fieldValue ->
                            match canonicalValue program field.FieldType fieldValue with
                            | Error reason -> problem <- Some reason
                            | Ok atom -> parts <- parts @ [ atom ]
                match problem with
                | Some reason -> Error reason
                | None ->
                    let keyParts = List.zip declared parts |> List.map (fun (field, atom) -> $"{field.FieldIndex}={atom.Key}")
                    let labelParts = List.zip declared parts |> List.map (fun (field, atom) -> $"{field.FieldName}={atom.Label}")
                    let keyText = String.concat ";" keyParts
                    let labelText = String.concat ", " labelParts
                    canonical $"record:{nominalKey key}{{{keyText}}}" $"{name}{{{labelText}}}"
            | _ -> Error $"record value {name} does not match {formatType program typeValue}"
        | IrNominal key, NamedValue(name, inner) ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrScalarDefinition definition) when definition.TypeName = name ->
                canonicalValue program definition.BaseType inner
                |> Result.map (fun nested -> { Key = $"scalar:{nominalKey key}({nested.Key})"; Label = $"{name}({nested.Label})" })
            | _ -> Error $"scalar value {name} does not match {formatType program typeValue}"
        | IrInt, IntValue _ | IrFloat, FloatValue _ | IrString, StringValue _ ->
            Error $"open value {formatType program typeValue} is not a finite coverage atom"
        | IrList _, ListValue _ -> Error $"open value {formatType program typeValue} is not a finite coverage atom"
        | _ -> Error $"observed value does not match declared type {formatType program typeValue}"

    let rec private observedTokens (program: IrProgram) (path: string) (shape: Shape) (value: Value) : Result<(string * string) list, string> =
        let one key label = Ok [ token path key, displayPath path + label ]
        match shape, value with
        | Closed(typeValue, _), _ -> canonicalValue program typeValue value |> Result.map (fun atom -> [ token path atom.Key, displayPath path + atom.Label ])
        | OpenScalar _, _ | OpenList _, _ -> Ok []
        | OpenOption(typeValue, child), _ ->
            match typeValue, value with
            | IrOption itemType, OptionValue(actualType, None) when actualType = langType program itemType -> one "option:none" "none"
            | IrOption itemType, OptionValue(actualType, Some inner) when actualType = langType program itemType ->
                observedTokens program (path + ".some") child inner
                |> Result.map (fun nested -> (token path "option:some", displayPath path + "some") :: nested)
            | _ -> Error $"observed value does not match open finite shape {formatType program typeValue}"
        | OpenResult(IrResult(okType, errorType), okShape, errorShape), ResultValue(actualOkType, actualErrorType, payload)
            when actualOkType = langType program okType && actualErrorType = langType program errorType ->
            match payload with
            | Ok inner ->
                observedTokens program (path + ".ok") okShape inner
                |> Result.map (fun nested -> (token path "result:ok", displayPath path + "ok") :: nested)
            | Error inner ->
                observedTokens program (path + ".error") errorShape inner
                |> Result.map (fun nested -> (token path "result:error", displayPath path + "error") :: nested)
        | OpenRecord(key, name, fields), RecordValue(actualName, values) when actualName = name ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition definition)
                when (values |> Map.toSeq |> Seq.map fst |> Set.ofSeq) = (definition.RecordFields |> List.map (fun field -> field.FieldName) |> Set.ofList) ->
                let mutable found = []
                let mutable problem = None
                for _, fieldName, _, child in fields do
                    if problem.IsNone then
                        match values.TryFind fieldName with
                        | None -> problem <- Some $"record {name} is missing field {fieldName}"
                        | Some childValue ->
                            match observedTokens program (path + "." + fieldName) child childValue with
                            | Error reason -> problem <- Some reason
                            | Ok observations -> found <- found @ observations
                match problem with Some reason -> Error reason | None -> Ok found
            | _ -> Error $"nominal type {name} no longer refers to a record"
        | OpenNominalScalar(key, baseType, child), NamedValue(actualName, inner) when typeName program key = actualName ->
            observedTokens program path child inner
        | _ -> Error "observed value does not match the verified open composite shape"

    let private isDirectFiniteInput (program: IrProgram) = function
        | IrBool -> true
        | IrNominal key -> program.NominalTypesByKey.TryFind key |> Option.exists (function IrEnumDefinition _ -> true | _ -> false)
        | _ -> false

    let private hasSupportedFiniteObligations program typeValue =
        match buildShape program Set.empty typeValue with
        | Unsupported _ -> false
        | shape -> not (requirements "$" shape).IsEmpty

    /// Whether finite observations can contribute evidence for this function's
    /// input or output positions. Open-only and unsupported directions can
    /// avoid decoding values while invocation tracking remains enabled.
    let observationPlan (program: IrProgram) (functionValue: IrFunction) =
        let inputs = functionValue.InputTypes |> List.exists (fun typeValue -> isDirectFiniteInput program typeValue && hasSupportedFiniteObligations program typeValue)
        let returns = functionValue.OutputTypes |> List.exists (hasSupportedFiniteObligations program)
        inputs, returns

    let private positionReport (program: IrProgram) index typeValue shape (observations: Value list list) (valueIndex: int) =
        let requiredPairs = requirements "$" shape |> List.distinctBy fst |> List.sortBy fst
        let requiredLabels = requiredPairs |> List.map snd |> List.sort
        let requiredKeys = requiredPairs |> List.map fst |> Set.ofList
        let mutable observedPairs = []
        let mutable malformed = None
        for observation in observations do
            if malformed.IsNone && valueIndex < observation.Length then
                match observedTokens program "$" shape observation[valueIndex] with
                | Error reason -> malformed <- Some reason
                | Ok pairs -> observedPairs <- observedPairs @ pairs
        let observedKeys = observedPairs |> List.map fst |> Set.ofList |> Set.intersect requiredKeys
        let labelByKey = Map.ofList requiredPairs
        let observedLabels = observedKeys |> Set.toList |> List.choose (fun key -> labelByKey.TryFind key) |> List.sort
        let missingLabels = Set.difference requiredKeys observedKeys |> Set.toList |> List.choose (fun key -> labelByKey.TryFind key) |> List.sort
        let typeLabel = formatType program typeValue
        let malformedLabels = malformed |> Option.map (fun reason -> [ $"observation invalid for {typeLabel}: {reason}" ]) |> Option.defaultValue []
        { Position = index
          TypeName = typeLabel
          Required = requiredLabels
          Observed = observedLabels
          Missing = missingLabels @ malformedLabels }

    let analyze (program: IrProgram) (functionValue: IrFunction) (inputObservations: Value list list) (returnObservations: Value list list) =
        let unsupported = ResizeArray<string>()
        let inputPositions =
            functionValue.InputTypes
            |> List.mapi (fun index typeValue -> index, typeValue)
            |> List.choose (fun (index, typeValue) ->
                if not (isDirectFiniteInput program typeValue) then None
                else
                    match buildShape program Set.empty typeValue with
                    | Unsupported reason ->
                        unsupported.Add($"input[{index}] {formatType program typeValue}: {reason}")
                        None
                    | shape -> Some(positionReport program index typeValue shape inputObservations index))
        let returnPositions =
            functionValue.OutputTypes
            |> List.mapi (fun index typeValue ->
                match buildShape program Set.empty typeValue with
                | Unsupported reason ->
                    unsupported.Add($"return[{index}] {formatType program typeValue}: {reason}")
                    None
                | shape -> Some(positionReport program index typeValue shape returnObservations index))
            |> List.choose id
        { Inputs = inputPositions
          Returns = returnPositions
          Unsupported = unsupported |> Seq.distinct |> Seq.sort |> Seq.toList }

    let missing (report: FiniteCoverageReport) =
        [ yield! report.Inputs |> List.collect (fun position -> position.Missing |> List.map (fun label -> $"input[{position.Position}] {position.TypeName}: {label}"))
          yield! report.Returns |> List.collect (fun position -> position.Missing |> List.map (fun label -> $"return[{position.Position}] {position.TypeName}: {label}")) ]
