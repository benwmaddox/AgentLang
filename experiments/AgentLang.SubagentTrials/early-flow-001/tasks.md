# Public pilot task prompts

The coordinator sends only the selected task, project schema description and
the relevant arm's interface primer. These prompts do not enumerate retained
helpers. The starting schema is Customer(kind:String,balance:Float) and
Subscription(term:String,renewable:Bool), with equivalent F# records in the
conventional arm. All text comparisons are exact and case-sensitive.

## 1

Add a reusable premium-customer predicate. A customer is premium exactly when
kind equals "premium". Required entry point: AgentLang `customer.premium? :
Customer -> Bool`; F# `Customer.premium : Customer -> bool`. Add relevant tests.

## 2

Add reusable customer discount calculation. Premium customers (kind exactly
"premium") receive a 10% discount: balance multiplied by 0.9. Other customers
keep their balance. Required entry point: AgentLang
`customer.discounted-balance : Customer -> Float`; F#
`Customer.discountedBalance : Customer -> float`. Add relevant tests.

## 3

Add reusable annual-renewal eligibility. A subscription qualifies exactly when
term is "annual" and renewable is true. Required entry point: AgentLang
`subscription.annual-renewable? : Subscription -> Bool`; F#
`Subscription.annualRenewable : Subscription -> bool`. Add relevant tests.

## 4

Calculate customer renewal balance. First multiply the balance by 0.9 for kind
exactly "premium"; otherwise keep it. Then multiply that result by 0.95 when
subscription term is exactly "annual" and renewable is true. That additional
discount applies to standard customers too. Required entry point: AgentLang
`customer.renewal-balance : Customer Subscription -> Float`; F#
`Customer.renewalBalance : Customer -> Subscription -> float`. Add relevant tests.

## 5

Calculate renewal savings: original customer balance minus the renewal balance.
Renewal balance first applies a multiplicative 10% discount to kind exactly
"premium", then a further multiplicative 5% discount to every subscription
whose term is exactly "annual" and renewable is true. Required entry point:
AgentLang `customer.renewal-savings : Customer Subscription -> Float`; F#
`Customer.renewalSavings : Customer -> Subscription -> float`. Add relevant tests.
