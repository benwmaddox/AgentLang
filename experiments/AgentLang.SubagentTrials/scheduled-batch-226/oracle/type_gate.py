"""Semantic gate for the new scheduled subscription input record."""

from __future__ import annotations

from typing import Any


TYPE_NAME = "ScheduledSubscriptionRenewal"
EXPECTED_FIELDS = [
    {"name": "renewal", "type": "SubscriptionRenewal"},
    {"name": "at", "type": "Instant"},
]


def described_type(response: Any) -> dict[str, Any] | None:
    if not isinstance(response, dict) or response.get("ok") is not True or response.get("kind") != "describe":
        return None
    data = response.get("data")
    return data if isinstance(data, dict) else None


def type_gate(response: Any) -> dict[str, Any]:
    data = described_type(response)
    passed = (
        data is not None
        and data.get("name") == TYPE_NAME
        and data.get("typeKind") == "record"
        and data.get("fields") == EXPECTED_FIELDS
    )
    return {
        "status": "pass" if passed else "failure",
        "typeName": TYPE_NAME,
        "typeKind": data.get("typeKind") if data else None,
        "fields": data.get("fields") if data else None,
    }


def equivalence_control(newline_response: Any, semicolon_response: Any) -> dict[str, Any]:
    newline = type_gate(newline_response)
    semicolon = type_gate(semicolon_response)
    passed = newline["status"] == "pass" and semicolon["status"] == "pass" and newline["fields"] == semicolon["fields"]
    return {
        "status": "pass" if passed else "failure",
        "newlineAccepted": newline["status"] == "pass",
        "semicolonAccepted": semicolon["status"] == "pass",
        "sameCanonicalFields": newline["fields"] == semicolon["fields"],
    }


def wrong_field_type_control(response: Any) -> dict[str, Any]:
    passed = type_gate(response)["status"] == "failure"
    return {"status": "pass" if passed else "failure", "wrongFieldTypeRejected": passed}
