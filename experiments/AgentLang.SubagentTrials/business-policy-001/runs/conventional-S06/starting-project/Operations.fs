namespace AgentLang.BusinessPolicy

open AgentLang.Business

/// Task operation module. External actors may replace this whole file.
module CustomerPolicies =
    /// Returns true only when Kind is exactly "premium", using ordinal comparison.
    let isPremium (customer: Customer) : bool =
        System.String.Equals(customer.Kind, "premium", System.StringComparison.Ordinal)

    let discountBasisPoints (_customer: Customer) : int64 =
        failwith "Not implemented."

    let discountedBalance (_customer: Customer) : Domain.Money =
        failwith "Not implemented."
