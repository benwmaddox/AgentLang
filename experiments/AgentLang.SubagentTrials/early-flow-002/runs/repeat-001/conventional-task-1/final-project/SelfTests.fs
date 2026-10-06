namespace AgentLang.EarlyPilot

module SelfTests =
    let private check (customer: Customer) (expected: bool) =
        let actual = Customer.premium customer
        if actual <> expected then
            failwithf "Customer.premium for kind %A expected %b but returned %b" customer.Kind expected actual

    [<EntryPoint>]
    let main _ =
        let cases: (Customer * bool) array =
            [|
                ({ Kind = "premium"; Balance = 0.0 }, true)
                ({ Kind = "premium"; Balance = 125.5 }, true)
                ({ Kind = "Premium"; Balance = 0.0 }, false)
                ({ Kind = "premium "; Balance = 0.0 }, false)
                ({ Kind = ""; Balance = 0.0 }, false)
                ({ Kind = "standard"; Balance = 0.0 }, false)
            |]
        cases |> Array.iter (fun (customer, expected) -> check customer expected)
        printfn "Customer.premium self-tests passed."
        0
