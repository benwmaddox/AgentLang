"""Unit/projection controls for the representation adapter; no CLI is launched."""

from __future__ import annotations

from copy import deepcopy
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

import score_reference as score


REFERENCE_TYPE = {
    "kind": "nominal",
    "name": "TrackingReference",
    "nominalKind": "record",
    "typeKey": 17,
}


def string_node(value: str) -> dict:
    return {"kind": "string", "value": value}


def field(name: str, value: dict, type_metadata: dict) -> dict:
    return {"name": name, "type": type_metadata, "value": value}


def record(name: str, fields: list[dict]) -> dict:
    return {"kind": "record", "name": name, "typeKey": 1, "fields": fields}


def tracking_reference(value: str) -> dict:
    return record("TrackingReference", [field("value", string_node(value), {"kind": "string"})])


def scan(reference: dict, status: str = "received") -> dict:
    return record("Scan", [
        field("reference", reference, REFERENCE_TYPE),
        field("status", string_node(status), {"kind": "string"}),
    ])


def store_node(value: dict) -> dict:
    shipments = []
    for shipment in value["shipments"]:
        scans = [scan(tracking_reference(row["reference"]), row["status"])
                 for row in shipment["scans"]]
        shipments.append(record("Shipment", [
            field("id", {"kind": "scalar", "name": "ShipmentId", "typeKey": 6,
                          "value": string_node(shipment["id"])},
                  {"kind": "nominal", "name": "ShipmentId", "nominalKind": "scalar", "typeKey": 6}),
            field("label", string_node(shipment["label"]), {"kind": "string"}),
            field("scans", {"kind": "list", "items": scans}, {"kind": "list"}),
        ]))
    return record("Store", [
        field("shipments", {"kind": "list", "items": shipments}, {"kind": "list"}),
        field("audit", string_node(value["audit"]), {"kind": "string"}),
        field("generation", {"kind": "int", "value": str(value["generation"])}, {"kind": "int"}),
    ])


def observation_response(input_store: dict, outcome: dict) -> dict:
    if outcome["tag"] == "ok":
        result = {"kind": "result", "case": "ok", "value": store_node(outcome["store"])}
    else:
        case = outcome["code"].lower().replace("_", "-")
        result = {"kind": "result", "case": "error",
                  "error": {"kind": "enum", "name": "ScanError", "case": case}}
    root = record("Reference184Observation", [
        field("input", store_node(input_store), {"kind": "nominal", "name": "Store", "nominalKind": "record"}),
        field("outcome", result, {"kind": "result"}),
    ])
    return {"data": {"structuredStack": {"values": [root]}}}


class TrackingReferenceAdapterTests(unittest.TestCase):
    def test_expression_always_uses_record_constructor(self) -> None:
        self.assertEqual(score.reference_expr(""), 'trackingReference.new(value = "")')
        expression = score.observation_expr({
            "id": "probe", "entry": "single", "store": {"shipments": [], "audit": "a", "generation": 1},
            "events": [{"shipmentId": "ship-a", "reference": "raw", "status": "received"}],
        })
        self.assertIn('trackingReference.new(value = "raw")', expression)
        self.assertNotIn('reference = "raw"', expression)

    def test_exact_record_unwraps_and_arbitrary_records_stay_structured(self) -> None:
        self.assertEqual(score.unwrap_tracking_reference(tracking_reference(" Ref "), REFERENCE_TYPE), " Ref ")
        other = record("OtherReference", [field("value", string_node(" Ref "), {"kind": "string"})])
        with self.assertRaisesRegex(ValueError, "TrackingReference record"):
            score.unwrap_tracking_reference(other, REFERENCE_TYPE)
        self.assertEqual(score.unwrap_structured(other), {"value": " Ref "})

    def test_raw_string_wrong_identity_and_wrong_declared_type_reject(self) -> None:
        with self.assertRaisesRegex(ValueError, "TrackingReference record"):
            score.unwrap_tracking_reference(string_node("raw"), REFERENCE_TYPE)
        with self.assertRaisesRegex(ValueError, "not declared"):
            score.unwrap_tracking_reference(tracking_reference("raw"), {"kind": "string"})
        with self.assertRaisesRegex(ValueError, "TrackingReference record"):
            score.unwrap_structured(scan(string_node("raw")))

    def test_wrong_record_shape_and_malformed_fields_reject(self) -> None:
        extra = record("TrackingReference", [
            field("value", string_node("raw"), {"kind": "string"}),
            field("extra", string_node("x"), {"kind": "string"}),
        ])
        wrong_inner_type = record("TrackingReference", [
            field("value", {"kind": "int", "value": "7"}, {"kind": "int"}),
        ])
        duplicate = record("TrackingReference", [
            field("value", string_node("one"), {"kind": "string"}),
            field("value", string_node("two"), {"kind": "string"}),
        ])
        malformed = record("TrackingReference", [{"name": "value", "type": {"kind": "string"}}])
        for value in (extra, wrong_inner_type, duplicate, malformed):
            with self.subTest(value=value), self.assertRaises(ValueError):
                score.unwrap_tracking_reference(value, REFERENCE_TYPE)

    def test_runtime_record_metadata_rejects_wrong_shape_or_validator(self) -> None:
        valid_type = {"name": "TrackingReference", "kind": "record",
                      "fields": [{"name": "value", "type": "String"}], "validator": None}

        def context(type_row: dict) -> dict:
            return {"ok": True, "kind": "context",
                    "data": {"types": [type_row], "truncated": False, "typesOmitted": 0}}

        self.assertEqual(score.tracking_reference_context_metadata(context(valid_type))["status"], "pass")
        for type_row in (
            {**valid_type, "kind": "scalar"},
            {**valid_type, "fields": [{"name": "other", "type": "String"}]},
            {**valid_type, "fields": [{"name": "value", "type": "String"},
                                       {"name": "extra", "type": "String"}]},
            {**valid_type, "validator": "string.lowercase?"},
        ):
            with self.subTest(type_row=type_row):
                self.assertEqual(score.tracking_reference_context_metadata(context(type_row))["status"], "failure")

    def test_helper_branch_requires_exact_lookup_target_field_and_accessor(self) -> None:
        helper_type = {"ok": True, "kind": "type-of",
                       "data": {"inputs": ["ShipmentScanLookup", "Scan"],
                                "outputs": ["ShipmentScanLookup"]}}
        unavailable = {"ok": False, "kind": "error"}

        def score_helper(target_field: dict, target_accessor: dict) -> dict:
            context = {"ok": True, "kind": "context", "data": {
                "types": [{"name": "ShipmentScanLookup", "kind": "record",
                           "fields": [target_field]}],
                "words": [{"name": "shipmentScanLookup.target", **target_accessor}],
                "truncated": False,
            }}
            def result(response: dict) -> dict:
                return {"command": ["fixture"], "exitCode": 0, "responseCount": 1,
                        "parseError": None, "responses": [response], "evidence": {}}
            with patch.object(score, "run_cli", side_effect=[result(unavailable), result(helper_type), result(context)]):
                return score.flow_helper_signature(Path("cli"), Path("project"), Path("run"))

        valid = score_helper({"name": "target", "type": "TrackingReference"},
                             {"inputs": ["ShipmentScanLookup"], "outputs": ["TrackingReference"]})
        self.assertEqual(valid["status"], "pass")
        selected = valid["helpers"][0]
        self.assertTrue(selected["lookupStateHasTargetTrackingReference"])
        self.assertTrue(selected["lookupTargetAccessorCompatible"])

        wrong_target = score_helper({"name": "other", "type": "TrackingReference"},
                                    {"inputs": ["ShipmentScanLookup"], "outputs": ["TrackingReference"]})
        self.assertEqual(wrong_target["status"], "signature-mismatch")
        wrong_accessor = score_helper({"name": "target", "type": "TrackingReference"},
                                      {"inputs": ["ShipmentScanLookup"], "outputs": ["String"]})
        self.assertEqual(wrong_accessor["status"], "signature-mismatch")

    def test_source_record_gate_rejects_extra_field_and_validator(self) -> None:
        for declaration in (
            "record TrackingReference {\n    field value: String\n}",
            "record TrackingReference {\n    field value: String\n    field other: String\n}",
            "record TrackingReference {\n    field value: String\n    validator trackingReference.valid?\n}",
        ):
            with tempfile.TemporaryDirectory(dir=score.REPO / ".agentlang/efficacy-maintenance-197") as directory:
                path = Path(directory)
                (path / "dictionary.agent").write_text(declaration + "\n", encoding="utf-8")
                status = score.tracking_reference_source_metadata(path)["status"]
            self.assertEqual(status, "pass" if declaration.endswith("field value: String\n}") else "failure")

    def test_projection_matches_unchanged_model_and_rejects_bad_observations(self) -> None:
        model = score.load_model()
        cases = {item["id"]: item for item in model.CASES}

        control = cases["first-scan"]
        expected = model.expected(control)
        projected = score.observation_projection(observation_response(control["store"], expected))
        self.assertEqual(projected, expected)

        distinct = cases["whitespace-and-case-are-distinct"]
        expected_distinct = model.expected(distinct)
        normalized = deepcopy(expected_distinct)
        for shipment in normalized["store"]["shipments"]:
            for scan_row in shipment["scans"]:
                scan_row["reference"] = scan_row["reference"].strip().lower()
        projected_normalized = score.observation_projection(
            observation_response(distinct["store"], normalized))
        self.assertNotEqual(projected_normalized, expected_distinct)

        shared = cases["same-reference-on-another-shipment"]
        expected_shared = model.expected(shared)
        known_bad = model.expected(shared, mutation="global-dedup")
        projected_bad = score.observation_projection(observation_response(shared["store"], known_bad))
        self.assertNotEqual(projected_bad, expected_shared)

    def test_copied_model_and_all_eighteen_cases_remain_consistent(self) -> None:
        self.assertEqual(len(score.frozen_cases()), 18)
        self.assertEqual(len(score.type_negative_requests()), 2)


if __name__ == "__main__":
    unittest.main()
