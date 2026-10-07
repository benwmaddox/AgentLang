namespace AgentLang.BusinessPolicy

open AgentLang.Business

/// Task operation module. External actors may replace this whole file.
module CustomerPolicies =
    let isPremium (_customer: Customer) : bool =
        failwith "Not implemented."

    let discountBasisPoints (_customer: Customer) : int64 =
        failwith "Not implemented."

    let discountedBalance (_customer: Customer) : Domain.Money =
        failwith "Not implemented."
