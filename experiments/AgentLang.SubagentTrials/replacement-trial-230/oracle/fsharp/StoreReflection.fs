namespace AgentLang.SubscriptionHandoff.Control

open System.Reflection
open Microsoft.FSharp.Reflection
open AgentLang.Business.Domain

module StoreReflection =
    let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

    let private readStoreMap<'key, 'value when 'key: comparison>
        (fieldName: string)
        (store: Store) : Map<'key, 'value> =
        let recordType = store.GetType()
        let properties = FSharpType.GetRecordFields(recordType, bindingFlags = flags)
        let fields = FSharpValue.GetRecordFields(store, bindingFlags = flags)
        let index = properties |> Array.findIndex (fun property -> property.Name = fieldName)
        fields.[index] :?> Map<'key, 'value>

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
            (fun values -> Map.remove id values) store

    let removeProduct (id: ProductId) (store: Store) =
        updateStoreMap<ProductId, Product> "ProductsById"
            (fun values -> Map.remove id values) store

    let customerIds (store: Store) =
        readStoreMap<CustomerId, Customer> "CustomersById" store |> Map.toList |> List.map fst

    let productIds (store: Store) =
        readStoreMap<ProductId, Product> "ProductsById" store |> Map.toList |> List.map fst

    let subscriptionIds (store: Store) =
        readStoreMap<SubscriptionId, Subscription> "SubscriptionsById" store |> Map.toList |> List.map fst

    let invoiceIds (store: Store) =
        readStoreMap<InvoiceId, Invoice> "InvoicesById" store |> Map.toList |> List.map fst

    let paymentIds (store: Store) =
        readStoreMap<PaymentId, Payment> "PaymentsById" store |> Map.toList |> List.map fst

    let subscriptions (store: Store) =
        readStoreMap<SubscriptionId, Subscription> "SubscriptionsById" store |> Map.toList |> List.map snd
