namespace AgentLang.SubscriptionHandoff

open AgentLang.Business.Domain

module SelfTests =
    let private assertEqual label expected actual =
        if actual <> expected then
            failwithf "%s: expected %A but received %A" label expected actual

    let private subscriptionId value =
        SubscriptionId.parse value
        |> Result.defaultWith (fun error -> failwithf "fixture subscription id: %s" (DomainError.message error))

    let run () =
        let seed = Fixtures.create ()
        let summary = Store.summary seed.Store
        assertEqual "seed customer count" 1 summary.Customers
        assertEqual "seed product count" 1 summary.Products
        assertEqual "seed includes one active subscription" 1 summary.Subscriptions
        let original =
            Store.subscription seed.OldSubscriptionId seed.Store
            |> Option.defaultWith (fun () -> failwith "seed subscription is missing")
        assertEqual "seed subscription is active" Active (Subscription.status original)
        assertEqual "seed subscription starts at the canonical time" (Fixtures.instant 2026 1 2) (Subscription.startedAt original)
        assertEqual "seed subscription expires at the canonical time" (Fixtures.instant 2026 2 1) (Subscription.expiresAt original)

        let adjacentId = subscriptionId "30000000-0000-0000-0000-000000000002"
        let started =
            match Subscription.start seed.Store adjacentId seed.CustomerId seed.ProductId "monthly"
                    (Fixtures.instant 2026 2 1) (Fixtures.instant 2026 3 1) with
            | Ok (updated, subscription) ->
                assertEqual "start returns the requested identifier" adjacentId (Subscription.id subscription)
                assertEqual "start creates an active subscription" Active (Subscription.status subscription)
                updated
            | Error error -> failwithf "valid adjacent subscription start failed: %s" (DomainError.message error)

        assertEqual "start leaves the input store unchanged" 1 (Store.summary seed.Store).Subscriptions
        assertEqual "start returns a new store" 2 (Store.summary started).Subscriptions

        let overlappingId = subscriptionId "30000000-0000-0000-0000-000000000003"
        match Subscription.start seed.Store overlappingId seed.CustomerId seed.ProductId "monthly"
                (Fixtures.instant 2026 1 15) (Fixtures.instant 2026 2 15) with
        | Error SubscriptionOverlap -> ()
        | Error error -> failwithf "overlap returned the wrong error: %s" (DomainError.message error)
        | Ok _ -> failwith "overlapping subscription was accepted"

        let blankTermId = subscriptionId "30000000-0000-0000-0000-000000000004"
        match Subscription.start seed.Store blankTermId seed.CustomerId seed.ProductId "  "
                (Fixtures.instant 2026 1 2) (Fixtures.instant 2026 2 1) with
        | Error InvalidSubscriptionTerm -> ()
        | Error error -> failwithf "blank term returned the wrong error: %s" (DomainError.message error)
        | Ok _ -> failwith "blank subscription term was accepted"

        let cancellationTime = Fixtures.instant 2026 1 15
        let cancelled =
            match Subscription.cancel seed.Store seed.OldSubscriptionId cancellationTime with
            | Ok (updated, subscription) ->
                assertEqual "cancel returns a cancelled subscription" Cancelled (Subscription.status subscription)
                assertEqual "cancel records the requested time" (Some cancellationTime) (Subscription.cancelledAt subscription)
                updated
            | Error error -> failwithf "valid subscription cancel failed: %s" (DomainError.message error)

        assertEqual "cancel leaves its input store unchanged" Active (Store.subscription seed.OldSubscriptionId seed.Store |> Option.map Subscription.status |> Option.defaultValue Cancelled)
        assertEqual "cancel returns an updated store" Cancelled (Store.subscription seed.OldSubscriptionId cancelled |> Option.map Subscription.status |> Option.defaultValue Active)

        match Subscription.cancel seed.Store seed.OldSubscriptionId (Fixtures.instant 2026 1 1) with
        | Error (CancelBeforeSubscriptionStart _) -> ()
        | Error error -> failwithf "early cancellation returned the wrong error: %s" (DomainError.message error)
        | Ok _ -> failwith "cancellation before start was accepted"

        let unrelatedId = subscriptionId "30000000-0000-0000-0000-000000000005"
        let unrelated =
            match Subscription.start seed.Store unrelatedId seed.CustomerId seed.ProductId "legacy"
                    (Fixtures.instant 2025 10 1) (Fixtures.instant 2025 12 1) with
            | Ok (updated, subscription) ->
                assertEqual "unrelated subscription starts active" Active (Subscription.status subscription)
                updated
            | Error error -> failwithf "valid unrelated subscription start failed: %s" (DomainError.message error)

        let unrelatedBefore =
            Store.subscription unrelatedId unrelated
            |> Option.defaultWith (fun () -> failwith "unrelated subscription is missing before handoff")

        let replacementId = subscriptionId "30000000-0000-0000-0000-000000000006"
        let handoffAt = Fixtures.instant 2026 1 15
        let replacementExpiresAt = Fixtures.instant 2026 3 1
        let handedOff =
            match Subscription.handoff unrelated seed.OldSubscriptionId replacementId "  quarterly  " handoffAt replacementExpiresAt with
            | Ok updated -> updated
            | Error error -> failwithf "valid subscription handoff failed: %s" (DomainError.message error)

        let oldAfterHandoff =
            Store.subscription seed.OldSubscriptionId handedOff
            |> Option.defaultWith (fun () -> failwith "handoff removed the old subscription")
        assertEqual "handoff preserves the old id" (Subscription.id original) (Subscription.id oldAfterHandoff)
        assertEqual "handoff preserves the old customer" (Subscription.customerId original) (Subscription.customerId oldAfterHandoff)
        assertEqual "handoff preserves the old product" (Subscription.productId original) (Subscription.productId oldAfterHandoff)
        assertEqual "handoff preserves the old term" (Subscription.term original) (Subscription.term oldAfterHandoff)
        assertEqual "handoff preserves the old start" (Subscription.startedAt original) (Subscription.startedAt oldAfterHandoff)
        assertEqual "handoff preserves the old expiry" (Subscription.expiresAt original) (Subscription.expiresAt oldAfterHandoff)
        assertEqual "handoff cancels the old subscription" Cancelled (Subscription.status oldAfterHandoff)
        assertEqual "handoff records its instant on the old subscription" (Some handoffAt) (Subscription.cancelledAt oldAfterHandoff)

        let replacement =
            Store.subscription replacementId handedOff
            |> Option.defaultWith (fun () -> failwith "handoff did not create the replacement")
        assertEqual "handoff uses the requested replacement id" replacementId (Subscription.id replacement)
        assertEqual "handoff keeps the old customer" seed.CustomerId (Subscription.customerId replacement)
        assertEqual "handoff keeps the old product" seed.ProductId (Subscription.productId replacement)
        assertEqual "handoff trims the replacement term" "quarterly" (Subscription.term replacement)
        assertEqual "handoff starts the replacement at the cancellation instant" handoffAt (Subscription.startedAt replacement)
        assertEqual "handoff uses the requested replacement expiry" replacementExpiresAt (Subscription.expiresAt replacement)
        assertEqual "handoff replacement is active" Active (Subscription.status replacement)
        assertEqual "handoff replacement has no cancellation time" None (Subscription.cancelledAt replacement)
        let unrelatedAfter =
            Store.subscription unrelatedId handedOff
            |> Option.defaultWith (fun () -> failwith "handoff removed the unrelated subscription")
        assertEqual "handoff preserves the unrelated id" (Subscription.id unrelatedBefore) (Subscription.id unrelatedAfter)
        assertEqual "handoff preserves the unrelated customer" (Subscription.customerId unrelatedBefore) (Subscription.customerId unrelatedAfter)
        assertEqual "handoff preserves the unrelated product" (Subscription.productId unrelatedBefore) (Subscription.productId unrelatedAfter)
        assertEqual "handoff preserves the unrelated term" (Subscription.term unrelatedBefore) (Subscription.term unrelatedAfter)
        assertEqual "handoff preserves the unrelated start" (Subscription.startedAt unrelatedBefore) (Subscription.startedAt unrelatedAfter)
        assertEqual "handoff preserves the unrelated expiry" (Subscription.expiresAt unrelatedBefore) (Subscription.expiresAt unrelatedAfter)
        assertEqual "handoff preserves the unrelated status" (Subscription.status unrelatedBefore) (Subscription.status unrelatedAfter)
        assertEqual "handoff preserves the unrelated cancellation time" (Subscription.cancelledAt unrelatedBefore) (Subscription.cancelledAt unrelatedAfter)
        assertEqual "handoff leaves the caller's old subscription active" Active (Store.subscription seed.OldSubscriptionId unrelated |> Option.map Subscription.status |> Option.defaultValue Cancelled)
        assertEqual "handoff leaves the caller's store unchanged" 2 (Store.summary unrelated).Subscriptions
        assertEqual "handoff adds one replacement to the returned store" 3 (Store.summary handedOff).Subscriptions
        assertEqual "handoff preserves customer and product" true (Store.customer seed.CustomerId handedOff |> Option.isSome && Store.product seed.ProductId handedOff |> Option.isSome)

        let missingOldId = subscriptionId "30000000-0000-0000-0000-000000000007"
        match Subscription.handoff seed.Store missingOldId seed.OldSubscriptionId " " handoffAt handoffAt with
        | Error error -> assertEqual "cancellation errors precede replacement validation" (SubscriptionNotFound missingOldId) error
        | Ok _ -> failwith "handoff succeeded when its old subscription was missing"

        match Subscription.handoff cancelled seed.OldSubscriptionId seed.OldSubscriptionId "" handoffAt handoffAt with
        | Error error -> assertEqual "already-cancelled error precedes replacement validation" (SubscriptionAlreadyCancelled seed.OldSubscriptionId) error
        | Ok _ -> failwith "handoff succeeded when its old subscription was already cancelled"

        match Subscription.handoff seed.Store seed.OldSubscriptionId seed.OldSubscriptionId "" handoffAt handoffAt with
        | Error error -> assertEqual "replacement creation keeps duplicate-id precedence" (DuplicateSubscription seed.OldSubscriptionId) error
        | Ok _ -> failwith "handoff returned a cancelled intermediate store after duplicate replacement failure"
        assertEqual "failed replacement creation leaves the caller's old subscription active" Active (Store.subscription seed.OldSubscriptionId seed.Store |> Option.map Subscription.status |> Option.defaultValue Cancelled)

        let invalidTermReplacementId = subscriptionId "30000000-0000-0000-0000-000000000009"
        match Subscription.handoff seed.Store seed.OldSubscriptionId invalidTermReplacementId "  " handoffAt handoffAt with
        | Error InvalidSubscriptionTerm -> ()
        | Error error -> failwithf "invalid replacement term returned the wrong error: %s" (DomainError.message error)
        | Ok _ -> failwith "handoff accepted a blank replacement term"
        assertEqual "invalid replacement input remains unchanged" Active (Store.subscription seed.OldSubscriptionId seed.Store |> Option.map Subscription.status |> Option.defaultValue Cancelled)

        let blockerId = subscriptionId "30000000-0000-0000-0000-000000000010"
        let withBlocker =
            match Subscription.start seed.Store blockerId seed.CustomerId seed.ProductId "blocker"
                    (Fixtures.instant 2026 2 15) (Fixtures.instant 2026 3 15) with
            | Ok (updated, _) -> updated
            | Error error -> failwithf "valid blocking subscription start failed: %s" (DomainError.message error)
        match Subscription.handoff withBlocker seed.OldSubscriptionId replacementId "quarterly"
                (Fixtures.instant 2026 2 1) replacementExpiresAt with
        | Error SubscriptionOverlap -> ()
        | Error error -> failwithf "overlapping replacement returned the wrong error: %s" (DomainError.message error)
        | Ok _ -> failwith "handoff accepted a replacement overlapping another subscription"
        assertEqual "overlap failure leaves the caller's old subscription active" Active (Store.subscription seed.OldSubscriptionId withBlocker |> Option.map Subscription.status |> Option.defaultValue Cancelled)

        printfn "Baseline and handoff checks passed"
