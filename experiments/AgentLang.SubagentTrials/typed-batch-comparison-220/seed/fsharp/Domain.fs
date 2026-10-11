namespace AgentLang.SubscriptionHandoff

open System

type Instant = DateTimeOffset
type CustomerId = AgentLang.Business.Domain.CustomerId
type ProductId = AgentLang.Business.Domain.ProductId
type SubscriptionId = AgentLang.Business.Domain.SubscriptionId
type Store = AgentLang.Business.Domain.Store
type SubscriptionRecord = AgentLang.Business.Domain.Subscription
type NativeDomainError = AgentLang.Business.Domain.DomainError

/// Stable error payload used by the F# comparison API and shared batch oracle.
/// Lower-level business operations retain their native DomainError values.
type DomainError =
    { Code: string
      Message: string }

module DomainError =
    let code (error: DomainError) = error.Code
    let message (error: DomainError) = error.Message

    /// Project native business errors into the nine frozen 216 cause values.
    /// The renewal API is the only boundary that uses this shared vocabulary.
    let fromNative (error: NativeDomainError) =
        let code, message =
            match error with
            | AgentLang.Business.Domain.DomainError.SubscriptionNotFound _ ->
                "SUBSCRIPTION_NOT_FOUND", "The subscription does not exist."
            | AgentLang.Business.Domain.DomainError.SubscriptionAlreadyCancelled _ ->
                "SUBSCRIPTION_ALREADY_CANCELLED", "The subscription is already cancelled."
            | AgentLang.Business.Domain.DomainError.CancelBeforeSubscriptionStart _ ->
                "CANCEL_BEFORE_START", "A subscription cannot be cancelled before it starts."
            | AgentLang.Business.Domain.DomainError.DuplicateSubscription _ ->
                "DUPLICATE_SUBSCRIPTION", "A subscription with this identifier already exists."
            | AgentLang.Business.Domain.DomainError.InvalidSubscriptionTerm ->
                "INVALID_SUBSCRIPTION_TERM", "Subscription term must contain non-whitespace text."
            | AgentLang.Business.Domain.DomainError.SubscriptionExpiryMustFollowStart ->
                "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START", "Subscription expiry must be later than its start."
            | AgentLang.Business.Domain.DomainError.CustomerNotFound _ ->
                "CUSTOMER_NOT_FOUND", "The customer does not exist."
            | AgentLang.Business.Domain.DomainError.ProductNotFound _ ->
                "PRODUCT_NOT_FOUND", "The product does not exist."
            | AgentLang.Business.Domain.DomainError.SubscriptionOverlap ->
                "SUBSCRIPTION_OVERLAP", "This customer already has an overlapping subscription for the product."
            | other ->
                AgentLang.Business.Domain.DomainError.code other,
                AgentLang.Business.Domain.DomainError.message other
        { Code = code; Message = message }

module Store =
    let empty: Store = AgentLang.Business.Domain.Store.empty
    let summary = AgentLang.Business.Domain.Store.summary
    let customer = AgentLang.Business.Domain.Store.customer
    let product = AgentLang.Business.Domain.Store.product
    let subscription = AgentLang.Business.Domain.Store.subscription
    let addCustomer = AgentLang.Business.Domain.Store.addCustomer
    let addProduct = AgentLang.Business.Domain.Store.addProduct
