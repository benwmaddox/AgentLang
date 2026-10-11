namespace AgentLang.SubscriptionHandoff

open System
open AgentLang.Business.Domain

type Seed =
    { Store: Store
      CustomerId: CustomerId
      ProductId: ProductId
      OldSubscriptionId: SubscriptionId }

module Fixtures =
    let private unwrap label = function
        | Ok value -> value
        | Error error -> failwithf "%s: %s" label (AgentLang.Business.Domain.DomainError.message error)

    let instant year month day = DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)

    let create () =
        let customerId =
            CustomerId.parse "10000000-0000-0000-0000-000000000001"
            |> unwrap "fixture customer id"

        let productId =
            ProductId.parse "20000000-0000-0000-0000-000000000001"
            |> unwrap "fixture product id"

        let email = Email.create "ada@example.test" |> unwrap "fixture email"
        let customer =
            Customer.create customerId email "regular" (Money.ofMinorUnits 1250L) (instant 2026 1 1)
            |> unwrap "fixture customer"

        let product =
            Product.create productId "Monthly plan" (Money.ofMinorUnits 199L)
            |> unwrap "fixture product"

        let store =
            Store.addCustomer customer Store.empty
            |> Result.bind (Store.addProduct product)
            |> unwrap "fixture store"

        let oldSubscriptionId =
            SubscriptionId.parse "30000000-0000-0000-0000-000000000001"
            |> unwrap "fixture subscription id"

        let store =
            match Subscription.start store oldSubscriptionId customerId productId "monthly"
                    (instant 2026 1 2) (instant 2026 2 1) with
            | Ok (updated, _) -> updated
            | Error error -> failwithf "fixture subscription: %s" (AgentLang.Business.Domain.DomainError.message error)

        { Store = store
          CustomerId = customerId
          ProductId = productId
          OldSubscriptionId = oldSubscriptionId }
