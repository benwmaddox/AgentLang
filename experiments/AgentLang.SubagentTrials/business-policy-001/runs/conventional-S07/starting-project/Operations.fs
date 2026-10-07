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

    let discountedBalance (_customer: Customer) : Domain.Money =
        failwith "Not implemented."
