"""Frozen cell oracle for the organic-reuse-200 creator and successor tasks.

The creator transitions reuse report 163's independent model after replacing
the fixture term with the operation's fixed value. The successor model applies
those transitions sequentially to an evolving immutable Store projection.
"""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

import cell_model_163 as cell


HERE = Path(__file__).resolve().parent
CREATOR_OPERATIONS = {
    "subscription.renew-monthly": "monthly",
    "subscription.renew-annual": "annual",
}


def creator_expected(item: dict, mutation: str | None = None) -> dict:
    request = item["request"]
    model_item = {
        "store": deepcopy(item["store"]),
        "request": {
            "oldId": request["oldId"],
            "newId": request["newId"],
            "term": item["fixedTerm"],
            "at": request["at"],
            "expiry": request["expiry"],
        },
    }
    return cell.expected(model_item, mutation=mutation)


def creator_cases() -> list[dict]:
    rows = []
    for operation, fixed_term in CREATOR_OPERATIONS.items():
        for inherited in cell.CASES:
            item = deepcopy(inherited)
            case_id = item["id"]
            item["id"] = f"{operation}/{case_id}"
            item["operation"] = operation
            item["fixedTerm"] = fixed_term
            item["request"].pop("term")
            item["expected"] = creator_expected(item)
            rows.append(item)
    return rows


def _change(old_id: str, new_id: str, plan: str = "monthly", expiry: int = 8) -> dict:
    return {"oldId": old_id, "newId": new_id, "plan": plan, "expiry": expiry}


def batch_case(name: str, *, subscriptions: list[dict] | None = None,
               changes: list[dict] | None = None, at: int = 2) -> dict:
    store = cell.baseline()
    if subscriptions is not None:
        store["subscriptions"] = deepcopy(subscriptions)
    return {
        "id": name,
        "operation": "subscription.renew-batch",
        "store": store,
        "request": {"at": at, "changes": deepcopy(changes or [])},
    }


_ALICE_OLD = cell.subscription("old", customer="alice", product="basic", start=0, finish=4)
_BOB_OLD = cell.subscription("third", customer="bob", product="basic", start=0, finish=4)
_ALICE_OTHER = cell.subscription("old2", customer="alice", product="other", start=0, finish=4)
_THIRD_OVERLAP = cell.subscription("third", customer="alice", product="basic", start=6, finish=10)

BATCH_CASES = [
    batch_case("empty-list", changes=[]),
    batch_case("one-monthly-item", changes=[_change("old", "new", "monthly", 8)]),
    batch_case("two-valid-plans", subscriptions=[_ALICE_OLD, _BOB_OLD], changes=[
        _change("old", "new", "monthly", 8),
        _change("third", "new2", "annual", 9),
    ]),
    batch_case("later-item-fails-after-valid-prefix", subscriptions=[_ALICE_OLD, _BOB_OLD], changes=[
        _change("old", "new", "monthly", 8),
        _change("third", "new2", "annual", 2),
    ]),
    batch_case("first-error-precedes-later-expiry", subscriptions=[_ALICE_OLD, _BOB_OLD], changes=[
        _change("missing", "new", "monthly", 8),
        _change("third", "new2", "annual", 2),
    ]),
    batch_case("repeated-replacement-id-fails-second", subscriptions=[_ALICE_OLD, _BOB_OLD], changes=[
        _change("old", "new", "monthly", 8),
        _change("third", "new", "annual", 9),
    ]),
    batch_case("unrelated-active-overlap", subscriptions=[_ALICE_OLD, _THIRD_OVERLAP], changes=[
        _change("old", "new", "monthly", 8),
    ]),
    batch_case("repeated-old-id-fails-second", changes=[
        _change("old", "new", "monthly", 8),
        _change("old", "new2", "annual", 9),
    ]),
    batch_case("preserves-store-and-subscription-order", subscriptions=[_BOB_OLD, _ALICE_OTHER, _ALICE_OLD], changes=[
        _change("old2", "new2", "annual", 8),
        _change("old", "new", "monthly", 9),
    ]),
]


def batch_expected(item: dict, mutation: str | None = None) -> dict:
    original = deepcopy(item["store"])
    working = deepcopy(original)
    changes = deepcopy(item["request"]["changes"])
    if mutation == "reverse-input-order":
        changes.reverse()

    for index, change in enumerate(changes):
        one = {
            "store": working,
            "request": {
                "oldId": change["oldId"],
                "newId": change["newId"],
                "term": change["plan"],
                "at": item["request"]["at"],
                "expiry": change["expiry"],
            },
        }
        transition = cell.expected(one)
        if transition["tag"] == "error":
            if mutation == "leak-partial-ok" and index > 0:
                return {"tag": "ok", "store": working, "input": original}
            return {"tag": "error", "code": transition["code"], "input": original}
        working = transition["store"]

    return {"tag": "ok", "store": working, "input": original}


def make_cases() -> dict:
    creator = creator_cases()
    for item in creator:
        item["expected"] = creator_expected(item)
    batch = deepcopy(BATCH_CASES)
    for item in batch:
        item["expected"] = batch_expected(item)
    return {"creator": creator, "successor": batch}


def _changed_case_ids(items: list[dict], normal, mutated) -> list[str]:
    return [item["id"] for item in items if normal(item) != mutated(item)]


def model_checks() -> dict:
    creator = creator_cases()
    batch = deepcopy(BATCH_CASES)
    for item in batch:
        item["expected"] = batch_expected(item)

    creator_controls = {
        "start-before-cancel": _changed_case_ids(
            creator,
            lambda item: creator_expected(item),
            lambda item: creator_expected(item, "start-before-cancel"),
        ),
        "leak-intermediate-as-ok": _changed_case_ids(
            creator,
            lambda item: creator_expected(item),
            lambda item: creator_expected(item, "leak-intermediate"),
        ),
    }
    batch_controls = {
        "ok-with-partial-store-after-later-error": _changed_case_ids(
            batch,
            lambda item: batch_expected(item),
            lambda item: batch_expected(item, "leak-partial-ok"),
        ),
        "reverse-item-order-error": _changed_case_ids(
            batch,
            lambda item: batch_expected(item),
            lambda item: batch_expected(item, "reverse-input-order"),
        ),
    }

    fixed_blank_term = [row for row in creator if row["id"].endswith("/blank-term")]
    assert len(creator) == 66
    assert len({row["id"] for row in creator}) == len(creator)
    assert all(row["expected"]["tag"] == "ok" for row in fixed_blank_term)
    assert creator_controls["start-before-cancel"]
    assert creator_controls["leak-intermediate-as-ok"]
    assert "later-item-fails-after-valid-prefix" in batch_controls["ok-with-partial-store-after-later-error"]
    assert "first-error-precedes-later-expiry" in batch_controls["reverse-item-order-error"]
    assert batch_expected(next(row for row in batch if row["id"] == "later-item-fails-after-valid-prefix"))["code"] == "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"
    assert batch_expected(next(row for row in batch if row["id"] == "first-error-precedes-later-expiry"))["code"] == "SUBSCRIPTION_NOT_FOUND"
    assert batch_expected(next(row for row in batch if row["id"] == "repeated-replacement-id-fails-second"))["code"] == "DUPLICATE_SUBSCRIPTION"
    assert batch_expected(next(row for row in batch if row["id"] == "unrelated-active-overlap"))["code"] == "SUBSCRIPTION_OVERLAP"

    return {
        "status": "model-only; runtime and control execution still require current pinned CLI preflight",
        "creatorCaseCount": len(creator),
        "creatorExpectedSuccesses": sum(row["expected"]["tag"] == "ok" for row in creator),
        "fixedTermOverrides": {term: sum(row["fixedTerm"] == term for row in creator) for term in ("monthly", "annual")},
        "blankFixtureRowsBecomeSuccessfulUnderFixedTerms": [row["id"] for row in fixed_blank_term],
        "creatorControls": creator_controls,
        "successorCaseCount": len(batch),
        "successorExpectedSuccesses": sum(row["expected"]["tag"] == "ok" for row in batch),
        "successorControls": batch_controls,
        "errorPayloadContract": "Result<Store, BusinessError> errors carry only the BusinessError; wrong-OK controls expose any returned partial Store.",
    }


def export() -> dict:
    cases = make_cases()
    checks = model_checks()
    (HERE / "cases.json").write_text(json.dumps(cases, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (HERE / "model-checks.json").write_text(json.dumps(checks, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return checks


if __name__ == "__main__":
    print(json.dumps(export(), indent=2, ensure_ascii=False))
