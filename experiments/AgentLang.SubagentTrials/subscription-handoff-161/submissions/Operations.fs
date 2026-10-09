namespace AgentLang.SubscriptionHandoff

module Subscription =
    let id = AgentLang.Business.Domain.Subscription.id
    let customerId = AgentLang.Business.Domain.Subscription.customerId
    let productId = AgentLang.Business.Domain.Subscription.productId
    let term = AgentLang.Business.Domain.Subscription.term
    let startedAt = AgentLang.Business.Domain.Subscription.startedAt
    let expiresAt = AgentLang.Business.Domain.Subscription.expiresAt
    let status = AgentLang.Business.Domain.Subscription.status
    let cancelledAt = AgentLang.Business.Domain.Subscription.cancelledAt
    let occupiedPeriod = AgentLang.Business.Domain.Subscription.occupiedPeriod
    let start = AgentLang.Business.Domain.Subscription.start
    let cancel = AgentLang.Business.Domain.Subscription.cancel

    /// Atomically cancels the existing subscription and starts a replacement at the same instant.
    /// Cancellation errors take precedence; creation rules run against the cancelled store only after cancellation succeeds.
    let handoff (store: Store) (oldId: SubscriptionId) (replacementId: SubscriptionId) (term: string) (handoffAt: Instant) (expiresAt: Instant) : Result<Store, DomainError> =
        match AgentLang.Business.Domain.Subscription.cancel store oldId handoffAt with
        | Error error -> Error error
        | Ok (cancelledStore, oldSubscription) ->
            AgentLang.Business.Domain.Subscription.start
                cancelledStore
                replacementId
                (AgentLang.Business.Domain.Subscription.customerId oldSubscription)
                (AgentLang.Business.Domain.Subscription.productId oldSubscription)
                term
                handoffAt
                expiresAt
            |> Result.map fst
