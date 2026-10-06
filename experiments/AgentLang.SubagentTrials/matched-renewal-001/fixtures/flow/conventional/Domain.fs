namespace AgentLang.MatchedRenewal.Conventional

open System

type Customer =
    { Kind: string
      Balance: float }

type Subscription =
    { Term: string
      Renewable: bool }

module Customer =
    let premium (customer: Customer) =
        String.Equals(customer.Kind, "premium", StringComparison.Ordinal)

    let discountedBalance (customer: Customer) =
        if premium customer then customer.Balance * 0.9
        else customer.Balance

module Renewal =
    /// Placeholder: returns the established baseline discount and ignores subscription eligibility.
    let balance (customer: Customer) (_subscription: Subscription) =
        Customer.discountedBalance customer
