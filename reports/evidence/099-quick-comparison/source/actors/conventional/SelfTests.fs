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
        let discountedBalanceMinor kind balanceMinor =
            Fixtures.customer kind balanceMinor
            |> CustomerPolicies.discountedBalance
            |> Domain.Money.minorUnits

        assertEqual "Premium receives a ten percent discount" 1125L (discountedBalanceMinor "premium" 1250L)
        assertEqual "Positive fractional minor unit truncates toward zero" 9L (discountedBalanceMinor "premium" 11L)
        assertEqual "Negative fractional minor unit truncates toward zero" -9L (discountedBalanceMinor "premium" -11L)
        assertEqual "Small negative discounted balance truncates to zero" 0L (discountedBalanceMinor "premium" -1L)
        assertEqual "Kind comparison is ordinal and case-sensitive" 1250L (discountedBalanceMinor "Premium" 1250L)
        assertEqual "Kind comparison preserves surrounding whitespace" 1250L (discountedBalanceMinor " premium " 1250L)
        assertEqual "Other kinds preserve the minimum balance" Int64.MinValue (discountedBalanceMinor "vip" Int64.MinValue)
        assertEqual "Other kinds preserve the maximum balance" Int64.MaxValue (discountedBalanceMinor "vip" Int64.MaxValue)
        assertEqual "Maximum premium balance does not overflow" 8301034833169298226L (discountedBalanceMinor "premium" Int64.MaxValue)
        assertEqual "Minimum premium balance does not overflow" -8301034833169298227L (discountedBalanceMinor "premium" Int64.MinValue)
        printfn "Own checks passed: 10"

    let runExamples () =
        let customer = Fixtures.customer "premium" 1250L
        let discounted = CustomerPolicies.discountedBalance customer |> Domain.Money.minorUnits
        printfn "Example discounted balance: %d -> %d minor units" (Domain.Money.minorUnits customer.Balance) discounted
