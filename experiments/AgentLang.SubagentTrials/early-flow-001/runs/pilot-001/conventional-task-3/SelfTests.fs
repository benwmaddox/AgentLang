namespace AgentLang.EarlyPilot

module SelfTests =
    let private assertEqual name expected actual =
        if actual <> expected then
            failwithf "Self-test '%s' failed: expected %A, got %A" name expected actual

    [<EntryPoint>]
    let main _ =
        let premiumCases =
            [|
                ("exact kind with negative balance", "premium", -1.0, true)
                ("exact kind with positive balance", "premium", 1000.0, true)
                ("ordinary kind", "standard", 0.0, false)
                ("title case", "Premium", 0.0, false)
                ("uppercase", "PREMIUM", 0.0, false)
                ("leading whitespace", " premium", 0.0, false)
                ("trailing whitespace", "premium ", 0.0, false)
            |]

        for (name, kind, balance, expected) in premiumCases do
            let customer = { Kind = kind; Balance = balance }
            assertEqual name expected (Customer.premium customer)

        let discountCases =
            [|
                ("premium positive balance", "premium", 100.0, 90.0)
                ("premium negative balance", "premium", -40.0, -36.0)
                ("premium zero balance", "premium", 0.0, 0.0)
                ("ordinary kind", "standard", 100.0, 100.0)
                ("title case", "Premium", 100.0, 100.0)
                ("uppercase", "PREMIUM", 100.0, 100.0)
                ("leading whitespace", " premium", 100.0, 100.0)
                ("trailing whitespace", "premium ", 100.0, 100.0)
            |]

        for (name, kind, balance, expected) in discountCases do
            let customer = { Kind = kind; Balance = balance }
            assertEqual name expected (Customer.discountedBalance customer)

        let subscriptionCases =
            [|
                ("annual renewable", "annual", true, true)
                ("annual not renewable", "annual", false, false)
                ("monthly renewable", "monthly", true, false)
                ("title case term", "Annual", true, false)
                ("uppercase term", "ANNUAL", true, false)
                ("leading whitespace", " annual", true, false)
                ("trailing whitespace", "annual ", true, false)
            |]

        for (name, term, renewable, expected) in subscriptionCases do
            let subscription = { Term = term; Renewable = renewable }
            assertEqual name expected (Subscription.annualRenewable subscription)

        printfn "Passed %d Customer.premium, %d Customer.discountedBalance, and %d Subscription.annualRenewable self-tests."
            premiumCases.Length discountCases.Length subscriptionCases.Length
        0
