namespace AgentLang.EarlyPilot

module SelfTests =
    let private assertEqual name expected actual =
        if actual <> expected then
            failwithf "Customer.premium case '%s': expected %b, got %b" name expected actual

    [<EntryPoint>]
    let main _ =
        let cases =
            [|
                ("exact kind with negative balance", "premium", -1.0, true)
                ("exact kind with positive balance", "premium", 1000.0, true)
                ("ordinary kind", "standard", 0.0, false)
                ("title case", "Premium", 0.0, false)
                ("uppercase", "PREMIUM", 0.0, false)
                ("leading whitespace", " premium", 0.0, false)
                ("trailing whitespace", "premium ", 0.0, false)
            |]

        for (name, kind, balance, expected) in cases do
            let customer = { Kind = kind; Balance = balance }
            assertEqual name expected (Customer.premium customer)

        printfn "Passed %d Customer.premium self-tests." cases.Length
        0
