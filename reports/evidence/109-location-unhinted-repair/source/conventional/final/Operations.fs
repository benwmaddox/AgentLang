namespace AgentLang.BusinessPolicy

open System
open AgentLang.Business

module CustomerPolicies =
    let isPremium (customer: Customer) : bool =
        String.Equals(customer.Kind, "premium", StringComparison.Ordinal)

    let discountBasisPoints (customer: Customer) : int64 =
        if isPremium customer then 1000L else 0L

    let discountedBalance (customer: Customer) : Domain.Money =
        let balance = Domain.Money.minorUnits customer.Balance
        if isPremium customer then
            let retainedBasisPoints = 10000L - discountBasisPoints customer
            let quotient = balance / 10000L
            let remainder = balance % 10000L
            let discounted = quotient * retainedBasisPoints + remainder * retainedBasisPoints / 10000L
            Domain.Money.ofMinorUnits discounted
        else
            customer.Balance
