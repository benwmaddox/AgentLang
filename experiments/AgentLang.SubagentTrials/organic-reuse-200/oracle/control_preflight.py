"""Trial-specific Flow/2 source and JSONL request builders for frozen controls.

The companion runner records receipts under the explicit organic-reuse-200
evidence root. It must remain unexecuted until root reviews these controls.
"""

from __future__ import annotations

import json
from pathlib import Path

import model
import score_organic_reuse as scorer


CREATOR_VARIANTS = ("creator-correct", "creator-start-before-cancel", "creator-wrong-ok")
SUCCESSOR_VARIANTS = ("successor-correct", "successor-partial-ok", "successor-reverse-order")
ALL_VARIANTS = CREATOR_VARIANTS + SUCCESSOR_VARIANTS

OBSERVER_SOURCE = """// frontend: flow/2
record OrganicReuse200Observation {
    field input: Store
    field outcome: Result<Store, BusinessError>
}
"""


def creator_source(mutant: str = "correct") -> str:
    if mutant == "correct":
        body = """    match .store.subscription(old-id, store) {
        some old => {
            match .subscription.cancel(store, old-id, at) {
                ok cancelled-store => .subscription.start(cancelled-store, new-id, old.customer-id, old.product-id, term, at, expiry)
                error cancellation-error => result.error<Store, BusinessError>(cancellation-error)
            }
        }
        none => .subscription.cancel(store, old-id, at)
    }"""
    elif mutant == "start-before-cancel":
        body = """    match .store.subscription(old-id, store) {
        some old => {
            match .subscription.start(store, new-id, old.customer-id, old.product-id, term, at, expiry) {
                ok started-store => .subscription.cancel(started-store, old-id, at)
                error start-error => result.error<Store, BusinessError>(start-error)
            }
        }
        none => .subscription.cancel(store, old-id, at)
    }"""
    elif mutant == "wrong-ok":
        body = """    match .store.subscription(old-id, store) {
        some old => {
            match .subscription.cancel(store, old-id, at) {
                ok cancelled-store => {
                    match .subscription.start(cancelled-store, new-id, old.customer-id, old.product-id, term, at, expiry) {
                        ok started-store => result.ok<Store, BusinessError>(started-store)
                        error start-error => result.ok<Store, BusinessError>(cancelled-store)
                    }
                }
                error cancellation-error => result.error<Store, BusinessError>(cancellation-error)
            }
        }
        none => .subscription.cancel(store, old-id, at)
    }"""
    else:
        raise ValueError(f"Unknown creator control variant: {mutant}")

    helper = f"""fn subscription.renew-with-term(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, term: String, at: Instant, expiry: Instant) -> Result<Store, BusinessError> {{
    doc "Cancel the old subscription, then start a fixed-term replacement while preserving error precedence."

{body}
}}
"""
    return helper + "\n" + _creator_roots()


def _creator_roots() -> str:
    return """fn subscription.renew-monthly(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError> {
    doc "Renew an existing subscription for the monthly term."

    .subscription.renew-with-term(store, old-id, new-id, "monthly", at, expiry)
}

fn subscription.renew-annual(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError> {
    doc "Renew an existing subscription for the annual term."

    .subscription.renew-with-term(store, old-id, new-id, "annual", at, expiry)
}
"""


def successor_source(mutant: str = "correct") -> str:
    if mutant not in {"correct", "partial-ok", "reverse-order"}:
        raise ValueError(f"Unknown successor control variant: {mutant}")

    step = """fn subscription.renew-batch-step(state: RenewalBatchState, item: SubscriptionRenewal) -> RenewalBatchState {
    match state.outcome {
        ok current => {
            let next = match item.plan {
                monthly => .subscription.renew-monthly(current, item.old-id, item.new-id, state.at, item.expires-at)
                annual => .subscription.renew-annual(current, item.old-id, item.new-id, state.at, item.expires-at)
            }
            NEXT_RESULT
        }
        error problem => state
    }
}
"""
    if mutant == "partial-ok":
        next_result = """match next {
                ok _ => renewalBatchState.new(outcome = next, at = state.at)
                error _ => renewalBatchState.new(outcome = result.ok<Store, BusinessError>(current), at = state.at)
            }"""
    else:
        next_result = "renewalBatchState.new(outcome = next, at = state.at)"
    step = step.replace("NEXT_RESULT", next_result)

    reverse_step = """fn subscription.reverse-renewal-step(acc: List<SubscriptionRenewal>, item: SubscriptionRenewal) -> List<SubscriptionRenewal> {
    list.concat(list.singleton<SubscriptionRenewal>(item), acc)
}
"""
    if mutant == "reverse-order":
        batch_body = """    let initial = renewalBatchState.new(outcome = result.ok<Store, BusinessError>(store), at = at)
    let reversed = changes.fold(list.empty<SubscriptionRenewal>(), subscription.reverse-renewal-step)
    reversed.fold(initial, subscription.renew-batch-step).outcome"""
    else:
        batch_body = """    let initial = renewalBatchState.new(outcome = result.ok<Store, BusinessError>(store), at = at)
    changes.fold(initial, subscription.renew-batch-step).outcome"""

    return (
        creator_source()
        + "\n"
        + """enum SubscriptionPlan {
    case monthly
    case annual
}

record SubscriptionRenewal {
    field old-id: SubscriptionId
    field new-id: SubscriptionId
    field plan: SubscriptionPlan
    field expires-at: Instant
}

record RenewalBatchState {
    field outcome: Result<Store, BusinessError>
    field at: Instant
}

"""
        + step
        + "\n"
        + reverse_step
        + "\n"
        + f"""fn subscription.renew-batch(store: Store, changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store, BusinessError> {{
    doc "Apply monthly and annual renewals in list order, returning only the first error or the final Store."

{batch_body}
}}
"""
    )


def control_source(variant: str) -> str:
    if variant == "creator-correct":
        return creator_source()
    if variant == "creator-start-before-cancel":
        return creator_source("start-before-cancel")
    if variant == "creator-wrong-ok":
        return creator_source("wrong-ok")
    if variant == "successor-correct":
        return successor_source()
    if variant == "successor-partial-ok":
        return successor_source("partial-ok")
    if variant == "successor-reverse-order":
        return successor_source("reverse-order")
    raise ValueError(f"Unknown control variant: {variant}")


def cases_for_variant(variant: str) -> tuple[str, list[dict]]:
    stage = "creator" if variant.startswith("creator-") else "successor"
    return stage, scorer.load_frozen_cases(stage)


def requests_for_variant(variant: str, example_owners: list[str]) -> list[dict]:
    stage, cases = cases_for_variant(variant)
    requests = [{"op": "test-all"}]
    requests.extend({"op": "example", "word": owner} for owner in example_owners)
    requests.append({"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": OBSERVER_SOURCE})
    requests.append({"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": control_source(variant)})
    requests.extend({
        "op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
        "code": scorer.observation_expression(item, stage),
    } for item in cases)
    return requests


def canonical_jsonl(requests: list[dict]) -> str:
    return "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n"


def expected_control_result(variant: str, case_results: list[dict]) -> dict:
    by_id = {row["caseId"]: row for row in case_results}
    executed_all = bool(case_results) and all(row["executed"] for row in case_results)
    passed_all = executed_all and all(row["passed"] for row in case_results)
    targets: dict[str, dict] = {}
    if variant == "creator-start-before-cancel":
        expected_ids = [
            "subscription.renew-monthly/mid-period-boundary",
            "subscription.renew-annual/mid-period-boundary",
        ]
        targets = {case_id: {"actualTag": "error", "code": "SUBSCRIPTION_OVERLAP"} for case_id in expected_ids}
    elif variant == "creator-wrong-ok":
        expected_ids = [
            "subscription.renew-monthly/bad-period-precedes-overlap",
            "subscription.renew-annual/bad-period-precedes-overlap",
        ]
        targets = {case_id: {"actualTag": "ok"} for case_id in expected_ids}
    elif variant == "successor-partial-ok":
        expected_ids = ["later-item-fails-after-valid-prefix"]
        targets = {case_id: {"actualTag": "ok", "expectedTag": "error"} for case_id in expected_ids}
    elif variant == "successor-reverse-order":
        expected_ids = ["first-error-precedes-later-expiry"]
        targets = {case_id: {"actualTag": "error", "code": "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"} for case_id in expected_ids}
    else:
        expected_ids = []

    target_results = {}
    for case_id in expected_ids:
        row = by_id.get(case_id, {})
        actual = row.get("actual", {})
        expected = row.get("expected", {})
        match = (
            row.get("executed") is True
            and row.get("passed") is False
            and row.get("actualTag") == targets[case_id].get("actualTag")
            and ("expectedTag" not in targets[case_id] or row.get("expectedTag") == targets[case_id]["expectedTag"])
            and ("code" not in targets[case_id] or actual.get("code") == targets[case_id]["code"])
        )
        target_results[case_id] = {"passed": match, "expected": targets[case_id],
                                   "actualTag": actual.get("tag"), "actualCode": actual.get("code"),
                                   "expectedTag": expected.get("tag"), "caseResult": row}

    if variant.endswith("-correct"):
        controls_passed = passed_all
    else:
        controls_passed = executed_all and all(row["passed"] for row in target_results.values())
    return {"passed": controls_passed, "allCasesExecuted": executed_all,
            "allCasesMatchedModel": passed_all, "targetMutantChecks": target_results}


def main() -> int:
    # Unit-level source/request invariants; does not launch AgentLang.
    assert set(ALL_VARIANTS) == set(CREATOR_VARIANTS + SUCCESSOR_VARIANTS)
    creator = creator_source()
    batch = successor_source()
    assert '"monthly"' in creator and '"annual"' in creator
    assert "subscription.start(cancelled-store" in creator and "result.error<Store, BusinessError>(cancellation-error)" in creator
    assert "field plan: SubscriptionPlan" in batch and "field at: Instant" in batch
    assert "changes.fold(initial, subscription.renew-batch-step)" in batch
    print(json.dumps({"status": "control-sources-and-request-builders-ready; no CLI invocation", "variants": list(ALL_VARIANTS)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
