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
        let balanceCases =
            [ ("premium at one", "premium", 1L, 0L)
              ("premium at nine", "premium", 9L, 8L)
              ("premium at nineteen", "premium", 19L, 17L)
              ("premium at negative one", "premium", -1L, 0L)
              ("premium at negative nine", "premium", -9L, -8L)
              ("premium at negative nineteen", "premium", -19L, -17L)
              ("premium at exact tens", "premium", 120L, 108L)
              ("premium at minimum Int64", "premium", System.Int64.MinValue, -8301034833169298227L)
              ("premium at maximum Int64", "premium", System.Int64.MaxValue, 8301034833169298226L)
              ("standard at minimum Int64", "standard", System.Int64.MinValue, System.Int64.MinValue)
              ("standard at maximum Int64", "standard", System.Int64.MaxValue, System.Int64.MaxValue)
              ("case mismatch remains unchanged", "Premium", -9L, -9L)
              ("leading space remains unchanged", " premium", 9L, 9L)
              ("trailing space remains unchanged", "premium ", -9L, -9L)
              ("null Kind remains unchanged", null, System.Int64.MaxValue, System.Int64.MaxValue) ]

        balanceCases
        |> List.iter (fun (label, kind, balanceMinor, expected) ->
            let customer = Fixtures.customer kind balanceMinor
            let actual = CustomerPolicies.discountedBalance customer |> Domain.Money.minorUnits
            assertEqual label expected actual)

        printfn "Own checks passed: %d" (2 + List.length rejectedKinds + List.length discountCases + 1 + List.length balanceCases)

    let runExamples () =
        [ "premium", 1250L; "premium", -19L; "Premium", 1250L; " premium ", 1250L ]
        |> List.iter (fun (kind, balanceMinor) ->
            let customer = Fixtures.customer kind balanceMinor
            let balanceMinor = Domain.Money.minorUnits customer.Balance
            let discountedMinor =
                CustomerPolicies.discountedBalance customer
                |> Domain.Money.minorUnits
            printfn
                "Kind %A, balance %d -> isPremium = %b, discountBasisPoints = %d, discountedBalance = %d"
                customer.Kind
                balanceMinor
                (CustomerPolicies.isPremium customer)
                (CustomerPolicies.discountBasisPoints customer)
                discountedMinor)
