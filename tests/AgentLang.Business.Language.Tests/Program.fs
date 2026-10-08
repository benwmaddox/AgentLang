namespace AgentLang.Business.Language.Tests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Numerics
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Business

module Program =
    let mutable private assertions = 0
    let mutable private groups = 0
    let mutable private totalTests = 0
    let mutable private totalExamples = 0
    let mutable private wordsCommitted = 0
    let mutable private typesCommitted = 0
    let mutable private reloadChecks = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private stringValue (node: JsonNode) = node.GetValue<string>()
    let private boolValue (node: JsonNode) = node.GetValue<bool>()

    let private args (fields: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in fields do
            result[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation fields =
        engine.Dispatch(operation, args fields)

    let private isOk (response: JsonObject) = boolValue response.["ok"]

    let private expectOk label (response: JsonObject) =
        if not (isOk response) then
            let code =
                try stringValue response.["error"].["code"]
                with _ -> "unknown"
            let message =
                try stringValue response.["error"].["message"]
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectError label (response: JsonObject) =
        check (not (isOk response)) $"{label}: expected rejection, got {response.ToJsonString()}"
        response

    let private eval (engine: Runtime.Engine) code =
        dispatch engine "eval"
            [ "frontend", jstr "flow"
              "code", jstr code
              "structured", jbool true ]
        |> expectOk $"evaluate Flow: {code}"

    let private evalFailure (engine: Runtime.Engine) code =
        dispatch engine "eval"
            [ "frontend", jstr "flow"
              "code", jstr code ]
        |> expectError $"reject Flow: {code}"

    let private outputValue (response: JsonObject) =
        let values = response.["data"].["structuredStack"].["values"].AsArray()
        values.[0]

    let private kind (node: JsonNode) = stringValue node.["kind"]

    let private scalarPayload (node: JsonNode) =
        let payload = node.["value"]
        if kind payload = "string" || kind payload = "int" then stringValue payload.["value"]
        else failwith $"Expected scalar base payload, got {payload.ToJsonString()}"

    let private scalarString (node: JsonNode) =
        equal "scalar" (kind node) "structured value is a nominal scalar"
        scalarPayload node

    let private field (node: JsonNode) name =
        equal "record" (kind node) $"{name} owner is a structured record"
        (node.["fields"].AsArray())
        |> Seq.find (fun item -> stringValue item.["name"] = name)
        |> fun item -> item.["value"]

    let private fieldString node name =
        let value = field node name
        equal "string" (kind value) $"{name} is a structured String field"
        stringValue value.["value"]
    let private fieldScalar node name = field node name |> scalarString
    let private fieldInt64 node name = field node name |> scalarPayload |> Int64.Parse

    let private resultCase (node: JsonNode) =
        equal "result" (kind node) "operation returns a typed Result"
        stringValue node.["case"]

    let private resultValue (node: JsonNode) = node.["value"]

    let private resultError node =
        equal "error" (resultCase node) "operation returns Error"
        resultValue node

    let private resultOk node =
        equal "ok" (resultCase node) "operation returns Ok"
        resultValue node

    let private resultErrorCode node = fieldString (resultError node) "code"
    let private resultErrorMessage node = fieldString (resultError node) "message"
    let private resultErrorString node =
        let error = resultError node
        equal "string" (kind error) "string-error Result contains a String error"
        stringValue error.["value"]

    let private successValue response = outputValue response |> resultOk
    let private errorCode response = outputValue response |> resultErrorCode

    let private flowString (value: string) =
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""

    let private runGroup name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private expectDomainSuccess label result =
        match result with
        | Ok value -> value
        | Error problem -> failwith $"{label}: oracle rejected with {Domain.DomainError.code problem}: {Domain.DomainError.message problem}"

    let private recordFields (node: JsonNode) = node.["fields"].AsArray()

    let private testResults (engine: Runtime.Engine) expected =
        let response = dispatch engine "test-all" [] |> expectOk "run all authored business tests"
        let results = (response.["data"].["results"]).AsArray()
        equal expected results.Count "fixture attached test count"
        for item in results do
            let caseName = stringValue item.["name"]
            check (boolValue item.["passed"]) $"authored test passed: {caseName} ({item.ToJsonString()})"
        totalTests <- results.Count
        response

    let private exampleResultsForWord (engine: Runtime.Engine) word =
        let names =
            dispatch engine "examples" [ "word", jstr word ]
            |> expectOk $"list examples for {word}"
            |> fun response -> response.["data"].AsArray() |> Seq.map stringValue |> Seq.toList
        let response =
            dispatch engine "example" [ "word", jstr word ]
            |> expectOk $"run examples for {word}"
        let results = (response.["data"].["results"]).AsArray()
        equal names.Length results.Count $"example result count for {word}"
        for item in results do
            let caseName = stringValue item.["name"]
            check (boolValue item.["passed"]) $"example passed: {word}/{caseName}"
        results.Count

    let private ids =
        [ "customer-id.parse", (fun value -> Domain.CustomerId.parse value |> Result.map Domain.CustomerId.toString)
          "subscription-id.parse", (fun value -> Domain.SubscriptionId.parse value |> Result.map Domain.SubscriptionId.toString)
          "invoice-id.parse", (fun value -> Domain.InvoiceId.parse value |> Result.map Domain.InvoiceId.toString)
          "product-id.parse", (fun value -> Domain.ProductId.parse value |> Result.map Domain.ProductId.toString)
          "payment-id.parse", (fun value -> Domain.PaymentId.parse value |> Result.map Domain.PaymentId.toString) ]

    let private runIdCase (engine: Runtime.Engine) (word: string) (oracleParse: string -> Result<string, Domain.DomainError>) (input: string) =
        let expected = oracleParse input
        let flowWord = word.Replace('.', ':').Replace(":", "::", StringComparison.Ordinal)
        let observed = eval engine $"{flowWord}({flowString input})"
        let value = outputValue observed
        match expected with
        | Ok canonical ->
            equal "ok" (resultCase value) $"{word} accepts Guid.TryParse input"
            let actual = resultValue value |> scalarString
            equal canonical actual $"{word} normalizes exactly like Guid formatting"
        | Error problem ->
            equal "error" (resultCase value) $"{word} rejects malformed identifier"
            equal (Domain.DomainError.code problem) (resultErrorCode value) $"{word} structured oracle error code"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage value))) $"{word} structured error has a message"

    let private testIdentifierEmailAndInstantOracle (engine: Runtime.Engine) =
        let canonical = "00112233-4455-6677-8899-aabbccddeeff"
        let forms =
            [ canonical
              canonical.ToUpperInvariant()
              "00112233445566778899AABBCCDDEEFF"
              "{00112233-4455-6677-8899-aabbccddeeff}"
              "(00112233-4455-6677-8899-aabbccddeeff)" ]
        for word, parse in ids do
            for input in forms do runIdCase engine word parse input
            runIdCase engine word parse "not-a-guid"

        let validEmails =
            [ "ada@example.test"
              "first.last+tag@sub.example.com"
              "A_1%tag@example-domain.test"
              String('a', 249) + "@a.bb" ]
        let invalidEmails =
            [ ""
              "not-an-address"
              "a@b"
              "@example.test"
              "a..b@example.test"
              "a@example..test"
              "a b@example.test"
              "a@@example.test"
              "a@-example.test"
              "a@example-.test"
              String('a', 250) + "@a.bb" ]
        for input in validEmails @ invalidEmails do
            let oracle = Domain.Email.create input
            let response = eval engine $"email::parse({flowString input})"
            let result = outputValue response
            match oracle with
            | Ok email ->
                equal "ok" (resultCase result) $"email.parse agrees accepted/rejected oracle: {input}"
                equal (Domain.Email.value email) (resultValue result |> scalarString) $"Email wrapper preserves accepted input: {input}"
            | Error problem ->
                equal "error" (resultCase result) $"email.parse agrees accepted/rejected oracle: {input}"
                equal (Domain.DomainError.code problem) (resultErrorCode result) $"email.parse oracle error code: {input}"
                check (not (String.IsNullOrWhiteSpace(resultErrorMessage result))) $"email.parse error message exists: {input}"

        let instants =
            [ "2024-02-29T14:00:00+02:00"
              "2024-02-29T07:04:56.1234567-04:55"
              "2024-01-01T01:00:00+01:00" ]
        for input in instants do
            let expected =
                DateTimeOffset.Parse(input, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    .ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture)
            let response = eval engine $"instant::normalize({flowString input})"
            let observed = successValue response |> scalarString
            equal expected observed $"instant normalization agrees with DateTimeOffset for {input}"
        let badInstant = eval engine "instant::normalize(\"not-an-instant\")"
        equal "INVALID_INSTANT" (errorCode badInstant) "invalid instant returns the stable business error code"

    let private moneyMinor response =
        successValue response |> scalarPayload |> Int64.Parse

    let private moneyScalarMinor response =
        outputValue response |> scalarPayload |> Int64.Parse

    let private testMoneyOracle (engine: Runtime.Engine) =
        let additionCases =
            [ 0L, 0L
              Int64.MinValue, 1L
              Int64.MaxValue, 0L
              Int64.MaxValue, 1L
              Int64.MinValue, -1L
              -125L, 75L ]
        for left, right in additionCases do
            let oracle = Domain.Money.add (Domain.Money.ofMinorUnits left) (Domain.Money.ofMinorUnits right)
            let response = eval engine $"money::add(Money::new({left}), Money::new({right}))"
            match oracle with
            | Ok total ->
                equal (Domain.Money.minorUnits total) (moneyMinor response) $"money.add exact Int64 result for {left}+{right}"
            | Error problem ->
                equal (Domain.DomainError.code problem) (errorCode response) $"money.add overflow maps to oracle code for {left}+{right}"
                equal (Domain.DomainError.message problem) (resultErrorMessage (outputValue response)) $"money.add overflow message matches oracle"

        let int32Min = int64 Int32.MinValue
        let int32Max = int64 Int32.MaxValue
        let multiplyCases =
            [ 1L, 0L
              5L, -7L
              1L, int32Min
              1L, int32Max
              2L, int32Max
              Int64.MaxValue, 2L
              1L, int32Min - 1L
              1L, int32Max + 1L ]
        for amount, quantity in multiplyCases do
            let response = eval engine $"money::multiply-by-quantity(Money::new({amount}), {quantity})"
            if quantity < int32Min || quantity > int32Max then
                equal "INVALID_QUANTITY" (errorCode response) $"quantity {quantity} is rejected outside signed Int32"
            else
                let oracle = Domain.Money.multiplyByQuantity (Domain.Money.ofMinorUnits amount) (int quantity)
                match oracle with
                | Ok total ->
                    equal (Domain.Money.minorUnits total) (moneyMinor response) $"multiply-by-quantity agrees at signed quantity {quantity}"
                | Error problem ->
                    equal (Domain.DomainError.code problem) (errorCode response) $"multiply-by-quantity overflow code at {quantity}"
                    equal (Domain.DomainError.message problem) (resultErrorMessage (outputValue response)) "multiply-by-quantity overflow message matches oracle"

    let private expectedRatioResult (value: int64) (numerator: int64) (denominator: int64) =
        if denominator = 0L then Error "DIVIDE_BY_ZERO"
        else
            let quotient = (BigInteger(value) * BigInteger(numerator)) / BigInteger(denominator)
            if quotient < BigInteger(Int64.MinValue) || quotient > BigInteger(Int64.MaxValue) then
                Error "INT_OVERFLOW"
            else
                Ok(int64 quotient)

    let private testMoneyRatioOracle (engine: Runtime.Engine) =
        let edgeValues =
            [ Int64.MinValue; Int64.MinValue + 1L; -3L; -2L; -1L; 0L; 1L; 2L; 3L
              Int64.MaxValue - 1L; Int64.MaxValue ]
        let edgeCases =
            [ for value in edgeValues do
                for numerator in edgeValues do
                    for denominator in edgeValues do
                        yield value, numerator, denominator ]
        let random = Random(0x51CA1E)
        let nextInt64 () =
            let bytes = Array.zeroCreate<byte> sizeof<int64>
            random.NextBytes bytes
            BitConverter.ToInt64(bytes, 0)
        let generatedCases =
            [ for index = 0 to 63 do
                let denominator = if index % 13 = 0 then 0L else nextInt64 ()
                yield nextInt64 (), nextInt64 (), denominator ]
        let cases =
            [ (7L, 1L, 2L)
              (-7L, 1L, 2L)
              (1L, 1L, 2L)
              (Int64.MaxValue, 2L, 2L)
              (Int64.MaxValue, 2L, 1L)
              (Int64.MinValue, -1L, -1L) ]
            @ edgeCases
            @ generatedCases

        for value, numerator, denominator in cases do
            let response =
                eval engine
                    $"money::scale-ratio-toward-zero(amount = Money::new({value}), numerator = {numerator}, denominator = {denominator})"
            match expectedRatioResult value numerator denominator with
            | Ok expected ->
                equal expected (moneyMinor response) $"Money ratio result agrees with BigInteger for {value}*{numerator}/{denominator}"
            | Error expected ->
                equal expected (resultErrorString (outputValue response)) $"Money ratio error agrees with BigInteger for {value}*{numerator}/{denominator}"

    let private expectedBasisPointResult (amount: int64) (rate: int64) =
        int64 ((BigInteger(amount) * BigInteger(rate)) / BigInteger(10000))

    let private testMoneyBasisPointOracle (engine: Runtime.Engine) =
        let amounts =
            [ Int64.MinValue; Int64.MinValue + 1L; -10001L; -7L; -1L; 0L; 1L; 7L; 10001L
              Int64.MaxValue - 1L; Int64.MaxValue ]
        let rates = [ 0L; 1L; 4999L; 5000L; 9000L; 9999L; 10000L ]
        let edgeCases = [ for amount in amounts do for rate in rates do yield amount, rate ]
        let random = Random(0xB0515)
        let nextInt64 () =
            let bytes = Array.zeroCreate<byte> sizeof<int64>
            random.NextBytes bytes
            BitConverter.ToInt64(bytes, 0)
        let generatedCases =
            [ for _ in 0 .. 127 do
                yield nextInt64 (), int64 (random.Next(0, 10001)) ]
        let cases =
            [ Int64.MinValue, 10000L
              Int64.MaxValue, 10000L
              Int64.MinValue, 0L
              Int64.MaxValue, 0L
              -7L, 5000L
              7L, 5000L ]
            @ edgeCases
            @ generatedCases

        for amount, rate in cases do
            let response =
                eval engine
                    $"money::scale-basis-points-toward-zero(Money::new({amount}), UnitBasisPoints::new({rate}))"
            equal (expectedBasisPointResult amount rate) (moneyScalarMinor response)
                $"basis-point scaling agrees with BigInteger for {amount} at {rate} bps"

    let private idValue value = Domain.CustomerId.parse value |> expectDomainSuccess "customer ID"
    let private productIdValue value = Domain.ProductId.parse value |> expectDomainSuccess "product ID"

    let private flowCustomer id address kind balance instant =
        $"customer::new(id = CustomerId::new({flowString id}), email = Email::new({flowString address}), kind = {flowString kind}, balance = Money::new({balance}), created-at = Instant::new({flowString instant}))"

    let private flowProduct id name price =
        $"product::new(id = ProductId::new({flowString id}), name = {flowString name}, unit-price = Money::new({price}))"

    let private testConstructorOracleAndPublicRecordBoundary (engine: Runtime.Engine) =
        let idText = "00112233-4455-6677-8899-aabbccddeeff"
        let addressText = "ada@example.test"
        let instantText = "2024-02-29T12:00:00.0000000+00:00"
        let id = idValue idText
        let email = Domain.Email.create addressText |> expectDomainSuccess "Email oracle"
        let timestamp = DateTimeOffset.Parse(instantText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        let expectedCustomer =
            Domain.Customer.create id email "  partner  " (Domain.Money.ofMinorUnits 1250L) timestamp
            |> expectDomainSuccess "valid customer constructor oracle"
        let actualCustomer =
            eval engine $"customer::create(CustomerId::new({flowString idText}), Email::new({flowString addressText}), \"  partner  \", Money::new(1250), Instant::new({flowString instantText}))"
            |> successValue
        equal (Domain.Customer.kind expectedCustomer) (fieldString actualCustomer "kind") "customer.create trims kind as the F# oracle does"
        equal (Domain.CustomerId.toString (Domain.Customer.id expectedCustomer)) (fieldScalar actualCustomer "id") "customer.create preserves nominal ID"
        equal (Domain.Email.value (Domain.Customer.email expectedCustomer)) (fieldScalar actualCustomer "email") "customer.create preserves nominal Email"
        equal (Domain.Money.minorUnits (Domain.Customer.balance expectedCustomer)) (fieldInt64 actualCustomer "balance") "customer.create preserves integer minor units"
        equal ((Domain.Customer.createdAt expectedCustomer).ToString("O", CultureInfo.InvariantCulture)) (fieldScalar actualCustomer "created-at") "customer.create normalizes instant"

        let blankCustomerOracle = Domain.Customer.create id email "   " Domain.Money.zero timestamp
        let blankCustomer =
            eval engine $"customer::create(CustomerId::new({flowString idText}), Email::new({flowString addressText}), \"   \", Money::new(0), Instant::new({flowString instantText}))"
            |> outputValue
        match blankCustomerOracle with
        | Error problem ->
            equal (Domain.DomainError.code problem) (resultErrorCode blankCustomer) "blank Customer result uses oracle error code"
            equal (Domain.DomainError.message problem) (resultErrorMessage blankCustomer) "blank Customer BusinessError message matches oracle"
        | Ok _ -> failwith "oracle unexpectedly accepted blank customer kind"

        let expectedProduct =
            Domain.Product.create (productIdValue idText) "  Notebook  " (Domain.Money.ofMinorUnits 250L)
            |> expectDomainSuccess "valid product constructor oracle"
        let actualProduct = eval engine $"product::create(ProductId::new({flowString idText}), \"  Notebook  \", Money::new(250))" |> successValue
        equal (Domain.Product.name expectedProduct) (fieldString actualProduct "name") "product.create trims the name like the F# oracle"
        equal (Domain.Money.minorUnits (Domain.Product.unitPrice expectedProduct)) (fieldInt64 actualProduct "unit-price") "product.create retains exact minor units"

        let blankAndNegativeOracle = Domain.Product.create (productIdValue idText) "  " (Domain.Money.ofMinorUnits -1L)
        let blankAndNegative = eval engine $"product::create(ProductId::new({flowString idText}), \"  \", Money::new(-1))" |> outputValue
        match blankAndNegativeOracle with
        | Error problem ->
            equal "INVALID_NAME" (Domain.DomainError.code problem) "F# validation precedence chooses blank name"
            equal (Domain.DomainError.code problem) (resultErrorCode blankAndNegative) "product validation precedence matches F# oracle"
        | Ok _ -> failwith "oracle unexpectedly accepted blank product name"
        let negativeOracle = Domain.Product.create (productIdValue idText) "item" (Domain.Money.ofMinorUnits -1L)
        let negative = eval engine $"product::create(ProductId::new({flowString idText}), \"item\", Money::new(-1))" |> outputValue
        match negativeOracle with
        | Error problem -> equal (Domain.DomainError.code problem) (resultErrorCode negative) "negative product price uses oracle error code"
        | Ok _ -> failwith "oracle unexpectedly accepted negative product price"

        let invalidConstructorCases: (string * string * Result<unit, Domain.DomainError>) list =
            [ "INVALID_PROVIDER_RECEIPT",
              $"payment-receipt::create(\"  \", Money::new(1))",
              (Domain.PaymentReceipt.create "  " (Domain.Money.ofMinorUnits 1L) |> Result.map ignore)
              "INVALID_EMAIL_MESSAGE",
              $"email-message::create(Email::new({flowString addressText}), \"  \", \"body\")",
              (Domain.EmailMessage.create email "  " "body" |> Result.map ignore)
              "INVALID_EMAIL_MESSAGE",
              $"email-message::create(Email::new({flowString addressText}), \"subject\", \"  \")",
              (Domain.EmailMessage.create email "subject" "  " |> Result.map ignore) ]
        for code, expr, oracle in invalidConstructorCases do
            let agent = eval engine expr |> outputValue
            match oracle with
            | Error problem ->
                equal code (Domain.DomainError.code problem) "F# constructor oracle reports expected stable error"
                equal (Domain.DomainError.code problem) (resultErrorCode agent) "AgentLang constructor error code matches oracle"
                check (not (String.IsNullOrWhiteSpace(resultErrorMessage agent))) "AgentLang BusinessError contains a human-readable message"
            | Ok _ -> failwith $"oracle unexpectedly accepted invalid input for {expr}"

        let receiptRef = eval engine "payment-receipt::create(\"  provider-ref  \", Money::new(1))" |> successValue
        equal "provider-ref" (fieldString receiptRef "reference") "receipt trims provider reference"
        let message = eval engine $"email-message::create(Email::new({flowString addressText}), \"  Receipt ready  \", \"  Body text.  \")" |> successValue
        equal "Receipt ready" (fieldString message "subject") "email message trims subject"
        equal "Body text." (fieldString message "body") "email message trims body"

        // Records deliberately remain structurally public in this prototype.
        // The smart operations reject these inputs, while generated constructors
        // currently permit them; keep this fidelity boundary visible in evidence.
        let uncheckedCustomer = eval engine (flowCustomer idText addressText "   " 0L instantText) |> outputValue
        equal "   " (fieldString uncheckedCustomer "kind") "public Customer constructor bypasses nonblank smart-constructor rule"
        let uncheckedProduct = eval engine (flowProduct idText "Unchecked" -5L) |> outputValue
        equal -5L (fieldInt64 uncheckedProduct "unit-price") "public Product constructor bypasses nonnegative-price smart-constructor rule"
        for expression in
            [ "CustomerId::new(\"not-a-guid\")"
              "Email::new(\"not-an-email\")"
              "Instant::new(\"2024-01-01T00:00:00+01:00\")"
              "SubscriptionStatus::new(\"paused\")"
              "InvoiceStatus::new(\"draft\")" ] do
            evalFailure engine expression |> ignore

    let private addChain startName (operations: (string * string) list) =
        let rec loop state index remaining =
            match remaining with
            | [] -> state
            | (operation, valueName) :: tail ->
                let next = $"state{index + 1}"
                let problem = $"problem{index + 1}"
                let nested = loop next (index + 1) tail
                $"match store::{operation}({valueName}, {state}) {{ ok {next} => {{ {nested} }} error {problem} => {{ store::empty(unit) }} }}"
        loop startName 0 operations

    let private populateStoreWord id1 id2 id3 =
        let instant = "2024-02-29T12:00:00.0000000+00:00"
        let customer1 = flowCustomer id1 "first@example.test" "first" 11L instant
        let customer2 = flowCustomer id2 "middle@example.test" "middle" 22L instant
        let customer3 = flowCustomer id3 "last@example.test" "last" 33L instant
        let product1 = flowProduct "10112233-4455-6677-8899-aabbccddeeff" "First product" 101L
        let product2 = flowProduct "20112233-4455-6677-8899-aabbccddeeff" "Middle product" 202L
        let product3 = flowProduct "30112233-4455-6677-8899-aabbccddeeff" "Last product" 303L
        let lines =
            [ "let initial = store::empty(seed);"
              $"let customer1 = {customer1};"
              $"let customer2 = {customer2};"
              $"let customer3 = {customer3};"
              $"let product1 = {product1};"
              $"let product2 = {product2};"
              $"let product3 = {product3};" ]
        "word conformance.populate-store(seed: Unit) -> Store {\n"
        + "    effects none\n"
        + String.concat "\n" lines
        + "\n"
        + addChain "initial"
            [ "add-customer", "customer1"
              "add-customer", "customer2"
              "add-customer", "customer3"
              "add-product", "product1"
              "add-product", "product2"
              "add-product", "product3" ]
        + "\n}"

    let private firstMatchWord id =
        let first = flowCustomer id "first@example.test" "first duplicate" 11L "2024-02-29T12:00:00.0000000+00:00"
        let second = flowCustomer id "second@example.test" "second duplicate" 12L "2024-02-29T12:00:00.0000000+00:00"
        "word conformance.first-match(seed: Unit) -> Bool {\n"
        + "    effects none\n"
        + $"    let first = {first};\n"
        + $"    let second = {second};\n"
        + "    let base = store::empty(seed);\n"
        + "    let duplicateStore = store::new(customers = list::append(list::singleton<Customer>(first), second), products = base.products(), subscriptions = base.subscriptions(), invoices = base.invoices(), payments = base.payments(), email-outbox = base.email-outbox(), sent-emails = base.sent-emails());\n"
        + $"    equals(store::customer(CustomerId::new({flowString id}), duplicateStore), option::some<Customer>(first))\n"
        + "}"

    let private structuredRecord name (node: JsonNode) =
        equal "record" (kind node) $"{name} stored as structured record"
        equal name (stringValue node.["name"]) "structured record retains nominal name"
        node

    let private listItems (value: JsonNode) =
        equal "list" (kind value) "Store collection is typed List"
        value.["items"].AsArray() |> Seq.toList

    let private testStoreAndNominalBoundary (engine: Runtime.Engine) =
        let id1 = "10112233-4455-6677-8899-aabbccddeeff"
        let id2 = "20112233-4455-6677-8899-aabbccddeeff"
        let id3 = "30112233-4455-6677-8899-aabbccddeeff"
        let id4 = "40112233-4455-6677-8899-aabbccddeeff"
        let probes = populateStoreWord id1 id2 id3 + Environment.NewLine + firstMatchWord id1
        let probeDefinition = dispatch engine "define" [ "frontend", jstr "flow"; "source", jstr probes; "temporary", jbool true ] |> expectOk "define temporary Store behavior probes"
        let probeWords = probeDefinition.["data"].["words"].AsArray()
        equal 2 probeWords.Count "Store probes remain temporary user words"
        let probeNames = probeWords |> Seq.map (fun word -> stringValue word.["name"]) |> Seq.sort |> Seq.toList
        equal [ "conformance.first-match"; "conformance.populate-store" ] probeNames "temporary Store probe names are explicit"
        let definedEntries = dispatch engine "words" [] |> expectOk "inspect dictionary after temporary definition" |> fun response -> response.["data"].["words"].AsArray()
        for word in probeWords do
            let name = stringValue word.["name"]
            let entry = definedEntries |> Seq.find (fun item -> stringValue item.["name"] = name)
            equal "temporary" (stringValue entry.["status"]) $"{name} has temporary status"
        let storeValue = eval engine "conformance::populate-store(unit)" |> outputValue |> structuredRecord "Store"
        let customers = field storeValue "customers" |> listItems
        let products = field storeValue "products" |> listItems
        equal 3 customers.Length "multi-add preserves three customers"
        equal 3 products.Length "multi-add preserves three products"
        equal [ id1; id2; id3 ] (customers |> List.map (fun customer -> fieldScalar customer "id")) "customer append order is stable"
        equal [ "First product"; "Middle product"; "Last product" ] (products |> List.map (fun product -> fieldString product "name")) "product append order is stable"
        for collection in [ "subscriptions"; "invoices"; "payments"; "email-outbox"; "sent-emails" ] do
            equal 0 (field storeValue collection |> listItems |> List.length) $"empty Store collection {collection} remains empty"

        let customer1 = flowCustomer id1 "first@example.test" "first" 11L "2024-02-29T12:00:00.0000000+00:00"
        let customer2 = flowCustomer id2 "middle@example.test" "middle" 22L "2024-02-29T12:00:00.0000000+00:00"
        let customer3 = flowCustomer id3 "last@example.test" "last" 33L "2024-02-29T12:00:00.0000000+00:00"
        let lookupCode =
            "bool::and("
            + "equals(store::customer(CustomerId::new(" + flowString id1 + "), conformance::populate-store(unit)), option::some<Customer>(" + customer1 + ")),"
            + "bool::and("
            + "equals(store::customer(CustomerId::new(" + flowString id2 + "), conformance::populate-store(unit)), option::some<Customer>(" + customer2 + ")),"
            + "bool::and("
            + "equals(store::customer(CustomerId::new(" + flowString id3 + "), conformance::populate-store(unit)), option::some<Customer>(" + customer3 + ")),"
            + "equals(store::customer(CustomerId::new(" + flowString id4 + "), conformance::populate-store(unit)), option::none<Customer>()))))"
        let lookupResult = eval engine lookupCode |> outputValue
        equal "bool" (kind lookupResult) "lookup assertion returns Bool"
        check (boolValue lookupResult.["value"]) "customer lookup finds first, middle, last, and missing keys"

        let firstMatch = eval engine "conformance::first-match(unit)" |> outputValue
        check (boolValue firstMatch.["value"]) "fold lookup preserves the first duplicate match in list order"

        let customerIdValue = idValue id1
        let timestamp = DateTimeOffset.Parse("2024-02-29T12:00:00.0000000+00:00", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        let oracleCustomers =
            [ id1, "first@example.test", "first", 11L
              id2, "middle@example.test", "middle", 22L
              id3, "last@example.test", "last", 33L ]
            |> List.map (fun (idText, address, kind, balance) ->
                let id = Domain.CustomerId.parse idText |> expectDomainSuccess "store customer ID"
                let emailValue = Domain.Email.create address |> expectDomainSuccess "store oracle email"
                Domain.Customer.create id emailValue kind (Domain.Money.ofMinorUnits balance) timestamp
                |> expectDomainSuccess "store oracle customer")
        let oracleStore =
            oracleCustomers
            |> List.fold (fun store customer -> Domain.Store.addCustomer customer store |> expectDomainSuccess "oracle multi-add customer") Domain.Store.empty
        equal 3 (Domain.Store.summary oracleStore).Customers "F# oracle store includes all three additions"
        for index, customer in oracleCustomers |> List.indexed do
            let actual = List.item index customers
            equal (Domain.CustomerId.toString (Domain.Customer.id customer)) (fieldScalar actual "id") $"Store customer {index} ID matches F# oracle"
            equal (Domain.Email.value (Domain.Customer.email customer)) (fieldScalar actual "email") $"Store customer {index} Email matches F# oracle"
            equal (Domain.Customer.kind customer) (fieldString actual "kind") $"Store customer {index} kind matches F# oracle"
            equal (Domain.Money.minorUnits (Domain.Customer.balance customer)) (fieldInt64 actual "balance") $"Store customer {index} Money matches F# oracle"
            equal ((Domain.Customer.createdAt customer).ToString("O", CultureInfo.InvariantCulture)) (fieldScalar actual "created-at") $"Store customer {index} Instant matches F# oracle"
            let found = Domain.Store.customer (Domain.Customer.id customer) oracleStore
            equal (Some customer) found $"F# map lookup returns Customer {index}"
        let missingId = Domain.CustomerId.parse id4 |> expectDomainSuccess "missing store ID"
        check (Domain.Store.customer missingId oracleStore |> Option.isNone) "F# oracle reports missing customer"
        match Domain.Store.addCustomer oracleCustomers.Head oracleStore with
        | Error problem ->
            equal "DUPLICATE_CUSTOMER" (Domain.DomainError.code problem) "F# duplicate-add oracle code"
            let duplicateResult = eval engine ("store::add-customer(" + customer1 + ", conformance::populate-store(unit))") |> outputValue
            equal (Domain.DomainError.code problem) (resultErrorCode duplicateResult) "AgentLang duplicate-add code matches oracle"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage duplicateResult))) "duplicate error carries a structured message"
        | Ok _ -> failwith "F# oracle unexpectedly accepted duplicate customer ID"

        let oracleProducts =
            [ "10112233-4455-6677-8899-aabbccddeeff", "First product", 101L
              "20112233-4455-6677-8899-aabbccddeeff", "Middle product", 202L
              "30112233-4455-6677-8899-aabbccddeeff", "Last product", 303L ]
            |> List.map (fun (productIdText, name, price) ->
                let productId = Domain.ProductId.parse productIdText |> expectDomainSuccess "oracle product ID"
                Domain.Product.create productId name (Domain.Money.ofMinorUnits price) |> expectDomainSuccess "oracle product")
        let oracleProductsStore =
            oracleProducts
            |> List.fold (fun store product -> Domain.Store.addProduct product store |> expectDomainSuccess "oracle multi-add product") Domain.Store.empty
        equal 3 (Domain.Store.summary oracleProductsStore).Products "F# oracle Store includes all three products"
        for index, product in oracleProducts |> List.indexed do
            let actual = List.item index products
            equal (Domain.ProductId.toString (Domain.Product.id product)) (fieldScalar actual "id") $"Store product {index} ID matches F# oracle"
            equal (Domain.Product.name product) (fieldString actual "name") $"Store product {index} name matches F# oracle"
            equal (Domain.Money.minorUnits (Domain.Product.unitPrice product)) (fieldInt64 actual "unit-price") $"Store product {index} Money matches F# oracle"
            equal (Some product) (Domain.Store.product (Domain.Product.id product) oracleProductsStore) $"F# map lookup returns Product {index}"
        match Domain.Store.addProduct oracleProducts.Head oracleProductsStore with
        | Error problem ->
            equal "DUPLICATE_PRODUCT" (Domain.DomainError.code problem) "F# duplicate product oracle code"
            let duplicateProduct = eval engine "store::add-product(product::new(id = ProductId::new(\"10112233-4455-6677-8899-aabbccddeeff\"), name = \"First product\", unit-price = Money::new(101)), conformance::populate-store(unit))" |> outputValue
            equal (Domain.DomainError.code problem) (resultErrorCode duplicateProduct) "AgentLang duplicate product code matches oracle"
        | Ok _ -> failwith "F# oracle unexpectedly accepted duplicate product ID"

        let wrongNominal =
            evalFailure engine $"store::customer(ProductId::new({flowString id1}), store::empty(unit))"
        let diagnostic = wrongNominal.["error"]
        let detail = diagnostic.ToJsonString()
        check (detail.Contains("CustomerId", StringComparison.Ordinal) && detail.Contains("ProductId", StringComparison.Ordinal)) "wrong nominal ID diagnostic names expected and actual strong types"

    let private jsonStrings (node: JsonNode) = node.AsArray() |> Seq.map stringValue |> Seq.toList

    let private wordIds (engine: Runtime.Engine) wordNames =
        let response = dispatch engine "words" [] |> expectOk "list dictionary words"
        let words = (response.["data"].["words"]).AsArray()
        wordNames
        |> List.map (fun name ->
            let item = words |> Seq.find (fun value -> stringValue value.["name"] = name)
            name, stringValue item.["id"])

    let private sourceForWord (engine: Runtime.Engine) word =
        dispatch engine "source" [ "word", jstr word ] |> expectOk $"read source for {word}" |> fun response -> stringValue response.["data"]

    let private sourceForType (engine: Runtime.Engine) name =
        dispatch engine "source" [ "type", jstr name ] |> expectOk $"read source for type {name}" |> fun response -> stringValue response.["data"]

    let private testLibraryCommitAndReload (engine: Runtime.Engine) projectPath (document: FlowProjectDocument) =
        let names = document.Words |> List.map _.Name |> List.sort
        let typeNames = (document.Records |> List.map _.Name) @ (document.Scalars |> List.map _.Name) |> List.sort
        let idsBefore = wordIds engine names
        let sourcesBefore = names |> List.map (fun name -> name, sourceForWord engine name)
        let typeSourcesBefore = typeNames |> List.map (fun name -> name, sourceForType engine name)
        let testsBefore = names |> List.map (fun name -> name, (dispatch engine "tests" [ "word", jstr name ] |> expectOk $"query tests {name}" |> fun response -> jsonStrings response.["data"]))
        let examplesBefore = names |> List.map (fun name -> name, (dispatch engine "examples" [ "word", jstr name ] |> expectOk $"query examples {name}" |> fun response -> jsonStrings response.["data"]))

        let exampleCount = names |> List.sumBy (exampleResultsForWord engine)
        totalExamples <- exampleCount
        equal document.Examples.Length exampleCount "all authored examples were executed"

        // Re-run tests after examples so this is the active execution evidence
        // immediately before the library coverage gate.
        testResults engine document.Tests.Length |> ignore

        let committed =
            dispatch engine "commit" [ "library", jbool true ]
            |> expectOk "publish all authored words and candidate types to library maturity"
        check (committed.ContainsKey "data") "aggregate library/type commit returns a result"
        wordsCommitted <- names.Length
        typesCommitted <- typeNames.Length

        let wordsResponse = dispatch engine "words" [] |> expectOk "inspect published dictionary"
        let words = wordsResponse.["data"].["words"].AsArray()
        for name in names do
            let entry = words |> Seq.find (fun value -> stringValue value.["name"] = name)
            equal "persistent" (stringValue entry.["status"]) $"{name} persisted"
            equal "library" (stringValue entry.["maturity"]) $"{name} published as library"
            let description = dispatch engine "describe" [ "word", jstr name ] |> expectOk $"describe committed library word {name}"
            let coverage = description.["data"].["coverage"]
            equal "current" (stringValue coverage.["status"]) $"{name} has current library coverage"
            equal 0 (coverage.["uncoveredInstructions"].AsArray().Count) $"{name} has no uncovered instructions"
            equal 0 (coverage.["uncoveredBranchOutcomes"].AsArray().Count) $"{name} has no uncovered branch outcomes"

        let fresh = Runtime.Engine(projectPath, Set.empty, "2024-02-29T12:00:00.0000000+00:00")
        let idsAfter = wordIds fresh names
        equal idsBefore idsAfter "every authored word retains its stable identity after reload"
        let reloadedWordEntries = dispatch fresh "words" [] |> expectOk "inspect reloaded dictionary" |> fun response -> response.["data"].["words"].AsArray()
        for temporaryName in [ "conformance.first-match"; "conformance.populate-store" ] do
            check (reloadedWordEntries |> Seq.forall (fun item -> stringValue item.["name"] <> temporaryName)) $"temporary probe {temporaryName} is absent after reload"
            let history = dispatch fresh "history" [ "word", jstr temporaryName ] |> expectError $"query history for temporary probe {temporaryName}"
            equal "HISTORY_WORD_UNKNOWN" (stringValue history.["error"].["code"]) $"temporary probe {temporaryName} has no durable history"
        for name, source in sourcesBefore do
            equal source (sourceForWord fresh name) $"exact authored source reloads for {name}"
        for name, source in typeSourcesBefore do
            equal source (sourceForType fresh name) $"exact authored type source reloads for {name}"
        for name, cases in testsBefore do
            let actual = dispatch fresh "tests" [ "word", jstr name ] |> expectOk $"query reloaded tests {name}" |> fun response -> jsonStrings response.["data"]
            equal cases actual $"attached test names reload for {name}"
        for name, cases in examplesBefore do
            let actual = dispatch fresh "examples" [ "word", jstr name ] |> expectOk $"query reloaded examples {name}" |> fun response -> jsonStrings response.["data"]
            equal cases actual $"attached example names reload for {name}"
        let reloadedTests = testResults fresh document.Tests.Length
        let reloadedExampleCount = names |> List.sumBy (exampleResultsForWord fresh)
        equal document.Examples.Length reloadedExampleCount "all examples pass on fresh Engine reload"
        totalExamples <- reloadedExampleCount

        let postReloadMoney = eval fresh "money::multiply-by-quantity(Money::new(123), -2)" |> moneyMinor
        equal -246L postReloadMoney "reloaded business words still execute with typed structured values"
        let postReloadStore = eval fresh "store::empty(unit)" |> outputValue |> structuredRecord "Store"
        equal 0 (field postReloadStore "customers" |> listItems |> List.length) "reloaded Store constructor remains executable"
        reloadChecks <- idsAfter.Length + sourcesBefore.Length + typeSourcesBefore.Length + testsBefore.Length + examplesBefore.Length + 2
        let reloadedRows = reloadedTests.["data"].["results"].AsArray()
        check (reloadedRows.Count = document.Tests.Length) "reloaded test result inventory is complete"
        fresh

    let private testMoneyRatioExtension (engine: Runtime.Engine) projectPath source (document: FlowProjectDocument) =
        let names = document.Words |> List.map _.Name |> List.sort
        equal [ "money.scale-basis-points-toward-zero"
                "money.scale-by-90-percent"
                "money.scale-ratio-toward-zero"
                "unit-basis-points.valid?" ]
            names "the Flow/2 Money extension declares the generic ratio, validated basis-point wrapper, and fixed caller"
        equal 1 document.Scalars.Length "the Flow/2 Money extension declares one validated scalar"
        equal ("UnitBasisPoints", TInt, Some "unit-basis-points.valid?")
            (document.Scalars.Head.Name, document.Scalars.Head.BaseType, document.Scalars.Head.Validator)
            "UnitBasisPoints is an Int scalar bound to its validator"
        equal 27 document.Tests.Length "the Flow/2 Money extension attaches all generic, validation, boundary, and fixed-caller cases"
        let fixedCaller = document.Words |> List.find (fun word -> word.Name = "money.scale-by-90-percent")
        equal ([ TNamed "Money" ], [ TNamed "Money" ])
            (fixedCaller.Parameters |> List.map (fun parameter -> parameter.Type), fixedCaller.Outputs)
            "the fixed 9000 caller has a total Money-to-Money signature without a Result arm"
        equal 2 fixedCaller.SyntaxVersion "the fixed 9000 caller is authored in Flow/2"
        let defined =
            dispatch engine "define"
                [ "frontend", jstr "flow"
                  "syntaxVersion", JsonValue.Create(2) :> JsonNode
                  "source", jstr source ]
            |> expectOk "define the dedicated Flow/2 Money extension after the base library reload"
        equal names
            (defined.["data"].["words"].AsArray() |> Seq.map (fun item -> stringValue item.["name"]) |> Seq.toList |> List.sort)
            "the Flow/2 extension stages each authored word"
        equal [ "UnitBasisPoints" ] (jsonStrings defined.["data"].["types"])
            "the Flow/2 extension stages UnitBasisPoints"

        let runAttachedTests (target: Runtime.Engine) =
            let mutable total = 0
            for word in names do
                let expectedCases =
                    document.Tests
                    |> List.filter (fun test -> test.Word = word)
                    |> List.map (fun test -> test.CaseName)
                    |> List.sort
                let tested = dispatch target "test" [ "word", jstr word ] |> expectOk $"run Flow/2 tests for {word}"
                let rows = tested.["data"].["results"].AsArray()
                equal expectedCases.Length rows.Count $"all authored Flow/2 tests run for {word}"
                for item in rows do
                    let caseName = stringValue item.["name"]
                    check (boolValue item.["passed"]) $"Flow/2 test passed for {word}/{caseName}"
                let described = dispatch target "describe" [ "word", jstr word ] |> expectOk $"inspect Flow/2 coverage for {word}"
                let coverage = described.["data"].["coverage"]
                equal "current" (stringValue coverage.["status"]) $"{word} has current own-test coverage"
                equal 0 (coverage.["uncoveredInstructions"].AsArray().Count) $"{word} has no uncovered instructions"
                equal 0 (coverage.["uncoveredBranchOutcomes"].AsArray().Count) $"{word} has no uncovered branch outcomes"
                total <- total + rows.Count
            equal document.Tests.Length total "all authored Flow/2 Money tests ran"
            total

        let assertInvalidConstructors (target: Runtime.Engine) =
            for rate in [ -1L; 10001L; Int64.MinValue; Int64.MaxValue ] do
                let rejected = evalFailure target $"UnitBasisPoints::new({rate})"
                equal "REFINEMENT_FAILED" (stringValue rejected.["error"].["code"])
                    $"UnitBasisPoints rejects invalid constructor input {rate}"

        let sourcesBefore = names |> List.map (fun name -> name, sourceForWord engine name)
        let typeSourceBefore = sourceForType engine "UnitBasisPoints"
        let basisPointSource =
            sourcesBefore
            |> List.find (fun (name, _) -> name = "money.scale-basis-points-toward-zero")
            |> snd
        check (basisPointSource.StartsWith("fn money.scale-basis-points-toward-zero", StringComparison.Ordinal))
            "the validated basis-point scaler is retained in Flow/2 function syntax"
        let precommitCaseCount = runAttachedTests engine
        assertInvalidConstructors engine
        testMoneyRatioOracle engine
        testMoneyBasisPointOracle engine

        dispatch engine "commit" [ "library", jbool true ]
        |> expectOk "commit the tested Flow/2 Money functions and UnitBasisPoints type as a library"
        |> ignore
        wordsCommitted <- wordsCommitted + names.Length
        typesCommitted <- typesCommitted + document.Scalars.Length

        let committedWords = dispatch engine "words" [] |> expectOk "inspect committed Flow/2 Money words" |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            let item = committedWords |> Seq.find (fun row -> stringValue row.["name"] = name)
            equal "persistent" (stringValue item.["status"]) $"{name} persists after library qualification"
            equal "library" (stringValue item.["maturity"]) $"{name} is library-qualified"
        runAttachedTests engine |> ignore

        let fresh = Runtime.Engine(projectPath, Set.empty, "2024-02-29T12:00:00.0000000+00:00")
        for name, source in sourcesBefore do
            equal source (sourceForWord fresh name) $"Flow/2 source reloads exactly for {name}"
        equal typeSourceBefore (sourceForType fresh "UnitBasisPoints") "the validated scalar source reloads exactly"
        assertInvalidConstructors fresh
        let reloadedBeforeTestsCoverage =
            dispatch fresh "describe" [ "word", jstr "money.scale-by-90-percent" ]
            |> expectOk "inspect fresh fixed-caller coverage before rerunning its own tests"
            |> fun response -> response.["data"].["coverage"]
        equal "not-run" (stringValue reloadedBeforeTestsCoverage.["status"])
            "fresh reload does not inherit transient test coverage"
        let reloadedCaseCount = runAttachedTests fresh
        equal precommitCaseCount reloadedCaseCount "all 27 Flow/2 Money tests pass after reload"
        let reloadedFixedCall =
            eval fresh "money::scale-by-90-percent(Money::new(10000))"
            |> moneyScalarMinor
        equal 9000L reloadedFixedCall "the fixed 9000 caller executes after fresh reload"

        let publishedWords = dispatch fresh "words" [] |> expectOk "inspect committed Flow/2 Money words" |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            let item = publishedWords |> Seq.find (fun row -> stringValue row.["name"] = name)
            equal "persistent" (stringValue item.["status"]) $"{name} persists after library qualification"
            equal "library" (stringValue item.["maturity"]) $"{name} is library-qualified"
        reloadChecks <- reloadChecks + names.Length + 1
        totalTests <- totalTests + reloadedCaseCount

    let private gitValue args =
        try
            let start = ProcessStartInfo("git")
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.WorkingDirectory <- Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            for arg in args do start.ArgumentList.Add arg
            use child = Process.Start start
            if isNull child then "unavailable"
            else
                let output = child.StandardOutput.ReadToEnd().Trim()
                child.WaitForExit()
                if child.ExitCode = 0 then output else "unavailable"
        with _ -> "unavailable"

    let private writeEvidence path command status failure =
        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let target = Path.GetFullPath(path, root)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        let report = JsonObject()
        report.["name"] <- jstr "focused-business-language"
        report.["status"] <- jstr status
        report.["revision"] <- jstr (gitValue [ "rev-parse"; "HEAD" ])
        report.["dirty"] <- jbool (not (String.IsNullOrWhiteSpace(gitValue [ "status"; "--porcelain" ])))
        report.["command"] <- jstr command
        report.["result"] <- jstr (failure |> Option.defaultValue $"passed {groups} groups, {assertions} assertions")
        report.["assertions"] <- System.Text.Json.JsonSerializer.SerializeToNode assertions
        report.["groups"] <- System.Text.Json.JsonSerializer.SerializeToNode groups
        report.["authoredWords"] <- System.Text.Json.JsonSerializer.SerializeToNode wordsCommitted
        report.["authoredTypes"] <- System.Text.Json.JsonSerializer.SerializeToNode typesCommitted
        report.["attachedTests"] <- System.Text.Json.JsonSerializer.SerializeToNode totalTests
        report.["examplesRun"] <- System.Text.Json.JsonSerializer.SerializeToNode totalExamples
        report.["reloadChecks"] <- System.Text.Json.JsonSerializer.SerializeToNode reloadChecks
        report.["recordConstructorBoundary"] <- jstr "Generated record constructors are public; Customer.kind and Product.unit-price invariants can be bypassed. Scalar validators still reject invalid Email, IDs, statuses, and Instant values."
        File.WriteAllText(target, report.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine, System.Text.UTF8Encoding(false))

    [<EntryPoint>]
    let main argv =
        let evidencePath, command =
            match argv |> Array.toList with
            | [] -> None, "dotnet run --project tests/AgentLang.Business.Language.Tests/AgentLang.Business.Language.Tests.fsproj --configuration Release"
            | [ "--evidence"; path ] ->
                Some path,
                $"dotnet run --project tests/AgentLang.Business.Language.Tests/AgentLang.Business.Language.Tests.fsproj --configuration Release -- --evidence {path}"
            | _ -> failwith "Usage: AgentLang.Business.Language.Tests [--evidence <relative-or-absolute-path>]"
        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let valuesPath = Path.Combine(root, "examples", "business-values.agent")
        let storePath = Path.Combine(root, "examples", "business-store.agent")
        let valuesSource = File.ReadAllText valuesPath
        let storeSource = File.ReadAllText storePath
        let source = valuesSource + Environment.NewLine + storeSource
        let document =
            FlowParser.parseDocument "<business-foundation>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        equal 32 document.Words.Length "fixture authors exactly 32 business words"
        equal 83 document.Tests.Length "fixture attaches 83 business tests"
        equal 27 document.Examples.Length "fixture attaches 27 business examples"
        equal 16 document.Records.Length "fixture defines 16 records including fold helper states"
        equal 10 document.Scalars.Length "fixture defines 10 nominal scalar types"

        let ratioPath = Path.Combine(root, "examples", "money-ratio.agent")
        let ratioSource = File.ReadAllText ratioPath
        let ratioDocument =
            FlowParser.parseDocumentWithVersion 2 "<business-money-ratio>" ratioSource
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))

        let projectPath = Path.Combine(Path.GetTempPath(), $"agentlang-business-language-{Guid.NewGuid():N}")
        Directory.CreateDirectory projectPath |> ignore
        let engine = Runtime.Engine(projectPath, Set.empty, "2024-02-29T12:00:00.0000000+00:00")
        try
            let definition = dispatch engine "define" [ "frontend", jstr "flow"; "source", jstr source ] |> expectOk "define both business fixture files in one Engine request"
            let definedWords = definition.["data"].["words"].AsArray()
            let definedTypes = definition.["data"].["types"].AsArray()
            equal 32 definedWords.Count "one Engine definition stages every authored word"
            equal 26 definedTypes.Count "one Engine definition stages all nominal types"

            runGroup "fixture and authored cases" (fun () ->
                testResults engine document.Tests.Length |> ignore
                let count = document.Words |> List.sumBy (fun word -> exampleResultsForWord engine word.Name)
                totalExamples <- count
                equal 27 count "all fixture examples run independently of tests")

            runGroup "nominal identifiers, Email, and normalized instants against oracle" (fun () -> testIdentifierEmailAndInstantOracle engine)
            runGroup "Money checked arithmetic and signed Int32 quantity against oracle" (fun () -> testMoneyOracle engine)
            runGroup "constructor validation, trimming precedence, and public-record boundary" (fun () -> testConstructorOracleAndPublicRecordBoundary engine)
            runGroup "Store empty, multi-add, duplicate rejection, lookup order, and nominal rejection" (fun () -> testStoreAndNominalBoundary engine)
            runGroup "library/type commit, source and metadata persistence, and fresh reload" (fun () -> testLibraryCommitAndReload engine projectPath document |> ignore)
            // Reopen the now-committed base in the same project so the Flow/2 extension can
            // depend on the persisted Money nominal type without changing the base fixture.
            let fresh = Runtime.Engine(projectPath, Set.empty, "2024-02-29T12:00:00.0000000+00:00")
            runGroup "Flow/2 Money ratio wrapper, independent oracle, and fresh reload" (fun () -> testMoneyRatioExtension fresh projectPath ratioSource ratioDocument)

            printfn "PASS %d groups, %d assertions; %d attached tests; %d examples; %d words and %d types persisted and reloaded." groups assertions totalTests totalExamples wordsCommitted typesCommitted
            evidencePath |> Option.iter (fun path -> writeEvidence path command "passed" None)
            0
        with error ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions error.Message
            evidencePath |> Option.iter (fun path -> writeEvidence path command "failed" (Some error.Message))
            reraise ()
