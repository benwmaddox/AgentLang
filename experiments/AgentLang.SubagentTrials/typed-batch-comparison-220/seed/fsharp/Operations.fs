namespace AgentLang.SubscriptionHandoff

open AgentLang.Business.Domain

type SubscriptionPlan =
    | Monthly
    | Annual

type SubscriptionRenewal =
    { OldId: SubscriptionId
      NewId: SubscriptionId
      Plan: SubscriptionPlan
      ExpiresAt: Instant }

type SubscriptionRenewalBatchState =
    { Outcome: Result<Store, AgentLang.SubscriptionHandoff.DomainError>
      At: Instant }

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

    let private renewWithTerm (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId)
                              (term: string) (at: Instant) (expiry: Instant) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        match AgentLang.Business.Domain.Store.subscription oldId store with
        | None ->
            AgentLang.Business.Domain.Subscription.cancel store oldId at
            |> Result.map fst
            |> Result.mapError AgentLang.SubscriptionHandoff.DomainError.fromNative
        | Some original ->
            match AgentLang.Business.Domain.Subscription.cancel store oldId at with
            | Error error -> Error(AgentLang.SubscriptionHandoff.DomainError.fromNative error)
            | Ok (cancelledStore, _) ->
                AgentLang.Business.Domain.Subscription.start cancelledStore newId
                    (AgentLang.Business.Domain.Subscription.customerId original)
                    (AgentLang.Business.Domain.Subscription.productId original)
                    term at expiry
                |> Result.map fst
                |> Result.mapError AgentLang.SubscriptionHandoff.DomainError.fromNative

    /// Cancel an active subscription and create a monthly replacement.
    let renewMonthly (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId)
                     (at: Instant) (expiry: Instant) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        renewWithTerm store oldId newId "monthly" at expiry

    /// Cancel an active subscription and create an annual replacement.
    let renewAnnual (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId)
                    (at: Instant) (expiry: Instant) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        renewWithTerm store oldId newId "annual" at expiry

    /// Apply one inherited batch step while retaining the first failure.
    let renewBatchStep (state: SubscriptionRenewalBatchState) (change: SubscriptionRenewal) =
        match state.Outcome with
        | Error _ -> state
        | Ok current ->
            let next =
                match change.Plan with
                | Monthly -> renewMonthly current change.OldId change.NewId state.At change.ExpiresAt
                | Annual -> renewAnnual current change.OldId change.NewId state.At change.ExpiresAt
            { state with Outcome = next }

    /// Renew subscriptions in list order and return the first error without partial Store state.
    let renewBatch (store: Store) (changes: SubscriptionRenewal list) (at: Instant) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let initial = { Outcome = Ok store; At = at }
        changes
        |> List.fold renewBatchStep initial
        |> fun state -> state.Outcome
