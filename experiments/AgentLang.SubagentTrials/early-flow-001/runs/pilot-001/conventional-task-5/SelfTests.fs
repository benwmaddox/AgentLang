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

        let renewalBalanceCases =
            [|
                ("premium annual renewable applies both discounts", "premium", 100.0, "annual", true, 85.5)
                ("premium annual not renewable gets premium discount", "premium", 100.0, "annual", false, 90.0)
                ("premium monthly renewable gets premium discount", "premium", 100.0, "monthly", true, 90.0)
                ("standard annual renewable gets renewal discount", "standard", 100.0, "annual", true, 95.0)
                ("standard annual not renewable gets no discount", "standard", 100.0, "annual", false, 100.0)
                ("standard monthly renewable gets no discount", "standard", 100.0, "monthly", true, 100.0)
                ("nonexact premium kind still gets annual discount", "Premium", 100.0, "annual", true, 95.0)
                ("nonexact annual term only gets premium discount", "premium", 100.0, "Annual", true, 90.0)
                ("annual renewal discount also applies to negative balance", "standard", -100.0, "annual", true, -95.0)
            |]

        for (name, kind, balance, term, renewable, expected) in renewalBalanceCases do
            let customer = { Kind = kind; Balance = balance }
            let subscription = { Term = term; Renewable = renewable }
            assertEqual name expected (Customer.renewalBalance customer subscription)

        let renewalSavingsCases =
            [|
                ("premium annual renewable savings include both discounts", "premium", 100.0, "annual", true, 14.5)
                ("premium annual nonrenewable savings include premium discount", "premium", 100.0, "annual", false, 10.0)
                ("premium monthly renewable savings include premium discount", "premium", 100.0, "monthly", true, 10.0)
                ("standard annual renewable gets renewal savings", "standard", 100.0, "annual", true, 5.0)
                ("standard annual nonrenewable has no savings", "standard", 100.0, "annual", false, 0.0)
                ("standard monthly renewable has no savings", "standard", 100.0, "monthly", true, 0.0)
                ("nonexact premium kind still gets annual renewal savings", "Premium", 100.0, "annual", true, 5.0)
                ("nonexact annual term only gets premium savings", "premium", 100.0, "Annual", true, 10.0)
                ("annual renewal savings applies to negative balances", "standard", -100.0, "annual", true, -5.0)
                ("zero balance has zero savings", "premium", 0.0, "annual", true, 0.0)
            |]

        for (name, kind, balance, term, renewable, expected) in renewalSavingsCases do
            let customer = { Kind = kind; Balance = balance }
            let subscription = { Term = term; Renewable = renewable }
            assertEqual name expected (Customer.renewalSavings customer subscription)

        printfn "Passed %d Customer.premium, %d Customer.discountedBalance, %d Subscription.annualRenewable, %d Customer.renewalBalance, and %d Customer.renewalSavings self-tests."
            premiumCases.Length discountCases.Length subscriptionCases.Length renewalBalanceCases.Length renewalSavingsCases.Length
        0
