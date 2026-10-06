namespace AgentLang.EarlyPilot

type Customer = { Kind: string; Balance: float }
type Subscription = { Term: string; Renewable: bool }

module Subscription =
    let annualRenewable (subscription: Subscription) : bool =
        subscription.Term = "annual" && subscription.Renewable

// Reusable policy helpers and public entry points.
module Customer =
    let premium (customer: Customer) : bool = customer.Kind = "premium"
    let discountedBalance (customer: Customer) : float =
        if premium customer then customer.Balance * 0.9 else customer.Balance
    let renewalBalance (customer: Customer) (subscription: Subscription) : float =
        let balance = discountedBalance customer
        if Subscription.annualRenewable subscription then balance * 0.95 else balance
    let renewalSavings (customer: Customer) (subscription: Subscription) : float =
        customer.Balance - renewalBalance customer subscription
