"""Build isolated AgentLang control projects for scheduled-batch-226."""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
ROOT = REPO / ".agentlang/scheduled-batch-226"
SEED = ROOT / "baselines/retained"
RUNTIME = ROOT / "runtime/agentlang/AgentLang.Cli.dll"
OUT = ROOT / "controls/agentlang-calibrated"
VARIANTS = ("correct", "shared-instant", "sort-by-at", "last-error", "partial-success")


COMMON = r'''// frontend: flow/2
record ScheduledSubscriptionRenewal {
    field renewal: SubscriptionRenewal
    field at: Instant
}

record ScheduledRenewalBatchState {
    field outcome: Result<Store, BusinessError>
    field current-store: Store
    field prefix-succeeded: Bool
    field shared-at: Option<Instant>
    field last-error: Option<BusinessError>
}

record ScheduledRenewalSortInsertState {
    field output: List<ScheduledSubscriptionRenewal>
    field item: ScheduledSubscriptionRenewal
    field inserted: Bool
}

fn subscription.sort-scheduled-scan(state: ScheduledRenewalSortInsertState, prior: ScheduledSubscriptionRenewal) -> ScheduledRenewalSortInsertState {
    if state.inserted {
        scheduledRenewalSortInsertState.new(output = list.append(state.output, prior), item = state.item, inserted = true)
    } else {
        if instant.before?(Instant.value(state.item.at), Instant.value(prior.at)) {
            scheduledRenewalSortInsertState.new(output = list.append(list.append(state.output, state.item), prior), item = state.item, inserted = true)
        } else {
            scheduledRenewalSortInsertState.new(output = list.append(state.output, prior), item = state.item, inserted = false)
        }
    }
}

fn subscription.sort-scheduled-insert(values: List<ScheduledSubscriptionRenewal>, item: ScheduledSubscriptionRenewal) -> List<ScheduledSubscriptionRenewal> {
    let initial = scheduledRenewalSortInsertState.new(output = list.empty<ScheduledSubscriptionRenewal>(), item = item, inserted = false)
    let scanned = values.fold(initial, subscription.sort-scheduled-scan)
    if scanned.inserted { scanned.output } else { list.append(scanned.output, item) }
}

'''


def source_for(variant: str) -> str:
    shared = variant == "shared-instant"
    sorted_items = variant == "sort-by-at"
    last_error = variant == "last-error"
    partial = variant == "partial-success"
    if shared:
        at_setup = """    let effective-at = match state.shared-at {
        some first-at => first-at
        none => item.at
    }
    let next-shared-at = option.some<Instant>(effective-at)
"""
    else:
        at_setup = """    let effective-at = item.at
    let next-shared-at = state.shared-at
"""

    if last_error:
        step = r'''fn subscription.renew-scheduled-batch-step(state: ScheduledRenewalBatchState, item: ScheduledSubscriptionRenewal) -> ScheduledRenewalBatchState {
    let applied = subscription.renew-batch(state.current-store, list.singleton<SubscriptionRenewal>(item.renewal), item.at)
    match applied {
        ok updated => scheduledRenewalBatchState.new(outcome = state.outcome, current-store = updated, prefix-succeeded = true, shared-at = state.shared-at, last-error = state.last-error)
        error problem => scheduledRenewalBatchState.new(outcome = result.error<Store, BusinessError>(problem), current-store = state.current-store, prefix-succeeded = state.prefix-succeeded, shared-at = state.shared-at, last-error = option.some<BusinessError>(problem))
    }
}
'''
        result = r'''    match final-state.last-error {
        some problem => result.error<Store, BusinessError>(problem)
        none => result.ok<Store, BusinessError>(final-state.current-store)
    }'''
    else:
        step = f'''fn subscription.renew-scheduled-batch-step(state: ScheduledRenewalBatchState, item: ScheduledSubscriptionRenewal) -> ScheduledRenewalBatchState {{
    match state.outcome {{
        error problem => state
        ok current-store => {{
{at_setup}            let applied = subscription.renew-batch(current-store, list.singleton<SubscriptionRenewal>(item.renewal), effective-at)
            match applied {{
                ok updated => scheduledRenewalBatchState.new(outcome = result.ok<Store, BusinessError>(updated), current-store = updated, prefix-succeeded = true, shared-at = next-shared-at, last-error = state.last-error)
                error problem => scheduledRenewalBatchState.new(outcome = result.error<Store, BusinessError>(problem), current-store = state.current-store, prefix-succeeded = state.prefix-succeeded, shared-at = next-shared-at, last-error = option.some<BusinessError>(problem))
            }}
        }}
    }}
}}
'''
        if partial:
            result = r'''    match final-state.outcome {
        ok updated => result.ok<Store, BusinessError>(updated)
        error problem => {
            if final-state.prefix-succeeded {
                result.ok<Store, BusinessError>(final-state.current-store)
            } else {
                result.error<Store, BusinessError>(problem)
            }
        }
    }'''
        else:
            result = "    final-state.outcome"
    if partial:
        prefix_failure_test = '''        ok updated => equals(updated.subscriptions.count(), 2)
        error problem => false'''
    else:
        prefix_failure_test = '''        ok updated => false
        error problem => equals(problem.code, "SUBSCRIPTION_NOT_FOUND")'''
    if last_error:
        missing_error_check = '''    match updated.last-error {
        some problem => equals(problem.code, "SUBSCRIPTION_NOT_FOUND")
        none => false
    }'''
    else:
        missing_error_check = '''    match updated.outcome {
        error problem => equals(problem.code, "SUBSCRIPTION_NOT_FOUND")
        ok value => false
    }'''
    if last_error:
        state_error_test = ""
    else:
        state_error_test = r'''test subscription.renew-scheduled-batch-step/preserves-existing-error {
    let initial-store = business.seed(unit)
    let earlier = businessError.new(code = "EARLIER_ERROR", message = "Keep the first error.")
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("30000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000021"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-15T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:00:00.0000000+00:00"))
    let initial = scheduledRenewalBatchState.new(outcome = result.error<Store, BusinessError>(earlier), current-store = initial-store, prefix-succeeded = false, shared-at = option.none<Instant>(), last-error = option.none<BusinessError>())
    let updated = subscription.renew-scheduled-batch-step(initial, item)
    match updated.outcome {
        error problem => equals(problem.code, "EARLIER_ERROR")
        ok value => false
    }
    => true
}

'''

    if sorted_items:
        apply = """let ordered = items.fold(list.empty<ScheduledSubscriptionRenewal>(), subscription.sort-scheduled-insert)
let final-state = ordered.fold(initial, subscription.renew-scheduled-batch-step)"""
    else:
        apply = "let final-state = items.fold(initial, subscription.renew-scheduled-batch-step)"
    root = f'''fn subscription.renew-scheduled-batch(store: Store, items: List<ScheduledSubscriptionRenewal>) -> Result<Store, BusinessError> {{
    let initial = scheduledRenewalBatchState.new(outcome = result.ok<Store, BusinessError>(store), current-store = store, prefix-succeeded = false, shared-at = option.none<Instant>(), last-error = option.none<BusinessError>())
{"\n".join("    " + line.strip() for line in apply.splitlines())}
{result}
}}
'''

    tests = r'''test subscription.renew-scheduled-batch/empty-preserves-store {
    let initial = business.seed(unit)
    match subscription.renew-scheduled-batch(initial, list.empty<ScheduledSubscriptionRenewal>()) {
        ok updated => bool.and(equals(updated.customers, initial.customers), bool.and(equals(updated.products, initial.products), bool.and(equals(updated.subscriptions, initial.subscriptions), bool.and(equals(updated.invoices, initial.invoices), bool.and(equals(updated.payments, initial.payments), bool.and(equals(updated.email-outbox, initial.email-outbox), equals(updated.sent-emails, initial.sent-emails)))))))
        error problem => false
    }
    => true
}

test subscription.renew-scheduled-batch/single-item-success {
    let initial = business.seed(unit)
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("30000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000011"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-15T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:00:00.0000000+00:00"))
    match subscription.renew-scheduled-batch(initial, list.singleton<ScheduledSubscriptionRenewal>(item)) {
        ok updated => equals(updated.subscriptions.count(), 2)
        error problem => false
    }
    => true
}

test subscription.renew-scheduled-batch/first-error-without-prefix {
    let initial = business.seed(unit)
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000099"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000014"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-15T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:00:00.0000000+00:00"))
    match subscription.renew-scheduled-batch(initial, list.singleton<ScheduledSubscriptionRenewal>(item)) {
        ok updated => false
        error problem => equals(problem.code, "SUBSCRIPTION_NOT_FOUND")
    }
    => true
}

test subscription.renew-scheduled-batch/prefix-then-first-error {
    let initial = business.seed(unit)
    let first = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("30000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000012"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-15T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:01:00.0000000+00:00"))
    let second = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000099"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000013"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2027-03-15T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:02:00.0000000+00:00"))
    let items = list.append(list.singleton<ScheduledSubscriptionRenewal>(first), second)
    match subscription.renew-scheduled-batch(initial, items) {
@@PREFIX_FAILURE@@
    }
    => true
}

test subscription.renew-scheduled-batch-step/missing-old-error {
    let initial-store = business.seed(unit)
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-15T12:00:00.0000000+00:00"))
    let initial = scheduledRenewalBatchState.new(outcome = result.ok<Store, BusinessError>(initial-store), current-store = initial-store, prefix-succeeded = false, shared-at = option.none<Instant>(), last-error = option.none<BusinessError>())
    let updated = subscription.renew-scheduled-batch-step(initial, item)
@@MISSING_ERROR_CHECK@@
    => true
}

@@STATE_ERROR_TEST@@
test subscription.renew-scheduled-batch-step/successful-item {
    let initial-store = business.seed(unit)
    let at = Instant.new("2026-01-15T12:00:00.0000000+00:00")
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("30000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000022"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-15T12:00:00.0000000+00:00")), at = at)
    let initial = scheduledRenewalBatchState.new(outcome = result.ok<Store, BusinessError>(initial-store), current-store = initial-store, prefix-succeeded = false, shared-at = option.some<Instant>(at), last-error = option.none<BusinessError>())
    let updated = subscription.renew-scheduled-batch-step(initial, item)
    bool.and(updated.prefix-succeeded, equals(updated.current-store.subscriptions.count(), 2))
    => true
}

test subscription.sort-scheduled-scan/inserts-before-later {
    let earlier = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    let later = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000003"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000004"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:02:00.0000000+00:00"))
    let initial = scheduledRenewalSortInsertState.new(output = list.empty<ScheduledSubscriptionRenewal>(), item = earlier, inserted = false)
    let updated = subscription.sort-scheduled-scan(initial, later)
    bool.and(updated.inserted, equals(updated.output, list.append(list.singleton<ScheduledSubscriptionRenewal>(earlier), later)))
    => true
}

test subscription.sort-scheduled-scan/appends-after-earlier {
    let earlier = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    let later = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000003"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000004"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:02:00.0000000+00:00"))
    let initial = scheduledRenewalSortInsertState.new(output = list.empty<ScheduledSubscriptionRenewal>(), item = later, inserted = false)
    let updated = subscription.sort-scheduled-scan(initial, earlier)
    bool.and(equals(updated.inserted, false), equals(updated.output, list.singleton<ScheduledSubscriptionRenewal>(earlier)))
    => true
}

test subscription.sort-scheduled-scan/appends-after-inserted-item {
    let earlier = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    let later = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000003"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000004"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:02:00.0000000+00:00"))
    let initial = scheduledRenewalSortInsertState.new(output = list.singleton<ScheduledSubscriptionRenewal>(earlier), item = earlier, inserted = true)
    let updated = subscription.sort-scheduled-scan(initial, later)
    bool.and(updated.inserted, equals(updated.output, list.append(list.singleton<ScheduledSubscriptionRenewal>(earlier), later)))
    => true
}

test subscription.sort-scheduled-insert/empty-list {
    let item = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    equals(subscription.sort-scheduled-insert(list.empty<ScheduledSubscriptionRenewal>(), item), list.singleton<ScheduledSubscriptionRenewal>(item))
    => true
}

test subscription.sort-scheduled-insert/inserts-at-front {
    let earlier = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    let later = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000003"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000004"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:02:00.0000000+00:00"))
    equals(subscription.sort-scheduled-insert(list.singleton<ScheduledSubscriptionRenewal>(later), earlier), list.append(list.singleton<ScheduledSubscriptionRenewal>(earlier), later))
    => true
}

test subscription.sort-scheduled-insert/appends-at-end {
    let earlier = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000001"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000002"), plan = SubscriptionPlan.monthly(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:01:00.0000000+00:00"))
    let later = scheduledSubscriptionRenewal.new(renewal = subscriptionRenewal.new(old-id = SubscriptionId.new("90000000-0000-0000-0000-000000000003"), new-id = SubscriptionId.new("90000000-0000-0000-0000-000000000004"), plan = SubscriptionPlan.annual(), expires-at = Instant.new("2026-03-01T12:00:00.0000000+00:00")), at = Instant.new("2026-01-01T12:02:00.0000000+00:00"))
    equals(subscription.sort-scheduled-insert(list.singleton<ScheduledSubscriptionRenewal>(earlier), later), list.append(list.singleton<ScheduledSubscriptionRenewal>(earlier), later))
    => true
}
'''
    tests = tests.replace("@@PREFIX_FAILURE@@", prefix_failure_test)
    tests = tests.replace("@@STATE_ERROR_TEST@@", state_error_test)
    tests = tests.replace("@@MISSING_ERROR_CHECK@@", missing_error_check)
    return COMMON + step + "\n" + root + "\n" + tests


def run_cli(project: Path, requests: list[dict]) -> dict:
    proc = subprocess.run(
        ["dotnet", str(RUNTIME), "--project", str(project), "--filesystem", "virtual", "--jsonl"],
        cwd=REPO,
        input="\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=300,
        check=False,
    )
    responses = [json.loads(line) for line in proc.stdout.splitlines() if line.strip()]
    return {"exitCode": proc.returncode, "responses": responses, "stdout": proc.stdout, "stderr": proc.stderr}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", choices=VARIANTS, help="Build only this control first")
    parser.add_argument("--output", type=Path, default=OUT)
    args = parser.parse_args()
    output = args.output if args.output.is_absolute() else REPO / args.output
    if output.exists():
        raise RuntimeError(f"Refusing to overwrite control tree: {output}")
    if not SEED.is_dir() or not RUNTIME.is_file():
        raise FileNotFoundError("Prepared retained baseline or pinned CLI is missing")
    output.mkdir(parents=True)
    built = {}
    variants = (args.variant,) if args.variant else VARIANTS
    for variant in variants:
        project = output / variant
        shutil.copytree(SEED, project)
        source = source_for(variant)
        requests = [
            {"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": source},
            {"op": "test-all"},
            {"op": "commit", "library": True},
            {"op": "test-all"},
        ]
        result = run_cli(project, requests)
        evidence_dir = ROOT / "controls" / "agentlang-buildlogs" / output.name / variant
        evidence_dir.mkdir(parents=True, exist_ok=True)
        (evidence_dir / "source.flow").write_text(source, encoding="utf-8", newline="")
        (evidence_dir / "responses.jsonl").write_text(result["stdout"], encoding="utf-8", newline="")
        (evidence_dir / "stderr.txt").write_text(result["stderr"], encoding="utf-8", newline="")
        (evidence_dir / "requests.jsonl").write_text("\n".join(json.dumps(row, ensure_ascii=False) for row in requests) + "\n", encoding="utf-8", newline="")
        built[variant] = {"project": str(project), "exitCode": result["exitCode"], "responses": result["responses"]}
        (evidence_dir / "build-summary.json").write_text(json.dumps(built[variant], indent=2) + "\n", encoding="utf-8", newline="")
        if result["exitCode"] != 0 or len(result["responses"]) != len(requests) or any(row.get("ok") is not True for row in result["responses"]):
            raise RuntimeError(f"{variant} control build failed; see {evidence_dir}")
    print(json.dumps({"status": "built", "variants": built}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
