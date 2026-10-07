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
        let premiumCustomer = Fixtures.customer "premium" System.Int64.MinValue
        assertEqual "exact lowercase premium Kind is accepted at minimum balance" true
            (CustomerPolicies.isPremium premiumCustomer)

        let rejectedKinds = [ "standard"; "Premium"; "PREMIUM"; " premium"; "premium "; "" ]
        rejectedKinds
        |> List.iter (fun kind ->
            let customer = Fixtures.customer kind System.Int64.MaxValue
            assertEqual (sprintf "Kind %A is not premium" kind) false (CustomerPolicies.isPremium customer))

        let nullKindCustomer = Fixtures.customer null 0L
        assertEqual "null Kind is not premium" false (CustomerPolicies.isPremium nullKindCustomer)

        let discountCases =
            [ ("premium at minimum balance", "premium", System.Int64.MinValue, 1000L)
              ("premium at zero balance", "premium", 0L, 1000L)
              ("premium at maximum balance", "premium", System.Int64.MaxValue, 1000L)
              ("standard at minimum balance", "standard", System.Int64.MinValue, 0L)
              ("standard at maximum balance", "standard", System.Int64.MaxValue, 0L)
              ("case mismatch", "Premium", 0L, 0L)
              ("uppercase mismatch", "PREMIUM", 0L, 0L)
              ("leading whitespace is preserved", " premium", 0L, 0L)
              ("trailing whitespace is preserved", "premium ", 0L, 0L)
              ("empty Kind", "", 0L, 0L) ]
        discountCases
        |> List.iter (fun (label, kind, balanceMinor, expected) ->
            let customer = Fixtures.customer kind balanceMinor
            assertEqual label expected (CustomerPolicies.discountBasisPoints customer))

        assertEqual "null Kind receives no discount" 0L (CustomerPolicies.discountBasisPoints nullKindCustomer)
        printfn "Own checks passed: %d" (2 + List.length rejectedKinds + List.length discountCases + 1)

    let runExamples () =
        [ "premium", 1250L; "Premium", 1250L; " premium ", 1250L ]
        |> List.iter (fun (kind, balanceMinor) ->
            let customer = Fixtures.customer kind balanceMinor
            printfn "Kind %A -> isPremium = %b, discountBasisPoints = %A" customer.Kind (CustomerPolicies.isPremium customer) (CustomerPolicies.discountBasisPoints customer))
