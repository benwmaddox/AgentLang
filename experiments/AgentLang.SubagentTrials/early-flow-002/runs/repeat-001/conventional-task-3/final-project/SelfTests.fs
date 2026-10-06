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
        let checkAnnualRenewable (subscription: Subscription) (expected: bool) =
            let actual = Subscription.annualRenewable subscription
            if actual <> expected then
                failwithf "Subscription.annualRenewable for term %A and renewable %b expected %b but returned %b" subscription.Term subscription.Renewable expected actual
        let annualRenewalCases: (Subscription * bool) array =
            [|
                ({ Term = "annual"; Renewable = true }, true)
                ({ Term = "annual"; Renewable = false }, false)
                ({ Term = "monthly"; Renewable = true }, false)
                ({ Term = "Annual"; Renewable = true }, false)
                ({ Term = "annual "; Renewable = true }, false)
                ({ Term = ""; Renewable = true }, false)
            |]
        annualRenewalCases |> Array.iter (fun (subscription, expected) -> checkAnnualRenewable subscription expected)
        printfn "Subscription.annualRenewable self-tests passed."

        let checkDiscount (customer: Customer) (expected: float) =
            let actual = Customer.discountedBalance customer
            if abs (actual - expected) > 1e-9 then
                failwithf "Customer.discountedBalance for kind %A and balance %g expected %g but returned %g" customer.Kind customer.Balance expected actual
        let discountCases: (Customer * float) array =
            [|
                ({ Kind = "premium"; Balance = 125.5 }, 112.95)
                ({ Kind = "premium"; Balance = 0.0 }, 0.0)
                ({ Kind = "premium"; Balance = -20.0 }, -18.0)
                ({ Kind = "standard"; Balance = 125.5 }, 125.5)
                ({ Kind = "Premium"; Balance = 125.5 }, 125.5)
                ({ Kind = "premium "; Balance = 125.5 }, 125.5)
            |]
        discountCases |> Array.iter (fun (customer, expected) -> checkDiscount customer expected)
        printfn "Customer.premium and discounted balance self-tests passed."
        0
