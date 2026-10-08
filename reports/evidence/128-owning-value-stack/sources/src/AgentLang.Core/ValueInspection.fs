namespace AgentLang

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// Bounded, structural JSON observation of values against one compiler-verified
/// program snapshot. This module never constructs language values or runs code.
module ValueInspection =
    [<Literal>]
    let FormatVersion = 1

    [<Literal>]
    let MaxNestingDepth = 12

    [<Literal>]
    let MaxValueNodes = 10000

    [<Literal>]
    let MaxJsonNodes = 100000

    [<Literal>]
    let MaxSnapshotNominals = 100000

    [<Literal>]
    let MaxJsonDepth = 48

    [<Literal>]
    let MaxSerializedUtf8Bytes = 1048576

    [<Literal>]
    let EmbeddingReserveBytes = 256

    type private Budget =
        { mutable ValueNodes: int
          mutable JsonNodes: int
          mutable EstimatedUtf8Bytes: int }

    let private fail code message path expected actual =
        let message = if String.IsNullOrEmpty path then message else message + " Path: " + path
        Diagnostics.raiseError code message None None expected actual

    let private chargeBytes (budget: Budget) path count =
        if count < 0 || count > MaxSerializedUtf8Bytes - budget.EstimatedUtf8Bytes then
            fail
                "VALUE_INSPECTION_SIZE_LIMIT"
                "Structured value output exceeds its UTF-8 byte budget."
                path
                [ $"<= {MaxSerializedUtf8Bytes} bytes including {EmbeddingReserveBytes}-byte embedding reserve" ]
                [ $"more than {MaxSerializedUtf8Bytes} estimated bytes" ]
        budget.EstimatedUtf8Bytes <- budget.EstimatedUtf8Bytes + count

    let private encodedStringSize path (value: string) =
        if isNull value then
            fail "VALUE_STRING_NULL" "Structured JSON strings cannot be null." path [ "non-null String" ] [ "null" ]
        let rawBytes = Encoding.UTF8.GetByteCount value
        if rawBytes > MaxSerializedUtf8Bytes then
            fail
                "VALUE_INSPECTION_SIZE_LIMIT"
                "A single string exceeds the structured output UTF-8 byte budget."
                path
                [ $"<= {MaxSerializedUtf8Bytes} bytes" ]
                [ $"{rawBytes} UTF-8 bytes before JSON escaping" ]
        // JsonEncodedText uses the same default encoder as the compact serializer
        // below. Two bytes account for the surrounding JSON quotes.
        JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length + 2

    let private chargeJsonNode (budget: Budget) path depth =
        if depth > MaxJsonDepth then
            fail
                "VALUE_INSPECTION_DEPTH_LIMIT"
                "Structured JSON output exceeds its bounded nesting depth."
                path
                [ $"<= {MaxJsonDepth} JSON levels" ]
                [ string depth ]
        if budget.JsonNodes >= MaxJsonNodes then
            fail
                "VALUE_INSPECTION_NODE_LIMIT"
                "Structured JSON output exceeds its node budget."
                path
                [ $"<= {MaxJsonNodes} JSON nodes" ]
                [ string (budget.JsonNodes + 1) ]
        budget.JsonNodes <- budget.JsonNodes + 1

    let private objectNode (budget: Budget) path depth =
        chargeJsonNode budget path depth
        chargeBytes budget path 2
        JsonObject()

    let private arrayNode (budget: Budget) path depth =
        chargeJsonNode budget path depth
        chargeBytes budget path 2
        JsonArray()

    let private stringNode (budget: Budget) path depth (value: string) =
        chargeJsonNode budget path depth
        chargeBytes budget path (encodedStringSize path value)
        JsonValue.Create(value) :> JsonNode

    let private intNode (budget: Budget) path depth (value: int) =
        chargeJsonNode budget path depth
        chargeBytes budget path (value.ToString(CultureInfo.InvariantCulture).Length)
        JsonValue.Create(value) :> JsonNode

    let private boolNode (budget: Budget) path depth (value: bool) =
        chargeJsonNode budget path depth
        chargeBytes budget path (if value then 4 else 5)
        JsonValue.Create(value) :> JsonNode

    let private addProperty (budget: Budget) path (target: JsonObject) key (value: JsonNode) =
        chargeBytes budget path (encodedStringSize path key + 2) // quoted key, colon, and a conservative comma
        target[key] <- value

    let private addString (budget: Budget) path (target: JsonObject) key depth value =
        addProperty budget path target key (stringNode budget (path + "." + key) depth value)

    let private addInt (budget: Budget) path (target: JsonObject) key depth value =
        addProperty budget path target key (intNode budget (path + "." + key) depth value)

    let private addBool (budget: Budget) path (target: JsonObject) key depth value =
        addProperty budget path target key (boolNode budget (path + "." + key) depth value)

    let private addChild (budget: Budget) path (target: JsonObject) key child =
        addProperty budget path target key child

    let private addArrayItem (budget: Budget) path (target: JsonArray) value =
        chargeBytes budget path 1 // one conservative comma per item
        target.Add(value)

    let private chargeValue (budget: Budget) path depth =
        if depth > MaxNestingDepth then
            fail
                "VALUE_INSPECTION_DEPTH_LIMIT"
                "Structured value exceeds its bounded semantic nesting depth."
                path
                [ $"<= {MaxNestingDepth} value levels" ]
                [ string depth ]
        if budget.ValueNodes >= MaxValueNodes then
            fail
                "VALUE_INSPECTION_NODE_LIMIT"
                "Structured value exceeds its value-node budget."
                path
                [ $"<= {MaxValueNodes} values" ]
                [ string (budget.ValueNodes + 1) ]
        budget.ValueNodes <- budget.ValueNodes + 1

    let private limitedTypeLabel ty =
        let rec render depth current =
            if depth > MaxNestingDepth then "<nested-type-limit>"
            else
                match current with
                | TInt -> "Int"
                | TFloat -> "Float"
                | TBool -> "Bool"
                | TString -> "String"
                | TUnit -> "Unit"
                | TList item -> "List<" + render (depth + 1) item + ">"
                | TOption item -> "Option<" + render (depth + 1) item + ">"
                | TResult(ok, error) -> "Result<" + render (depth + 1) ok + ", " + render (depth + 1) error + ">"
                | TNamed name -> if isNull name then "<null-nominal>" else name
                | TVar name -> "?" + (if isNull name then "<null>" else name)
        render 0 ty

    let private runtimeTag = function
        | IntValue _ -> "Int"
        | FloatValue _ -> "Float"
        | BoolValue _ -> "Bool"
        | StringValue _ -> "String"
        | UnitValue -> "Unit"
        | ListValue _ -> "List"
        | OptionValue _ -> "Option"
        | ResultValue _ -> "Result"
        | RecordValue(name, _) -> if isNull name then "record with null name" else $"record {name}"
        | EnumValue(typeName, caseName) -> if isNull typeName || isNull caseName then "enum with null identity" else $"enum {typeName}.{caseName}"
        | NamedValue(name, _) -> if isNull name then "scalar with null name" else $"scalar {name}"

    let private typeNameAndKind = function
        | IrRecordDefinition record -> record.TypeName, "record"
        | IrScalarDefinition scalar -> scalar.TypeName, "scalar"
        | IrEnumDefinition enumDefinition -> enumDefinition.TypeName, "enum"

    /// Return one deterministic JSON object for a batch of already-produced values.
    /// The caller must pair these values with the exact verified program snapshot
    /// whose schema they came from; this function cannot prove value provenance.
    let toData (verified: VerifiedIrProgram) (values: Value list) : JsonObject =
        VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verified
        let program = VerifiedIrProgram.inspect verified
        let mutable nominalByName: Map<string, ProgramTypeKey * IrNominalDefinition> option = None

        let budget =
            { ValueNodes = 0
              JsonNodes = 0
              EstimatedUtf8Bytes = EmbeddingReserveBytes }

        let nominalInfo path name =
            if isNull name || String.IsNullOrWhiteSpace name then
                fail "VALUE_NOMINAL_NAME_INVALID" "Nominal value or type requires a nonempty name." path [ "known nominal type name" ] [ "empty or null name" ]
            if nominalByName.IsNone then
                if program.NominalTypesByKey.Count > MaxSnapshotNominals then
                    fail "VALUE_INSPECTION_NODE_LIMIT" "Snapshot nominal index exceeds the bounded lookup budget." path [ $"<= {MaxSnapshotNominals} nominal types" ] [ string program.NominalTypesByKey.Count ]
                nominalByName <-
                    program.NominalTypesByKey
                    |> Map.toList
                    |> List.map (fun (key, definition) ->
                        let name, _ = typeNameAndKind definition
                        name, (key, definition))
                    |> Map.ofList
                    |> Some
            match nominalByName |> Option.get |> Map.tryFind name with
            | Some value -> value
            | None -> fail "VALUE_UNKNOWN_NOMINAL" "Value refers to a nominal type absent from the verified snapshot." path [ "nominal type in verified snapshot" ] [ name ]

        let rec toLangType path depth = function
            | _ when depth > MaxNestingDepth ->
                fail "VALUE_INSPECTION_DEPTH_LIMIT" "Snapshot type exceeds the bounded semantic nesting depth." path [ $"<= {MaxNestingDepth} type levels" ] [ string depth ]
            | IrInt -> TInt
            | IrFloat -> TFloat
            | IrBool -> TBool
            | IrString -> TString
            | IrUnit -> TUnit
            | IrList item -> TList(toLangType path (depth + 1) item)
            | IrOption item -> TOption(toLangType path (depth + 1) item)
            | IrResult(ok, error) -> TResult(toLangType path (depth + 1) ok, toLangType path (depth + 1) error)
            | IrNominal key ->
                match program.NominalTypesByKey.TryFind key with
                | Some definition -> TNamed(fst (typeNameAndKind definition))
                | None -> fail "VALUE_UNKNOWN_NOMINAL" "Snapshot IR type key is absent from its verified nominal table." path [ "known nominal type key" ] [ string (let (ProgramTypeKey index) = key in index) ]

        let rec validateType path depth ty =
            if depth > MaxNestingDepth then
                fail "VALUE_INSPECTION_DEPTH_LIMIT" "Value type exceeds the bounded semantic nesting depth." path [ $"<= {MaxNestingDepth} type levels" ] [ string depth ]
            if isNull (box ty) then
                fail "VALUE_TYPE_INVALID" "Runtime value contains a null language type." path [ "closed language type" ] [ "null" ]
            match ty with
            | TInt | TFloat | TBool | TString | TUnit -> ()
            | TList item | TOption item -> validateType path (depth + 1) item
            | TResult(ok, error) -> validateType path (depth + 1) ok; validateType path (depth + 1) error
            | TNamed name -> nominalInfo path name |> ignore
            | TVar name -> fail "VALUE_TYPE_UNRESOLVED" "Runtime value contains an unresolved type variable." path [ "closed language type" ] [ if isNull name then "<null>" else name ]

        let rec typeNode path jsonDepth typeDepth ty : JsonNode =
            if typeDepth > MaxNestingDepth then
                fail "VALUE_INSPECTION_DEPTH_LIMIT" "Type DTO exceeds the bounded semantic nesting depth." path [ $"<= {MaxNestingDepth} type levels" ] [ string typeDepth ]
            match box ty with
            | null -> fail "VALUE_TYPE_INVALID" "Runtime value contains a null language type." path [ "closed language type" ] [ "null" ]
            | _ -> ()
            let node = objectNode budget path jsonDepth
            let addKind kind = addString budget path node "kind" (jsonDepth + 1) kind
            match ty with
            | TInt -> addKind "int"
            | TFloat -> addKind "float"
            | TBool -> addKind "bool"
            | TString -> addKind "string"
            | TUnit -> addKind "unit"
            | TList item ->
                addKind "list"
                addChild budget path node "elementType" (typeNode (path + ".elementType") (jsonDepth + 1) (typeDepth + 1) item)
            | TOption item ->
                addKind "option"
                addChild budget path node "elementType" (typeNode (path + ".elementType") (jsonDepth + 1) (typeDepth + 1) item)
            | TResult(ok, error) ->
                addKind "result"
                addChild budget path node "okType" (typeNode (path + ".okType") (jsonDepth + 1) (typeDepth + 1) ok)
                addChild budget path node "errorType" (typeNode (path + ".errorType") (jsonDepth + 1) (typeDepth + 1) error)
            | TNamed name ->
                let key, definition = nominalInfo path name
                let actualName, nominalKind = typeNameAndKind definition
                addKind "nominal"
                addString budget path node "name" (jsonDepth + 1) actualName
                addString budget path node "nominalKind" (jsonDepth + 1) nominalKind
                let (ProgramTypeKey index) = key
                addInt budget path node "typeKey" (jsonDepth + 1) index
                match definition with
                | IrEnumDefinition enumDefinition ->
                    let cases = arrayNode budget (path + ".cases") (jsonDepth + 1)
                    enumDefinition.Cases
                    |> List.iteri (fun caseIndex caseName ->
                        let casePath = path + ".cases[" + string caseIndex + "]"
                        let caseNode = JsonValue.Create(caseName) :> JsonNode
                        addArrayItem budget casePath cases caseNode)
                    addChild budget path node "cases" cases
                | _ -> ()
            | TVar name ->
                fail "VALUE_TYPE_UNRESOLVED" "Runtime value contains an unresolved type variable." path [ "closed language type" ] [ if isNull name then "<null>" else name ]
            node :> JsonNode

        let typeMismatch path expected value =
            fail
                "VALUE_TYPE_MISMATCH"
                "Structured value does not match its declared snapshot type."
                path
                [ limitedTypeLabel expected ]
                [ runtimeTag value ]

        let rec valueNode path jsonDepth valueDepth expected value : JsonNode =
            if isNull (box value) then
                fail "VALUE_INVALID" "Runtime value cannot be null." path [ "well-formed Value" ] [ "null" ]
            chargeValue budget path valueDepth
            validateType path 0 expected
            match expected, value with
            | TInt, IntValue number ->
                let node = objectNode budget path jsonDepth
                addString budget path node "kind" (jsonDepth + 1) "int"
                addString budget path node "value" (jsonDepth + 1) (number.ToString(CultureInfo.InvariantCulture))
                node :> JsonNode
            | TFloat, FloatValue number ->
                if not (Double.IsFinite number) then
                    fail "VALUE_FLOAT_NONFINITE" "Structured Float values must be finite." path [ "finite Float" ] [ number.ToString("R", CultureInfo.InvariantCulture) ]
                let node = objectNode budget path jsonDepth
                let roundTrip = number.ToString("R", CultureInfo.InvariantCulture)
                let negativeZero = number = 0.0 && BitConverter.DoubleToInt64Bits(number) = Int64.MinValue
                addString budget path node "kind" (jsonDepth + 1) "float"
                addString budget path node "value" (jsonDepth + 1) roundTrip
                addBool budget path node "negativeZero" (jsonDepth + 1) negativeZero
                node :> JsonNode
            | TBool, BoolValue boolean ->
                let node = objectNode budget path jsonDepth
                addString budget path node "kind" (jsonDepth + 1) "bool"
                addBool budget path node "value" (jsonDepth + 1) boolean
                node :> JsonNode
            | TString, StringValue text ->
                if isNull text then fail "VALUE_STRING_NULL" "Structured String values cannot be null." path [ "non-null String" ] [ "null" ]
                let node = objectNode budget path jsonDepth
                addString budget path node "kind" (jsonDepth + 1) "string"
                addString budget path node "value" (jsonDepth + 1) text
                node :> JsonNode
            | TUnit, UnitValue ->
                let node = objectNode budget path jsonDepth
                addString budget path node "kind" (jsonDepth + 1) "unit"
                node :> JsonNode
            | TList itemType, ListValue(declaredType, items) ->
                // Validate attacker-controlled container metadata before structural
                // equality. A deeply nested host-created LangType must hit our
                // deterministic depth diagnostic, not recurse inside F# equality.
                validateType path 0 declaredType
                if isNull (box items) then
                    fail "VALUE_CONTAINER_SHAPE_MISMATCH" "List payload collection cannot be null." path [ "List of values" ] [ "null" ]
                if declaredType <> itemType then typeMismatch path expected value
                else
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "list"
                    addChild budget path node "elementType" (typeNode (path + ".elementType") (jsonDepth + 1) 0 itemType)
                    let itemArray = arrayNode budget (path + ".items") (jsonDepth + 1)
                    let mutable index = 0
                    for item in items do
                        let itemPath = path + ".items[" + string index + "]"
                        addArrayItem budget itemPath itemArray (valueNode itemPath (jsonDepth + 2) (valueDepth + 1) itemType item)
                        index <- index + 1
                    addChild budget path node "items" itemArray
                    node :> JsonNode
            | TOption itemType, OptionValue(declaredType, option) ->
                validateType path 0 declaredType
                if declaredType <> itemType then typeMismatch path expected value
                else
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "option"
                    addChild budget path node "elementType" (typeNode (path + ".elementType") (jsonDepth + 1) 0 itemType)
                    match option with
                    | None -> addString budget path node "case" (jsonDepth + 1) "none"
                    | Some item ->
                        addString budget path node "case" (jsonDepth + 1) "some"
                        let itemPath = path + ".value"
                        addChild budget path node "value" (valueNode itemPath (jsonDepth + 1) (valueDepth + 1) itemType item)
                    node :> JsonNode
            | TResult(okType, errorType), ResultValue(declaredOk, declaredError, result) ->
                validateType path 0 declaredOk
                validateType path 0 declaredError
                if declaredOk <> okType || declaredError <> errorType then typeMismatch path expected value
                else
                    if isNull (box result) then
                        fail "VALUE_CONTAINER_SHAPE_MISMATCH" "Result case payload cannot be null." path [ "Ok or Error case" ] [ "null" ]
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "result"
                    addChild budget path node "okType" (typeNode (path + ".okType") (jsonDepth + 1) 0 okType)
                    addChild budget path node "errorType" (typeNode (path + ".errorType") (jsonDepth + 1) 0 errorType)
                    match result with
                    | Ok item ->
                        addString budget path node "case" (jsonDepth + 1) "ok"
                        let itemPath = path + ".value"
                        addChild budget path node "value" (valueNode itemPath (jsonDepth + 1) (valueDepth + 1) okType item)
                    | Error item ->
                        addString budget path node "case" (jsonDepth + 1) "error"
                        let itemPath = path + ".value"
                        addChild budget path node "value" (valueNode itemPath (jsonDepth + 1) (valueDepth + 1) errorType item)
                    node :> JsonNode
            | TNamed expectedName, RecordValue(actualName, fields) ->
                let key, definition = nominalInfo path expectedName
                match definition with
                | IrRecordDefinition record when actualName = expectedName ->
                    if isNull (box fields) then
                        fail "VALUE_RECORD_SHAPE_MISMATCH" "Record field map cannot be null." path [ "declared field map" ] [ "null" ]
                    if record.RecordFields.Length > MaxValueNodes || fields.Count > MaxValueNodes then
                        fail "VALUE_INSPECTION_NODE_LIMIT" "Record schema or value exceeds the bounded field-node budget." path [ $"<= {MaxValueNodes} fields" ] [ string (max record.RecordFields.Length fields.Count) ]
                    let schemaNames = record.RecordFields |> List.map (fun field -> field.FieldName) |> Set.ofList
                    let actualNames = fields |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if actualNames <> schemaNames then
                        fail
                            "VALUE_RECORD_SHAPE_MISMATCH"
                            "Record value fields do not exactly match the verified declaration."
                            path
                            (schemaNames |> Set.toList |> List.sort)
                            (actualNames |> Set.toList |> List.sort)
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "record"
                    addString budget path node "name" (jsonDepth + 1) record.TypeName
                    let (ProgramTypeKey typeKey) = key
                    addInt budget path node "typeKey" (jsonDepth + 1) typeKey
                    let fieldArray = arrayNode budget (path + ".fields") (jsonDepth + 1)
                    for field in record.RecordFields |> List.sortBy (fun item -> item.FieldIndex) do
                        let fieldPath = path + ".fields." + field.FieldName
                        let fieldType = toLangType fieldPath 0 field.FieldType
                        let fieldValue = fields[field.FieldName]
                        let fieldNode = objectNode budget fieldPath (jsonDepth + 2)
                        addString budget fieldPath fieldNode "name" (jsonDepth + 3) field.FieldName
                        addChild budget fieldPath fieldNode "type" (typeNode (fieldPath + ".type") (jsonDepth + 3) 0 fieldType)
                        addChild budget fieldPath fieldNode "value" (valueNode (fieldPath + ".value") (jsonDepth + 3) (valueDepth + 1) fieldType fieldValue)
                        addArrayItem budget fieldPath fieldArray (fieldNode :> JsonNode)
                    addChild budget path node "fields" fieldArray
                    node :> JsonNode
                | IrScalarDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Record value name resolves to a scalar type in the verified snapshot." path [ "record nominal type" ] [ "scalar nominal type" ]
                | IrEnumDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Record value name resolves to an enum type in the verified snapshot." path [ "record nominal type" ] [ "enum nominal type" ]
                | IrRecordDefinition _ ->
                    fail "VALUE_TYPE_MISMATCH" "Record value name does not match its declared snapshot type." path [ expectedName ] [ if isNull actualName then "<null>" else actualName ]
            | TNamed expectedName, NamedValue(actualName, payload) ->
                let key, definition = nominalInfo path expectedName
                match definition with
                | IrScalarDefinition scalar when actualName = expectedName ->
                    let baseType = toLangType (path + ".baseType") 0 scalar.BaseType
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "scalar"
                    addString budget path node "name" (jsonDepth + 1) scalar.TypeName
                    let (ProgramTypeKey typeKey) = key
                    addInt budget path node "typeKey" (jsonDepth + 1) typeKey
                    addChild budget path node "baseType" (typeNode (path + ".baseType") (jsonDepth + 1) 0 baseType)
                    addChild budget path node "value" (valueNode (path + ".value") (jsonDepth + 1) (valueDepth + 1) baseType payload)
                    node :> JsonNode
                | IrRecordDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Scalar value name resolves to a record type in the verified snapshot." path [ "scalar nominal type" ] [ "record nominal type" ]
                | IrEnumDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Scalar value name resolves to an enum type in the verified snapshot." path [ "scalar nominal type" ] [ "enum nominal type" ]
                | IrScalarDefinition _ ->
                    fail "VALUE_TYPE_MISMATCH" "Scalar value name does not match its declared snapshot type." path [ expectedName ] [ if isNull actualName then "<null>" else actualName ]
            | TNamed expectedName, EnumValue(actualName, caseName) ->
                let key, definition = nominalInfo path expectedName
                match definition with
                | IrEnumDefinition enumDefinition when actualName = expectedName ->
                    if isNull caseName then
                        fail "VALUE_ENUM_CASE_INVALID" "Enum values require a non-null declared case name." path enumDefinition.Cases [ "null" ]
                    if not (List.contains caseName enumDefinition.Cases) then
                        fail "VALUE_ENUM_CASE_INVALID" "Enum value case is absent from the verified snapshot's closed case table." path enumDefinition.Cases [ caseName ]
                    let node = objectNode budget path jsonDepth
                    addString budget path node "kind" (jsonDepth + 1) "enum"
                    addString budget path node "name" (jsonDepth + 1) enumDefinition.TypeName
                    addString budget path node "case" (jsonDepth + 1) caseName
                    let (ProgramTypeKey typeKey) = key
                    addInt budget path node "typeKey" (jsonDepth + 1) typeKey
                    node :> JsonNode
                | IrRecordDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Enum value name resolves to a record type in the verified snapshot." path [ "enum nominal type" ] [ "record nominal type" ]
                | IrScalarDefinition _ ->
                    fail "VALUE_NOMINAL_KIND_MISMATCH" "Enum value name resolves to a scalar type in the verified snapshot." path [ "enum nominal type" ] [ "scalar nominal type" ]
                | IrEnumDefinition _ ->
                    fail "VALUE_TYPE_MISMATCH" "Enum value name does not match its declared snapshot type." path [ expectedName ] [ if isNull actualName then "<null>" else actualName ]
            | TNamed expectedName, _ ->
                match nominalInfo path expectedName |> snd with
                | IrRecordDefinition _ -> typeMismatch path expected value
                | IrScalarDefinition _ -> typeMismatch path expected value
                | IrEnumDefinition _ -> typeMismatch path expected value
            | _ -> typeMismatch path expected value

        if isNull (box values) then
            fail "VALUE_INVALID" "Runtime value collection cannot be null." "$" [ "list of values" ] [ "null" ]

        let root = objectNode budget "$" 1
        addInt budget "$" root "formatVersion" 2 FormatVersion
        let outputValues = arrayNode budget "$.values" 2
        let mutable index = 0
        for value in values do
            if isNull (box value) then
                fail "VALUE_INVALID" "Runtime value cannot be null." ($"$.values[{index}]") [ "well-formed Value" ] [ "null" ]
            let expected = Types.ofValue value
            let path = "$" + ".values[" + string index + "]"
            addArrayItem budget path outputValues (valueNode path 3 1 expected value)
            index <- index + 1
        addChild budget "$" root "values" outputValues
        root

    /// Compact stable JSON convenience form. Int64 and Float payloads are strings
    /// so adapters can preserve the exact value before normalizing into host JSON.
    let toJson verified values =
        let jsonOptions = JsonSerializerOptions(WriteIndented = false)
        jsonOptions.MaxDepth <- MaxJsonDepth
        toData verified values
        |> fun node -> node.ToJsonString(jsonOptions)
