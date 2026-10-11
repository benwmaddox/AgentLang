"""Create isolated correct and mutant F# producer sources for oracle calibration."""
from __future__ import annotations

import hashlib
import json
import shutil
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
DATA = REPO / ".agentlang/scheduled-batch-226"
BASELINE = DATA / "baselines/fsharp"
OUT = DATA / "controls/fsharp-calibrated"
VARIANTS = ("correct", "shared-instant", "sort-by-at", "last-error", "partial-success")
TYPE = """type ScheduledSubscriptionRenewal =
    { Renewal: SubscriptionRenewal
      At: Instant }
"""
ITEM = """    let private renewScheduledItem (store: Store) (item: ScheduledSubscriptionRenewal) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let renewal = item.Renewal
        match renewal.Plan with
        | Monthly -> renewMonthly store renewal.OldId renewal.NewId item.At renewal.ExpiresAt
        | Annual -> renewAnnual store renewal.OldId renewal.NewId item.At renewal.ExpiresAt

"""
CORRECT = """    let renewScheduledBatch (store: Store) (items: ScheduledSubscriptionRenewal list) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let apply outcome item =
            match outcome with
            | Error error -> Error error
            | Ok current -> renewScheduledItem current item
        List.fold apply (Ok store) items
"""
SHARED_INSTANT = """    let renewScheduledBatch (store: Store) (items: ScheduledSubscriptionRenewal list) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let sharedAt = items |> List.tryHead |> Option.map (fun item -> item.At) |> Option.defaultValue System.DateTimeOffset.MinValue
        let apply outcome item =
            match outcome with
            | Error error -> Error error
            | Ok current -> renewScheduledItem current { item with At = sharedAt }
        List.fold apply (Ok store) items
"""
SORTED = """    let renewScheduledBatch (store: Store) (items: ScheduledSubscriptionRenewal list) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let apply outcome item =
            match outcome with
            | Error error -> Error error
            | Ok current -> renewScheduledItem current item
        items |> List.sortBy (fun item -> item.At) |> List.fold apply (Ok store)
"""
LAST_ERROR = """    let renewScheduledBatch (store: Store) (items: ScheduledSubscriptionRenewal list) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let apply (current, lastError) item =
            match renewScheduledItem current item with
            | Ok next -> (next, lastError)
            | Error error -> (current, Some error)
        let finalStore, lastError = List.fold apply (store, None) items
        match lastError with
        | Some error -> Error error
        | None -> Ok finalStore
"""
PARTIAL = """    let renewScheduledBatch (store: Store) (items: ScheduledSubscriptionRenewal list) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        let apply (current, succeeded, failure) item =
            match failure with
            | Some _ -> (current, succeeded, failure)
            | None ->
                match renewScheduledItem current item with
                | Ok next -> (next, true, None)
                | Error error -> (current, succeeded, Some error)
        let finalStore, succeeded, failure = List.fold apply (store, false, None) items
        match failure with
        | Some _ when succeeded -> Ok finalStore
        | Some error -> Error error
        | None -> Ok finalStore
"""
IMPLEMENTATIONS = {
    "correct": CORRECT,
    "shared-instant": SHARED_INSTANT,
    "sort-by-at": SORTED,
    "last-error": LAST_ERROR,
    "partial-success": PARTIAL,
}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def make_operations(source: str, variant: str) -> str:
    marker = "module Subscription =\n"
    if source.count(marker) != 1:
        raise RuntimeError("Expected one Subscription module in the frozen F# baseline.")
    type_marker = source.index(marker)
    module_start = type_marker
    head = source[:module_start]
    module = source[module_start:]
    if "renewScheduledBatch" in module or "ScheduledSubscriptionRenewal" in head:
        raise RuntimeError("Scheduled API already exists in the baseline.")
    return head + TYPE + "\n" + module + "\n" + ITEM + IMPLEMENTATIONS[variant]


def main() -> None:
    if OUT.exists():
        raise RuntimeError(f"Refusing to overwrite existing control tree: {OUT}")
    domain = BASELINE / "Domain.fs"
    operations = BASELINE / "Operations.fs"
    if not domain.is_file() or not operations.is_file():
        raise RuntimeError("Frozen F# baseline sources are missing.")
    OUT.mkdir(parents=True)
    rows = {}
    for variant in VARIANTS:
        destination = OUT / variant
        destination.mkdir()
        shutil.copyfile(domain, destination / "Domain.fs")
        (destination / "Operations.fs").write_text(
            make_operations(operations.read_text(encoding="utf-8"), variant),
            encoding="utf-8", newline="",
        )
        rows[variant] = {
            "domainSha256": sha(destination / "Domain.fs"),
            "operationsSha256": sha(destination / "Operations.fs"),
            "changedSourceFiles": ["Operations.fs"],
        }
    (OUT / "source-index.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8", newline="")
    print(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
