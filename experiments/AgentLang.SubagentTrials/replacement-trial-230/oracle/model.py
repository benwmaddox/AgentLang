"""Independent model for replacement-trial-230.

The fixture reuses the bounded subscription/store model from report 163. It
keeps the seven scheduled cases from 228 intact and adds direct monthly cases
that exercise the grouped timing input. Integer cells are exact minute offsets
from the scorer epoch.
"""

from __future__ import annotations

from copy import deepcopy
import importlib.util
import json
from pathlib import Path


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[3]
CELL_MODEL_PATH = (
    REPOSITORY / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210"
    / "oracle" / "cell_model_163.py"
)
_SPEC = importlib.util.spec_from_file_location("scheduled_batch_226_cell_model", CELL_MODEL_PATH)
if _SPEC is None or _SPEC.loader is None:
    raise ImportError(f"Cannot load report 163 cell model from {CELL_MODEL_PATH}")
cell = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(cell)


ERROR_MESSAGES = {
    "SUBSCRIPTION_NOT_FOUND": "The subscription does not exist.",
    "SUBSCRIPTION_ALREADY_CANCELLED": "The subscription is already cancelled.",
    "CANCEL_BEFORE_START": "A subscription cannot be cancelled before it starts.",
    "DUPLICATE_SUBSCRIPTION": "A subscription with this identifier already exists.",
    "INVALID_SUBSCRIPTION_TERM": "Subscription term must contain non-whitespace text.",
    "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START": "Subscription expiry must be later than its start.",
    "CUSTOMER_NOT_FOUND": "The customer does not exist.",
    "PRODUCT_NOT_FOUND": "The product does not exist.",
    "SUBSCRIPTION_OVERLAP": "This customer already has an overlapping subscription for the product.",
}


def _store(*subscriptions: dict) -> dict:
    result = cell.baseline()
    result["subscriptions"] = deepcopy(list(subscriptions))
    return result


_ALICE_OLD = cell.subscription("old", customer="alice", product="basic", start=0, finish=300)
_BOB_OLD = cell.subscription("third", customer="bob", product="basic", start=0, finish=300)
_ALICE_OTHER = cell.subscription("old2", customer="alice", product="other", start=0, finish=300)


def scheduled(old_id: str, new_id: str, plan: str, at: int, expiry: int) -> dict:
    return {
        "renewal": {"oldId": old_id, "newId": new_id, "plan": plan, "expiry": expiry},
        "at": at,
    }


def case(identifier: str, subscriptions: tuple[dict, ...], items: list[dict]) -> dict:
    return {
        "id": identifier,
        "operation": "subscription.renew-scheduled-batch",
        "store": _store(*subscriptions),
        "request": {"items": deepcopy(items)},
    }


def monthly_case(identifier: str, subscriptions: tuple[dict, ...], at: int, expiry: int) -> dict:
    return {
        "id": identifier,
        "operation": "subscription.renew-monthly",
        "store": _store(*subscriptions),
        "request": {
            "oldId": "old",
            "newId": "new",
            "timing": {"at": at, "expiresAt": expiry},
        },
    }


CASES = [
    case("empty-list", (_ALICE_OLD,), []),
    case("monthly-annual-distinct-instants", (_ALICE_OLD, _BOB_OLD), [
        scheduled("old", "new", "monthly", 75, 240),
        scheduled("third", "new2", "annual", 140, 420),
    ]),
    case("chained-renewal-replacement-later", (_ALICE_OLD,), [
        scheduled("old", "new", "monthly", 60, 300),
        scheduled("new", "new2", "annual", 180, 480),
    ]),
    case("reverse-chronological-independent-items", (_ALICE_OLD, _BOB_OLD), [
        scheduled("old", "new2", "annual", 180, 420),
        scheduled("third", "new", "monthly", 60, 300),
    ]),
    case("missing-old-first-beats-later-failure", (_ALICE_OLD, _BOB_OLD), [
        scheduled("missing", "new2", "monthly", 75, 300),
        scheduled("third", "new", "annual", 60, 60),
    ]),
    case("valid-prefix-then-missing-old-is-atomic", (_ALICE_OLD,), [
        scheduled("old", "new", "monthly", 60, 240),
        scheduled("missing", "new2", "annual", 90, 360),
    ]),
    case("chained-second-before-first-start", (_ALICE_OLD,), [
        scheduled("old", "new", "monthly", 180, 360),
        scheduled("new", "new2", "annual", 120, 300),
    ]),
    monthly_case("monthly-grouped-time-direct", (_ALICE_OLD, _BOB_OLD, _ALICE_OTHER), 75, 240),
    monthly_case("monthly-expiry-equals-at-exact-error", (_ALICE_OLD, _BOB_OLD), 75, 75),
]


def _transition(store: dict, item: dict) -> dict:
    renewal = item["renewal"]
    return cell.expected({
        "store": deepcopy(store),
        "request": {
            "oldId": renewal["oldId"],
            "newId": renewal["newId"],
            "term": renewal["plan"],
            "at": item["at"],
            "expiry": renewal["expiry"],
        },
    })


def _direct_monthly_transition(store: dict, request: dict) -> dict:
    timing = request["timing"]
    return cell.expected({
        "store": deepcopy(store),
        "request": {
            "oldId": request["oldId"],
            "newId": request["newId"],
            "term": "monthly",
            "at": timing["at"],
            "expiry": timing["expiresAt"],
        },
    })


def _complete_transition(transition: dict, original: dict) -> dict:
    if transition["tag"] == "error":
        return {
            "tag": "error",
            "code": transition["code"],
            "message": ERROR_MESSAGES[transition["code"]],
            "input": original,
        }
    return {"tag": "ok", "store": transition["store"], "input": original}


def expected(item: dict, mutation: str | None = None) -> dict:
    original = deepcopy(item["store"])
    if item["operation"] == "subscription.renew-monthly":
        request = deepcopy(item["request"])
        if mutation == "swap-monthly-timing":
            request["timing"]["at"], request["timing"]["expiresAt"] = (
                request["timing"]["expiresAt"], request["timing"]["at"]
            )
        return _complete_transition(_direct_monthly_transition(original, request), original)

    working = deepcopy(original)
    scheduled_items = deepcopy(item["request"]["items"])
    if mutation == "shared-instant" and scheduled_items:
        shared_at = scheduled_items[0]["at"]
        for scheduled_item in scheduled_items:
            scheduled_item["at"] = shared_at
    elif mutation == "sort-by-at":
        scheduled_items.sort(key=lambda scheduled_item: scheduled_item["at"])
    elif mutation == "swap-monthly-timing":
        for scheduled_item in scheduled_items:
            if scheduled_item["renewal"]["plan"] == "monthly":
                scheduled_item["at"], scheduled_item["renewal"]["expiry"] = (
                    scheduled_item["renewal"]["expiry"], scheduled_item["at"]
                )

    last_error = None
    for index, scheduled_item in enumerate(scheduled_items):
        transition = _transition(working, scheduled_item)
        if transition["tag"] == "error":
            failure = _complete_transition(transition, original)
            if mutation == "partial-success" and index > 0:
                return {"tag": "ok", "store": working, "input": original}
            if mutation == "last-error":
                last_error = failure
                continue
            return failure
        working = transition["store"]

    if last_error is not None:
        return last_error
    return {"tag": "ok", "store": working, "input": original}


def make_cases() -> list[dict]:
    return [dict(**deepcopy(item), expected=expected(item)) for item in CASES]


def _fingerprint(outcome: dict) -> dict:
    if outcome["tag"] == "error":
        return {"tag": "error", "code": outcome["code"], "message": outcome["message"]}
    return {
        "tag": "ok",
        "subscriptions": [
            {key: row[key] for key in ("id", "start", "finish", "status", "cancelled", "term")}
            for row in outcome["store"]["subscriptions"]
        ],
    }


def _canonical_outcome(outcome: dict) -> dict:
    normalized = deepcopy(outcome)
    for key in ("store", "input"):
        projection = normalized.get(key)
        if isinstance(projection, dict):
            for collection in ("customers", "products", "subscriptions", "invoices", "payments"):
                rows = projection.get(collection)
                if isinstance(rows, list):
                    projection[collection] = sorted(
                        rows, key=lambda row: row.get("id", "") if isinstance(row, dict) else row
                    )
    return normalized


def model_checks() -> dict:
    cases = make_cases()
    variants = {
        "correct": None,
        "shared-instant": "shared-instant",
        "sort-by-at": "sort-by-at",
        "last-error": "last-error",
        "partial-success": "partial-success",
        "swap-monthly-timing": "swap-monthly-timing",
    }
    results = {
        variant: [expected(item, mutation) for item in CASES]
        for variant, mutation in variants.items()
    }
    discrimination = {
        variant: [
            item["id"]
            for index, item in enumerate(CASES)
            if _canonical_outcome(results[variant][index]) != _canonical_outcome(results["correct"][index])
        ]
        for variant in variants
        if variant != "correct"
    }
    list_order_only = {
        variant: [
            item["id"]
            for index, item in enumerate(CASES)
            if results[variant][index] != results["correct"][index]
            and _canonical_outcome(results[variant][index]) == _canonical_outcome(results["correct"][index])
        ]
        for variant in variants
        if variant != "correct"
    }
    assert len(cases) == 9 and len({item["id"] for item in cases}) == 9
    assert all(len(rows) == 9 for rows in results.values()), "every model variant must execute all nine cases"
    assert discrimination["shared-instant"]
    assert "chained-second-before-first-start" in discrimination["sort-by-at"]
    assert "missing-old-first-beats-later-failure" in discrimination["last-error"]
    assert "valid-prefix-then-missing-old-is-atomic" in discrimination["partial-success"]
    assert expected(CASES[1])["store"]["subscriptions"][-2]["start"] == 75
    assert expected(CASES[1])["store"]["subscriptions"][-1]["start"] == 140
    assert expected(CASES[2])["store"]["subscriptions"][-1]["start"] == 180
    assert expected(CASES[4])["code"] == "SUBSCRIPTION_NOT_FOUND"
    assert expected(CASES[4])["message"] == ERROR_MESSAGES["SUBSCRIPTION_NOT_FOUND"]
    assert expected(CASES[5])["code"] == "SUBSCRIPTION_NOT_FOUND"
    assert expected(CASES[5])["input"] == CASES[5]["store"]
    assert expected(CASES[6])["code"] == "CANCEL_BEFORE_START"
    assert expected(CASES[6])["message"] == ERROR_MESSAGES["CANCEL_BEFORE_START"]
    assert results["sort-by-at"][6]["code"] == "SUBSCRIPTION_NOT_FOUND"
    assert results["last-error"][4]["code"] == "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"
    assert results["partial-success"][5]["tag"] == "ok"

    direct_success = expected(CASES[7])
    assert direct_success["tag"] == "ok"
    assert direct_success["input"] == CASES[7]["store"]
    expected_rows = {row["id"]: row for row in direct_success["store"]["subscriptions"]}
    assert expected_rows["old"]["cancelled"] == 75
    assert expected_rows["new"]["start"] == 75 and expected_rows["new"]["finish"] == 240
    assert "third" in expected_rows and "old2" in expected_rows
    assert expected(CASES[8])["code"] == "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"
    assert expected(CASES[8])["message"] == ERROR_MESSAGES["SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"]
    assert expected(CASES[8])["input"] == CASES[8]["store"]
    assert "monthly-grouped-time-direct" in discrimination["swap-monthly-timing"]

    return {
        "status": "model-controls-passed",
        "caseCount": len(cases),
        "casesEvaluatedPerVariant": {name: len(rows) for name, rows in results.items()},
        "expectedSuccesses": sum(item["expected"]["tag"] == "ok" for item in cases),
        "minuteCellContract": "integer cells are exact minute offsets from 2026-01-01T12:00:00Z",
        "discrimination": discrimination,
        "listOrderOnlyDifferences": list_order_only,
        "comparisonContract": "canonical full Store contents sorted by entity ID; list-order behavior remains observable in list-backed AgentLang stores",
        "perCaseControlResults": [
            {
                "caseId": item["id"],
                **{
                    variant: {
                        "matchesCorrect": _canonical_outcome(results[variant][index]) == _canonical_outcome(results["correct"][index]),
                        "matchesCorrectWithListOrder": results[variant][index] == results["correct"][index],
                        "outcome": _fingerprint(results[variant][index]),
                    }
                    for variant in variants
                },
            }
            for index, item in enumerate(CASES)
        ],
        "errorContract": "Result errors expose the exact BusinessError code and message; errors return no partial Store.",
    }


def export() -> dict:
    cases = make_cases()
    checks = model_checks()
    (HERE / "cases.json").write_text(json.dumps(cases, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (HERE / "model-checks.json").write_text(json.dumps(checks, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return checks


if __name__ == "__main__":
    print(json.dumps(export(), indent=2, ensure_ascii=False))
