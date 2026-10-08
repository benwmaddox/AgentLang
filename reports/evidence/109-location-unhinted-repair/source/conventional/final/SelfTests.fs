namespace AgentLang.BusinessPolicy

open System
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
        let kindCases =
            [ "premium", true
              "Premium", false
              "PREMIUM", false
              " premium", false
              "premium ", false
              "", false ]

        for kind, expected in kindCases do
            let customer = Fixtures.customer kind 1059L
            assertEqual (sprintf "S01 raw kind %A" kind) expected (CustomerPolicies.isPremium customer)
            assertEqual (sprintf "S06 basis points for %A" kind) (if expected then 1000L else 0L)
                (CustomerPolicies.discountBasisPoints customer)

        let positivePremium = Fixtures.customer "premium" 1059L
        assertEqual "S07 premium 1059 minor units" 953L
            (CustomerPolicies.discountedBalance positivePremium |> Domain.Money.minorUnits)

        let negativePremium = Fixtures.customer "premium" -1059L
        assertEqual "S07 premium -1059 minor units" -953L
            (CustomerPolicies.discountedBalance negativePremium |> Domain.Money.minorUnits)

        let regular = Fixtures.customer "regular" 1059L
        assertEqual "S07 regular balance remains unchanged" 1059L
            (CustomerPolicies.discountedBalance regular |> Domain.Money.minorUnits)

        let unnormalizedPremium = Fixtures.customer " premium " 1059L
        assertEqual "S07 unnormalized kind keeps balance" 1059L
            (CustomerPolicies.discountedBalance unnormalizedPremium |> Domain.Money.minorUnits)

        for balance, expected in [ 1L, 0L; -1L, 0L; 9L, 8L; -9L, -8L; 19L, 17L; -19L, -17L ] do
            let customer = Fixtures.customer "premium" balance
            assertEqual (sprintf "S07 truncation for %d minor units" balance) expected
                (CustomerPolicies.discountedBalance customer |> Domain.Money.minorUnits)

        let minimum = Fixtures.customer "premium" Int64.MinValue
        assertEqual "S07 premium Int64 minimum" -8301034833169298227L
            (CustomerPolicies.discountedBalance minimum |> Domain.Money.minorUnits)

        let maximum = Fixtures.customer "premium" Int64.MaxValue
        assertEqual "S07 premium Int64 maximum" 8301034833169298226L
            (CustomerPolicies.discountedBalance maximum |> Domain.Money.minorUnits)

        let regularMinimum = Fixtures.customer "regular" Int64.MinValue
        assertEqual "S07 regular Int64 minimum remains unchanged" Int64.MinValue
            (CustomerPolicies.discountedBalance regularMinimum |> Domain.Money.minorUnits)

        let regularMaximum = Fixtures.customer "regular" Int64.MaxValue
        assertEqual "S07 regular Int64 maximum remains unchanged" Int64.MaxValue
            (CustomerPolicies.discountedBalance regularMaximum |> Domain.Money.minorUnits)

        printfn "Own checks passed: 26"

    let runExamples () =
        ()
