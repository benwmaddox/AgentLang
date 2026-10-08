module AgentLang.TrustedValues.Tests

open System
open System.Globalization
open System.Numerics
open AgentLang
open AgentLang.Business.Domain
open AgentLang.Business.Contracts

let mutable private assertions = 0
let mutable private groups = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private equal name expected actual =
    assertions <- assertions + 1
    if expected <> actual then
        failwith $"{name}: expected {expected}, got {actual}"

let private group name action =
    action ()
    groups <- groups + 1
    printfn "PASS %s" name

let private canonicalGuid = "00112233-4455-6677-8899-aabbccddeeff"

let private testGuidReferenceFormsAndCanonicalStorage () =
    let validForms = [
        "00112233-4455-6677-8899-aabbccddeeff"
        "00112233445566778899aabbccddeeff"
        "{00112233-4455-6677-8899-AABBCCDDEEFF}"
        "(00112233-4455-6677-8899-aabbccddeeff)"
        "{0x00112233,0x4455,0x6677,{0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff}}"
        "  00112233-4455-6677-8899-AABBCCDDEEFF  "
    ]

    for value in validForms do
        let reference = CustomerId.parse value |> Result.map CustomerId.toString
        equal $"reference Guid.TryParse form {value}" (Ok canonicalGuid) reference
        equal $"normalize Guid.TryParse form {value}" (Ok canonicalGuid) (TrustedValues.guidNormalize value)

    let invalidForms = [
        ""
        "not-a-guid"
        "00112233-4455-6677-8899-aabbccddeefg"
        "{00112233-4455-6677-8899-aabbccddeeff"
        "{00112233445566778899AABBCCDDEEFF}"
    ]
    for value in invalidForms do
        check $"reference rejects malformed GUID {value}" (CustomerId.parse value |> Result.isError)
        equal $"normalizer reports malformed GUID {value}" (Error "INVALID_GUID") (TrustedValues.guidNormalize value)

    check "canonical lowercase D form is accepted" (TrustedValues.guidCanonical canonicalGuid)
    check "uppercase D form is not canonical storage text" (not (TrustedValues.guidCanonical (canonicalGuid.ToUpperInvariant())))
    check "N form is not canonical storage text" (not (TrustedValues.guidCanonical "00112233445566778899aabbccddeeff"))
    check "braced form is not canonical storage text" (not (TrustedValues.guidCanonical ("{" + canonicalGuid + "}")))
    check "whitespace is not canonical storage text" (not (TrustedValues.guidCanonical (" " + canonicalGuid)))
    check "malformed text is not canonical storage text" (not (TrustedValues.guidCanonical "bad"))
    check "null is not canonical storage text" (not (TrustedValues.guidCanonical null))
    equal "null GUID normalization is a value error" (Error "INVALID_GUID") (TrustedValues.guidNormalize null)

let private testEmailPolicyAgainstReference () =
    let longestAccepted = "a@" + String.replicate 250 "d" + ".t"
    let tooLong = "a@" + String.replicate 251 "d" + ".t"
    equal "254-character adversarial address fixture" 254 longestAccepted.Length
    equal "255-character adversarial address fixture" 255 tooLong.Length

    let accepted = [
        "ada@example.test"
        "first.last+tag@sub.example.com"
        "A_1%tag@ex-ample.test"
        "a@b.c"
        longestAccepted
    ]
    let rejected = [
        ""
        "not-an-address"
        "a@b"
        "@example.test"
        ".a@example.test"
        "a..b@example.test"
        "a.@example.test"
        "a@example..test"
        "a@-example.test"
        "a@example-.test"
        "a b@example.test"
        "a@@example.test"
        "a\u00e9@example.test"
        "a@exampl\u00e9.test"
        "a@example.test\n"
        "a@example.test\r\n"
        tooLong
    ]

    for value in accepted do
        check $"reference accepts address {value}" (Email.create value |> Result.isOk)
        check $"trusted validator accepts reference address {value}" (TrustedValues.emailAddressValid value)

    for value in rejected do
        check $"reference rejects address {value}" (Email.create value |> Result.isError)
        check $"trusted validator rejects reference address {value}" (not (TrustedValues.emailAddressValid value))

    let corpus = accepted @ rejected
    for value in corpus do
        equal $"email policy agrees with immutable reference for {value}" (Email.create value |> Result.isOk) (TrustedValues.emailAddressValid value)
    check "null email is rejected by the reference" (Email.create null |> Result.isError)
    check "null email is rejected by the trusted validator" (not (TrustedValues.emailAddressValid null))

let private expectedInt64Result (value: BigInteger) =
    if value < BigInteger(Int64.MinValue) || value > BigInteger(Int64.MaxValue) then
        Error "INT_OVERFLOW"
    else
        Ok(int64 value)

let private expectedScaleRatioResult (value: int64) (numerator: int64) (denominator: int64) =
    if denominator = 0L then Error "DIVIDE_BY_ZERO"
    else
        expectedInt64Result ((BigInteger(value) * BigInteger(numerator)) / BigInteger(denominator))

let private helperOverflowAsMoneyCode = function
    | Ok value -> Ok value
    | Error "INT_OVERFLOW" -> Error "MONEY_OVERFLOW"
    | Error code -> Error code

let private moneyReferenceResult = function
    | Ok money -> Ok(Money.minorUnits money)
    | Error error -> Error(DomainError.code error)

let private testCheckedIntegerArithmeticAgainstBigIntegerAndMoney () =
    let values = [ Int64.MinValue; -3_037_000_500L; -1L; 0L; 1L; 3_037_000_499L; 3_037_000_500L; Int64.MaxValue ]

    for left in values do
        for right in values do
            let expectedSum = expectedInt64Result (BigInteger(left) + BigInteger(right))
            let actualSum = TrustedValues.addChecked left right
            equal $"checked addition matches exact BigInteger oracle for {left} + {right}" expectedSum actualSum
            let referenceSum = Money.add (Money.ofMinorUnits left) (Money.ofMinorUnits right) |> moneyReferenceResult
            equal $"checked addition matches Money.add for {left} + {right}" referenceSum (helperOverflowAsMoneyCode actualSum)

            let expectedProduct = expectedInt64Result (BigInteger(left) * BigInteger(right))
            let actualProduct = TrustedValues.multiplyChecked left right
            equal $"checked multiplication matches exact BigInteger oracle for {left} * {right}" expectedProduct actualProduct

    let referenceAmounts = [ Int64.MinValue; -2L; -1L; 0L; 1L; 2L; Int64.MaxValue ]
    let referenceQuantities = [ Int32.MinValue; -1; 0; 1; 2; Int32.MaxValue ]
    for amount in referenceAmounts do
        for quantity in referenceQuantities do
            let actual = TrustedValues.multiplyChecked amount (int64 quantity)
            let reference = Money.multiplyByQuantity (Money.ofMinorUnits amount) quantity |> moneyReferenceResult
            equal $"checked multiplication matches Money.multiplyByQuantity for {amount} * {quantity}" reference (helperOverflowAsMoneyCode actual)

    equal "adding the signed extremes reports low-level overflow" (Error "INT_OVERFLOW") (TrustedValues.addChecked Int64.MaxValue 1L)
    equal "subtracting below Int64.MinValue reports low-level overflow" (Error "INT_OVERFLOW") (TrustedValues.addChecked Int64.MinValue -1L)
    equal "minimum multiplied by negative one reports low-level overflow" (Error "INT_OVERFLOW") (TrustedValues.multiplyChecked Int64.MinValue -1L)
    equal "multiplication by zero remains exact at the maximum" (Ok 0L) (TrustedValues.multiplyChecked Int64.MaxValue 0L)

let private testScaleRatioAgainstBigIntegerOracle () =
    let edgeValues =
        [ Int64.MinValue; Int64.MinValue + 1L; -3_037_000_500L; -3L; -2L; -1L
          0L; 1L; 2L; 3L; 3_037_000_499L; Int64.MaxValue - 1L; Int64.MaxValue ]

    for value in edgeValues do
        for numerator in edgeValues do
            for denominator in edgeValues do
                let expected = expectedScaleRatioResult value numerator denominator
                equal
                    $"ratio scaling matches exact BigInteger oracle for {value} * {numerator} / {denominator}"
                    expected
                    (TrustedValues.scaleRatioTowardZero value numerator denominator)

    let random = Random(0x51CA1E)
    let nextInt64 () =
        let bytes = Array.zeroCreate<byte> sizeof<int64>
        random.NextBytes bytes
        BitConverter.ToInt64(bytes, 0)
    for index = 0 to 511 do
        let value = nextInt64 ()
        let numerator = nextInt64 ()
        let denominator = if index % 17 = 0 then 0L else nextInt64 ()
        equal
            $"generated ratio case {index} matches independent BigInteger oracle"
            (expectedScaleRatioResult value numerator denominator)
            (TrustedValues.scaleRatioTowardZero value numerator denominator)

    equal "positive fractional quotient truncates toward zero" (Ok 3L) (TrustedValues.scaleRatioTowardZero 7L 1L 2L)
    equal "negative fractional quotient truncates toward zero" (Ok -3L) (TrustedValues.scaleRatioTowardZero -7L 1L 2L)
    equal "a small positive fraction truncates to zero" (Ok 0L) (TrustedValues.scaleRatioTowardZero 1L 1L 2L)
    equal "a small negative fraction truncates to zero" (Ok 0L) (TrustedValues.scaleRatioTowardZero -1L 1L 2L)
    equal "negative numerator and denominator preserve a positive quotient" (Ok 3L) (TrustedValues.scaleRatioTowardZero 7L -1L -2L)
    equal "intermediate product overflow can have an exact representable quotient"
        (Ok Int64.MaxValue)
        (TrustedValues.scaleRatioTowardZero Int64.MaxValue 2L 2L)
    equal "the minimum signed quotient remains representable after a wide intermediate"
        (Ok Int64.MinValue)
        (TrustedValues.scaleRatioTowardZero Int64.MinValue -1L -1L)
    equal "a zero denominator returns a structured value error even for a zero numerator"
        (Error "DIVIDE_BY_ZERO")
        (TrustedValues.scaleRatioTowardZero 0L 0L 0L)
    equal "a quotient above Int64.MaxValue reports checked overflow"
        (Error "INT_OVERFLOW")
        (TrustedValues.scaleRatioTowardZero Int64.MaxValue 2L 1L)
    equal "a quotient above Int64.MaxValue reports checked overflow when MinValue is negated"
        (Error "INT_OVERFLOW")
        (TrustedValues.scaleRatioTowardZero Int64.MinValue -1L 1L)

let private contractInstantFixture (value: string) =
    let quoted = System.Text.Json.JsonSerializer.Serialize(value)
    "{\"schema\":" + System.Text.Json.JsonSerializer.Serialize(Contract.Schema)
    + ",\"version\":" + string Contract.Version
    + ",\"customers\":[{\"createdAt\":" + quoted + "}]}"

let private referenceInstant value =
    match Contract.Fragment.parseCustomerFragments (contractInstantFixture value) with
    | Ok parsed ->
        match parsed.Customers with
        | [ { CreatedAt = Some instant } ] ->
            Ok(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        | _ -> failwith "reference instant fixture did not produce exactly one timestamp"
    | Error diagnostic -> Error diagnostic.Code

let private parseReferenceFormatted (value: string) =
    match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, instant -> instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    | false, _ -> failwith $"test vector must be a valid explicit-zone instant: {value}"

let private testInstantParsingAgainstImmutableContract () =
    let accepted = [
        "2024-02-29T23:15:04Z"
        "2024-02-29T23:15:04.1+02:30"
        "2024-02-29T23:15:04.1234567-05:00"
        "2026-01-01T00:00:00z"
        "9999-12-31T23:59:59.9999999+00:00"
    ]
    for value in accepted do
        let expected = referenceInstant value
        equal $"trusted parse matches contract parser for {value}" expected (TrustedValues.parseUtc value)
        equal $"normalized output matches DateTimeOffset invariant round-trip for {value}" (parseReferenceFormatted value) (Result.defaultValue "" (TrustedValues.parseUtc value))

    equal "offset input normalizes across the leap day with exact fractional precision"
        (Ok "2024-02-29T20:45:04.1000000+00:00")
        (TrustedValues.parseUtc "2024-02-29T23:15:04.1+02:30")
    equal "negative offset normalization crosses into the next UTC date"
        (Ok "2024-03-01T04:15:04.1234567+00:00")
        (TrustedValues.parseUtc "2024-02-29T23:15:04.1234567-05:00")

    let rejected = [
        ""
        "2024-01-01 12:00:00Z"
        "2024-1-1T12:00:00Z"
        "2024-01-01T12:00:00"
        "2024-01-01T12:00:00+0000"
        "2024-01-01T12:00:00.12345678Z"
        "2024-02-30T12:00:00Z"
        "2024-01-01T25:00:00Z"
        "2024-01-01T12:00:00+14:01"
        "0001-01-01T00:00:00+14:00"
    ]
    for value in rejected do
        equal $"contract rejects malformed/out-of-range instant {value}" (Error "INVALID_INSTANT") (referenceInstant value)
        equal $"trusted parser rejects malformed/out-of-range instant {value}" (Error "INVALID_INSTANT") (TrustedValues.parseUtc value)

    equal "null parse is a structured invalid-instant result" (Error "INVALID_INSTANT") (TrustedValues.parseUtc null)

    let before = CultureInfo.CurrentCulture
    let beforeUi = CultureInfo.CurrentUICulture
    try
        for cultureName in [ "fr-FR"; "tr-TR" ] do
            let culture = CultureInfo(cultureName)
            CultureInfo.CurrentCulture <- culture
            CultureInfo.CurrentUICulture <- culture
            equal $"parsing stays invariant in {cultureName}"
                (Ok "2024-02-29T20:45:04.1000000+00:00")
                (TrustedValues.parseUtc "2024-02-29T23:15:04.1+02:30")
    finally
        CultureInfo.CurrentCulture <- before
        CultureInfo.CurrentUICulture <- beforeUi

let private testCanonicalInstantComparisonAndCheckedDayArithmetic () =
    let canonical = [
        "0001-01-01T00:00:00.0000000+00:00"
        "2024-02-29T23:59:59.1234567+00:00"
        "9999-12-31T23:59:59.9999999+00:00"
    ]
    for value in canonical do
        check $"exact round-trip UTC representation is canonical: {value}" (TrustedValues.instantIsCanonicalUtc value)

    let noncanonical = [
        "2024-02-29T23:59:59Z"
        "2024-02-29T23:59:59.123456+00:00"
        "2024-02-29T23:59:59.1234567Z"
        "2024-02-29T23:59:59.1234567+01:00"
        "2024-02-29 23:59:59.1234567+00:00"
        "2024-02-29T23:59:59.12345678+00:00"
        "2024-02-30T23:59:59.1234567+00:00"
    ]
    for value in noncanonical do
        check $"near-canonical or malformed representation is rejected: {value}" (not (TrustedValues.instantIsCanonicalUtc value))
    check "null is not canonical UTC" (not (TrustedValues.instantIsCanonicalUtc null))

    let first = "2024-02-29T23:59:59.1234566+00:00"
    let nextFraction = "2024-02-29T23:59:59.1234567+00:00"
    let nextSecond = "2024-03-01T00:00:00.0000000+00:00"
    equal "instant comparison sees a one-tick fraction difference" (Ok true) (TrustedValues.instantBefore first nextFraction)
    equal "instant comparison sees a calendar boundary" (Ok true) (TrustedValues.instantBefore nextFraction nextSecond)
    equal "instant comparison is false at equality" (Ok false) (TrustedValues.instantBefore nextFraction nextFraction)
    equal "instant comparison is false in reverse order" (Ok false) (TrustedValues.instantBefore nextSecond first)
    equal "noncanonical comparison input is an error" (Error "INVALID_INSTANT") (TrustedValues.instantBefore "2024-02-29T23:59:59Z" nextSecond)
    equal "null comparison input is an error" (Error "INVALID_INSTANT") (TrustedValues.instantBefore null nextSecond)

    let start = "2024-02-28T12:34:56.1234567+00:00"
    for days in [ -365L; -1L; 0L; 1L; 2L; 365L ] do
        let expected =
            let instant = DateTimeOffset.ParseExact(start, "O", CultureInfo.InvariantCulture, DateTimeStyles.None)
            Ok(instant.AddDays(float days).ToString("O", CultureInfo.InvariantCulture))
        equal $"day arithmetic agrees with DateTimeOffset.AddDays for {days} days" expected (TrustedValues.instantAddDays start days)

    equal "leap day addition advances by calendar days"
        (Ok "2024-02-29T12:34:56.1234567+00:00")
        (TrustedValues.instantAddDays start 1L)
    equal "two days from leap eve reaches March 1"
        (Ok "2024-03-01T12:34:56.1234567+00:00")
        (TrustedValues.instantAddDays start 2L)
    equal "invalid add-days input is a value error" (Error "INVALID_INSTANT") (TrustedValues.instantAddDays "2024-02-28T12:34:56Z" 1L)
    equal "maximum date plus one day reports range" (Error "INSTANT_RANGE") (TrustedValues.instantAddDays "9999-12-31T23:59:59.9999999+00:00" 1L)
    equal "minimum date minus one day reports range" (Error "INSTANT_RANGE") (TrustedValues.instantAddDays "0001-01-01T00:00:00.0000000+00:00" -1L)
    equal "huge positive day count reports range" (Error "INSTANT_RANGE") (TrustedValues.instantAddDays start Int64.MaxValue)
    equal "huge negative day count reports range" (Error "INSTANT_RANGE") (TrustedValues.instantAddDays start Int64.MinValue)

let private span file line column length =
    { File = file
      Line = line
      Column = column
      Length = length }

let private trustedContext () : Compiler.IrLoweringContext =
    let words = Compiler.primitives
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
    { Words = words
      Records = Map.empty
      Scalars = Map.empty
      WordIds = wordIds }

let private noOpHost effectCounter =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ ->
          effectCounter ()
          EffectUnit
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let private trustedPrimitiveContracts = [
    "string.guid-canonical?", [ TString ], [ TBool ]
    "string.guid-normalize", [ TString ], [ TResult(TString, TString) ]
    "string.email-address-valid?", [ TString ], [ TBool ]
    "int.add-checked", [ TInt; TInt ], [ TResult(TInt, TString) ]
    "int.multiply-checked", [ TInt; TInt ], [ TResult(TInt, TString) ]
    "int.scale-ratio-toward-zero", [ TInt; TInt; TInt ], [ TResult(TInt, TString) ]
    "instant.parse-utc", [ TString ], [ TResult(TString, TString) ]
    "instant.is-canonical-utc?", [ TString ], [ TBool ]
    "instant.before?", [ TString; TString ], [ TBool ]
    "instant.add-days", [ TString; TInt ], [ TResult(TString, TString) ]
]

let private expectDiagnostic name code action =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
        Some diagnostic
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private testRegisteredPrimitiveContracts () =
    for name, inputs, outputs in trustedPrimitiveContracts do
        match Compiler.primitives.TryFind name with
        | None -> failwith $"trusted primitive {name} is not registered"
        | Some entry ->
            equal $"{name} input signature" inputs entry.Definition.Inputs
            equal $"{name} output signature" outputs entry.Definition.Outputs
            equal $"{name} is implemented by its named trusted operation" (Some(BuiltinOp name)) entry.Builtin
            equal $"{name} declares no effects" Set.empty entry.Definition.Effects
            check $"{name} has public documentation metadata" (not (String.IsNullOrWhiteSpace entry.Definition.Documentation))

    let documentation name = Compiler.primitives[name].Definition.Documentation
    check "GUID normalization documents Guid.TryParse input coverage" ((documentation "string.guid-normalize").Contains("Guid.TryParse", StringComparison.Ordinal))
    check "email primitive documents the bounded ASCII policy" ((documentation "string.email-address-valid?").Contains("254 characters", StringComparison.Ordinal))
    check "checked arithmetic documents its structured overflow code" ((documentation "int.add-checked").Contains("INT_OVERFLOW", StringComparison.Ordinal))
    check "ratio arithmetic documents its exact product, truncation policy, and error codes"
        ((documentation "int.scale-ratio-toward-zero").Contains("full signed Int64 input range", StringComparison.Ordinal)
         && (documentation "int.scale-ratio-toward-zero").Contains("truncating the quotient toward zero", StringComparison.Ordinal)
         && (documentation "int.scale-ratio-toward-zero").Contains("DIVIDE_BY_ZERO", StringComparison.Ordinal)
         && (documentation "int.scale-ratio-toward-zero").Contains("INT_OVERFLOW", StringComparison.Ordinal))
    check "instant parser documents exact precision and explicit zone rules" ((documentation "instant.parse-utc").Contains("one-to-seven fractional digits", StringComparison.Ordinal))
    check "instant comparison documents its invalid-input runtime diagnostic" ((documentation "instant.before?").Contains("RUNTIME_INVALID_INSTANT", StringComparison.Ordinal))
    check "instant day addition documents range errors" ((documentation "instant.add-days").Contains("INSTANT_RANGE", StringComparison.Ordinal))

let private executePrimitiveCall program host name arguments callSpan =
    let expressions =
        (arguments |> List.map (fun literal -> Push(literal, callSpan)))
        @ [ Call(name, callSpan) ]
    let body = Compiler.compileIrBodyAgainstProgram (trustedContext ()) program name [] expressions
    IrInterpreter.executeBody host name body

let private oneResult label = function
    | [ value ] -> value
    | values -> failwith $"{label}: expected one output, got {List.length values}"

let private testTypedIrRuntimeAndFailureBoundaries () =
    let context = trustedContext ()
    let program = Compiler.compileIrProgram context
    let mutable hostEffects = 0
    let host = noOpHost (fun () -> hostEffects <- hostEffects + 1)
    let site = span "trusted-values.agent" 7 5 18
    let call name arguments = executePrimitiveCall program host name arguments site |> oneResult name

    let canonicalFlag = call "string.guid-canonical?" [ LString canonicalGuid ]
    equal "GUID predicate executes through verified IR" (BoolValue true) canonicalFlag
    equal "GUID predicate retains Bool type through verified IR" TBool (Types.ofValue canonicalFlag)

    let normalizedGuid = call "string.guid-normalize" [ LString "{00112233-4455-6677-8899-AABBCCDDEEFF}" ]
    equal "GUID normalizer returns a typed success value through verified IR"
        (ResultValue(TString, TString, Ok(StringValue canonicalGuid))) normalizedGuid
    equal "GUID Result retains String/String type arguments" (TResult(TString, TString)) (Types.ofValue normalizedGuid)
    let invalidGuid = call "string.guid-normalize" [ LString "malformed" ]
    equal "GUID normalizer returns a typed error value through verified IR"
        (ResultValue(TString, TString, Error(StringValue "INVALID_GUID"))) invalidGuid

    let acceptedEmail = call "string.email-address-valid?" [ LString "first.last+tag@sub.example.test" ]
    equal "email predicate executes its accepted branch" (BoolValue true) acceptedEmail
    equal "email predicate returns Bool" TBool (Types.ofValue acceptedEmail)
    equal "email predicate executes its rejected branch" (BoolValue false) (call "string.email-address-valid?" [ LString "a..b@example.test" ])

    let checkedSum = call "int.add-checked" [ LInt 40L; LInt 2L ]
    equal "checked add returns a typed Result through verified IR"
        (ResultValue(TInt, TString, Ok(IntValue 42L))) checkedSum
    equal "checked add retains Int/String Result type arguments" (TResult(TInt, TString)) (Types.ofValue checkedSum)
    equal "checked multiply returns an error value instead of a runtime failure"
        (ResultValue(TInt, TString, Error(StringValue "INT_OVERFLOW")))
        (call "int.multiply-checked" [ LInt Int64.MaxValue; LInt 2L ])
    let scaledRatio = call "int.scale-ratio-toward-zero" [ LInt -7L; LInt 1L; LInt 2L ]
    equal "ratio primitive returns a typed Result through verified IR"
        (ResultValue(TInt, TString, Ok(IntValue -3L))) scaledRatio
    equal "ratio primitive preserves Int/String Result type arguments"
        (TResult(TInt, TString)) (Types.ofValue scaledRatio)
    equal "ratio primitive returns division by zero as a structured Result error"
        (ResultValue(TInt, TString, Error(StringValue "DIVIDE_BY_ZERO")))
        (call "int.scale-ratio-toward-zero" [ LInt 0L; LInt 0L; LInt 0L ])
    equal "ratio primitive returns final overflow as a structured Result error"
        (ResultValue(TInt, TString, Error(StringValue "INT_OVERFLOW")))
        (call "int.scale-ratio-toward-zero" [ LInt Int64.MaxValue; LInt 2L; LInt 1L ])

    let normalizedInstant = call "instant.parse-utc" [ LString "2024-02-29T23:15:04.1+02:30" ]
    equal "instant parser returns the normalized typed Result through verified IR"
        (ResultValue(TString, TString, Ok(StringValue "2024-02-29T20:45:04.1000000+00:00"))) normalizedInstant
    equal "instant parser preserves String/String Result type arguments" (TResult(TString, TString)) (Types.ofValue normalizedInstant)
    let canonicalFlag = call "instant.is-canonical-utc?" [ LString "2024-02-29T20:45:04.1000000+00:00" ]
    equal "canonical instant predicate executes through IR" (BoolValue true) canonicalFlag
    equal "instant before returns false at the equality boundary" (BoolValue false)
        (call "instant.before?" [ LString "2024-02-29T20:45:04.1000000+00:00"; LString "2024-02-29T20:45:04.1000000+00:00" ])
    equal "instant add-days returns a typed success Result through IR"
        (ResultValue(TString, TString, Ok(StringValue "2024-03-01T20:45:04.1000000+00:00")))
        (call "instant.add-days" [ LString "2024-02-29T20:45:04.1000000+00:00"; LInt 1L ])

    let badCallSpan = span "trusted-values.agent" 23 9 15
    let invalidBeforeDiagnostic =
        expectDiagnostic "invalid instant.before? is a runtime diagnostic" "RUNTIME_INVALID_INSTANT" (fun () ->
            executePrimitiveCall program host "instant.before?"
                [ LString "2024-02-29T23:59:59Z"; LString "2024-03-01T00:00:00.0000000+00:00" ]
                badCallSpan
            |> ignore)
    match invalidBeforeDiagnostic with
    | Some diagnostic -> equal "invalid before diagnostic points to the source call" (Some badCallSpan) diagnostic.Span
    | None -> failwith "expected the invalid before diagnostic"

    expectDiagnostic "wrong primitive operand types fail during typed IR compilation" "TYPE_STACK_MISMATCH" (fun () ->
        let wrongTypes = [
            Push(LString "2024-01-01T00:00:00.0000000+00:00", site)
            Push(LInt 1L, site)
            Call("instant.before?", site)
        ]
        Compiler.compileIrBodyAgainstProgram context program "wrong-trusted-value-types" [] wrongTypes |> ignore)
    |> ignore

    expectDiagnostic "ratio primitive rejects non-Int operands during typed IR compilation" "TYPE_STACK_MISMATCH" (fun () ->
        let wrongTypes = [
            Push(LInt 1L, site)
            Push(LString "2", site)
            Push(LInt 3L, site)
            Call("int.scale-ratio-toward-zero", site)
        ]
        Compiler.compileIrBodyAgainstProgram context program "wrong-ratio-operand-types" [] wrongTypes |> ignore)
    |> ignore

    expectDiagnostic "legacy add retains its runtime overflow diagnostic" "RUNTIME_OVERFLOW" (fun () ->
        executePrimitiveCall program host "add" [ LInt Int64.MaxValue; LInt 1L ] site |> ignore)
    |> ignore
    expectDiagnostic "legacy multiply retains its runtime overflow diagnostic" "RUNTIME_OVERFLOW" (fun () ->
        executePrimitiveCall program host "multiply" [ LInt Int64.MaxValue; LInt 2L ] site |> ignore)
    |> ignore
    equal "pure trusted primitive calls never invoke a host effect" 0 hostEffects

[<EntryPoint>]
let main _ =
    try
        group "GUID normalization and canonical storage" testGuidReferenceFormsAndCanonicalStorage
        group "Email validation against the conventional fixture" testEmailPolicyAgainstReference
        group "checked Int64 arithmetic against BigInteger and Money" testCheckedIntegerArithmeticAgainstBigIntegerAndMoney
        group "exact ratio scaling against an independent BigInteger oracle" testScaleRatioAgainstBigIntegerOracle
        group "instant parsing against the immutable contract" testInstantParsingAgainstImmutableContract
        group "canonical UTC comparison and day arithmetic" testCanonicalInstantComparisonAndCheckedDayArithmetic
        group "primitive metadata and typed IR execution" testRegisteredPrimitiveContracts
        group "typed IR runtime and failure boundaries" testTypedIrRuntimeAndFailureBoundaries
        printfn "Trusted values tests: %d groups, %d assertions" groups assertions
        0
    with error ->
        eprintfn "FAIL after %d assertions: %s" assertions error.Message
        1
