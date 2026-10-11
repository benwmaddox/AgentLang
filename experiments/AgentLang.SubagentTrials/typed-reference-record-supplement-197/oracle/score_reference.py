"""Score saved typed-reference candidates against the independent model.

The draft runner is local-only. It never launches a broker or reads candidate
self-tests as acceptance evidence. The AgentLang adapter uses a wrapper-only
observer on a disposable copy of the captured project.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import traceback
from typing import Any
from uuid import uuid4


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
EVIDENCE = REPO / ".agentlang/efficacy-maintenance-197/oracle-results"
CASE_SOURCE = HERE / "cases.json"
EXPECTED_COUNT = 18


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def load_model():
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("typed_reference_184_model", HERE / "model.py")
    if spec is None or spec.loader is None:
        raise RuntimeError("Unable to load the independent reference model")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def frozen_cases() -> list[dict[str, Any]]:
    model = load_model()
    expected = [dict(**item, expected=model.expected(item)) for item in model.CASES]
    serialized = json.dumps(expected, indent=2, ensure_ascii=False) + "\n"
    if not CASE_SOURCE.is_file() or CASE_SOURCE.read_text(encoding="utf-8") != serialized:
        raise ValueError("cases.json differs from independent model derivation")
    if len(expected) != EXPECTED_COUNT or len({item["id"] for item in expected}) != EXPECTED_COUNT:
        raise ValueError("Expected 18 unique typed-reference vectors")
    return expected


def next_run_directory(label: str) -> Path:
    target = EVIDENCE / label
    target.mkdir(parents=True, exist_ok=False)
    return target


def inventory(root: Path) -> dict[str, str]:
    return {
        path.relative_to(root).as_posix(): sha256(path)
        for path in sorted(root.rglob("*"))
        if path.is_file()
        and not any(part.lower() in {"bin", "obj", "temp", "tmp"}
                    for part in path.relative_to(root).parts)
    }


def json_line(value: dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def parse_jsonl(stdout: str) -> tuple[list[dict[str, Any]], str | None]:
    rows: list[dict[str, Any]] = []
    for line_number, line in enumerate(stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError as error:
            return rows, f"stdout line {line_number} is not JSONL: {error}"
        if not isinstance(value, dict):
            return rows, f"stdout line {line_number} is not a JSON object"
        rows.append(value)
    return rows, None


def run_cli(cli: Path, project: Path, requests: list[dict[str, Any]], evidence_dir: Path,
            name: str) -> dict[str, Any]:
    command = ["dotnet", str(cli), "--project", str(project), "--jsonl"]
    payload = "\n".join(json_line(item) for item in requests) + "\n"
    process = subprocess.run(command, cwd=REPO, input=payload, capture_output=True,
                             text=True, encoding="utf-8", errors="replace", timeout=600,
                             env=dict(os.environ, DOTNET_NOLOGO="1"))
    (evidence_dir / f"{name}-requests.jsonl").write_text(payload, encoding="utf-8")
    (evidence_dir / f"{name}-stdout.txt").write_text(process.stdout, encoding="utf-8")
    (evidence_dir / f"{name}-stderr.txt").write_text(process.stderr, encoding="utf-8")
    rows, parse_error = parse_jsonl(process.stdout)
    return {
        "command": command,
        "exitCode": process.returncode,
        "responseCount": len(rows),
        "parseError": parse_error,
        "responses": rows,
        "evidence": {
            "requests": f"{name}-requests.jsonl",
            "stdout": f"{name}-stdout.txt",
            "stderr": f"{name}-stderr.txt",
        },
    }


def flow_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def list_expr(type_name: str, expressions: list[str]) -> str:
    if not expressions:
        return f"list.empty<{type_name}>()"
    result = f"list.singleton<{type_name}>({expressions[0]})"
    for expression in expressions[1:]:
        result = f"list.append({result}, {expression})"
    return result


def reference_expr(raw: str) -> str:
    return f"trackingReference.new(value = {flow_string(raw)})"


def scan_expr(row: dict[str, str]) -> str:
    return ("scan.new(" +
            f"reference = {reference_expr(row['reference'])}, " +
            f"status = {flow_string(row['status'])})")


def shipment_expr(row: dict[str, Any]) -> str:
    scans = list_expr("Scan", [scan_expr(scan) for scan in row["scans"]])
    return ("shipment.new(" +
            f"id = ShipmentId.new({flow_string(row['id'])}), " +
            f"label = {flow_string(row['label'])}, scans = {scans})")


def store_expr(value: dict[str, Any]) -> str:
    shipments = list_expr("Shipment", [shipment_expr(row) for row in value["shipments"]])
    return ("store.new(" +
            f"shipments = {shipments}, audit = {flow_string(value['audit'])}, " +
            f"generation = {value['generation']})")


def scan_input_expr(row: dict[str, str]) -> str:
    return ("scanInput.new(" +
            f"shipment-id = ShipmentId.new({flow_string(row['shipmentId'])}), " +
            f"reference = {reference_expr(row['reference'])}, " +
            f"status = {flow_string(row['status'])})")


OBSERVER_SOURCE = r'''record Reference184Observation {
    field input: Store
    field outcome: Result<Store, ScanError>
}

fn reference184.oracle.observe-single(input: Store, event: ScanInput) -> Reference184Observation {
    reference184Observation.new(input = input, outcome = shipment.ingest(input, event))
}

fn reference184.oracle.observe-batch(input: Store, events: List<ScanInput>) -> Reference184Observation {
    reference184Observation.new(input = input, outcome = shipment.ingest-batch(input, events))
}
'''


def observation_expr(item: dict[str, Any]) -> str:
    if item["entry"] == "single":
        if len(item["events"]) != 1:
            raise ValueError(f"Single-entry vector {item['id']} must contain one event")
        event_expression = scan_input_expr(item["events"][0])
        observer = "reference184.oracle.observe-single"
    elif item["entry"] == "batch":
        event_expression = list_expr("ScanInput", [scan_input_expr(event)
                                                    for event in item["events"]])
        observer = "reference184.oracle.observe-batch"
    else:
        raise ValueError(f"Unknown vector entry kind: {item['entry']!r}")
    return f"{observer}({store_expr(item['store'])}, {event_expression})"


def tracking_reference_source_metadata(project_path: Path) -> dict[str, Any]:
    source_path = project_path / "dictionary.agent"
    if not source_path.is_file():
        return {"status": "failure", "error": "dictionary.agent is missing"}
    source = source_path.read_text(encoding="utf-8")
    declarations = re.findall(r"(?m)^record\s+TrackingReference\s*\{([^}]*)\}", source)
    fields = [line.strip() for line in declarations[0].splitlines() if line.strip()] if len(declarations) == 1 else []
    passed = len(declarations) == 1 and fields == ["field value: String"]
    return {
        "status": "pass" if passed else "failure",
        "declarationCount": len(declarations),
        "bodyLines": fields,
        "exactOneStringFieldWithoutValidator": passed,
        "source": "captured dictionary.agent declaration",
    }


def tracking_reference_context_metadata(response: dict[str, Any] | None) -> dict[str, Any]:
    data = response.get("data") if isinstance(response, dict) else None
    types = data.get("types", []) if isinstance(data, dict) else []
    if not isinstance(types, list):
        types = []
    matching = [row for row in types if isinstance(row, dict) and row.get("name") == "TrackingReference"]
    row = matching[0] if len(matching) == 1 else None
    passed = (
        response is not None and response.get("ok") is True and response.get("kind") == "context"
        and isinstance(data, dict) and data.get("truncated") is False and data.get("typesOmitted") == 0
        and len(matching) == 1 and row.get("kind") == "record"
        and row.get("fields") == [{"name": "value", "type": "String"}]
        and "validator" in row and row.get("validator") is None
    )
    return {
        "status": "pass" if passed else "failure",
        "exactOneStringFieldWithoutValidator": passed,
        "typeMetadata": row,
        "contextResponse": response,
    }


def _is_tracking_reference_type(type_metadata: Any) -> bool:
    return (isinstance(type_metadata, dict)
            and type_metadata.get("kind") == "nominal"
            and type_metadata.get("name") == "TrackingReference"
            and type_metadata.get("nominalKind") == "record")


def _record_fields(node: dict[str, Any]) -> list[dict[str, Any]]:
    fields = node.get("fields")
    if not isinstance(fields, list):
        raise ValueError("Structured record fields are missing or malformed")
    seen: set[str] = set()
    for field in fields:
        if (not isinstance(field, dict) or not isinstance(field.get("name"), str)
                or not isinstance(field.get("value"), dict)):
            raise ValueError("Structured record contains a malformed field")
        if field["name"] in seen:
            raise ValueError(f"Structured record contains duplicate field {field['name']!r}")
        seen.add(field["name"])
    return fields


def unwrap_tracking_reference(node: Any, declared_type: Any = None) -> str:
    if declared_type is not None and not _is_tracking_reference_type(declared_type):
        raise ValueError("Reference field is not declared as nominal TrackingReference record")
    if not isinstance(node, dict) or node.get("kind") != "record" or node.get("name") != "TrackingReference":
        raise ValueError("Reference value is not the TrackingReference record")
    fields = _record_fields(node)
    if len(fields) != 1:
        raise ValueError("TrackingReference must contain exactly one field")
    field = fields[0]
    value = field["value"]
    if (field["name"] != "value" or field.get("type") != {"kind": "string"}
            or value.get("kind") != "string" or not isinstance(value.get("value"), str)):
        raise ValueError("TrackingReference.value must be exactly one String value")
    return value["value"]


def unwrap_structured(node: dict[str, Any]) -> Any:
    if not isinstance(node, dict):
        raise ValueError("Structured value is not an object")
    kind = node.get("kind")
    if kind == "record":
        if node.get("name") == "TrackingReference":
            return unwrap_tracking_reference(node)
        fields = _record_fields(node)
        if node.get("name") in {"Scan", "ScanInput"} and not any(field["name"] == "reference" for field in fields):
            raise ValueError(f"{node['name']} is missing its reference field")
        result: dict[str, Any] = {}
        for field in fields:
            if node.get("name") in {"Scan", "ScanInput"} and field["name"] == "reference":
                declared_type = field.get("type")
                if not _is_tracking_reference_type(declared_type):
                    raise ValueError("Scan reference field is not declared as TrackingReference record")
                result[field["name"]] = unwrap_tracking_reference(field["value"], declared_type)
            else:
                result[field["name"]] = unwrap_structured(field["value"])
        return result
    if kind == "scalar":
        return unwrap_structured(node["value"])
    if kind == "list":
        return [unwrap_structured(item) for item in node.get("items", [])]
    if kind == "option":
        if node.get("case") == "none":
            return None
        return unwrap_structured(node["value"])
    if kind == "result":
        payload = node.get("value", node.get("error"))
        return {"tag": node["case"], "value": None if payload is None else unwrap_structured(payload)}
    if kind == "enum":
        return {"tag": "enum", "name": node.get("name"), "case": node.get("case")}
    if kind in ("string", "bool"):
        return node.get("value")
    if kind == "int":
        return int(node["value"])
    raise ValueError(f"Unsupported structured AgentLang node: {node!r}")


def canonical_error_code(error: Any) -> str | None:
    if isinstance(error, dict):
        if error.get("tag") == "enum":
            case_name = str(error.get("case", ""))
        elif "code" in error:
            return str(error["code"])
        else:
            return None
    else:
        case_name = str(error)
    normalized = "".join(character.lower() for character in case_name if character.isalnum())
    return {
        "unknownshipment": "UNKNOWN_SHIPMENT",
        "invalidstatus": "INVALID_STATUS",
    }.get(normalized)


def observation_projection(response: dict[str, Any]) -> dict[str, Any] | None:
    structured = response.get("data", {}).get("structuredStack")
    values = structured.get("values", []) if isinstance(structured, dict) else []
    if len(values) != 1:
        return None
    observation = unwrap_structured(values[0])
    if not isinstance(observation, dict) or "input" not in observation or "outcome" not in observation:
        return None
    outcome = observation["outcome"]
    if not isinstance(outcome, dict):
        return None
    if outcome.get("tag") == "ok":
        return {"tag": "ok", "store": outcome.get("value"), "input": observation["input"]}
    if outcome.get("tag") == "error":
        return {"tag": "error", "code": canonical_error_code(outcome.get("value")),
                "input": observation["input"]}
    return None


def type_negative_requests() -> list[dict[str, Any]]:
    common = ('shipment-id = ShipmentId.new("ship-a"), '
              'status = "received"')
    return [
        {"op": "eval", "frontend": "flow", "syntaxVersion": 2,
         "code": f'scanInput.new({common}, reference = "raw-string")'},
        {"op": "eval", "frontend": "flow", "syntaxVersion": 2,
         "code": f'scanInput.new({common}, reference = ShipmentId.new("raw-id"))'},
    ]


def type_negative_result(name: str, expected_source_type: str,
                         response: dict[str, Any] | None) -> dict[str, Any]:
    if response is None:
        return {"name": name, "executed": False, "passed": False,
                "setupError": "No response was captured"}
    rendered = json.dumps(response, ensure_ascii=False)
    diagnostics = rendered.lower()
    rejected = response.get("ok") is False
    has_target_type = "trackingreference" in diagnostics
    has_source_type = expected_source_type.lower() in diagnostics
    return {
        "name": name,
        "executed": True,
        "rejected": rejected,
        "diagnosticsNameExpectedTypes": has_target_type and has_source_type,
        "passed": rejected and has_target_type and has_source_type,
        "response": response,
    }


def flow_helper_signature(cli_path: Path, scoring_project: Path, run_dir: Path) -> dict[str, Any]:
    helper_names = ("shipment.find-scan", "shipment.scan-lookup-step")
    runs = {
        name: run_cli(cli_path, scoring_project,
                      [{"op": "type-of", "word": name}], run_dir,
                      "helper-signature-" + name.rsplit(".", 1)[-1])
        for name in helper_names
    }
    available: list[tuple[str, dict[str, Any], dict[str, Any]]] = []
    for name, result in runs.items():
        response = result["responses"][0] if result["responses"] else None
        if (response is not None and response.get("ok") is True
                and response.get("kind") == "type-of"
                and isinstance(response.get("data"), dict)):
            available.append((name, result, response["data"]))

    helpers: list[dict[str, Any]] = []
    for name, result, description in available:
        inputs = description.get("inputs", [])
        outputs = description.get("outputs", [])
        if not isinstance(inputs, list):
            inputs = []
        if not isinstance(outputs, list):
            outputs = []
        helper: dict[str, Any] = {
            "name": name,
            "inputs": inputs,
            "outputs": outputs,
            "typeOfEvidence": {key: value for key, value in result.items() if key != "responses"},
        }
        if name == "shipment.find-scan":
            helper["shape"] = "reusable shipment/reference lookup"
            helper["trackingReferenceInSignature"] = "TrackingReference" in inputs
            helper["compatible"] = ("Shipment" in inputs
                                    and "TrackingReference" in inputs
                                    and "Option<Scan>" in outputs)
        else:
            helper["shape"] = "reset-rich lookup step"
            state_type = inputs[0] if inputs else None
            helper["stateType"] = state_type
            helper["signatureCompatible"] = (state_type is not None
                                             and state_type in outputs
                                             and "Scan" in inputs)
            context_run = run_cli(
                cli_path, scoring_project,
                [{"op": "context", "word": name,
                  "maxDepth": 2, "maxWords": 16, "maxUtf8Bytes": 12000}],
                run_dir, "helper-signature-reset-context")
            context_response = context_run["responses"][0] if context_run["responses"] else None
            context_data = context_response.get("data") if isinstance(context_response, dict) else None
            type_rows = context_data.get("types", []) if isinstance(context_data, dict) else []
            state_row = next((row for row in type_rows
                              if isinstance(row, dict) and row.get("name") == state_type), None)
            fields = state_row.get("fields", []) if isinstance(state_row, dict) else []
            helper["lookupStateHasTrackingReference"] = any(
                isinstance(field, dict) and field.get("type") == "TrackingReference"
                for field in fields
            )
            helper["lookupStateHasTargetTrackingReference"] = any(
                isinstance(field, dict) and field.get("name") == "target"
                and field.get("type") == "TrackingReference"
                for field in fields
            )
            context_words = context_data.get("words", []) if isinstance(context_data, dict) else []
            target_accessor = next((word for word in context_words
                                    if isinstance(word, dict)
                                    and word.get("name") == "shipmentScanLookup.target"), None)
            helper["lookupTargetAccessorCompatible"] = (
                context_run["exitCode"] == 0 and context_run["parseError"] is None
                and context_run["responseCount"] == 1
                and isinstance(context_response, dict) and context_response.get("ok") is True
                and context_response.get("kind") == "context"
                and isinstance(context_data, dict) and context_data.get("truncated") is False
                and isinstance(target_accessor, dict)
                and target_accessor.get("inputs") == ["ShipmentScanLookup"]
                and target_accessor.get("outputs") == ["TrackingReference"]
            )
            helper["contextEvidence"] = {
                "command": context_run["command"],
                "exitCode": context_run["exitCode"],
                "responseCount": context_run["responseCount"],
                "parseError": context_run["parseError"],
                "stateType": state_row,
                "targetAccessor": target_accessor,
                "evidence": context_run["evidence"],
            }
            helper["compatible"] = (helper["signatureCompatible"]
                                    and helper["lookupStateHasTargetTrackingReference"]
                                    and helper["lookupTargetAccessorCompatible"])
        helpers.append(helper)

    # The retained arm intentionally contains a reusable wrapper and its
    # lower-level traversal callback. Prefer the wrapper as the arm's helper
    # vocabulary when it exists; only score the callback in reset-rich, where
    # the wrapper is absent. Both discovered signatures remain in the receipt.
    reusable = next((helper for helper in helpers
                     if helper["name"] == "shipment.find-scan"), None)
    selected = reusable or next((helper for helper in helpers
                                 if helper["name"] == "shipment.scan-lookup-step"), None)
    if reusable is not None:
        selection_reason = "reusable wrapper present; lower-level callback is implementation detail"
    elif selected is not None:
        selection_reason = "no reusable wrapper; score reset-rich lower-level lookup step"
    else:
        selection_reason = "neither supported helper shape is available"
    status = ("unavailable" if selected is None else
              "pass" if selected.get("compatible") else "signature-mismatch")
    return {
        "status": status,
        "metadataSource": "existing AgentLang type-of/context discovery operations",
        "helpers": helpers,
        "selectedHelper": selected["name"] if selected else None,
        "selectionReason": selection_reason,
        "queryEvidence": {
            name: {key: value for key, value in result.items() if key != "responses"}
            for name, result in runs.items()
        },
        "limit": "This checks public helper and lookup-state signatures only. It does not infer that ingest and ingest-batch call the helper; caller wiring remains captured source evidence.",
    }


def score_flow(project_path: Path, cli_path: Path, label: str, cases: list[dict[str, Any]],
               run_dir: Path) -> dict[str, Any]:
    project_path = project_path.resolve()
    cli_path = cli_path.resolve()
    if not project_path.is_dir():
        raise FileNotFoundError(f"Captured AgentLang project is missing: {project_path}")
    if not cli_path.is_file():
        raise FileNotFoundError(f"Fresh Release CLI is missing: {cli_path}")
    before = inventory(project_path)
    source_reference_metadata = tracking_reference_source_metadata(project_path)
    scoring_project = run_dir / "scoring-project"
    shutil.copytree(project_path, scoring_project)
    observer = {"op": "define", "frontend": "flow", "syntaxVersion": 2,
                "source": OBSERVER_SOURCE}
    observer_probe_project = run_dir / "observer-probe-project"
    shutil.copytree(scoring_project, observer_probe_project)
    observer_probe_run = run_cli(cli_path, observer_probe_project, [observer], run_dir,
                                 "observer-preflight")
    observer_probe_response = (observer_probe_run["responses"][0]
                               if observer_probe_run["responses"] else None)
    observer_preflight_ok = (
        observer_probe_run["exitCode"] == 0
        and observer_probe_run["parseError"] is None
        and observer_probe_run["responseCount"] == 1
        and observer_probe_response is not None
        and observer_probe_response.get("ok") is True
        and observer_probe_response.get("kind") == "defined"
    )

    nominal_reference_probe = [
        {"op": "eval", "frontend": "flow", "syntaxVersion": 2,
         "code": 'scan.new(reference = trackingReference.new(value = "probe"), status = "received")'},
        {"op": "eval", "frontend": "flow", "syntaxVersion": 2,
         "code": 'scanInput.new(shipment-id = ShipmentId.new("ship-a"), reference = trackingReference.new(value = "probe"), status = "received")'},
    ]
    probe_run = run_cli(cli_path, scoring_project, nominal_reference_probe, run_dir,
                        "nominal-reference-positive-control")
    probe_responses = probe_run.get("responses", [])
    probe_setup_ok = (probe_run["exitCode"] == 0 and probe_run["parseError"] is None
                      and probe_run["responseCount"] == 2)
    scan_reference_nominal = (
        probe_setup_ok and probe_responses[0].get("ok") is True
        and probe_responses[0].get("kind") == "eval"
        and isinstance(probe_responses[0].get("data"), dict)
        and probe_responses[0].get("data", {}).get("stackTypes") == ["Scan"]
    )
    input_reference_nominal = (
        probe_setup_ok and probe_responses[1].get("ok") is True
        and probe_responses[1].get("kind") == "eval"
        and isinstance(probe_responses[1].get("data"), dict)
        and probe_responses[1].get("data", {}).get("stackTypes") == ["ScanInput"]
    )
    nominal_reference_available = scan_reference_nominal and input_reference_nominal
    stack_types = [response.get("data", {}).get("stackTypes")
                   for response in probe_run.get("responses", [])]
    static_reference_types = [
        types[0] if isinstance(types, list) and len(types) == 1 else None
        for types in stack_types
    ]
    constructor_metadata_run = run_cli(
        cli_path, scoring_project,
        [{"op": "type-of", "word": "trackingReference.new"},
         {"op": "type-of", "word": "scan.new"},
         {"op": "type-of", "word": "scanInput.new"}],
        run_dir, "reference-field-static-metadata")
    type_context_run = run_cli(
        cli_path, scoring_project,
        [{"op": "context", "word": "trackingReference.new", "maxDepth": 1,
          "maxWords": 8, "maxUtf8Bytes": 6000}],
        run_dir, "tracking-reference-type-context")
    type_context_response = type_context_run["responses"][0] if type_context_run["responses"] else None
    runtime_reference_metadata = tracking_reference_context_metadata(type_context_response)
    type_context_query_ok = (type_context_run["exitCode"] == 0
                             and type_context_run["parseError"] is None
                             and type_context_run["responseCount"] == 1)
    if not type_context_query_ok:
        runtime_reference_metadata["status"] = "failure"
        runtime_reference_metadata["exactOneStringFieldWithoutValidator"] = False
    runtime_reference_metadata["queryEvidence"] = {
        key: value for key, value in type_context_run.items() if key != "responses"
    }
    constructor_descriptions = [
        result.get("data") if result.get("ok") is True and result.get("kind") == "type-of" else None
        for result in constructor_metadata_run["responses"]
    ]
    def constructor_types(index: int) -> tuple[list[Any], list[Any]]:
        description = constructor_descriptions[index] if len(constructor_descriptions) > index else None
        return (description.get("inputs", []), description.get("outputs", [])) if description else ([], [])

    reference_inputs, reference_outputs = constructor_types(0)
    scan_inputs, scan_outputs = constructor_types(1)
    input_inputs, input_outputs = constructor_types(2)
    static_type_metadata = {
        "status": "pass" if (
            constructor_metadata_run["exitCode"] == 0
            and constructor_metadata_run["parseError"] is None
            and constructor_metadata_run["responseCount"] == 3
            and reference_inputs == ["String"] and reference_outputs == ["TrackingReference"]
            and scan_inputs == ["TrackingReference", "String"] and scan_outputs == ["Scan"]
            and input_inputs == ["ShipmentId", "TrackingReference", "String"] and input_outputs == ["ScanInput"]
        ) else "identity-failure",
        "trackingReferenceConstructorInputs": reference_inputs,
        "trackingReferenceConstructorOutputs": reference_outputs,
        "scanConstructorInputs": scan_inputs,
        "scanInputConstructorInputs": input_inputs,
        "scanConstructorOutputs": scan_outputs,
        "scanInputConstructorOutputs": input_outputs,
        "bothNamedTrackingReference": (
            constructor_metadata_run["exitCode"] == 0
            and constructor_metadata_run["parseError"] is None
            and constructor_metadata_run["responseCount"] == 3
            and reference_inputs == ["String"] and reference_outputs == ["TrackingReference"]
            and scan_inputs == ["TrackingReference", "String"] and scan_outputs == ["Scan"]
            and input_inputs == ["ShipmentId", "TrackingReference", "String"] and input_outputs == ["ScanInput"]
        ),
        "source": "AgentLang type-of metadata for generated record constructors",
        "metadataEvidence": {key: value for key, value in constructor_metadata_run.items()
                             if key != "responses"},
        "recordContextEvidence": {key: value for key, value in type_context_run.items()
                                  if key != "responses"},
        "constructionStackTypes": static_reference_types,
        "limit": "Field signatures, eval construction, and negative eval controls are separate evidence. Internal caller wiring remains captured source evidence.",
    }

    helper_signature = flow_helper_signature(cli_path, scoring_project, run_dir)
    reference_record_ok = (source_reference_metadata["status"] == "pass"
                           and runtime_reference_metadata["status"] == "pass")
    representation_gate_ok = (reference_record_ok and nominal_reference_available
                              and static_type_metadata["bothNamedTrackingReference"]
                              and helper_signature["status"] == "pass")
    behavior_requests = [
        {"op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
         "code": observation_expr(item)}
        for item in cases
    ]
    # A Flow definition is available in the active evaluation session. Check
    # it on a disposable preflight copy, then keep the actual observer
    # definition and calls in one CLI process. A later process against the
    # same project does not inherit this uncommitted definition.
    if observer_preflight_ok and representation_gate_ok:
        behavior_run = run_cli(cli_path, scoring_project, [observer, *behavior_requests],
                               run_dir, "behavior")
    else:
        behavior_run = {
            "command": observer_probe_run["command"],
            "exitCode": observer_probe_run["exitCode"],
            "responseCount": 0,
            "parseError": observer_probe_run["parseError"] or "Representation or observer gate failed; behavior was not evaluated",
            "responses": [],
            "evidence": observer_probe_run["evidence"],
        }
    behavior_rows: list[dict[str, Any]] = []
    response_rows = behavior_run["responses"]
    for index, item in enumerate(cases):
        response_index = index + 1
        response = response_rows[response_index] if response_index < len(response_rows) else None
        result: dict[str, Any] = {"caseId": item["id"], "executed": False, "passed": False}
        if response is None:
            if not observer_preflight_ok:
                result["setupError"] = "Observer definition failed preflight; behavior was not evaluated"
            elif not representation_gate_ok:
                result["setupError"] = "Representation gate failed; behavior was not evaluated"
            else:
                result["setupError"] = "Missing CLI response"
        elif not response.get("ok") or response.get("kind") != "eval":
            result["setupError"] = response.get("error", response.get("text", "Evaluation failed"))
        else:
            try:
                actual = observation_projection(response)
                if actual is None:
                    result["setupError"] = "Structured observation is missing input or outcome"
                else:
                    result.update(executed=True, passed=(actual == item["expected"]))
                    if actual != item["expected"]:
                        result.update(expected=item["expected"], actual=actual)
            except (ValueError, KeyError, TypeError) as error:
                result["setupError"] = f"Structured observation was rejected by the representation adapter: {error}"
        behavior_rows.append(result)

    type_requests = type_negative_requests()
    type_run = run_cli(cli_path, scoring_project, type_requests, run_dir, "type-negative")
    type_rows = [
        type_negative_result("String cannot fill TrackingReference", "String",
                             type_run["responses"][0] if type_run["responses"] else None),
        type_negative_result("ShipmentId cannot fill TrackingReference", "ShipmentId",
                             type_run["responses"][1] if len(type_run["responses"]) > 1 else None),
    ]
    unchanged = inventory(project_path) == before
    define_response = response_rows[0] if response_rows else observer_probe_response
    observer_ok = (observer_preflight_ok
                   and behavior_run["exitCode"] == 0 and behavior_run["parseError"] is None
                   and define_response is not None and define_response.get("ok") is True
                   and define_response.get("kind") == "defined")
    behavior_setup_ok = (observer_preflight_ok
                         and behavior_run["exitCode"] == 0 and behavior_run["parseError"] is None
                         and behavior_run["responseCount"] == len(cases) + 1)
    type_setup_ok = (type_run["exitCode"] == 0 and type_run["parseError"] is None
                     and type_run["responseCount"] == 2)
    behavior_executed = sum(row["executed"] for row in behavior_rows)
    behavior_passed = sum(row["passed"] for row in behavior_rows)
    type_passed = sum(row["passed"] for row in type_rows)
    return {
        "arm": "flow",
        "label": label,
        "runtime": str(cli_path),
        "runtimeSha256": sha256(cli_path),
        "participantProject": str(project_path),
        "participantInputInventory": before,
        "scoringProject": str(scoring_project),
        "observerSource": OBSERVER_SOURCE,
        "representationGate": {
            "status": "pass" if representation_gate_ok else "setup-failure",
            "sourceDeclaration": source_reference_metadata,
            "runtimeTypeMetadata": runtime_reference_metadata,
            "nominalConstructionStackTypes": stack_types,
            "staticTypeMetadata": static_type_metadata,
            "helperSignature": helper_signature,
            "meaning": "Behavior runs only when the one-field record shape, Scan/ScanInput construction, nominal field signatures, and lookup target signature all pass.",
        },
        "observerSetup": {
            "status": "pass" if observer_ok else "setup-failure",
            "preflightCommand": observer_probe_run["command"],
            "preflightExitCode": observer_probe_run["exitCode"],
            "preflightResponseCount": observer_probe_run["responseCount"],
            "preflightParseError": observer_probe_run["parseError"],
            "preflightResponse": observer_probe_response,
            "defineResponse": define_response,
            "evidence": observer_probe_run["evidence"],
        },
        "referenceConstruction": {
            "nominalPositiveControl": {key: value for key, value in probe_run.items() if key != "responses"},
            "nominalPositiveControlPassed": nominal_reference_available,
            "stackTypes": stack_types,
        },
        "staticTypeMetadata": static_type_metadata,
        "helperSignature": helper_signature,
        "behavior": {
            "status": "setup-failure" if not observer_ok or not behavior_setup_ok
                      else "pass" if behavior_passed == len(cases) else "behavioral-mismatch",
            "expectedCaseCount": len(cases),
            "executedCaseCount": behavior_executed,
            "passedCaseCount": behavior_passed,
            "failingCaseIds": [row["caseId"] for row in behavior_rows if row["executed"] and not row["passed"]],
            "setupErrorCaseIds": [row["caseId"] for row in behavior_rows if not row["executed"]],
            "command": behavior_run["command"],
            "responseCount": max(0, behavior_run["responseCount"] - (1 if response_rows else 0)),
            "totalResponseCountIncludingObserver": behavior_run["responseCount"],
            "parseError": behavior_run["parseError"],
            "caseResults": behavior_rows,
        },
        "typeNegativeControls": {
            "status": "setup-failure" if not type_setup_ok
                      else "pass" if type_passed == len(type_rows) else "type-negative-failure",
            "evidenceKind": "AgentLang eval rejection with captured diagnostics; not a separate compiler invocation",
            "responseCount": type_run["responseCount"],
            "parseError": type_run["parseError"],
            "controls": type_rows,
        },
        "separateEndpoints": {
            "inheritedEvidencePreservation": "captured task audit evidence; not inferred from behavior score",
            "libraryQualification": "captured AgentLang test/commit evidence; not run by this oracle wrapper",
            "taskFinalization": "captured broker termination evidence",
            "recovery": "captured broker termination evidence",
        },
        "participantSourceUnchanged": unchanged,
        "status": "setup-failure" if not observer_ok or not behavior_setup_ok or not unchanged
                  or not representation_gate_ok
                  else "pass" if behavior_passed == len(cases) and type_passed == len(type_rows)
                  and nominal_reference_available and static_type_metadata["bothNamedTrackingReference"]
                  and helper_signature["status"] == "pass"
                  else "behavioral-or-type-rejection",
    }


def score_fsharp(project_path: Path, label: str, cases: list[dict[str, Any]],
                 run_dir: Path) -> dict[str, Any]:
    project_path = project_path.resolve()
    if not project_path.exists():
        raise FileNotFoundError(f"Captured F# project is missing: {project_path}")
    source_path = project_path if project_path.is_file() else None
    if source_path is None:
        preferred = project_path / "business" / "Business.fs"
        candidates = [preferred] if preferred.is_file() else sorted(project_path.rglob("Business.fs"))
        if len(candidates) != 1:
            raise FileNotFoundError(
                f"Expected exactly one Business.fs in captured F# project, found {len(candidates)}")
        source_path = candidates[0]
    if source_path.suffix.lower() != ".fs":
        raise ValueError("F# scoring accepts a project directory or a Business.fs source file")

    participant_root = project_path if project_path.is_dir() else source_path.parent.parent
    before = inventory(participant_root)
    source_hash_before = sha256(source_path)
    scoring_project = run_dir / "scoring-project"
    business_dir = scoring_project / "business"
    business_dir.mkdir(parents=True)
    copied_source = business_dir / "Business.fs"
    shutil.copy2(source_path, copied_source)
    (scoring_project / "Reference184Oracle.fsproj").write_text(
        """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Reference184Oracle</AssemblyName>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=\"business/Business.fs\" />
    <Compile Include=\"OracleProgram.fs\" />
  </ItemGroup>
</Project>
""", encoding="utf-8")
    program = fsharp_oracle_program(cases)
    (scoring_project / "OracleProgram.fs").write_text(program, encoding="utf-8")
    project_file = scoring_project / "Reference184Oracle.fsproj"
    build_command = ["dotnet", "build", str(project_file), "--configuration", "Release",
                     "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
    build = subprocess.run(build_command, cwd=scoring_project, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=600)
    (run_dir / "fsharp-build.stdout.txt").write_text(build.stdout, encoding="utf-8")
    (run_dir / "fsharp-build.stderr.txt").write_text(build.stderr, encoding="utf-8")
    report: dict[str, Any] = {
        "arm": "fsharp",
        "label": label,
        "participantProject": str(project_path),
        "participantSource": str(source_path),
        "participantSourceSha256": source_hash_before,
        "participantInputInventory": before,
        "scoringProject": str(scoring_project),
        "compileInputs": [
            {"path": "business/Business.fs", "sha256": sha256(copied_source)},
            {"path": "OracleProgram.fs", "sha256": sha256(scoring_project / "OracleProgram.fs")},
        ],
        "buildCommand": build_command,
        "buildExitCode": build.returncode,
        "buildEvidence": {"stdout": "fsharp-build.stdout.txt", "stderr": "fsharp-build.stderr.txt"},
        "expectedCaseCount": len(cases),
        "separateEndpoints": {
            "inheritedEvidencePreservation": "captured task audit evidence; not inferred from behavior score",
            "libraryQualification": "captured conventional test evidence; not run by this oracle wrapper",
            "taskFinalization": "captured broker termination evidence",
            "recovery": "captured broker termination evidence",
        },
    }
    if build.returncode != 0:
        report.update(status="setup-failure", setupFailure="Fresh F# oracle project build failed",
                      participantSourceUnchanged=sha256(source_path) == source_hash_before
                      and inventory(participant_root) == before)
        return report

    oracle_dll = scoring_project / "bin" / "Release" / "net9.0" / "Reference184Oracle.dll"
    command = ["dotnet", str(oracle_dll)]
    process = subprocess.run(command, cwd=scoring_project, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=600)
    (run_dir / "fsharp-oracle.stdout.txt").write_text(process.stdout, encoding="utf-8")
    (run_dir / "fsharp-oracle.stderr.txt").write_text(process.stderr, encoding="utf-8")
    observations, parse_error = parse_jsonl(process.stdout)
    cases_by_id = {item["id"]: item for item in cases}
    results: list[dict[str, Any]] = []
    for index, item in enumerate(cases):
        row = observations[index] if index < len(observations) else None
        if row is None:
            results.append({"caseId": item["id"], "executed": False, "passed": False,
                            "setupError": "Missing F# oracle response"})
            continue
        if row.get("id") != item["id"]:
            results.append({"caseId": item["id"], "executed": False, "passed": False,
                            "setupError": f"F# oracle response ID mismatch: {row.get('id')!r}"})
            continue
        actual = row.get("actual")
        expected = cases_by_id[item["id"]]["expected"]
        results.append({"caseId": item["id"], "executed": True, "passed": actual == expected,
                        **({} if actual == expected else {"expected": expected, "actual": actual})})
    process_setup_ok = (process.returncode == 0 and parse_error is None
                        and [row.get("id") for row in observations] == [item["id"] for item in cases])

    type_controls = run_fsharp_type_negatives(scoring_project, run_dir)
    helper_signature = run_fsharp_helper_signature(scoring_project, run_dir)
    reference_type_metadata = None
    if observations:
        reference_type_metadata = {
            "scanReferenceType": observations[0].get("scanReferenceType"),
            "scanInputReferenceType": observations[0].get("scanInputReferenceType"),
            "sameStaticType": observations[0].get("scanReferenceType") ==
                              observations[0].get("scanInputReferenceType"),
            "bothNamedTrackingReference": all(
                str(observations[0].get(key, "")).split("+")[-1].endswith("TrackingReference")
                for key in ("scanReferenceType", "scanInputReferenceType")),
        }
        reference_type_metadata["status"] = (
            "pass" if reference_type_metadata["bothNamedTrackingReference"]
            and reference_type_metadata["sameStaticType"] else "identity-failure"
        )
    source_unchanged = sha256(source_path) == source_hash_before and inventory(participant_root) == before
    executed = sum(row["executed"] for row in results)
    passed = sum(row["passed"] for row in results)
    type_passed = sum(row["passed"] for row in type_controls["controls"])
    report.update({
        "oracleCommand": command,
        "oracleExitCode": process.returncode,
        "oracleParseError": parse_error,
        "oracleResponseCount": len(observations),
        "oracleEvidence": {"stdout": "fsharp-oracle.stdout.txt", "stderr": "fsharp-oracle.stderr.txt"},
        "behavior": {
            "status": "setup-failure" if not process_setup_ok else
                      "pass" if passed == len(cases) else "behavioral-mismatch",
        "expectedCaseCount": len(cases),
            "executedCaseCount": executed,
            "passedCaseCount": passed,
            "failingCaseIds": [row["caseId"] for row in results if row["executed"] and not row["passed"]],
            "setupErrorCaseIds": [row["caseId"] for row in results if not row["executed"]],
            "caseResults": results,
        },
        "staticTypeMetadata": reference_type_metadata,
        "runtimeReferenceConstruction": {
            "method": "FSharp.Reflection constructs a one-field String union or record from the public Reference field type",
            "positiveCompileControl": {
                "passed": build.returncode == 0,
                "evidence": "fresh compilation of typed Scan.Reference and ScanInput.Reference field assignments in the oracle runner",
            },
            "limit": "Runtime construction does not prove nominal identity; static metadata and compiler-negative probes are separate.",
        },
        "typeNegativeControls": {
            "status": type_controls["status"],
            "controls": type_controls["controls"],
        },
        "helperSignature": helper_signature,
        "participantSourceUnchanged": source_unchanged,
        "status": "setup-failure" if not process_setup_ok or type_controls["status"] == "setup-failure"
                  or not source_unchanged else
                  "pass" if passed == len(cases) and type_passed == len(type_controls["controls"])
                  and reference_type_metadata is not None
                  and reference_type_metadata["bothNamedTrackingReference"]
                  and reference_type_metadata["sameStaticType"]
                  and helper_signature["status"] == "pass"
                  else "behavioral-or-type-rejection",
    })
    return report


def fsharp_oracle_program(cases: list[dict[str, Any]]) -> str:
    def literal(value: str) -> str:
        return json.dumps(value, ensure_ascii=False)

    def scan(row: dict[str, str]) -> str:
        return f"mkScan {literal(row['reference'])} {literal(row['status'])}"

    def shipment(row: dict[str, Any]) -> str:
        scans = "; ".join(scan(item) for item in row["scans"])
        return f"mkShipment {literal(row['id'])} {literal(row['label'])} [ {scans} ]"

    def store(value: dict[str, Any]) -> str:
        shipments = "; ".join(shipment(item) for item in value["shipments"])
        return (f"mkStore [ {shipments} ] {literal(value['audit'])} "
                f"{value['generation']}")

    def input_event(value: dict[str, str]) -> str:
        return (f"mkInput {literal(value['shipmentId'])} "
                f"{literal(value['reference'])} {literal(value['status'])}")

    vector_lines = []
    for item in cases:
        events = "; ".join(input_event(event) for event in item["events"])
        batch = "true" if item["entry"] == "batch" else "false"
        vector_lines.append(
            f"        ({literal(item['id'])}, {store(item['store'])}, [ {events} ], {batch})")
    vectors = ";\n".join(vector_lines)
    return f'''namespace AgentLang.TypedReferenceMaintenance.Oracle184

open System
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection
open AgentLang.TypedReferenceMaintenance.Domain

module Program =
    let private makeReference<'T> (raw: string) : 'T =
        let referenceType = typeof<'T>
        if referenceType = typeof<string> then unbox<'T> (box raw)
        elif FSharpType.IsUnion(referenceType, allowAccessToPrivateRepresentation = true) then
            let cases = FSharpType.GetUnionCases(referenceType, allowAccessToPrivateRepresentation = true)
            if cases.Length <> 1 then failwithf "Reference wrapper %s is not a single-case union" referenceType.FullName
            let fields = cases[0].GetFields()
            if fields.Length <> 1 || fields[0].PropertyType <> typeof<string> then
                failwithf "Reference wrapper %s does not contain one String" referenceType.FullName
            FSharpValue.MakeUnion(cases[0], [| box raw |], allowAccessToPrivateRepresentation = true) :?> 'T
        elif FSharpType.IsRecord(referenceType, allowAccessToPrivateRepresentation = true) then
            let fields = FSharpType.GetRecordFields(referenceType, allowAccessToPrivateRepresentation = true)
            if fields.Length <> 1 || fields[0].PropertyType <> typeof<string> then
                failwithf "Reference wrapper %s does not contain one String" referenceType.FullName
            FSharpValue.MakeRecord(referenceType, [| box raw |], allowAccessToPrivateRepresentation = true) :?> 'T
        else
            failwithf "Reference wrapper %s is neither a single-case union nor a one-field record" referenceType.FullName

    let private referenceValue<'T> (value: 'T) : string =
        let referenceType = typeof<'T>
        if referenceType = typeof<string> then unbox<string> (box value)
        elif FSharpType.IsUnion(referenceType, allowAccessToPrivateRepresentation = true) then
            let _, fields = FSharpValue.GetUnionFields(box value, referenceType, allowAccessToPrivateRepresentation = true)
            unbox<string> fields.[0]
        elif FSharpType.IsRecord(referenceType, allowAccessToPrivateRepresentation = true) then
            let fields = FSharpValue.GetRecordFields(box value, allowAccessToPrivateRepresentation = true)
            unbox<string> fields.[0]
        else
            failwithf "Reference wrapper %s is neither a single-case union nor a one-field record" referenceType.FullName

    let private mkScan (reference: string) (status: string) : Scan =
        {{ Reference = makeReference reference; Status = status }}

    let private mkShipment (id: string) (label: string) (scans: Scan list) : Shipment =
        {{ Id = ShipmentId.create id; Label = label; Scans = scans }}

    let private mkStore (shipments: Shipment list) (audit: string) (generation: int) : Store =
        {{ Shipments = shipments; Audit = audit; Generation = generation }}

    let private mkInput (shipmentId: string) (reference: string) (status: string) : ScanInput =
        {{ ShipmentId = ShipmentId.create shipmentId; Reference = makeReference reference; Status = status }}

    let private storeJson (value: Store) : JsonObject =
        let shipments = JsonArray()
        for shipment in value.Shipments do
            let scans = JsonArray()
            for scan in shipment.Scans do
                let scanNode = JsonObject()
                scanNode["reference"] <- JsonValue.Create(referenceValue scan.Reference)
                scanNode["status"] <- JsonValue.Create(scan.Status)
                scans.Add(scanNode)
            let shipmentNode = JsonObject()
            shipmentNode["id"] <- JsonValue.Create(ShipmentId.value shipment.Id)
            shipmentNode["label"] <- JsonValue.Create(shipment.Label)
            shipmentNode["scans"] <- scans
            shipments.Add(shipmentNode)
        let node = JsonObject()
        node["shipments"] <- shipments
        node["audit"] <- JsonValue.Create(value.Audit)
        node["generation"] <- JsonValue.Create(value.Generation)
        node

    let private observationJson (input: Store) (outcome: Result<Store, ScanError>) : JsonObject =
        let node = JsonObject()
        match outcome with
        | Ok result ->
            node["tag"] <- JsonValue.Create("ok")
            node["store"] <- storeJson result
            node["input"] <- storeJson input
        | Error UnknownShipment ->
            node["tag"] <- JsonValue.Create("error")
            node["code"] <- JsonValue.Create("UNKNOWN_SHIPMENT")
            node["input"] <- storeJson input
        | Error InvalidStatus ->
            node["tag"] <- JsonValue.Create("error")
            node["code"] <- JsonValue.Create("INVALID_STATUS")
            node["input"] <- storeJson input
        node

    let private vectors: (string * Store * ScanInput list * bool) list =
        [
{vectors}
        ]

    [<EntryPoint>]
    let main _ =
        for caseId, input, events, isBatch in vectors do
            let outcome =
                if isBatch then Shipment.ingestBatch input events
                else
                    match events with
                    | [ event ] -> Shipment.ingest input event
                    | _ -> failwithf "single vector %s does not have exactly one event" caseId
            let row = JsonObject()
            row["id"] <- JsonValue.Create(caseId)
            row["actual"] <- observationJson input outcome
            row["scanReferenceType"] <- JsonValue.Create(typeof<Scan>.GetProperty("Reference").PropertyType.FullName)
            row["scanInputReferenceType"] <- JsonValue.Create(typeof<ScanInput>.GetProperty("Reference").PropertyType.FullName)
            Console.Out.WriteLine(row.ToJsonString())
        0
'''


def run_fsharp_type_negatives(scoring_project: Path, run_dir: Path) -> dict[str, Any]:
    results: list[dict[str, Any]] = []
    cases = [
        ("String cannot fill TrackingReference", 'Reference = "raw-string"', "string", "NegativeString.fs"),
        ("ShipmentId cannot fill TrackingReference", 'Reference = ShipmentId.create "raw-id"',
         "ShipmentId", "NegativeShipmentId.fs"),
    ]
    for name, expression, source_type, source_name in cases:
        directory = scoring_project / source_name.removesuffix(".fs").lower()
        directory.mkdir(parents=True)
        negative_source = directory / source_name
        negative_source.write_text(f'''namespace AgentLang.TypedReferenceMaintenance.Oracle184

open AgentLang.TypedReferenceMaintenance.Domain

module Negative =
    let invalid : ScanInput =
        {{ ShipmentId = ShipmentId.create "ship-a"; {expression}; Status = "received" }}
''', encoding="utf-8")
        project_file = directory / "Negative.fsproj"
        project_file.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Library</OutputType>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../business/Business.fs" Link="Business.fs" />
    <Compile Include="{source_name}" />
  </ItemGroup>
</Project>
''', encoding="utf-8")
        command = ["dotnet", "build", str(project_file), "--configuration", "Release",
                   "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
        process = subprocess.run(command, cwd=directory, capture_output=True, text=True,
                                 encoding="utf-8", errors="replace", timeout=600)
        (run_dir / f"{source_name}.stdout.txt").write_text(process.stdout, encoding="utf-8")
        (run_dir / f"{source_name}.stderr.txt").write_text(process.stderr, encoding="utf-8")
        diagnostics = (process.stdout + "\n" + process.stderr).lower()
        target_found = "trackingreference" in diagnostics
        source_found = source_type.lower() in diagnostics
        rejected = process.returncode != 0
        results.append({
            "name": name,
            "command": command,
            "buildExitCode": process.returncode,
            "executed": True,
            "rejected": rejected,
            "diagnosticsNameExpectedTypes": target_found and source_found,
            "passed": rejected and target_found and source_found,
            "stdoutEvidence": f"{source_name}.stdout.txt",
            "stderrEvidence": f"{source_name}.stderr.txt",
        })
    return {"status": "pass" if all(result["passed"] for result in results)
            else "type-negative-failure",
            "controls": results}


def run_fsharp_helper_signature(scoring_project: Path, run_dir: Path) -> dict[str, Any]:
    source = """namespace AgentLang.TypedReferenceMaintenance.Oracle184

open AgentLang.TypedReferenceMaintenance.Domain

module HelperSignatureProbe =
    let private subject : Shipment =
        { Id = ShipmentId.create "ship-a"; Label = "probe"; Scans = [] }

    let private typedReference : TrackingReference = unbox<TrackingReference> null

    let positive : Scan option = Shipment.findScan subject typedReference
"""
    positive_dir = scoring_project / "fsharp-helper-positive"
    positive_dir.mkdir(parents=True)
    positive_source = positive_dir / "HelperPositive.fs"
    positive_source.write_text(source, encoding="utf-8")
    positive_project = positive_dir / "HelperPositive.fsproj"
    positive_project.write_text("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Library</OutputType>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../business/Business.fs" Link="Business.fs" />
    <Compile Include="HelperPositive.fs" />
  </ItemGroup>
</Project>
""", encoding="utf-8")
    positive_command = ["dotnet", "build", str(positive_project), "--configuration", "Release",
                        "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
    positive_process = subprocess.run(positive_command, cwd=positive_dir, capture_output=True,
                                      text=True, encoding="utf-8", errors="replace", timeout=600)
    (run_dir / "fsharp-helper-positive.stdout.txt").write_text(positive_process.stdout, encoding="utf-8")
    (run_dir / "fsharp-helper-positive.stderr.txt").write_text(positive_process.stderr, encoding="utf-8")

    negative_dir = scoring_project / "fsharp-helper-string-negative"
    negative_dir.mkdir(parents=True)
    negative_source = negative_dir / "HelperStringNegative.fs"
    negative_source.write_text("""namespace AgentLang.TypedReferenceMaintenance.Oracle184

open AgentLang.TypedReferenceMaintenance.Domain

module HelperStringNegative =
    let private subject : Shipment =
        { Id = ShipmentId.create "ship-a"; Label = "probe"; Scans = [] }

    let invalid : Scan option = Shipment.findScan subject "raw-string"
""", encoding="utf-8")
    negative_project = negative_dir / "HelperStringNegative.fsproj"
    negative_project.write_text("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Library</OutputType>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../business/Business.fs" Link="Business.fs" />
    <Compile Include="HelperStringNegative.fs" />
  </ItemGroup>
</Project>
""", encoding="utf-8")
    negative_command = ["dotnet", "build", str(negative_project), "--configuration", "Release",
                        "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
    negative_process = subprocess.run(negative_command, cwd=negative_dir, capture_output=True,
                                      text=True, encoding="utf-8", errors="replace", timeout=600)
    (run_dir / "fsharp-helper-negative.stdout.txt").write_text(negative_process.stdout, encoding="utf-8")
    (run_dir / "fsharp-helper-negative.stderr.txt").write_text(negative_process.stderr, encoding="utf-8")
    diagnostics = (negative_process.stdout + "\n" + negative_process.stderr).lower()
    negative_passed = (negative_process.returncode != 0
                       and "trackingreference" in diagnostics and "string" in diagnostics)
    passed = positive_process.returncode == 0 and negative_passed
    return {
        "status": "pass" if passed else "helper-signature-failure",
        "helper": "Shipment.findScan",
        "positiveCompileControl": {
            "passed": positive_process.returncode == 0,
            "command": positive_command,
            "exitCode": positive_process.returncode,
            "evidence": {
                "source": str(positive_source.relative_to(run_dir)),
                "stdout": "fsharp-helper-positive.stdout.txt",
                "stderr": "fsharp-helper-positive.stderr.txt",
            },
            "meaning": "A TrackingReference-typed value is accepted by the public helper signature; the probe is compiled, not executed.",
        },
        "stringNegativeCompileControl": {
            "passed": negative_passed,
            "command": negative_command,
            "exitCode": negative_process.returncode,
            "diagnosticsNameExpectedTypes": "trackingreference" in diagnostics and "string" in diagnostics,
            "evidence": {
                "source": str(negative_source.relative_to(run_dir)),
                "stdout": "fsharp-helper-negative.stdout.txt",
                "stderr": "fsharp-helper-negative.stderr.txt",
            },
        },
        "limit": "Compiler controls verify helper input typing. They do not inspect whether ingest and ingestBatch call the helper; that wiring remains captured source evidence.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm", choices=("flow", "fsharp"), required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--cli", type=Path,
                        help="Fresh Release AgentLang.Cli.dll. Required for --arm flow.")
    parser.add_argument("--label", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,48}", args.label):
        parser.error("--label must be a unique path-safe label of at most 48 characters")
    if args.arm == "flow" and args.cli is None:
        parser.error("--cli is required for --arm flow")
    cases = frozen_cases()
    run_dir = next_run_directory(args.label)
    if args.arm == "flow":
        result = score_flow(args.project, args.cli, args.label, cases, run_dir)
    else:
        result = score_fsharp(args.project, args.label, cases, run_dir)
    result["caseSource"] = str(CASE_SOURCE.relative_to(REPO))
    result["caseSourceSha256"] = sha256(CASE_SOURCE)
    result["scoreScriptSha256"] = sha256(Path(__file__))
    write_json(run_dir / "result.json", result)
    summary = {key: value for key, value in result.items()
               if key not in ("behavior", "typeNegativeControls", "observerSource", "participantInputInventory")}
    if "behavior" in result:
        summary["behavior"] = {key: value for key, value in result["behavior"].items() if key != "caseResults"}
    if "typeNegativeControls" in result:
        summary["typeNegativeControls"] = {key: value for key, value in result["typeNegativeControls"].items()
                                           if key != "controls"}
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    # A behavioral rejection is evidence. Exit nonzero only for scorer setup or
    # source-preservation failures; inspect result.json for outcome counts.
    return 1 if result.get("status") == "setup-failure" else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        EVIDENCE.mkdir(parents=True, exist_ok=True)
        failure_path = EVIDENCE / f"setup-failure-{uuid4().hex}.json"
        write_json(failure_path, {"status": "setup-exception", "exceptionType": type(error).__name__,
                                  "message": str(error), "traceback": traceback.format_exc()})
        print(f"scorer setup failed; see {failure_path}", file=sys.stderr)
        raise
