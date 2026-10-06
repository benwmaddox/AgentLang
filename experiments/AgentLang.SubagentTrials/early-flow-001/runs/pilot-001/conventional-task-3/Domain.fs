namespace AgentLang.EarlyPilot

type Customer = { Kind: string; Balance: float }
type Subscription = { Term: string; Renewable: bool }

// Customer policy helpers used by the pilot acceptance surface.
module Customer =
    let premium (customer: Customer) : bool = customer.Kind = "premium"
    let discountedBalance (customer: Customer) : float =
        if premium customer then customer.Balance * 0.9
        else customer.Balance
    let renewalBalance (_customer: Customer) (_subscription: Subscription) : float = failwith "NOT_IMPLEMENTED"
    let renewalSavings (_customer: Customer) (_subscription: Subscription) : float = failwith "NOT_IMPLEMENTED"

module Subscription =
    let annualRenewable (subscription: Subscription) : bool =
        subscription.Term = "annual" && subscription.Renewable
