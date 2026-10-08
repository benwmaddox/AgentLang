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
            Domain.Money.ofMinorUnits (balance - balance / 10L)
        else
            customer.Balance
