namespace AgentLang.BusinessPolicy

open AgentLang.Business

/// Task operation module. External actors may replace this whole file.
module CustomerPolicies =
    /// Returns true only when Kind is exactly "premium", using ordinal comparison.
    let isPremium (customer: Customer) : bool =
        System.String.Equals(customer.Kind, "premium", System.StringComparison.Ordinal)

    /// Returns 1000 basis points for an exact raw "premium" Kind; otherwise zero.
    let discountBasisPoints (customer: Customer) : int64 =
        if isPremium customer then 1000L else 0L

    /// Discounts exact raw premium balances by 10%, truncating fractional minor units toward zero.
    let discountedBalance (customer: Customer) : Domain.Money =
        let balanceMinor = Domain.Money.minorUnits customer.Balance

        let discountedMinor =
            if isPremium customer then
                let wholeTens = balanceMinor / 10L
                let remainder = balanceMinor % 10L
                (wholeTens * 9L) + ((remainder * 9L) / 10L)
            else
                balanceMinor

        Domain.Money.ofMinorUnits discountedMinor
