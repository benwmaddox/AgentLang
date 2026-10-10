namespace AgentLang.SubscriptionHandoff.Control

open System.Reflection
open Microsoft.FSharp.Reflection
open AgentLang.Business.Domain

type ControlKind =
    | Composed
    | SkipCreationValidationOnDryRun
    | ReturnModifiedStoreOnDryRun

module private StoreReflection =
    let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

    let private updateField (record: Store) fieldName value =
        let recordType = record.GetType()
        let properties = FSharpType.GetRecordFields(recordType, bindingFlags = flags)
        let fields = FSharpValue.GetRecordFields(record, bindingFlags = flags)
        let index = properties |> Array.findIndex (fun property -> property.Name = fieldName)
        fields.[index] <- value
        FSharpValue.MakeRecord(recordType, fields, bindingFlags = flags) :?> Store

    let private updateStoreMap<'key, 'value when 'key: comparison>
        (fieldName: string)
        (update: Map<'key, 'value> -> Map<'key, 'value>)
        (store: Store) : Store =
        let recordType = store.GetType()
        let properties = FSharpType.GetRecordFields(recordType, bindingFlags = flags)
        let fields = FSharpValue.GetRecordFields(store, bindingFlags = flags)
        let index = properties |> Array.findIndex (fun property -> property.Name = fieldName)
        let values = fields.[index] :?> Map<'key, 'value>
        fields.[index] <- box (update values)
        FSharpValue.MakeRecord(recordType, fields, bindingFlags = flags) :?> Store

    let removeCustomer (id: CustomerId) (store: Store) =
        updateStoreMap<CustomerId, Customer> "CustomersById"
            (fun (values: Map<CustomerId, Customer>) -> Map.remove id values) store

    let removeProduct (id: ProductId) (store: Store) =
        updateStoreMap<ProductId, Product> "ProductsById"
            (fun (values: Map<ProductId, Product>) -> Map.remove id values) store

    let clearSubscriptions (store: Store) =
        updateField store "SubscriptionsById" (box (Map.empty<SubscriptionId, Subscription>))

    let addSubscription (id: SubscriptionId) (subscription: Subscription) (store: Store) =
        updateStoreMap<SubscriptionId, Subscription> "SubscriptionsById"
            (fun (values: Map<SubscriptionId, Subscription>) -> Map.add id subscription values) store

    let subscriptions (store: Store) =
        let properties = FSharpType.GetRecordFields(store.GetType(), bindingFlags = flags)
        let fields = FSharpValue.GetRecordFields(store, bindingFlags = flags)
        let index = properties |> Array.findIndex (fun property -> property.Name = "SubscriptionsById")
        (fields.[index] :?> Map<SubscriptionId, Subscription>)
        |> Map.toList
        |> List.map snd

module SubscriptionHandoff =
    let private startReplacement (store: Store) (id: SubscriptionId) (old: Subscription) term at expiry =
        Subscription.start store id (Subscription.customerId old) (Subscription.productId old) term at expiry

    let private afterCancellation (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId) term at expiry dryRun =
        match Subscription.cancel store oldId at with
        | Error error -> Error error
        | Ok (cancelledStore, oldSubscription) ->
            match startReplacement cancelledStore newId oldSubscription term at expiry with
            | Error error -> Error error
            | Ok (updatedStore, _) -> Ok (if dryRun then store else updatedStore)

    let handoff (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId) term at expiry dryRun : Result<Store, DomainError> =
        afterCancellation store oldId newId term at expiry dryRun

    let private skipCreationValidationOnDryRun (store: Store) oldId newId term at expiry dryRun =
        match Subscription.cancel store oldId at with
        | Error error -> Error error
        | Ok _ when dryRun -> Ok store
        | Ok (cancelledStore, oldSubscription) ->
            startReplacement cancelledStore newId oldSubscription term at expiry
            |> Result.map fst

    let private returnModifiedStoreOnDryRun (store: Store) oldId newId term at expiry _dryRun =
        match Subscription.cancel store oldId at with
        | Error error -> Error error
        | Ok (cancelledStore, oldSubscription) ->
            startReplacement cancelledStore newId oldSubscription term at expiry
            |> Result.map fst

    let run kind store oldId newId term at expiry dryRun =
        match kind with
        | Composed -> handoff store oldId newId term at expiry dryRun
        | SkipCreationValidationOnDryRun -> skipCreationValidationOnDryRun store oldId newId term at expiry dryRun
        | ReturnModifiedStoreOnDryRun -> returnModifiedStoreOnDryRun store oldId newId term at expiry dryRun
