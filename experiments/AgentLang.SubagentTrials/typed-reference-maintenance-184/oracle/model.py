"""Independent immutable reference model for shipment scan maintenance.

This file describes only the public contract in the draft task contract. It
does not load candidate source or call either participant implementation.
"""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
from typing import Any


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
EVIDENCE = REPO / ".agentlang/efficacy-maintenance-184/oracle-draft"
VALID_STATUSES = {"received", "in-transit", "delivered"}


def shipment(identifier: str, label: str, scans: list[dict[str, str]] | None = None) -> dict[str, Any]:
    return {"id": identifier, "label": label, "scans": deepcopy(scans or [])}


def store(*shipments: dict[str, Any], audit: str = "audit-sentinel", generation: int = 73) -> dict[str, Any]:
    return {"shipments": deepcopy(list(shipments)), "audit": audit, "generation": generation}


def scan(reference: str, status: str) -> dict[str, str]:
    return {"reference": reference, "status": status}


def event(shipment_id: str, reference: str, status: str) -> dict[str, str]:
    return {"shipmentId": shipment_id, "reference": reference, "status": status}


def case(case_id: str, initial: dict[str, Any], events: list[dict[str, str]], *, entry: str = "single") -> dict[str, Any]:
    return {"id": case_id, "entry": entry, "store": deepcopy(initial), "events": deepcopy(events)}


CASES = [
    case("first-scan", store(shipment("ship-a", "Alpha")), [event("ship-a", "scan-001", "received")]),
    case("append-after-existing", store(shipment("ship-a", "Alpha", [scan("scan-old", "received")])),
         [event("ship-a", "scan-new", "in-transit")]),
    case("same-shipment-replay-same-status",
         store(shipment("ship-a", "Alpha", [scan("scan-dup", "delivered")])),
         [event("ship-a", "scan-dup", "delivered")]),
    case("same-shipment-replay-keeps-first-status",
         store(shipment("ship-a", "Alpha", [scan("scan-dup", "received")])),
         [event("ship-a", "scan-dup", "delivered")]),
    case("same-reference-on-another-shipment",
         store(shipment("ship-a", "Alpha", [scan("shared-ref", "received")]),
               shipment("ship-b", "Beta")),
         [event("ship-b", "shared-ref", "delivered")]),
    case("reference-text-equals-shipment-id",
         store(shipment("same-text", "Text collision")),
         [event("same-text", "same-text", "received")]),
    case("empty-batch-is-identity",
         store(shipment("ship-a", "Alpha", [scan("keep", "in-transit")]), shipment("ship-b", "Beta")),
         [], entry="batch"),
    case("replay-within-batch-keeps-first",
         store(shipment("ship-a", "Alpha")),
         [event("ship-a", "batch-dup", "received"), event("ship-a", "batch-dup", "delivered")],
         entry="batch"),
    case("batch-replay-of-existing-reference-is-identity",
         store(shipment("ship-a", "Alpha", [scan("existing", "in-transit")])),
         [event("ship-a", "existing", "delivered")], entry="batch"),
    case("batch-appends-fresh-references-in-order",
         store(shipment("ship-a", "Alpha", [scan("before", "received")])),
         [event("ship-a", "new-1", "in-transit"), event("ship-a", "new-2", "delivered")],
         entry="batch"),
    case("interleaved-batch-preserves-shipments-and-unrelated-fields",
         store(shipment("ship-b", "Beta", [scan("b-old", "received")]),
               shipment("ship-a", "Alpha", [scan("a-old", "in-transit")]),
               shipment("ship-c", "Gamma", [scan("c-old", "delivered")]),
               audit="audit: keep exact", generation=9001),
         [event("ship-a", "a-new", "delivered"), event("ship-b", "b-new", "in-transit"),
          event("ship-a", "a-newer", "received")], entry="batch"),
    case("unknown-shipment",
         store(shipment("ship-a", "Alpha")), [event("missing", "scan-unknown", "received")]),
    case("invalid-status-for-known-shipment",
         store(shipment("ship-a", "Alpha")), [event("ship-a", "scan-invalid", "pending")]),
    case("unknown-shipment-precedes-invalid-status",
         store(shipment("ship-a", "Alpha")), [event("missing", "scan-unknown", "pending")]),
    case("invalid-status-precedes-replay-deduplication",
         store(shipment("ship-a", "Alpha", [scan("replayed", "received")])),
         [event("ship-a", "replayed", "pending")]),
    case("batch-stops-at-first-error",
         store(shipment("ship-a", "Alpha", [scan("initial", "received")])),
         [event("ship-a", "valid-before-error", "in-transit"),
          event("ship-a", "bad-status", "pending"),
          event("missing", "later-unknown", "received")], entry="batch"),
    case("empty-reference-is-valid-and-deduplicates-exactly",
         store(shipment("ship-a", "Alpha")),
         [event("ship-a", "", "received"), event("ship-a", "", "delivered")], entry="batch"),
    case("whitespace-and-case-are-distinct",
         store(shipment("ship-a", "Alpha")),
         [event("ship-a", "Ref", "received"), event("ship-a", " ref", "in-transit"),
          event("ship-a", "ref", "delivered"), event("ship-a", "Ref ", "received")],
         entry="batch"),
]


def _ingest(current: dict[str, Any], item: dict[str, str], *, mutation: str | None = None) -> tuple[dict[str, Any] | None, str | None]:
    """Return (new store, error code); input values are never modified."""
    shipment_id = item["shipmentId"]
    index = next((i for i, candidate in enumerate(current["shipments"])
                  if candidate["id"] == shipment_id), None)
    if index is None:
        return None, "UNKNOWN_SHIPMENT"
    if item["status"] not in VALID_STATUSES:
        return None, "INVALID_STATUS"

    scoped_duplicate = any(row["reference"] == item["reference"]
                           for row in current["shipments"][index]["scans"])
    global_duplicate = any(row["reference"] == item["reference"]
                           for candidate in current["shipments"] for row in candidate["scans"])
    if mutation == "global-dedup" and global_duplicate:
        return deepcopy(current), None
    if mutation != "always-append" and scoped_duplicate:
        return deepcopy(current), None

    changed = deepcopy(current)
    changed["shipments"][index]["scans"].append(scan(item["reference"], item["status"]))
    return changed, None


def expected(item: dict[str, Any], mutation: str | None = None) -> dict[str, Any]:
    """Evaluate one vector, projecting the full original input on every path."""
    original = deepcopy(item["store"])
    current = deepcopy(original)
    for request in item["events"]:
        current, error = _ingest(current, request, mutation=mutation)
        if error is not None:
            return {"tag": "error", "code": error, "input": original}
        assert current is not None
    return {"tag": "ok", "store": current, "input": original}


def validate_cases() -> dict[str, Any]:
    assert len(CASES) == 18
    assert len({item["id"] for item in CASES}) == len(CASES)
    expected_by_id: dict[str, dict[str, Any]] = {}
    for item in CASES:
        before = deepcopy(item)
        expected_by_id[item["id"]] = expected(item)
        assert item == before, f"model mutated input case {item['id']}"
        assert expected_by_id[item["id"]]["input"] == item["store"], item["id"]
        if expected_by_id[item["id"]]["tag"] == "error":
            assert expected_by_id[item["id"]] == {
                "tag": "error",
                "code": expected_by_id[item["id"]]["code"],
                "input": item["store"],
            }, item["id"]

    required_errors = {
        "unknown-shipment": "UNKNOWN_SHIPMENT",
        "invalid-status-for-known-shipment": "INVALID_STATUS",
        "unknown-shipment-precedes-invalid-status": "UNKNOWN_SHIPMENT",
        "invalid-status-precedes-replay-deduplication": "INVALID_STATUS",
        "batch-stops-at-first-error": "INVALID_STATUS",
    }
    assert {case_id: expected_by_id[case_id]["code"] for case_id in required_errors} == required_errors
    assert expected_by_id["empty-batch-is-identity"]["store"] == expected_by_id["empty-batch-is-identity"]["input"]
    assert expected_by_id["same-shipment-replay-keeps-first-status"]["store"] == expected_by_id["same-shipment-replay-keeps-first-status"]["input"]
    assert expected_by_id["same-reference-on-another-shipment"]["store"]["shipments"][1]["scans"] == [
        {"reference": "shared-ref", "status": "delivered"}
    ]
    interleaved = expected_by_id["interleaved-batch-preserves-shipments-and-unrelated-fields"]["store"]
    assert [row["id"] for row in interleaved["shipments"]] == ["ship-b", "ship-a", "ship-c"]
    assert [row["reference"] for row in interleaved["shipments"][1]["scans"]] == [
        "a-old", "a-new", "a-newer"
    ]
    assert (interleaved["audit"], interleaved["generation"]) == ("audit: keep exact", 9001)
    assert expected_by_id["whitespace-and-case-are-distinct"]["store"]["shipments"][0]["scans"] == [
        {"reference": "Ref", "status": "received"},
        {"reference": " ref", "status": "in-transit"},
        {"reference": "ref", "status": "delivered"},
        {"reference": "Ref ", "status": "received"},
    ]

    always_append_failures = [item["id"] for item in CASES
                              if expected(item, "always-append") != expected(item)]
    global_dedup_failures = [item["id"] for item in CASES
                             if expected(item, "global-dedup") != expected(item)]
    assert "same-shipment-replay-same-status" in always_append_failures
    assert "same-shipment-replay-keeps-first-status" in always_append_failures
    assert "same-reference-on-another-shipment" in global_dedup_failures
    return {
        "status": "model-only; runtime control evidence required separately",
        "caseCount": len(CASES),
        "expectedSuccesses": sum(row["tag"] == "ok" for row in expected_by_id.values()),
        "expectedErrors": {case_id: expected_by_id[case_id]["code"] for case_id in required_errors},
        "mutationDiscrimination": {
            "alwaysAppendFails": always_append_failures,
            "globalCrossShipmentDedupFails": global_dedup_failures,
        },
        "separateEndpoints": [
            "nominal-type-negative-controls",
            "inherited-evidence-preservation",
            "library-qualification",
            "task-finalization",
            "recovery",
        ],
    }


def export() -> dict[str, Any]:
    rows = [dict(**item, expected=expected(item)) for item in CASES]
    HERE.mkdir(parents=True, exist_ok=True)
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (HERE / "cases.json").write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    checks = validate_cases()
    rendered = json.dumps(checks, indent=2, ensure_ascii=False) + "\n"
    (EVIDENCE / "model-checks.json").write_text(rendered, encoding="utf-8")
    (EVIDENCE / "model-checks.stdout.txt").write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return checks


if __name__ == "__main__":
    export()
