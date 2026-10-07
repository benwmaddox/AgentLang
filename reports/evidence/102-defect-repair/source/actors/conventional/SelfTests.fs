namespace AgentLang.BusinessPolicy

open AgentLang.Business

module SelfTests =
    let private assertEqual label expected actual =
        if actual <> expected then
            failwithf "%s: expected %A but received %A" label expected actual

    // This immutable prefix is part of every conventional task seed.
    let runSeedTests () =
        let customer = Fixtures.customer " premium " 1250L
        assertEqual "Customer preserves raw Kind" " premium " customer.Kind
        assertEqual "Money retains exact minor units" 1250L (Domain.Money.minorUnits customer.Balance)
        assertEqual "CustomerId remains nominal and canonical"
            "10000000-0000-0000-0000-000000000001"
            (Domain.CustomerId.toString customer.Id)
        assertEqual "Email remains nominal and exact" "ada@example.test" (Domain.Email.value customer.Email)
        assertEqual "CreatedAt is canonical UTC text" "2026-01-01T12:00:00.0000000+00:00" (Instant.value customer.CreatedAt)
        match Instant.parse "2026-01-01T12:00:00.0000000Z" with
        | Ok _ -> failwith "Instant must reject the noncanonical Z suffix."
        | Error _ -> ()
        match Instant.parse "2026-01-01T12:00:00" with
        | Ok _ -> failwith "Instant must reject timestamps without an explicit UTC offset."
        | Error _ -> ()
        printfn "Seed checks passed: 7"

    let runOwnTests () =
        let positivePremium = Fixtures.customer "premium" 1059L
        assertEqual "S07 premium 1059 minor units" 953L
            (CustomerPolicies.discountedBalance positivePremium |> Domain.Money.minorUnits)

        let negativePremium = Fixtures.customer "premium" -1059L
        assertEqual "S07 premium -1059 minor units" -953L
            (CustomerPolicies.discountedBalance negativePremium |> Domain.Money.minorUnits)

        let regular = Fixtures.customer "regular" 1059L
        assertEqual "S07 regular balance remains unchanged" 1059L
            (CustomerPolicies.discountedBalance regular |> Domain.Money.minorUnits)

        let smallestBalance = Fixtures.customer "premium" System.Int64.MinValue
        assertEqual "S07 premium Int64.MinValue is safe and truncates toward zero" -8301034833169298227L
            (CustomerPolicies.discountedBalance smallestBalance |> Domain.Money.minorUnits)

        let largestBalance = Fixtures.customer "premium" System.Int64.MaxValue
        assertEqual "S07 premium Int64.MaxValue is safe and truncates toward zero" 8301034833169298226L
            (CustomerPolicies.discountedBalance largestBalance |> Domain.Money.minorUnits)

        let positiveUnit = Fixtures.customer "premium" 1L
        assertEqual "S07 positive fractional minor unit truncates toward zero" 0L
            (CustomerPolicies.discountedBalance positiveUnit |> Domain.Money.minorUnits)

        let negativeUnit = Fixtures.customer "premium" -1L
        assertEqual "S07 negative fractional minor unit truncates toward zero" 0L
            (CustomerPolicies.discountedBalance negativeUnit |> Domain.Money.minorUnits)

        let mixedCaseKind = Fixtures.customer "Premium" 1059L
        assertEqual "S07 kind matching is case-sensitive" 1059L
            (CustomerPolicies.discountedBalance mixedCaseKind |> Domain.Money.minorUnits)

        let paddedKind = Fixtures.customer "premium " 1059L
        assertEqual "S07 kind matching preserves raw whitespace" 1059L
            (CustomerPolicies.discountedBalance paddedKind |> Domain.Money.minorUnits)

        printfn "Own checks passed: 9"

    let runExamples () =
        ()
