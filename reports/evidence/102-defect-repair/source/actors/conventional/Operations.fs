namespace AgentLang.BusinessPolicy

open System
open AgentLang.Business

module CustomerPolicies =
    let isPremium (_customer: Customer) : bool =
        failwith "Not implemented."

    let discountBasisPoints (_customer: Customer) : int64 =
        failwith "Not implemented."

    let discountedBalance (customer: Customer) : Domain.Money =
        let balance = Domain.Money.minorUnits customer.Balance
        if String.Equals(customer.Kind, "premium", StringComparison.Ordinal) then
            // Split by ten before multiplying so this remains safe across the full Int64 range.
            let discounted =
                (balance / 10L * 9L) + (balance % 10L * 9L / 10L)
            Domain.Money.ofMinorUnits discounted
        else
            customer.Balance
