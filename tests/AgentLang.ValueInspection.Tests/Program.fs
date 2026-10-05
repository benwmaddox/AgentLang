module AgentLang.ValueInspection.Tests

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private expectDiagnostic name code (action: unit -> unit) =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private span file line =
    { File = file
      Line = line
      Column = 1
      Length = 1 }

let private snapshot () =
    let record =
        { Name = "Telemetry"
          Fields =
            [ { Name = "zeta"; Type = TString }
              { Name = "speed"; Type = TNamed "MetersPerSecond" }
              { Name = "alpha"; Type = TNamed "Email" } ]
          SourceText = "record Telemetry"
          Span = span "values.agent" 1 }
    let email =
        { Name = "Email"
          BaseType = TString
          Validator = None
          SourceText = "type Email = String"
          Span = span "values.agent" 2 }
    let speed =
        { Name = "MetersPerSecond"
          BaseType = TFloat
          Validator = None
          SourceText = "type MetersPerSecond = Float"
          Span = span "values.agent" 3 }
    let containers =
        { Name = "ContainerBox"
          Fields =
            [ { Name = "items"; Type = TList TInt }
              { Name = "optional"; Type = TOption TInt }
              { Name = "result"; Type = TResult(TInt, TString) } ]
          SourceText = "record ContainerBox"
          Span = span "values.agent" 4 }
    let words = Compiler.primitives
    let wordIds =
        words
        |> Map.toList
        |> List.map (fun (name, _) -> name, WordId("primitive-" + name))
        |> Map.ofList
    let context: Compiler.IrLoweringContext =
        { Words = words
          Records = Map.ofList [ "Telemetry", record; "ContainerBox", containers ]
          Scalars = Map.ofList [ "Email", email; "MetersPerSecond", speed ]
          WordIds = wordIds }
    Compiler.compileIrProgram context

let private prop (name: string) (node: JsonNode) = node[name]
let private at (index: int) (node: JsonNode) = node.AsArray()[index]
let private stringValue (node: JsonNode) = node.GetValue<string>()
let private intValue (node: JsonNode) = node.GetValue<int>()
let private boolValue (node: JsonNode) = node.GetValue<bool>()
let private stringField name node = node |> prop name |> stringValue
let private intField name node = node |> prop name |> intValue
let private boolField name node = node |> prop name |> boolValue

let private testNestedValuesAndSchema () =
    let verified = snapshot ()
    let emailResult = TResult(TNamed "Email", TString)
    let optionalEmailResult = TOption emailResult
    let nested =
        ListValue(
            optionalEmailResult,
            [ OptionValue(
                  emailResult,
                  Some(ResultValue(TNamed "Email", TString, Ok(NamedValue("Email", StringValue "a@example.test")))))
              OptionValue(emailResult, None)
              OptionValue(emailResult, Some(ResultValue(TNamed "Email", TString, Error(StringValue "rejected")))) ]
        )
    let telemetry =
        RecordValue(
            "Telemetry",
            Map.ofList
                [ "zeta", StringValue "north"
                  "speed", NamedValue("MetersPerSecond", FloatValue 2.5)
                  "alpha", NamedValue("Email", StringValue "a@example.test") ]
        )
    let document = ValueInspection.toData verified [ nested; telemetry ]
    check "output has a stable explicit format version" (intField "formatVersion" document = ValueInspection.FormatVersion)

    let values = prop "values" document
    let nestedNode = at 0 values
    check "nested List, Option, and Result retain explicit type tags"
        (stringField "kind" nestedNode = "list"
         && stringField "kind" (nestedNode |> prop "elementType") = "option"
         && stringField "kind" (nestedNode |> prop "elementType" |> prop "elementType") = "result")
    let items = prop "items" nestedNode
    check "all option outcomes remain distinguishable"
        (stringField "case" (at 0 items) = "some"
         && stringField "case" (at 1 items) = "none"
         && stringField "case" (at 2 items) = "some")
    check "both Result arms retain their selected case"
        (stringField "case" (at 0 items |> prop "value") = "ok"
         && stringField "case" (at 2 items |> prop "value") = "error")

    let recordNode = at 1 values
    check "record type identity is explicit" (stringField "kind" recordNode = "record" && stringField "name" recordNode = "Telemetry")
    let fields = prop "fields" recordNode |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.toList
    let fieldNames = fields |> List.map (stringField "name")
    check "record fields retain source declaration order rather than map order" (fieldNames = [ "zeta"; "speed"; "alpha" ])
    let speedField = fields |> List.find (fun field -> stringField "name" field = "speed")
    let speedType = prop "type" speedField
    let speedValue = prop "value" speedField
    check "nominal scalar preserves its own type identity and base payload"
        (stringField "kind" speedType = "nominal"
         && stringField "name" speedType = "MetersPerSecond"
         && stringField "nominalKind" speedType = "scalar"
         && stringField "kind" speedValue = "scalar"
         && stringField "name" speedValue = "MetersPerSecond"
         && stringField "kind" (prop "baseType" speedValue) = "float"
         && stringField "value" (prop "value" speedValue) = "2.5")

    let serializedOnce = ValueInspection.toJson verified [ nested; telemetry ]
    let serializedAgain = ValueInspection.toJson verified [ nested; telemetry ]
    check "same snapshot and values produce deterministic JSON" (serializedOnce = serializedAgain)
    use parsed = JsonDocument.Parse(serializedOnce)
    check "convenience JSON contains the versioned object" (parsed.RootElement.GetProperty("formatVersion").GetInt32() = 1)

let private testNominalDistinctionAndOrdering () =
    let verified = snapshot ()
    let data = ValueInspection.toData verified [ StringValue "same"; NamedValue("Email", StringValue "same"); NamedValue("MetersPerSecond", FloatValue 4.0) ]
    let values = prop "values" data
    let stringNode = at 0 values
    let emailNode = at 1 values
    let speedNode = at 2 values
    check "nominal scalar and primitive string have distinct closed tags" (stringField "kind" stringNode = "string" && stringField "kind" emailNode = "scalar")
    check "nominal type keys distinguish separate scalar declarations"
        (intField "typeKey" emailNode <> intField "typeKey" speedNode
         && stringField "name" emailNode = "Email"
         && stringField "name" speedNode = "MetersPerSecond")
    check "Float payload is separate from scalar nominal identity" (stringField "kind" (prop "value" speedNode) = "float")

let private testLosslessIntegerAndFloatEncoding () =
    let verified = snapshot ()
    let samples =
        [ IntValue Int64.MinValue
          IntValue Int64.MaxValue
          FloatValue Double.MinValue
          FloatValue Double.MaxValue
          FloatValue Double.Epsilon
          FloatValue -0.0
          FloatValue 0.0 ]
    let document = ValueInspection.toData verified samples
    let nodes = prop "values" document
    check "Int64 minimum is serialized as exact invariant decimal text"
        (stringField "value" (at 0 nodes) = Int64.MinValue.ToString(CultureInfo.InvariantCulture))
    check "Int64 maximum is serialized as exact invariant decimal text"
        (stringField "value" (at 1 nodes) = Int64.MaxValue.ToString(CultureInfo.InvariantCulture))
    for index in 2 .. 6 do
        let node = at index nodes
        let text = stringField "value" node
        let parsed = Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
        let negativeZero = boolField "negativeZero" node
        let roundTripped = if negativeZero then BitConverter.Int64BitsToDouble(Int64.MinValue) else parsed
        let source =
            match samples[index] with
            | FloatValue value -> value
            | _ -> failwith "float fixture index did not refer to FloatValue"
        check $"finite Float sample {index} round-trips its binary64 bits" (BitConverter.DoubleToInt64Bits(roundTripped) = BitConverter.DoubleToInt64Bits(source))
    check "negative zero is signaled independently for neutral JSON adapters"
        (boolField "negativeZero" (at 5 nodes) && not (boolField "negativeZero" (at 6 nodes)))

let private testInvalidHostValuesAndSnapshots () =
    let verified = snapshot ()
    let expect code name values =
        expectDiagnostic name code (fun () -> ValueInspection.toData verified values |> ignore)

    expect "VALUE_UNKNOWN_NOMINAL" "unknown scalar name" [ NamedValue("Ghost", StringValue "x") ]
    expect "VALUE_UNKNOWN_NOMINAL" "unknown nominal nested in container metadata" [ OptionValue(TNamed "Ghost", None) ]
    expect "VALUE_NOMINAL_KIND_MISMATCH" "record tag cannot claim a scalar name" [ RecordValue("Email", Map.empty) ]
    expect "VALUE_NOMINAL_KIND_MISMATCH" "scalar tag cannot claim a record name" [ NamedValue("Telemetry", StringValue "x") ]
    expect "VALUE_TYPE_MISMATCH" "scalar base payload must match its declared base type" [ NamedValue("Email", IntValue 1L) ]
    expect "VALUE_RECORD_SHAPE_MISMATCH" "record must contain every declared field" [ RecordValue("Telemetry", Map.ofList [ "zeta", StringValue "x" ]) ]
    expect "VALUE_RECORD_SHAPE_MISMATCH" "record cannot contain undeclared fields" [ RecordValue("Telemetry", Map.ofList [ "zeta", StringValue "x"; "speed", NamedValue("MetersPerSecond", FloatValue 1.0); "alpha", NamedValue("Email", StringValue "a"); "extra", UnitValue ]) ]
    expect "VALUE_TYPE_MISMATCH" "list elements must match the closed element type" [ ListValue(TInt, [ StringValue "wrong" ]) ]
    expect "VALUE_TYPE_MISMATCH" "Option payload must match the closed item type" [ OptionValue(TString, Some(IntValue 1L)) ]
    expect "VALUE_TYPE_MISMATCH" "Result error payload must match the closed error type" [ ResultValue(TInt, TString, Error(BoolValue true)) ]
    expect "VALUE_STRING_NULL" "null string payload is rejected" [ StringValue null ]
    expect "VALUE_FLOAT_NONFINITE" "NaN is rejected" [ FloatValue Double.NaN ]
    expect "VALUE_FLOAT_NONFINITE" "infinity is rejected" [ FloatValue Double.PositiveInfinity ]
    expect "VALUE_TYPE_UNRESOLVED" "unresolved container metadata is rejected" [ ListValue(TVar "T", []) ]
    expect "VALUE_CONTAINER_SHAPE_MISMATCH" "null list payload is rejected" [ ListValue(TInt, Unchecked.defaultof<Value list>) ]
    expect "VALUE_INVALID" "null active result payload is rejected" [ ResultValue(TInt, TString, Unchecked.defaultof<Result<Value, Value>>) ]

    let validBox =
        Map.ofList
            [ "items", ListValue(TInt, [])
              "optional", OptionValue(TInt, None)
              "result", ResultValue(TInt, TString, Ok(IntValue 1L)) ]
    let boxWith field value = RecordValue("ContainerBox", Map.add field value validBox)
    let deeplyNestedType = [ 1 .. 256 ] |> List.fold (fun current _ -> TOption current) TInt
    expectDiagnostic "deep list metadata is bounded before equality" "VALUE_INSPECTION_DEPTH_LIMIT" (fun () ->
        ValueInspection.toData verified [ boxWith "items" (ListValue(deeplyNestedType, [])) ] |> ignore)
    expectDiagnostic "deep option metadata is bounded before equality" "VALUE_INSPECTION_DEPTH_LIMIT" (fun () ->
        ValueInspection.toData verified [ boxWith "optional" (OptionValue(deeplyNestedType, None)) ] |> ignore)
    expectDiagnostic "deep result metadata is bounded before equality" "VALUE_INSPECTION_DEPTH_LIMIT" (fun () ->
        ValueInspection.toData verified [ boxWith "result" (ResultValue(deeplyNestedType, TString, Ok(IntValue 1L))) ] |> ignore)

    let modelOnly = IrVerifier.verify Compiler.primitiveIrCatalog (VerifiedIrProgram.inspect verified)
    expectDiagnostic "model-only verifier handles cannot inspect backend values" "IR_BACKEND_UNTRUSTED_PROGRAM" (fun () -> ValueInspection.toData modelOnly [ IntValue 1L ] |> ignore)
    expectDiagnostic "null outer collection is a structured failure" "VALUE_INVALID" (fun () ->
        ValueInspection.toData verified (Unchecked.defaultof<Value list>) |> ignore)

let private testDeterministicLimits () =
    let verified = snapshot ()
    let rec optionType depth = if depth = 0 then TInt else TOption(optionType (depth - 1))
    let rec optionValue depth =
        if depth = 0 then IntValue 1L
        else OptionValue(optionType (depth - 1), Some(optionValue (depth - 1)))
    expectDiagnostic "semantic nesting cap returns a structured limit" "VALUE_INSPECTION_DEPTH_LIMIT" (fun () -> ValueInspection.toData verified [ optionValue (ValueInspection.MaxNestingDepth + 1) ] |> ignore)
    expectDiagnostic "value-node cap returns a structured limit without truncation" "VALUE_INSPECTION_NODE_LIMIT" (fun () -> ValueInspection.toData verified (List.replicate (ValueInspection.MaxValueNodes + 1) (IntValue 0L)) |> ignore)

    let oversized = String.replicate (ValueInspection.MaxSerializedUtf8Bytes - 300) "x"
    expectDiagnostic "UTF-8 output cap rejects a large string before returning JSON" "VALUE_INSPECTION_SIZE_LIMIT" (fun () -> ValueInspection.toData verified [ StringValue oversized ] |> ignore)

let private tests =
    [ "nested values and snapshot schema", testNestedValuesAndSchema
      "nominal distinction and ordering", testNominalDistinctionAndOrdering
      "lossless integer and float encoding", testLosslessIntegerAndFloatEncoding
      "invalid host values and snapshots", testInvalidHostValuesAndSnapshots
      "deterministic limits", testDeterministicLimits ]

[<EntryPoint>]
let main _ =
    try
        for name, test in tests do
            test ()
            printfn "PASS %s" name
        printfn "AgentLang.ValueInspection.Tests: %d assertions passed." assertions
        0
    with error ->
        eprintfn "%s" error.Message
        1
