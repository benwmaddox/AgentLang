namespace AgentLang.SubscriptionHandoff

open System
open AgentLang.Business.Domain

module SelfTests =
    let private assertEqual label expected actual =
        if actual <> expected then
            failwithf "%s: expected %A but received %A" label expected actual

    let private subscriptionId value =
        SubscriptionId.parse value
        |> Result.defaultWith (fun error -> failwithf "fixture subscription id: %s" (AgentLang.Business.Domain.DomainError.message error))

    let private customerId value =
        CustomerId.parse value
        |> Result.defaultWith (fun error -> failwithf "fixture customer id: %s" (AgentLang.Business.Domain.DomainError.message error))

    let private productId value =
        ProductId.parse value
        |> Result.defaultWith (fun error -> failwithf "fixture product id: %s" (AgentLang.Business.Domain.DomainError.message error))

    let private assertRenewalError label expectedCode expectedMessage
                                   (result: Result<Store, AgentLang.SubscriptionHandoff.DomainError>) =
        match result with
        | Error error ->
            assertEqual (label + " code") expectedCode error.Code
            assertEqual (label + " message") expectedMessage error.Message
        | Ok _ -> failwithf "%s: expected %s" label expectedCode

    let private requireOk label = function
        | Ok value -> value
        | Error error -> failwithf "%s: %s: %s" label error.Code error.Message

    let private expectedRenewal store oldId newId term at expiry =
        match AgentLang.Business.Domain.Subscription.cancel store oldId at with
        | Error error -> Error error
        | Ok (cancelledStore, oldSubscription) ->
            AgentLang.Business.Domain.Subscription.start cancelledStore newId
                (AgentLang.Business.Domain.Subscription.customerId oldSubscription)
                (AgentLang.Business.Domain.Subscription.productId oldSubscription)
                term at expiry
            |> Result.map fst

    let private legacySeed oldId =
        let fixture = Fixtures.create ()
        let customer =
            Store.customer fixture.CustomerId fixture.Store
            |> Option.defaultWith (fun () -> failwith "legacy renewal fixture customer is missing")
        let product =
            Store.product fixture.ProductId fixture.Store
            |> Option.defaultWith (fun () -> failwith "legacy renewal fixture product is missing")
        let customerStore =
            AgentLang.Business.Domain.Store.addCustomer customer AgentLang.Business.Domain.Store.empty
            |> Result.defaultWith (fun error -> failwithf "legacy fixture customer: %s" (AgentLang.Business.Domain.DomainError.message error))
        let productStore =
            AgentLang.Business.Domain.Store.addProduct product customerStore
            |> Result.defaultWith (fun error -> failwithf "legacy fixture product: %s" (AgentLang.Business.Domain.DomainError.message error))
        let store =
            Subscription.start productStore oldId fixture.CustomerId fixture.ProductId "legacy"
                (Fixtures.instant 2026 1 2) (Fixtures.instant 2026 2 1)
            |> Result.map fst
            |> Result.defaultWith (fun error -> failwithf "legacy fixture subscription: %s" (AgentLang.Business.Domain.DomainError.message error))
        { fixture with Store = store; OldSubscriptionId = oldId }

    let private runRenewalScenarios (label: string)
                                    (renewal: Store -> SubscriptionId -> SubscriptionId -> Instant -> Instant -> Result<Store, AgentLang.SubscriptionHandoff.DomainError>)
                                    (term: string) (oldId: SubscriptionId) (newId: SubscriptionId) =
        let seed = legacySeed oldId
        let at = Fixtures.instant 2026 1 15
        let expiry = if term = "annual" then Fixtures.instant 2027 1 15 else Fixtures.instant 2026 3 15
        let originalStore = seed.Store
        let originalSummary = Store.summary originalStore
        let originalSubscription =
            Store.subscription oldId originalStore
            |> Option.defaultWith (fun () -> failwithf "%s legacy input subscription is missing" label)
        let expected =
            expectedRenewal originalStore seed.OldSubscriptionId newId term at expiry
            |> Result.defaultWith (fun error -> failwithf "expected %s renewal failed: %s" label (AgentLang.Business.Domain.DomainError.message error))

        let actual = renewal originalStore seed.OldSubscriptionId newId at expiry
        assertEqual (label + " replacement and full store") (Ok expected) actual
        assertEqual (label + " leaves its input summary unchanged") originalSummary (Store.summary seed.Store)
        assertEqual (label + " leaves its input subscription unchanged") (Some originalSubscription) (Store.subscription oldId seed.Store)

        let missingOld = subscriptionId "30000000-0000-0000-0000-000000000004"
        let absent = renewal Store.empty missingOld newId at expiry
        assertRenewalError (label + " missing old subscription")
            "SUBSCRIPTION_NOT_FOUND" "The subscription does not exist." absent

        let cancelledStore =
            AgentLang.Business.Domain.Subscription.cancel seed.Store seed.OldSubscriptionId at
            |> Result.map fst
            |> Result.defaultWith (fun error -> failwithf "fixture cancellation failed: %s" (AgentLang.Business.Domain.DomainError.message error))
        let cancelled = renewal cancelledStore seed.OldSubscriptionId newId (Fixtures.instant 2026 1 1) expiry
        assertRenewalError (label + " already-cancelled precedence")
            "SUBSCRIPTION_ALREADY_CANCELLED" "The subscription is already cancelled." cancelled

        let beforeStart = renewal seed.Store seed.OldSubscriptionId newId (Fixtures.instant 2026 1 1) expiry
        assertRenewalError (label + " before-start cancellation")
            "CANCEL_BEFORE_START" "A subscription cannot be cancelled before it starts." beforeStart
        assertEqual (label + " before-start failure preserves input summary") originalSummary (Store.summary seed.Store)
        assertEqual (label + " before-start failure preserves subscription") (Some originalSubscription) (Store.subscription oldId seed.Store)

        let duplicateReplacement = renewal seed.Store seed.OldSubscriptionId seed.OldSubscriptionId at at
        assertRenewalError (label + " replacement creation error")
            "DUPLICATE_SUBSCRIPTION" "A subscription with this identifier already exists." duplicateReplacement
        assertEqual (label + " replacement error preserves input summary") originalSummary (Store.summary seed.Store)
        assertEqual (label + " replacement error preserves subscription") (Some originalSubscription) (Store.subscription oldId seed.Store)

    let private renewalChange oldId newId plan expiry =
        { OldId = oldId
          NewId = newId
          Plan = plan
          ExpiresAt = expiry }

    let private runBatchScenarios () =
        let seed = Fixtures.create ()
        let at = Fixtures.instant 2026 1 15
        let original = seed.Store
        let oldId = seed.OldSubscriptionId
        let newId = subscriptionId "30000000-0000-0000-0000-000000000002"
        let finalId = subscriptionId "30000000-0000-0000-0000-000000000003"
        let intermediateExpiry = Fixtures.instant 2026 3 1
        let finalExpiry = Fixtures.instant 2027 1 15

        assertEqual "batch empty list returns the full input store" (Ok original) (Subscription.renewBatch original [] at)

        let single = renewalChange oldId newId Annual finalExpiry
        let singleExpected =
            expectedRenewal original oldId newId "annual" at finalExpiry
            |> Result.defaultWith (fun error -> failwithf "single annual expected result failed: %s" (AgentLang.Business.Domain.DomainError.message error))
        assertEqual "batch single annual item" (Ok singleExpected) (Subscription.renewBatch original [ single ] at)

        let monthly = renewalChange oldId newId Monthly intermediateExpiry
        let annual = renewalChange newId finalId Annual finalExpiry
        let chainedExpected =
            expectedRenewal original oldId newId "monthly" at intermediateExpiry
            |> Result.bind (fun afterMonthly -> expectedRenewal afterMonthly newId finalId "annual" at finalExpiry)
            |> Result.defaultWith (fun error -> failwithf "monthly-to-annual expected result failed: %s" (AgentLang.Business.Domain.DomainError.message error))
        assertEqual "batch monthly then annual at the same instant" (Ok chainedExpected) (Subscription.renewBatch original [ monthly; annual ] at)
        assertEqual "batch success leaves original input unchanged" original seed.Store

        let repeatedOld = renewalChange oldId finalId Annual finalExpiry
        let missingOld = subscriptionId "50000000-0000-0000-0000-000000000106"
        let missingAfterRepeatedOld = renewalChange missingOld (subscriptionId "50000000-0000-0000-0000-000000000107") Monthly finalExpiry
        let firstError = Subscription.renewBatch original [ monthly; repeatedOld; missingAfterRepeatedOld ] at
        assertRenewalError "batch reports first failure after a successful prefix"
            "SUBSCRIPTION_ALREADY_CANCELLED" "The subscription is already cancelled." firstError
        assertEqual "failed batch returns no partial store and preserves its input" original seed.Store

        let missingOldStep = subscriptionId "50000000-0000-0000-0000-000000000203"
        let missingChange = renewalChange missingOldStep (subscriptionId "50000000-0000-0000-0000-000000000204") Monthly finalExpiry
        let initialState = { Outcome = Ok original; At = at }
        let missingStep = Subscription.renewBatchStep initialState missingChange
        assertRenewalError "batch helper missing-old step"
            "SUBSCRIPTION_NOT_FOUND" "The subscription does not exist." missingStep.Outcome
        assertEqual "batch helper missing-old step retains timestamp" at missingStep.At

        let helperError = { Code = "FIRST_BATCH_ERROR"; Message = "Keep the first error." }
        let failedState = { Outcome = Error helperError; At = at }
        let preservedState = Subscription.renewBatchStep failedState monthly
        assertEqual "batch step keeps an earlier error" failedState preservedState
        assertEqual "batch helper preserved error retains timestamp" at preservedState.At

        let monthlyState = Subscription.renewBatchStep initialState monthly
        let annualState = Subscription.renewBatchStep initialState (renewalChange oldId finalId Annual finalExpiry)
        let monthlyExpected = expectedRenewal original oldId newId "monthly" at intermediateExpiry |> Result.mapError AgentLang.SubscriptionHandoff.DomainError.fromNative
        let annualExpected = expectedRenewal original oldId finalId "annual" at finalExpiry |> Result.mapError AgentLang.SubscriptionHandoff.DomainError.fromNative
        assertEqual "batch helper monthly plan case" monthlyExpected monthlyState.Outcome
        assertEqual "batch helper annual plan case" annualExpected annualState.Outcome
        assertEqual "batch helper monthly plan retains timestamp" at monthlyState.At
        assertEqual "batch helper annual plan retains timestamp" at annualState.At

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
            | Error error -> failwithf "valid adjacent subscription start failed: %s" (AgentLang.Business.Domain.DomainError.message error)

        assertEqual "start leaves the input store unchanged" 1 (Store.summary seed.Store).Subscriptions
        assertEqual "start returns a new store" 2 (Store.summary started).Subscriptions

        let overlappingId = subscriptionId "30000000-0000-0000-0000-000000000003"
        match Subscription.start seed.Store overlappingId seed.CustomerId seed.ProductId "monthly"
                (Fixtures.instant 2026 1 15) (Fixtures.instant 2026 2 15) with
        | Error SubscriptionOverlap -> ()
        | Error error -> failwithf "overlap returned the wrong error: %s" (AgentLang.Business.Domain.DomainError.message error)
        | Ok _ -> failwith "overlapping subscription was accepted"

        let blankTermId = subscriptionId "30000000-0000-0000-0000-000000000004"
        match Subscription.start seed.Store blankTermId seed.CustomerId seed.ProductId "  "
                (Fixtures.instant 2026 1 2) (Fixtures.instant 2026 2 1) with
        | Error InvalidSubscriptionTerm -> ()
        | Error error -> failwithf "blank term returned the wrong error: %s" (AgentLang.Business.Domain.DomainError.message error)
        | Ok _ -> failwith "blank subscription term was accepted"

        let cancellationTime = Fixtures.instant 2026 1 15
        let cancelled =
            match Subscription.cancel seed.Store seed.OldSubscriptionId cancellationTime with
            | Ok (updated, subscription) ->
                assertEqual "cancel returns a cancelled subscription" Cancelled (Subscription.status subscription)
                assertEqual "cancel records the requested time" (Some cancellationTime) (Subscription.cancelledAt subscription)
                updated
            | Error error -> failwithf "valid subscription cancel failed: %s" (AgentLang.Business.Domain.DomainError.message error)

        assertEqual "cancel leaves its input store unchanged" Active (Store.subscription seed.OldSubscriptionId seed.Store |> Option.map Subscription.status |> Option.defaultValue Cancelled)
        assertEqual "cancel returns an updated store" Cancelled (Store.subscription seed.OldSubscriptionId cancelled |> Option.map Subscription.status |> Option.defaultValue Active)

        match Subscription.cancel seed.Store seed.OldSubscriptionId (Fixtures.instant 2026 1 1) with
        | Error (CancelBeforeSubscriptionStart _) -> ()
        | Error error -> failwithf "early cancellation returned the wrong error: %s" (AgentLang.Business.Domain.DomainError.message error)
        | Ok _ -> failwith "cancellation before start was accepted"

        runRenewalScenarios "monthly" Subscription.renewMonthly "monthly"
            (subscriptionId "50000000-0000-0000-0000-000000000040")
            (subscriptionId "50000000-0000-0000-0000-000000000041")
        runRenewalScenarios "annual" Subscription.renewAnnual "annual"
            (subscriptionId "50000000-0000-0000-0000-000000000050")
            (subscriptionId "50000000-0000-0000-0000-000000000051")
        runBatchScenarios ()
        printfn "Baseline checks passed: domain, eight monthly/annual scenarios, and six batch/helper scenarios"
