namespace AgentLang.MatchedRenewal.Conventional

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

module Program =
    type private RenewalCase =
        { Name: string
          Customer: Customer
          Subscription: Subscription
          Expected: float }

    let private cases =
        [ { Name = "premium-annual-renewable"
            Customer = { Kind = "premium"; Balance = 100.0 }
            Subscription = { Term = "annual"; Renewable = true }
            Expected = 85.5 }
          { Name = "premium-annual-not-renewable"
            Customer = { Kind = "premium"; Balance = 100.0 }
            Subscription = { Term = "annual"; Renewable = false }
            Expected = 90.0 }
          { Name = "premium-monthly-renewable"
            Customer = { Kind = "premium"; Balance = 100.0 }
            Subscription = { Term = "monthly"; Renewable = true }
            Expected = 90.0 }
          { Name = "premium-monthly-not-renewable"
            Customer = { Kind = "premium"; Balance = 100.0 }
            Subscription = { Term = "monthly"; Renewable = false }
            Expected = 90.0 }
          { Name = "standard-annual-renewable"
            Customer = { Kind = "standard"; Balance = 100.0 }
            Subscription = { Term = "annual"; Renewable = true }
            Expected = 100.0 }
          { Name = "standard-annual-not-renewable"
            Customer = { Kind = "standard"; Balance = 100.0 }
            Subscription = { Term = "annual"; Renewable = false }
            Expected = 100.0 }
          { Name = "standard-monthly-renewable"
            Customer = { Kind = "standard"; Balance = 100.0 }
            Subscription = { Term = "monthly"; Renewable = true }
            Expected = 100.0 }
          { Name = "standard-monthly-not-renewable"
            Customer = { Kind = "standard"; Balance = 100.0 }
            Subscription = { Term = "monthly"; Renewable = false }
            Expected = 100.0 } ]

    let private approximatelyEqual left right = abs (left - right) <= 1e-10

    let private runSelfTests () =
        let mutable failures = 0
        for testCase in cases do
            let actual = Renewal.balance testCase.Customer testCase.Subscription
            if approximatelyEqual actual testCase.Expected then
                printfn "PASS %s" testCase.Name
            else
                failures <- failures + 1
                printfn "FAIL %s: expected %.17g, received %.17g" testCase.Name testCase.Expected actual
        printfn "%d/%d renewal eligibility self-tests passed" (cases.Length - failures) cases.Length
        if failures = 0 then 0 else 1

    let private jsonError (code: string) (message: string) : JsonObject =
        let detail = JsonObject()
        detail["code"] <- JsonValue.Create(code)
        detail["message"] <- JsonValue.Create(message)
        let response = JsonObject()
        response["ok"] <- JsonValue.Create(false)
        response["error"] <- detail
        response

    let private readBoundedLine (reader: TextReader) maximumCharacters =
        let buffer = StringBuilder()
        let mutable consumed = false
        let mutable oversized = false
        let mutable next = reader.Read()
        while next >= 0 && next <> int '\n' do
            consumed <- true
            if buffer.Length < maximumCharacters then buffer.Append(char next) |> ignore
            else oversized <- true
            next <- reader.Read()
        if not consumed && next < 0 then None
        elif oversized then Some(Error "A baseline case cannot exceed 65536 characters.")
        else Some(Ok(buffer.ToString().TrimEnd('\r')))

    let private exactCase (line: string) : JsonObject =
        use document = JsonDocument.Parse(line, JsonDocumentOptions(MaxDepth = 4))
        let root = document.RootElement
        if root.ValueKind <> JsonValueKind.Object then invalidArg "line" "Each case must be a JSON object."

        let mutable kind: string option = None
        let mutable balance: float option = None
        let mutable seen = Set.empty<string>
        for property in root.EnumerateObject() do
            if Set.contains property.Name seen then invalidArg "line" $"Duplicate field '{property.Name}'."
            seen <- Set.add property.Name seen
            match property.Name with
            | "kind" when property.Value.ValueKind = JsonValueKind.String ->
                kind <- Some(property.Value.GetString())
            | "balance" when property.Value.ValueKind = JsonValueKind.Number ->
                match property.Value.TryGetDouble() with
                | true, value when Double.IsFinite value -> balance <- Some value
                | _ -> invalidArg "line" "Field 'balance' must be a finite JSON number."
            | "kind" -> invalidArg "line" "Field 'kind' must be a JSON string."
            | "balance" -> invalidArg "line" "Field 'balance' must be a JSON number."
            | name -> invalidArg "line" $"Unknown field '{name}'."

        let kind = kind |> Option.defaultWith (fun () -> invalidArg "line" "Required field 'kind' is missing.")
        if isNull kind then invalidArg "line" "Field 'kind' cannot be null."
        let balance = balance |> Option.defaultWith (fun () -> invalidArg "line" "Required field 'balance' is missing.")
        let customer = { Kind = kind; Balance = balance }
        let discountedBalance = Customer.discountedBalance customer

        let response = JsonObject()
        response["ok"] <- JsonValue.Create(true)
        response["kind"] <- JsonValue.Create kind
        response["balance"] <- JsonValue.Create balance
        response["discountedBalance"] <- JsonValue.Create discountedBalance
        response

    let private runBaselineJsonLines () =
        let options = JsonSerializerOptions(WriteIndented = false)
        let mutable line = readBoundedLine Console.In 65_536
        let mutable exitCode = 0
        while Option.isSome line do
            let response =
                match line with
                | Some(Error message) ->
                    exitCode <- 1
                    jsonError "CASE_TOO_LARGE" message
                | Some(Ok source) ->
                    try exactCase source
                    with ex ->
                        exitCode <- 1
                        jsonError "CASE_INVALID" ex.Message
                | None -> failwith "The baseline JSONL reader returned an unexpected empty request."
            Console.Out.WriteLine(response.ToJsonString(options))
            line <- readBoundedLine Console.In 65_536
        exitCode

    [<EntryPoint>]
    let main arguments =
        match arguments with
        | [| "--baseline-jsonl" |] -> runBaselineJsonLines ()
        | [||] -> runSelfTests ()
        | _ ->
            Console.Error.WriteLine("Usage: MatchedRenewal [--baseline-jsonl]")
            64
