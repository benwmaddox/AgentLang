"""Frozen typed-failure model for the nine inherited subscription batch cases.

Input stores and changes come from organic-reuse-210. The output projection is
independent: it adds the first failing input index, ID, and the exact
BusinessError payload that the inherited single-renewal transition produces.
"""

from __future__ import annotations

from copy import deepcopy
import hashlib
import importlib.util
from pathlib import Path
import re
import sys


HERE = Path(__file__).resolve().parent
PREDECESSOR_ORACLE = (
    HERE.parents[1] / "organic-reuse-210" / "oracle"
).resolve()
PREDECESSOR_MODEL_PATH = PREDECESSOR_ORACLE / "model.py"


BUSINESS_ERROR_MESSAGES = {
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
CAUSE_SOURCE_HASHES = {
    "subscription.cancel": "b4b1f8761da831fac75f29f5e99ad3aa98f9f90096a1a2b7fbe31ae9fc43f149",
    "subscription.start": "595b6c781287504a4b6530864de52f92462ad172daca2a9cbd0ae226d4d0743d",
}
CAUSE_SOURCE_FOR_CODE = {
    "SUBSCRIPTION_NOT_FOUND": "subscription.cancel",
    "SUBSCRIPTION_ALREADY_CANCELLED": "subscription.cancel",
    "CANCEL_BEFORE_START": "subscription.cancel",
    "DUPLICATE_SUBSCRIPTION": "subscription.start",
    "INVALID_SUBSCRIPTION_TERM": "subscription.start",
    "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START": "subscription.start",
    "CUSTOMER_NOT_FOUND": "subscription.start",
    "PRODUCT_NOT_FOUND": "subscription.start",
    "SUBSCRIPTION_OVERLAP": "subscription.start",
}


def _load_predecessor_model():
    """Load 210's frozen input cases without leaving its generic module names installed."""
    original_path = list(sys.path)
    previous_cell_model = sys.modules.pop("cell_model_163", None)
    sys.path.insert(0, str(PREDECESSOR_ORACLE))
    try:
        spec = importlib.util.spec_from_file_location(
            "typed_batch_failure_216_organic_reuse_210_model",
            PREDECESSOR_MODEL_PATH,
        )
        if spec is None or spec.loader is None:
            raise ImportError(f"Cannot load frozen 210 model from {PREDECESSOR_MODEL_PATH}")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module
    finally:
        sys.path[:] = original_path
        if previous_cell_model is not None:
            sys.modules["cell_model_163"] = previous_cell_model
        else:
            sys.modules.pop("cell_model_163", None)


PREDECESSOR = _load_predecessor_model()
CASE_COUNT = 9


def _business_error(code: str) -> dict:
    try:
        message = BUSINESS_ERROR_MESSAGES[code]
    except KeyError as error:
        raise ValueError(f"No frozen BusinessError message for {code!r}") from error
    return {"code": code, "message": message}


def cause_source_provenance() -> dict:
    """Verify expected errors against immutable report-163 seed definitions."""
    repo = PREDECESSOR_ORACLE.parents[3]
    seed_project = repo / ".agentlang" / "subscription-handoff-161" / "seeds" / "agentlang-project"
    source_rows = {}
    for name, expected_hash in CAUSE_SOURCE_HASHES.items():
        source_path = seed_project / ".agentlang" / "store" / "objects" / f"{expected_hash}.agent"
        source = source_path.read_text(encoding="utf-8-sig")
        actual_hash = hashlib.sha256(source_path.read_bytes()).hexdigest()
        if actual_hash != expected_hash:
            raise ValueError(f"Frozen cause source hash changed for {name}")
        pairs = dict(re.findall(
            r'code\s*=\s*"([^\"]+)"\s*,\s*message\s*=\s*"([^\"]+)"',
            source,
        ))
        expected_codes = {
            code for code, source_name in CAUSE_SOURCE_FOR_CODE.items()
            if source_name == name
        }
        if any(pairs.get(code) != BUSINESS_ERROR_MESSAGES[code] for code in expected_codes):
            raise ValueError(f"Frozen BusinessError constructors changed for {name}")
        source_rows[name] = {
            "definitionHash": expected_hash,
            "sha256": actual_hash,
            "verifiedCodes": sorted(expected_codes),
        }
    if set(CAUSE_SOURCE_FOR_CODE) != set(BUSINESS_ERROR_MESSAGES):
        raise ValueError("Every exact cause message must map to one frozen source definition")
    return {
        "study": "subscription-handoff-161 report-163 seed",
        "seedProject": str(seed_project),
        "sources": source_rows,
        "expectedBusinessErrorMessagesVerified": True,
    }


def _transition(item: dict, store: dict, change: dict) -> dict:
    return PREDECESSOR.cell.expected({
        "store": deepcopy(store),
        "request": {
            "oldId": change["oldId"],
            "newId": change["newId"],
            "term": change["plan"],
            "at": item["request"]["at"],
            "expiry": change["expiry"],
        },
    })


def batch_expected(item: dict, mutation: str | None = None) -> dict:
    """Apply changes in input order, stopping at the first error by default."""
    original = deepcopy(item["store"])
    working = deepcopy(original)
    changes = deepcopy(item["request"]["changes"])
    first_failure = None
    last_failure = None

    for index, change in enumerate(changes):
        transition = _transition(item, working, change)
        if transition["tag"] == "error":
            failure = {
                "index": index,
                "old-id": change["oldId"],
                "cause": _business_error(transition["code"]),
            }
            if first_failure is None:
                first_failure = failure
            last_failure = failure
            if mutation != "last-error-wins":
                break
            continue
        working = transition["store"]

    failure = last_failure if mutation == "last-error-wins" else first_failure
    if failure is not None:
        if mutation == "always-zero-index":
            failure = {**failure, "index": 0}
        elif mutation == "changed-message-same-code":
            failure = {
                **failure,
                "cause": {
                    **failure["cause"],
                    "message": failure["cause"]["message"] + " (changed)",
                },
            }
        return {"tag": "error", "failure": failure, "input": original}
    return {"tag": "ok", "store": working, "input": original}


def make_cases() -> list[dict]:
    cases = deepcopy(PREDECESSOR.BATCH_CASES)
    if len(cases) != CASE_COUNT:
        raise ValueError(f"Frozen predecessor model has {len(cases)} cases, expected {CASE_COUNT}")
    for item in cases:
        item["expected"] = batch_expected(item)
    return cases


def model_checks() -> dict:
    cases = make_cases()
    expected_by_id = {item["id"]: item["expected"] for item in cases}
    always_zero_failures = [
        item["id"] for item in cases
        if batch_expected(item, "always-zero-index") != item["expected"]
    ]
    last_error_failures = [
        item["id"] for item in cases
        if batch_expected(item, "last-error-wins") != item["expected"]
    ]
    changed_message_failures = [
        item["id"] for item in cases
        if batch_expected(item, "changed-message-same-code") != item["expected"]
    ]
    outcome_counts = {
        "success": sum(row["expected"]["tag"] == "ok" for row in cases),
        "error": sum(row["expected"]["tag"] == "error" for row in cases),
    }
    for item in cases:
        inherited = next(row for row in PREDECESSOR.BATCH_CASES if row["id"] == item["id"])
        assert item["store"] == inherited["store"]
        assert item["request"] == inherited["request"]

    expected_failures = {
        "later-item-fails-after-valid-prefix": {
            "index": 1,
            "old-id": "third",
            "cause": _business_error("SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"),
        },
        "first-error-precedes-later-expiry": {
            "index": 0,
            "old-id": "missing",
            "cause": _business_error("SUBSCRIPTION_NOT_FOUND"),
        },
        "repeated-replacement-id-fails-second": {
            "index": 1,
            "old-id": "third",
            "cause": _business_error("DUPLICATE_SUBSCRIPTION"),
        },
        "unrelated-active-overlap": {
            "index": 0,
            "old-id": "old",
            "cause": _business_error("SUBSCRIPTION_OVERLAP"),
        },
        "repeated-old-id-fails-second": {
            "index": 1,
            "old-id": "old",
            "cause": _business_error("SUBSCRIPTION_ALREADY_CANCELLED"),
        },
    }
    for case_id, failure in expected_failures.items():
        assert expected_by_id[case_id] == {
            "tag": "error", "failure": failure,
            "input": next(item["store"] for item in cases if item["id"] == case_id),
        }

    assert outcome_counts == {"success": 4, "error": 5}
    assert len(always_zero_failures) == 3
    assert len(last_error_failures) == 1
    assert len(changed_message_failures) == 5
    assert last_error_failures == ["first-error-precedes-later-expiry"]
    provenance = cause_source_provenance()
    return {
        "status": "model-checks-passed; actor source preservation review remains required",
        "caseCount": len(cases),
        "outcomeCounts": outcome_counts,
        "correctControlPassed": len(cases),
        "alwaysZeroIndex": {
            "predictedPassed": len(cases) - len(always_zero_failures),
            "failureIds": always_zero_failures,
        },
        "lastErrorWins": {
            "predictedPassed": len(cases) - len(last_error_failures),
            "failureIds": last_error_failures,
        },
        "changedMessageSameCode": {
            "predictedPassed": len(cases) - len(changed_message_failures),
            "failureIds": changed_message_failures,
        },
        "causeSourceProvenance": provenance,
        "semanticPreservationReviewRequired": True,
        "causeProjection": "exact BusinessError code and message from the frozen subscription transition definitions",
    }


if __name__ == "__main__":
    import json

    print(json.dumps(model_checks(), indent=2, ensure_ascii=False))
