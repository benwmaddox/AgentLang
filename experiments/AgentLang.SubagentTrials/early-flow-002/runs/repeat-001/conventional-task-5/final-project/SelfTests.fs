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
        let checkRenewalBalance (customer: Customer) (subscription: Subscription) (expected: float) =
            let actual = Customer.renewalBalance customer subscription
            if abs (actual - expected) > 1e-9 then
                failwithf "Customer.renewalBalance for kind %A, balance %g, term %A, and renewable %b expected %g but returned %g" customer.Kind customer.Balance subscription.Term subscription.Renewable expected actual
        let renewalCases: (Customer * Subscription * float) array =
            [|
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 107.3025)
                ({ Kind = "standard"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 119.225)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "annual"; Renewable = false }, 112.95)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "monthly"; Renewable = true }, 112.95)
                ({ Kind = "standard"; Balance = 125.5 }, { Term = "annual"; Renewable = false }, 125.5)
                ({ Kind = "Premium"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 119.225)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "Annual"; Renewable = true }, 112.95)
                ({ Kind = "premium"; Balance = 0.0 }, { Term = "annual"; Renewable = true }, 0.0)
                ({ Kind = "standard"; Balance = -20.0 }, { Term = "annual"; Renewable = true }, -19.0)
                ({ Kind = "premium"; Balance = -20.0 }, { Term = "annual"; Renewable = true }, -17.1)
            |]
        renewalCases |> Array.iter (fun (customer, subscription, expected) -> checkRenewalBalance customer subscription expected)
        printfn "Customer.renewalBalance self-tests passed."
        let checkRenewalSavings (customer: Customer) (subscription: Subscription) (expected: float) =
            let actual = Customer.renewalSavings customer subscription
            if abs (actual - expected) > 1e-9 then
                failwithf "Customer.renewalSavings for kind %A, balance %g, term %A, and renewable %b expected %g but returned %g" customer.Kind customer.Balance subscription.Term subscription.Renewable expected actual
        let renewalSavingsCases: (Customer * Subscription * float) array =
            [|
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 18.1975)
                ({ Kind = "standard"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 6.275)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "annual"; Renewable = false }, 12.55)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "monthly"; Renewable = true }, 12.55)
                ({ Kind = "standard"; Balance = 125.5 }, { Term = "annual"; Renewable = false }, 0.0)
                ({ Kind = "Premium"; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 6.275)
                ({ Kind = "premium "; Balance = 125.5 }, { Term = "annual"; Renewable = true }, 6.275)
                ({ Kind = "premium"; Balance = 125.5 }, { Term = "Annual"; Renewable = true }, 12.55)
                ({ Kind = "premium"; Balance = 0.0 }, { Term = "annual"; Renewable = true }, 0.0)
                ({ Kind = "standard"; Balance = -20.0 }, { Term = "annual"; Renewable = true }, -1.0)
                ({ Kind = "premium"; Balance = -20.0 }, { Term = "annual"; Renewable = true }, -2.9)
            |]
        renewalSavingsCases |> Array.iter (fun (customer, subscription, expected) -> checkRenewalSavings customer subscription expected)
        printfn "Customer.renewalSavings self-tests passed."
        0
