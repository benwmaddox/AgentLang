namespace AgentLang.EarlyPilot
type Customer = { Kind: string; Balance: float }
type Subscription = { Term: string; Renewable: bool }
module Subscription =
 let annualRenewable (subscription: Subscription) = subscription.Term = "annual" && subscription.Renewable
module Customer =
 let premium (customer: Customer) = customer.Kind = "premium"
 let discountedBalance (customer: Customer) = if premium customer then customer.Balance * 0.9 else customer.Balance
 let renewalBalance (customer: Customer) (subscription: Subscription) =
  let baseline = discountedBalance customer
  if Subscription.annualRenewable subscription then baseline * 0.95 else baseline
 let renewalSavings (customer: Customer) (subscription: Subscription) = customer.Balance - renewalBalance customer subscription