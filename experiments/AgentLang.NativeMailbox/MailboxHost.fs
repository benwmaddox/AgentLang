namespace AgentLang.NativeMailbox

open System
open System.Collections.Generic
open System.Threading
open AgentLang

[<Sealed>]
type CompletionToken internal (mailboxId: string, sequence: int64) =
    member _.MailboxId = mailboxId
    member _.Sequence = sequence
    override _.ToString() = $"{mailboxId}:{sequence}"

exception MailboxProtocolException of code: string * message: string

type ArenaUsage =
    { Bytes: int
      Nodes: int }

type MailboxBackend<'Owner> =
    { Initialize: int64 -> 'Owner
      Begin: 'Owner -> int64 -> 'Owner
      Resume: 'Owner -> int64 -> int option -> 'Owner
      Decode: 'Owner -> Value list
      Usage: 'Owner -> ArenaUsage option
      DisposeOwner: 'Owner -> unit
      EnterScratchLease: unit -> unit
      LeaveScratchLease: unit -> unit
      OutstandingScratchLeases: unit -> int
      PoisonScratch: unit -> unit
      ScratchArenaOwnersCreated: int
      ScratchArenaByteCapacity: int
      ScratchArenaNodeCapacity: int
      ScratchLeaseAcquisitions: unit -> int
      ScratchLeaseReturns: unit -> int
      ScratchPoisonPasses: unit -> int
      DisposeBackend: unit -> unit }

type SuspensionObservation =
    { MailboxId: string
      TokenSequence: int64
      OutstandingScratchLeases: int }

type private MailboxState<'Owner> =
    { mutable Current: 'Owner
      mutable Pending: CompletionToken option
      mutable Busy: bool }

/// A synchronous, single-threaded host model for fixed mailboxes. Handler
/// execution owns one scratch lease only for that call; suspended state and its
/// continuation remain in the promoted retained owner.
[<Sealed>]
type MailboxHost<'Owner>(mailboxIds: string list, backend: MailboxBackend<'Owner>) =
    let ownerThreadId = Thread.CurrentThread.ManagedThreadId
    let allowedIds = Set.ofList mailboxIds
    let mailboxes = Dictionary<string, MailboxState<'Owner>>(StringComparer.Ordinal)
    let lastCompletedToken = Dictionary<string, int64>(StringComparer.Ordinal)
    let suspensions = ResizeArray<SuspensionObservation>()
    let mutable nextSequence = 0L
    let mutable handlerInvocations = 0
    let mutable decodeCalls = 0
    let mutable disposed = false

    let fail code message = raise (MailboxProtocolException(code, message))

    let ensureThread () =
        if Thread.CurrentThread.ManagedThreadId <> ownerThreadId then
            fail "MAILBOX_FOREIGN_THREAD" "Mailbox operations must stay on the host's creating thread."

    let ensureOpen () =
        if disposed then raise (ObjectDisposedException(nameof MailboxHost<'Owner>))

    let requireMailbox mailboxId =
        match mailboxes.TryGetValue mailboxId with
        | true, state -> state
        | false, _ -> fail "MAILBOX_UNKNOWN_ID" $"Mailbox '{mailboxId}' has not been initialized."

    let withHandler action =
        backend.EnterScratchLease ()
        try
            handlerInvocations <- handlerInvocations + 1
            action ()
        finally
            backend.LeaveScratchLease ()

    let nextTokenSequence () =
        if nextSequence = Int64.MaxValue then fail "MAILBOX_TOKEN_EXHAUSTED" "Completion token sequence space is exhausted."
        nextSequence + 1L

    let disposeCore () =
        if not disposed then
            ensureThread ()
            let mutable firstError: exn option = None
            for state in mailboxes.Values do
                try backend.DisposeOwner state.Current
                with error -> if firstError.IsNone then firstError <- Some error
            mailboxes.Clear()
            try backend.DisposeBackend ()
            with error -> if firstError.IsNone then firstError <- Some error
            disposed <- true
            match firstError with
            | Some error -> raise error
            | None -> ()

    do
        if mailboxIds.IsEmpty then invalidArg (nameof mailboxIds) "At least one fixed mailbox identity is required."
        if mailboxIds.Length <> allowedIds.Count then invalidArg (nameof mailboxIds) "Mailbox identities must be unique."
        if mailboxIds |> List.exists String.IsNullOrWhiteSpace then invalidArg (nameof mailboxIds) "Mailbox identities must be nonempty."

    member _.Initialize(mailboxId: string, seed: int64) =
        ensureThread ()
        ensureOpen ()
        if not (allowedIds.Contains mailboxId) then fail "MAILBOX_UNKNOWN_ID" $"'{mailboxId}' is not one of the fixed mailbox identities."
        if mailboxes.ContainsKey mailboxId then fail "MAILBOX_ALREADY_INITIALIZED" $"Mailbox '{mailboxId}' is already initialized."
        let owner = withHandler (fun () -> backend.Initialize seed)
        mailboxes.Add(mailboxId, { Current = owner; Pending = None; Busy = false })

    member _.Begin(mailboxId: string, delta: int64) =
        ensureThread ()
        ensureOpen ()
        let state = requireMailbox mailboxId
        if state.Busy || state.Pending.IsSome then
            fail "MAILBOX_BUSY" $"Mailbox '{mailboxId}' already has an active call or pending completion."
        let sequence = nextTokenSequence ()
        state.Busy <- true
        try
            let nextOwner = withHandler (fun () -> backend.Begin state.Current delta)
            let outstanding = backend.OutstandingScratchLeases ()
            if outstanding <> 0 then
                backend.DisposeOwner nextOwner
                invalidOp $"Mailbox '{mailboxId}' suspended with {outstanding} outstanding scratch lease(s)."
            let previous = state.Current
            let token = CompletionToken(mailboxId, sequence)
            state.Current <- nextOwner
            state.Pending <- Some token
            nextSequence <- sequence
            backend.DisposeOwner previous
            suspensions.Add({ MailboxId = mailboxId; TokenSequence = sequence; OutstandingScratchLeases = outstanding })
            token
        finally
            state.Busy <- false

    member _.Resume(mailboxId: string, token: CompletionToken, divisor: int64, ?retainedByteCapacity: int) =
        ensureThread ()
        ensureOpen ()
        let state = requireMailbox mailboxId
        if state.Busy then fail "MAILBOX_BUSY" $"Mailbox '{mailboxId}' is currently executing a handler."
        if token.MailboxId <> mailboxId then
            fail "MAILBOX_WRONG_OWNER" $"Completion token {token} belongs to mailbox '{token.MailboxId}', not '{mailboxId}'."
        let isLastCompleted =
            match lastCompletedToken.TryGetValue mailboxId with
            | true, sequence -> sequence = token.Sequence
            | false, _ -> false
        match state.Pending with
        | Some pending when Object.ReferenceEquals(pending, token) -> ()
        | Some pending when token.Sequence < pending.Sequence ->
            fail "MAILBOX_STALE_TOKEN" $"Completion token {token} predates the pending token {pending}."
        | Some _ -> fail "MAILBOX_STALE_TOKEN" $"Completion token {token} is not the mailbox's pending token."
        | None when isLastCompleted -> fail "MAILBOX_DUPLICATE_TOKEN" $"Completion token {token} has already been consumed."
        | None -> fail "MAILBOX_STALE_TOKEN" $"Completion token {token} is not pending."

        state.Busy <- true
        try
            let nextOwner = withHandler (fun () -> backend.Resume state.Current divisor retainedByteCapacity)
            let previous = state.Current
            state.Current <- nextOwner
            state.Pending <- None
            lastCompletedToken[mailboxId] <- token.Sequence
            backend.DisposeOwner previous
        finally
            state.Busy <- false

    member _.DecodeCurrent(mailboxId: string) =
        ensureThread ()
        ensureOpen ()
        let state = requireMailbox mailboxId
        decodeCalls <- decodeCalls + 1
        backend.Decode state.Current

    member _.CurrentUsage(mailboxId: string) =
        ensureThread ()
        ensureOpen ()
        requireMailbox mailboxId |> fun state -> backend.Usage state.Current

    member _.CurrentReference(mailboxId: string) : obj =
        ensureThread ()
        ensureOpen ()
        requireMailbox mailboxId |> fun state -> box state.Current

    member _.PendingToken(mailboxId: string) =
        ensureThread ()
        ensureOpen ()
        requireMailbox mailboxId |> fun state -> state.Pending

    member _.PoisonScratch() =
        ensureThread ()
        ensureOpen ()
        let outstanding = backend.OutstandingScratchLeases ()
        if outstanding <> 0 then invalidOp $"Cannot poison scratch while {outstanding} lease(s) are outstanding."
        backend.PoisonScratch ()

    member _.HandlerInvocations = handlerInvocations
    member _.DecodeCalls = decodeCalls
    member _.Suspensions = suspensions |> Seq.toList
    member _.OutstandingScratchLeases = backend.OutstandingScratchLeases ()
    member _.ScratchArenaOwnersCreated = backend.ScratchArenaOwnersCreated
    member _.ScratchArenaByteCapacity = backend.ScratchArenaByteCapacity
    member _.ScratchArenaNodeCapacity = backend.ScratchArenaNodeCapacity
    member _.ScratchLeaseAcquisitions = backend.ScratchLeaseAcquisitions ()
    member _.ScratchLeaseReturns = backend.ScratchLeaseReturns ()
    member _.ScratchPoisonPasses = backend.ScratchPoisonPasses ()

    member this.Dispose() = disposeCore ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
