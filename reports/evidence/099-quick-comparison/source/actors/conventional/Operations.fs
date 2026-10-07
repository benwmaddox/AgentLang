namespace AgentLang.BusinessPolicy

open System
open AgentLang.Business

/// Task operation module. External actors may replace this whole file.
module CustomerPolicies =
    let isPremium (_customer: Customer) : bool =
        failwith "Not implemented."

    let discountBasisPoints (_customer: Customer) : int64 =
        failwith "Not implemented."

    let discountedBalance (customer: Customer) : Domain.Money =
        if String.Equals(customer.Kind, "premium", StringComparison.Ordinal) then
            let balanceMinor = Domain.Money.minorUnits customer.Balance
            let tens = balanceMinor / 10L
            let remainder = balanceMinor % 10L
            let discountedMinor = tens * 9L + (remainder * 9L) / 10L
            Domain.Money.ofMinorUnits discountedMinor
        else
            customer.Balance
