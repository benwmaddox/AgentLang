"""Score a copied typed-batch-failure-216 project against frozen 210 cases.

Behavior is scored mechanically. The report always leaves assertion and
scenario preservation pending for a separate source review.
"""

from __future__ import annotations

import argparse
from collections import Counter
from copy import deepcopy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import sys
from time import gmtime, strftime

import model as MODEL


REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
STUDY_ROOT = REPO / ".agentlang" / "typed-batch-failure-216"
SCORER_DIR = REPO / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210" / "oracle"
SCORER_PATH = SCORER_DIR / "score_organic_reuse.py"
SEED_INVENTORY_PATH = SCORER_DIR / "seed-inventory.json"
ACCEPTED_213_ROOT = REPO / ".agentlang" / "paired-vocabulary-213"
ACCEPTED_213_SOURCE_PIN = ACCEPTED_213_ROOT / "source-pin.json"
ACCEPTED_213_RUNTIME_PIN = ACCEPTED_213_ROOT / "accepted-runtime-pin.json"
DEFAULT_SEED_PROJECT = REPO / ".agentlang" / "subscription-handoff-161" / "seeds" / "agentlang-project"
DEFAULT_BASELINE_PROJECTS = {
    "reset": ACCEPTED_213_ROOT / "actors" / "reset",
    "retained": ACCEPTED_213_ROOT / "actors" / "retained",
}
EXPECTED_BASELINE_FINGERPRINTS = {
    "reset": "b939b981fb1ba47a3f004d2bd76c7851adbe8f744f72d1cf62ea100873b1361e",
    "retained": "b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611",
}
BATCH_ROOT = "subscription.renew-batch"
BATCH_STEP = "subscription.renew-batch-step"
BATCH_SIGNATURE = (
    ("Store", "List<SubscriptionRenewal>", "Instant"),
    "Result<Store,SubscriptionRenewalFailure>",
)
FAILURE_TYPE = "SubscriptionRenewalFailure"
STATE_TYPE_BY_CONDITION = {
    "reset": "SubscriptionRenewalState",
    "retained": "SubscriptionRenewalBatchState",
}
EXPECTED_FAILURE_FIELDS = [
    ("index", "Int"),
    ("old-id", "SubscriptionId"),
    ("cause", "BusinessError"),
]
EXPECTED_EXAMPLE_COUNT = 39
CASE_COUNT = 9
CREATOR_CASE_COUNT = 66
SCORER_MODULE_NAME = "typed_batch_failure_216_organic_reuse_210_scorer"


def _import_210_scorer():
    """Import 210's low-level projections without shadowing this study's model."""
    original_path = list(sys.path)
    previous_model = sys.modules.pop("model", None)
    previous_cell_model = sys.modules.pop("cell_model_163", None)
    sys.path.insert(0, str(SCORER_DIR))
    try:
        spec = importlib.util.spec_from_file_location(SCORER_MODULE_NAME, SCORER_PATH)
        if spec is None or spec.loader is None:
            raise ImportError(f"Cannot load organic-reuse-210 scorer from {SCORER_PATH}")
        module = importlib.util.module_from_spec(spec)
        sys.modules[SCORER_MODULE_NAME] = module
        spec.loader.exec_module(module)
        return module
    finally:
        sys.path[:] = original_path
        if previous_model is not None:
            sys.modules["model"] = previous_model
        else:
            sys.modules.pop("model", None)
        if previous_cell_model is not None:
            sys.modules["cell_model_163"] = previous_cell_model
        else:
            sys.modules.pop("cell_model_163", None)


scorer = _import_210_scorer()
scorer.STUDY_ROOT = STUDY_ROOT.resolve()
scorer.OLD.SUBSCRIPTION_IDS.update({
    "old2": "30000000-0000-0000-0000-000000000005",
    "new2": "30000000-0000-0000-0000-000000000006",
})


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_fingerprint(project: Path) -> str:
    return scorer.project_fingerprint(project)


def resolve_repo_path(path: Path) -> Path:
    return path.resolve() if path.is_absolute() else (REPO / path).resolve()


def validate_evidence_root(path: Path) -> Path:
    root = STUDY_ROOT.resolve()
    resolved = path if path.is_absolute() else REPO / path
    resolved = resolved.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"--evidence-root must be below {root}")
    return resolved


def validate_baseline_fingerprint(condition: str, actual_fingerprint: str) -> dict:
    if condition not in EXPECTED_BASELINE_FINGERPRINTS:
        raise ValueError(f"Unsupported condition: {condition}")
    expected = EXPECTED_BASELINE_FINGERPRINTS[condition]
    return {
        "passed": actual_fingerprint == expected,
        "condition": condition,
        "expectedProjectFingerprint": expected,
        "projectFingerprint": actual_fingerprint,
    }


def validate_baseline(condition: str, baseline_project: Path, seed_project: Path) -> tuple[str, dict]:
    fingerprint = project_fingerprint(baseline_project)
    fingerprint_record = validate_baseline_fingerprint(condition, fingerprint)
    if not fingerprint_record["passed"]:
        raise ValueError(f"The {condition} baseline does not match its accepted terminal 213 snapshot")
    _, manifest, _ = scorer.current_manifest(baseline_project)
    projection = scorer.projection_for_manifest(manifest)
    _, seed_manifest, _ = scorer.current_manifest(seed_project)
    seed_projection = scorer.projection_for_manifest(seed_manifest)
    seed_preservation = scorer.preservation_diff(seed_projection, projection)
    return fingerprint, {
        **fingerprint_record,
        "path": str(baseline_project.resolve()),
        "manifestHash": json.loads(
            (baseline_project / ".agentlang" / "store" / "CURRENT").read_text(encoding="utf-8-sig")
        )["manifestHash"],
        "seedPreservationPassed": seed_preservation["passed"],
        "seedPreservation": seed_preservation,
        "projection": projection,
        "manifest": manifest,
    }


def validate_accepted_213_pins(cli: Path) -> dict:
    """Require the accepted 37-source and five-runtime fingerprints before use."""
    source_pin = json.loads(ACCEPTED_213_SOURCE_PIN.read_text(encoding="utf-8"))
    expected_sources = source_pin.get("files")
    if not isinstance(expected_sources, list) or len(expected_sources) != 37:
        raise ValueError("Accepted 213 source pin must contain exactly 37 files")
    source_results = []
    for row in expected_sources:
        path = REPO / row["path"]
        actual = {
            "bytes": path.stat().st_size if path.is_file() else None,
            "sha256": sha256_file(path) if path.is_file() else None,
        }
        source_results.append({
            "path": row["path"],
            "expected": {"bytes": row["bytes"], "sha256": row["sha256"]},
            "actual": actual,
            "passed": actual == {"bytes": row["bytes"], "sha256": row["sha256"]},
        })

    runtime_pin = json.loads(ACCEPTED_213_RUNTIME_PIN.read_text(encoding="utf-8"))
    expected_runtime = runtime_pin.get("runtimeFiles")
    if not isinstance(expected_runtime, list) or len(expected_runtime) != 5:
        raise ValueError("Accepted 213 runtime pin must contain exactly five files")
    runtime_results = []
    for row in expected_runtime:
        path = cli.parent / row["name"]
        actual = {
            "bytes": path.stat().st_size if path.is_file() else None,
            "sha256": sha256_file(path) if path.is_file() else None,
        }
        runtime_results.append({
            "name": row["name"],
            "path": str(path.resolve()),
            "expected": {"bytes": row["bytes"], "sha256": row["sha256"]},
            "actual": actual,
            "passed": actual == {"bytes": row["bytes"], "sha256": row["sha256"]},
        })
    if not all(row["passed"] for row in source_results):
        mismatches = [row["path"] for row in source_results if not row["passed"]]
        raise ValueError(f"Accepted 213 source pins changed: {mismatches}")
    if not all(row["passed"] for row in runtime_results):
        mismatches = [row["name"] for row in runtime_results if not row["passed"]]
        raise ValueError(f"Accepted 213 runtime pins changed: {mismatches}")
    return {
        "status": runtime_pin.get("status"),
        "sourceInputCount": len(source_results),
        "sourcePinsPassed": all(row["passed"] for row in source_results),
        "sourceFiles": source_results,
        "runtimeFileCount": len(runtime_results),
        "runtimePinsPassed": all(row["passed"] for row in runtime_results),
        "runtimeFiles": runtime_results,
    }


def _definition_source(project: Path, definition: dict) -> str:
    path = project / ".agentlang" / "store" / "objects" / f"{definition['hash']}.agent"
    return path.read_text(encoding="utf-8-sig")


def _type_source(project: Path, manifest: dict, type_name: str) -> tuple[dict | None, str | None]:
    entry = next((row for row in manifest.get("types", []) if row.get("name") == type_name), None)
    if entry is None:
        return None, None
    return entry, _definition_source(project, entry["definition"])


def failure_type_contract(source: str | None, expected_name: str = FAILURE_TYPE) -> dict:
    declaration = None
    if isinstance(source, str):
        declaration = re.search(
            r"(?m)^\s*(record|type)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{(.*?)^\s*\}",
            source,
            re.DOTALL,
        )
    if declaration is None:
        return {
            "present": False,
            "representation": None,
            "name": None,
            "fields": [],
            "exactFieldsPassed": False,
            "nominalNamePassed": False,
            "passed": False,
        }
    representation, name, body = declaration.groups()
    fields = [
        (match.group(1), re.sub(r"\s+", "", match.group(2)))
        for match in re.finditer(
            r"(?m)^\s*field\s+([A-Za-z_][A-Za-z0-9_-]*)\s*:\s*([^\r\n]+?)\s*$",
            body,
        )
    ]
    fields_passed = fields == EXPECTED_FAILURE_FIELDS
    nominal_passed = representation == "record" and name == expected_name
    return {
        "present": True,
        "representation": representation,
        "name": name,
        "fields": [{"name": field, "type": type_name} for field, type_name in fields],
        "exactFieldsPassed": fields_passed,
        "nominalNamePassed": nominal_passed,
        "passed": fields_passed and nominal_passed,
    }


def _manifest_test_count(manifest: dict) -> int:
    current = scorer.current_revisions(manifest)
    return sum(len(entry["revision"].get("tests", [])) for entry in current.values())


def _manifest_examples(manifest: dict) -> dict[str, int]:
    return {
        name: len(entry["revision"].get("examples", []))
        for name, entry in scorer.current_revisions(manifest).items()
        if entry["revision"].get("examples")
    }


def _test_identities(project: Path, revision: dict) -> list[str]:
    identities = []
    for row in revision.get("tests", []):
        path = project / ".agentlang" / "store" / "objects" / f"{row['hash']}.agent"
        source = path.read_text(encoding="utf-8-sig")
        found = re.findall(r"(?m)^\s*test\s+([^\s{]+)\s*\{", source)
        if len(found) != 1:
            raise ValueError(f"Expected one test identity in {path}, found {len(found)}")
        identities.append(found[0])
    return identities


def _multiset_preservation_diff(expected: list[str], actual: list[str]) -> dict:
    expected_sorted = sorted(expected)
    actual_sorted = sorted(actual)
    expected_counts = Counter(expected)
    actual_counts = Counter(actual)
    missing = sorted((expected_counts - actual_counts).elements())
    added = sorted((actual_counts - expected_counts).elements())
    return {
        # Additions are allowed, but every inherited value and its multiplicity
        # must remain present.
        "passed": not missing,
        "expected": expected_sorted,
        "actual": actual_sorted,
        "missing": missing,
        "added": added,
    }


def test_identity_diff(expected: list[str], actual: list[str]) -> dict:
    return _multiset_preservation_diff(expected, actual)


def preservation_report(condition: str, baseline_project: Path, baseline_manifest: dict,
                        baseline_projection: dict, actor_project: Path, actor_manifest: dict,
                        actor_projection: dict) -> dict:
    """Check immutable inherited objects while isolating the allowed batch edits."""
    base_current = scorer.current_revisions(baseline_manifest)
    actor_current = scorer.current_revisions(actor_manifest)
    changed_words = {}
    missing_words = []
    target_checks = {}
    inherited_test_identities = {}
    for name, expected in baseline_projection["words"].items():
        actual = actor_projection["words"].get(name)
        if actual is None:
            missing_words.append(name)
            continue
        if name in {BATCH_ROOT, BATCH_STEP}:
            base_entry = base_current[name]
            actor_entry = actor_current[name]
            base_tests = _test_identities(baseline_project, base_entry["revision"])
            actor_tests = _test_identities(actor_project, actor_entry["revision"])
            test_diff = test_identity_diff(base_tests, actor_tests)
            inherited_test_identities[name] = test_diff
            example_diff = _multiset_preservation_diff(expected["examples"], actual["examples"])
            check = {
                "wordIdPreserved": expected["wordId"] == actual["wordId"],
                "maturityPreserved": expected["maturity"] == actual["maturity"] == "library",
                "testIdentitiesPreserved": test_diff["passed"],
                "exampleHashesPreserved": example_diff["passed"],
            }
            target_checks[name] = {**check, "exampleHashDiff": example_diff}
            if not all(check.values()):
                changed_words[name] = {"expected": expected, "actual": actual, "checks": check}
        elif actual != expected:
            changed_words[name] = {"expected": expected, "actual": actual}

    baseline_type_hashes = baseline_projection["types"]
    actor_type_hashes = actor_projection["types"]
    allowed_state_type = STATE_TYPE_BY_CONDITION[condition]
    changed_types = {
        name: {"expected": value, "actual": actor_type_hashes.get(name)}
        for name, value in baseline_type_hashes.items()
        if name != allowed_state_type and actor_type_hashes.get(name) != value
    }
    state_type_present = allowed_state_type in actor_type_hashes
    new_words = sorted(set(actor_projection["words"]) - set(baseline_projection["words"]))
    new_types = sorted(set(actor_type_hashes) - set(baseline_type_hashes))
    non_library_new_words = [
        name for name in new_words
        if actor_current.get(name, {}).get("revision", {}).get("maturity") != "library"
    ]
    return {
        "passed": not changed_words and not missing_words and not changed_types
                  and state_type_present and not non_library_new_words,
        "immutableInheritedWordsPassed": not changed_words and not missing_words,
        "immutableInheritedTypesPassed": not changed_types and state_type_present,
        "inheritedTestIdentitiesPassed": all(row["passed"] for row in inherited_test_identities.values()),
        "batchTargetChecks": target_checks,
        "inheritedTestIdentities": inherited_test_identities,
        "changedInheritedWords": changed_words,
        "missingInheritedWords": missing_words,
        "changedInheritedTypes": changed_types,
        "allowedStateType": allowed_state_type,
        "allowedStateTypePresent": state_type_present,
        "newWords": new_words,
        "newWordsLibraryQualified": not non_library_new_words,
        "newWordsWithNonLibraryMaturity": non_library_new_words,
        "newTypes": new_types,
        "scopeReviewRequiredForAdditions": bool(new_words or new_types),
        "independentSourceReviewRequired": True,
    }


def inspect_batch_contract(project: Path, manifest: dict, baseline_manifest: dict,
                           condition: str) -> dict:
    current = scorer.current_revisions(manifest)
    baseline_current = scorer.current_revisions(baseline_manifest)
    root = current.get(BATCH_ROOT)
    step = current.get(BATCH_STEP)
    base_root = baseline_current.get(BATCH_ROOT)
    base_step = baseline_current.get(BATCH_STEP)
    root_signature = None
    root_word_id = None
    root_maturity = None
    root_test_count = 0
    if root is not None:
        root_word_id = root["word"]["wordId"]
        root_maturity = root["revision"].get("maturity")
        root_test_count = len(root["revision"].get("tests", []))
        _, _, store = scorer.current_manifest(project)
        root_signature = scorer.flow_definition_signature(
            scorer.definition_source(store, root["revision"])
        )
    step_word_id = step["word"]["wordId"] if step is not None else None
    step_maturity = step["revision"].get("maturity") if step is not None else None
    expected_ids = {
        BATCH_ROOT: base_root["word"]["wordId"] if base_root is not None else None,
        BATCH_STEP: base_step["word"]["wordId"] if base_step is not None else None,
    }
    failure_entry, failure_source = _type_source(project, manifest, FAILURE_TYPE)
    failure_contract = failure_type_contract(failure_source)
    state_name = STATE_TYPE_BY_CONDITION[condition]
    state_entry, state_source = _type_source(project, manifest, state_name)
    state_match = re.search(rf"(?m)^\s*record\s+{re.escape(state_name)}\s*\{{", state_source or "")
    id_checks = {
        BATCH_ROOT: root is not None and root_word_id == expected_ids[BATCH_ROOT],
        BATCH_STEP: step is not None and step_word_id == expected_ids[BATCH_STEP],
    }
    maturity_checks = {
        BATCH_ROOT: root_maturity == "library",
        BATCH_STEP: step_maturity == "library",
    }
    signature_json = {"inputs": list(root_signature[0]), "output": root_signature[1]} if root_signature else None
    signature_passed = root_signature == BATCH_SIGNATURE
    state_type_passed = state_entry is not None and state_match is not None
    own_tests_passed = root_test_count > 0
    passed = (
        signature_passed and failure_contract["passed"] and state_type_passed
        and all(id_checks.values()) and all(maturity_checks.values()) and own_tests_passed
    )
    return {
        "present": root is not None,
        "wordId": root_word_id,
        "expectedWordId": expected_ids[BATCH_ROOT],
        "wordIdPassed": id_checks[BATCH_ROOT],
        "revision": root["word"]["currentRevision"] if root else None,
        "definitionHash": root["revision"]["definition"]["hash"] if root else None,
        "signature": signature_json,
        "signaturePassed": signature_passed,
        "maturity": root_maturity,
        "libraryPassed": maturity_checks[BATCH_ROOT],
        "ownTestCount": root_test_count,
        "ownTestsPassed": own_tests_passed,
        "step": {
            "present": step is not None,
            "wordId": step_word_id,
            "expectedWordId": expected_ids[BATCH_STEP],
            "wordIdPassed": id_checks[BATCH_STEP],
            "maturity": step_maturity,
            "libraryPassed": maturity_checks[BATCH_STEP],
            "testCount": len(step["revision"].get("tests", [])) if step else 0,
        },
        "failureType": {"wordId": failure_entry.get("name") if failure_entry else None, **failure_contract},
        "stateType": {
            "name": state_name,
            "present": state_entry is not None,
            "recordDeclarationPassed": state_match is not None,
        },
        "passed": passed,
    }


def typed_observer_source(include_creator_observer: bool) -> str:
    source = """record TypedBatchFailure216Observation {
    field input: Store
    field outcome: Result<Store, SubscriptionRenewalFailure>
}
"""
    if include_creator_observer:
        source += "\n" + scorer.OBSERVATION_SOURCE
    return source


def observation_expression(item: dict) -> str:
    old = scorer.OLD
    store = old.store_expr(item["store"])
    changes = []
    for change in item["request"]["changes"]:
        changes.append(
            "subscriptionRenewal.new("
            f"old-id = SubscriptionId.new({old.flow_string(old.id_for(old.SUBSCRIPTION_IDS, change['oldId']))}), "
            f"new-id = SubscriptionId.new({old.flow_string(old.id_for(old.SUBSCRIPTION_IDS, change['newId']))}), "
            f"plan = SubscriptionPlan.{change['plan']}(), "
            f"expires-at = Instant.new({old.flow_string(old.iso_day(change['expiry']))}))"
        )
    change_list = old.list_expr("SubscriptionRenewal", changes)
    at = old.flow_string(old.iso_day(item["request"]["at"]))
    call = f"{BATCH_ROOT}({store}, {change_list}, Instant.new({at}))"
    return f"typedBatchFailure216Observation.new(input = {store}, outcome = {call})"


def expected_projection(item: dict) -> dict:
    expected = item["expected"]
    input_store = scorer.OLD.normalized_projection(item)
    if expected["tag"] == "ok":
        return {
            "tag": "ok",
            "store": scorer.OLD.normalized_projection({"store": expected["store"]}),
            "input": input_store,
        }
    failure = expected["failure"]
    return {
        "tag": "error",
        "failure": {
            "index": failure["index"],
            "old-id": scorer.OLD.id_for(scorer.OLD.SUBSCRIPTION_IDS, failure["old-id"]),
            "cause": {"code": failure["cause"]["code"], "message": failure["cause"]["message"]},
        },
        "input": input_store,
    }


def typed_result_projection(observation: dict) -> dict:
    input_store = observation["input"]
    outcome = observation["outcome"]
    if outcome["tag"] == "ok":
        return {"tag": "ok", "store": outcome["value"], "input": input_store}
    if outcome["tag"] != "error":
        raise ValueError(f"Unexpected Result case: {outcome['tag']!r}")
    failure = outcome.get("value")
    if not isinstance(failure, dict):
        raise ValueError("typed error payload is missing its SubscriptionRenewalFailure record")
    if set(failure) != {"index", "old-id", "cause"}:
        raise ValueError("typed error payload does not have exactly index, old-id, and cause fields")
    if type(failure["index"]) is not int:
        raise ValueError("SubscriptionRenewalFailure.index is not an Int")
    if not isinstance(failure["old-id"], str):
        raise ValueError("SubscriptionRenewalFailure.old-id is not a structured SubscriptionId value")
    cause = failure["cause"]
    if not isinstance(cause, dict) or set(cause) != {"code", "message"}:
        raise ValueError("SubscriptionRenewalFailure.cause is not a complete BusinessError record")
    if not all(isinstance(cause[key], str) for key in ("code", "message")):
        raise ValueError("BusinessError code and message must both be strings")
    return {"tag": "error", "failure": failure, "input": input_store}


def _node_fields(node: dict, expected_name: str) -> dict[str, dict]:
    if not isinstance(node, dict) or node.get("kind") != "record" or node.get("name") != expected_name:
        actual_name = node.get("name") if isinstance(node, dict) else None
        raise ValueError(f"Expected nominal record {expected_name}, found {actual_name!r}")
    rows = node.get("fields")
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        raise ValueError(f"{expected_name} structured fields are malformed")
    fields = {row.get("name"): row for row in rows}
    if len(fields) != len(rows):
        raise ValueError(f"{expected_name} has duplicate structured fields")
    return fields


def _nominal_metadata_passed(metadata: dict, expected_name: str, nominal_kind: str) -> bool:
    return (
        isinstance(metadata, dict)
        and metadata.get("kind") == "nominal"
        and metadata.get("name") == expected_name
        and metadata.get("nominalKind") == nominal_kind
    )


def _string_metadata_passed(metadata: dict) -> bool:
    return isinstance(metadata, dict) and metadata.get("kind") == "string"


def validate_typed_observation_node(node: dict) -> dict:
    """Check static nominal metadata before projecting away the wire types."""
    observation_fields = _node_fields(node, "TypedBatchFailure216Observation")
    input_field = observation_fields.get("input")
    outcome_field = observation_fields.get("outcome")
    if input_field is None or outcome_field is None or set(observation_fields) != {"input", "outcome"}:
        raise ValueError("typed observation does not have exactly input and outcome fields")
    if not _nominal_metadata_passed(input_field.get("type"), "Store", "record"):
        raise ValueError("typed observation input field is not nominal Store")
    input_node = input_field.get("value")
    if not isinstance(input_node, dict) or input_node.get("kind") != "record" or input_node.get("name") != "Store":
        raise ValueError("typed observation input value is not a Store record")

    outcome_type = outcome_field.get("type")
    if not isinstance(outcome_type, dict) or outcome_type.get("kind") != "result" \
            or not _nominal_metadata_passed(outcome_type.get("okType"), "Store", "record") \
            or not _nominal_metadata_passed(outcome_type.get("errorType"), FAILURE_TYPE, "record"):
        raise ValueError("typed observation outcome is not Result<Store, SubscriptionRenewalFailure>")
    outcome_node = outcome_field.get("value")
    if not isinstance(outcome_node, dict) or outcome_node.get("kind") != "result" \
            or outcome_node.get("case") not in {"ok", "error"}:
        raise ValueError("typed observation outcome Result payload is malformed")
    if not _nominal_metadata_passed(outcome_node.get("okType"), "Store", "record") \
            or not _nominal_metadata_passed(outcome_node.get("errorType"), FAILURE_TYPE, "record"):
        raise ValueError("typed Result payload carries the wrong nominal error type")

    input_value = scorer.OLD.unwrap_structured(input_node)
    if outcome_node["case"] == "ok":
        output = outcome_node.get("value")
        if not isinstance(output, dict) or output.get("kind") != "record" or output.get("name") != "Store":
            raise ValueError("successful typed Result payload is not a Store record")
        observation = {"input": input_value, "outcome": {
            "tag": "ok", "value": scorer.OLD.unwrap_structured(output),
        }}
        return typed_result_projection(observation)

    failure_node = outcome_node.get("value", outcome_node.get("error"))
    failure_fields = _node_fields(failure_node, FAILURE_TYPE)
    required_failure_fields = {"index", "old-id", "cause"}
    if set(failure_fields) != required_failure_fields:
        raise ValueError("SubscriptionRenewalFailure does not have exactly index, old-id, and cause fields")
    index_field = failure_fields["index"]
    if not isinstance(index_field.get("type"), dict) or index_field["type"].get("kind") != "int" \
            or not isinstance(index_field.get("value"), dict) or index_field["value"].get("kind") != "int":
        raise ValueError("SubscriptionRenewalFailure.index metadata is not Int")
    old_id_field = failure_fields["old-id"]
    if not _nominal_metadata_passed(old_id_field.get("type"), "SubscriptionId", "scalar"):
        raise ValueError("SubscriptionRenewalFailure.old-id metadata is not nominal SubscriptionId")
    old_id_node = old_id_field.get("value")
    if not isinstance(old_id_node, dict) or old_id_node.get("kind") != "scalar" \
            or old_id_node.get("name") != "SubscriptionId":
        raise ValueError("SubscriptionRenewalFailure.old-id value is not a SubscriptionId scalar")
    cause_field = failure_fields["cause"]
    if not _nominal_metadata_passed(cause_field.get("type"), "BusinessError", "record"):
        raise ValueError("SubscriptionRenewalFailure.cause metadata is not nominal BusinessError")
    cause_fields = _node_fields(cause_field.get("value"), "BusinessError")
    if set(cause_fields) != {"code", "message"} \
            or any(not _string_metadata_passed(row.get("type")) for row in cause_fields.values()):
        raise ValueError("BusinessError cause does not have its exact string code and message fields")
    observation = {"input": input_value, "outcome": {
        "tag": "error", "value": scorer.OLD.unwrap_structured(failure_node),
    }}
    return typed_result_projection(observation)


def score_typed_eval(item: dict, response: dict) -> dict:
    result = {"caseId": item["id"], "executed": False, "passed": False}
    if not isinstance(response, dict) or response.get("ok") is not True or response.get("kind") != "eval":
        result["setupError"] = response.get("error", response.get("text", "missing eval response")) \
            if isinstance(response, dict) else "missing eval response"
        return result
    structured = response.get("data", {}).get("structuredStack") if isinstance(response.get("data"), dict) else None
    if not isinstance(structured, dict) or not isinstance(structured.get("values"), list) \
            or len(structured["values"]) != 1:
        result["setupError"] = "eval response omitted one structured typed observation value"
        return result
    actual = validate_typed_observation_node(structured["values"][0])
    expected = expected_projection(item)
    result.update(executed=True, passed=(actual == expected), actualTag=actual["tag"], expectedTag=expected["tag"])
    if actual != expected:
        result.update(expected=expected, actual=actual)
    return result


def safe_score_typed_eval(item: dict, response: dict) -> dict:
    try:
        return score_typed_eval(item, response)
    except Exception as error:
        return {
            "caseId": item.get("id", "unknown-case"),
            "executed": False,
            "passed": False,
            "setupError": f"Could not project typed structured eval response: {type(error).__name__}: {error}",
        }


def safe_score_creator_eval(item: dict, response: dict) -> dict:
    try:
        return scorer.score_eval(item, response)
    except Exception as error:
        return {
            "caseId": item.get("id", "unknown-case"),
            "executed": False,
            "passed": False,
            "setupError": f"Could not project creator structured eval response: {type(error).__name__}: {error}",
        }


def _response_counts(response: dict) -> tuple[int, int] | None:
    if not isinstance(response, dict):
        return None
    match = re.search(r"(\d+)\s*/\s*(\d+)", response.get("text", ""))
    if match is None:
        return None
    return int(match.group(1)), int(match.group(2))


def _response_passed(response: dict, kind: str, expected_count: int) -> bool:
    counts = _response_counts(response)
    return bool(
        isinstance(response, dict)
        and response.get("ok") is True
        and response.get("kind") == kind
        and counts == (expected_count, expected_count)
    )


def score_suite(test_response: dict, example_owners: dict[str, int], example_responses: list[dict],
                baseline_example_owners: dict[str, int], actor_manifest: dict,
                seed_manifest: dict, seed_projection: dict) -> dict:
    data = test_response.get("data") if isinstance(test_response, dict) else None
    results = data.get("results") if isinstance(data, dict) else None
    expected_total = _manifest_test_count(actor_manifest)
    expected_common_seed = _manifest_test_count(seed_manifest)
    seed_names = set(seed_projection["words"])
    rows_valid = isinstance(results, list) and all(isinstance(row, dict) for row in results)
    actual_total = len(results) if isinstance(results, list) else None
    actual_common_seed = (
        sum(1 for row in results if row.get("word") in seed_names)
        if rows_valid else None
    )
    result_rows_complete = bool(
        rows_valid
        and len(results) == expected_total
        and all(row.get("passed") is True for row in results)
        and actual_common_seed == expected_common_seed
    )
    test_all_passed = (
        _response_passed(test_response, "test", expected_total)
        and result_rows_complete
    )
    owner_rows = []
    for index, (owner, expected_count) in enumerate(sorted(example_owners.items())):
        response = example_responses[index] if index < len(example_responses) else {}
        counts = _response_counts(response)
        owner_rows.append({
            "owner": owner,
            "expectedExampleCount": expected_count,
            "passed": _response_passed(response, "example", expected_count),
            "responseCounts": list(counts) if counts is not None else None,
            "response": response,
        })
    expected_examples = sum(example_owners.values())
    baseline_examples = sum(baseline_example_owners.values())
    baseline_owner_counts_preserved = all(
        example_owners.get(owner, 0) >= count
        for owner, count in baseline_example_owners.items()
    )
    examples_passed = (
        len(owner_rows) == len(example_owners)
        and len(example_responses) == len(example_owners)
        and baseline_examples == EXPECTED_EXAMPLE_COUNT
        and baseline_owner_counts_preserved
        and all(row["passed"] for row in owner_rows)
    )
    return {
        "testAllPassed": test_all_passed,
        "testAllResponse": test_response,
        "testResultRowsComplete": result_rows_complete,
        "commonSeedTestCount": actual_common_seed,
        "commonSeedExpectedTestCount": expected_common_seed,
        "totalTestCount": actual_total,
        "totalExpectedTestCount": expected_total,
        "exampleOwnerCount": len(example_owners),
        "expectedExampleCount": expected_examples,
        "baselineExampleCount": baseline_examples,
        "baselineExampleCountIsExpected": baseline_examples == EXPECTED_EXAMPLE_COUNT,
        "baselineOwnerCountsPreserved": baseline_owner_counts_preserved,
        "examplesPassed": examples_passed,
        "exampleResults": owner_rows,
    }


def build_requests(cases: list[dict], example_owners: dict[str, int], condition: str,
                   creator_cases: list[dict]) -> list[dict]:
    if len(cases) != CASE_COUNT:
        raise ValueError(f"Expected exactly {CASE_COUNT} frozen batch cases, found {len(cases)}")
    if condition == "retained" and len(creator_cases) != CREATOR_CASE_COUNT:
        raise ValueError(f"Retained scoring needs all {CREATOR_CASE_COUNT} inherited creator cases")
    if condition == "reset" and creator_cases:
        raise ValueError("Reset scoring must not require retained creator functions")
    return (
        [{"op": "test-all"}]
        + [{"op": "example", "word": owner} for owner in sorted(example_owners)]
        + [{"op": "define", "frontend": "flow", "syntaxVersion": 2,
            "source": typed_observer_source(condition == "retained")}]
        + [{"op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
            "code": observation_expression(item)} for item in cases]
        + ([{"op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
             "code": scorer.observation_expression(item, "creator")} for item in creator_cases]
           if condition == "retained" else [])
    )


def create_run_directory(evidence_root: Path, condition: str) -> Path:
    timestamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    parent = evidence_root / condition
    parent.mkdir(parents=True, exist_ok=True)
    index = 1
    while True:
        candidate = parent / f"{timestamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def _write_json(path: Path, payload: dict) -> None:
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _jsonl(rows: list[dict]) -> str:
    return "".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows)


def _source_hashes() -> dict:
    paths = {
        "scoreFailure": Path(__file__).resolve(),
        "model": HERE / "model.py",
        "unitChecks": HERE / "unit_checks.py",
        "organicReuse210Scorer": SCORER_PATH,
        "organicReuse210Model": SCORER_DIR / "model.py",
        "organicReuse210Cases": SCORER_DIR / "cases.json",
        "organicReuse210CellModel": SCORER_DIR / "cell_model_163.py",
        "organicReuse210SeedInventory": SEED_INVENTORY_PATH,
        "failureCauseSubscriptionStart": DEFAULT_SEED_PROJECT / ".agentlang" / "store" / "objects"
            / f"{MODEL.CAUSE_SOURCE_HASHES['subscription.start']}.agent",
        "failureCauseSubscriptionCancel": DEFAULT_SEED_PROJECT / ".agentlang" / "store" / "objects"
            / f"{MODEL.CAUSE_SOURCE_HASHES['subscription.cancel']}.agent",
        "accepted213SourcePin": ACCEPTED_213_SOURCE_PIN,
        "accepted213RuntimePin": ACCEPTED_213_RUNTIME_PIN,
        "accepted213ResetReport": ACCEPTED_213_ROOT / "s" / "reset" / "20261011T102609Z-01" / "score.json",
        "accepted213RetainedReport": ACCEPTED_213_ROOT / "s" / "retained" / "20261011T103930Z-01" / "score.json",
    }
    return {name: {"path": str(path.resolve()), "sha256": sha256_file(path)} for name, path in paths.items()}


def evaluate_acceptance(*, condition: str, cli_exit_code: int | None, response_count: int,
                        expected_response_count: int, case_results: list[dict],
                        creator_results: list[dict], suite: dict, observer_defined: bool,
                        batch_contract: dict, baseline_binding_passed: bool,
                        baseline_seed_preservation_passed: bool,
                        seed_preservation_passed: bool, baseline_preservation_passed: bool,
                        actor_unchanged: bool, baseline_unchanged: bool,
                        scoring_copy_unchanged: bool) -> dict:
    executed = sum(row.get("executed") is True for row in case_results)
    passed_cases = sum(row.get("executed") is True and row.get("passed") is True for row in case_results)
    expected_creator_count = CREATOR_CASE_COUNT if condition == "retained" else 0
    creator_executed = sum(row.get("executed") is True for row in creator_results)
    creator_passed = sum(row.get("executed") is True and row.get("passed") is True for row in creator_results)
    gates = {
        "cliExitZero": cli_exit_code == 0,
        "responseCountExact": response_count == expected_response_count,
        "allNineCasesExecuted": len(case_results) == CASE_COUNT and executed == CASE_COUNT,
        "allNineCasesPassed": len(case_results) == CASE_COUNT and passed_cases == CASE_COUNT,
        "retainedCreatorsComplete": len(creator_results) == expected_creator_count
            and creator_executed == expected_creator_count and creator_passed == expected_creator_count,
        "observationSetupPassed": observer_defined,
        "testAllPassed": suite["testAllPassed"],
        "allCurrentExamplesPassed": suite["examplesPassed"],
        "batchRootAndFailureTypePassed": batch_contract["passed"],
        "baselineBindingPassed": baseline_binding_passed,
        "baselineSeedPreservationPassed": baseline_seed_preservation_passed,
        "commonSeedPreserved": seed_preservation_passed,
        "armBaselinePreserved": baseline_preservation_passed,
        "actorProjectUnchanged": actor_unchanged,
        "baselineProjectUnchanged": baseline_unchanged,
        "scoringCopyUnchanged": scoring_copy_unchanged,
    }
    automated_passed = all(gates.values())
    return {
        "status": "behavior-passed-review-required" if automated_passed else "score-failure",
        "condition": condition,
        "automatedPassed": automated_passed,
        "acceptedEditPassed": False,
        "passed": False,
        "gates": gates,
        "caseCounts": {"expected": CASE_COUNT, "executed": executed, "passed": passed_cases},
        "retainedCreatorCounts": {
            "expected": expected_creator_count,
            "executed": creator_executed,
            "passed": creator_passed,
        },
        "independentSourceReview": {
            "status": "required",
            "passed": None,
            "reason": "The adapter cannot establish that inherited batch/helper test scenarios and expected causes were preserved by assertion changes; this requires independent source review.",
            "reviewItems": [
                "Compare every inherited batch/helper test fixture and expected cause with the baseline source.",
                "Confirm only typed error wrappers, counter fields, and helper state construction changed.",
                "Confirm no assertion was weakened and the new later-failure/first-error tests were added.",
            ],
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Accepted pinned AgentLang CLI")
    parser.add_argument("--actor-project", type=Path, required=True, help="Saved study actor project")
    parser.add_argument("--condition", choices=("reset", "retained"), required=True)
    parser.add_argument("--baseline-project", type=Path,
                        help="Accepted terminal 213 actor snapshot; defaults by condition")
    parser.add_argument("--seed-project", type=Path, default=DEFAULT_SEED_PROJECT,
                        help="Frozen 47-word/29-type report-163 seed project")
    parser.add_argument("--evidence-root", type=Path, required=True,
                        help="Evidence directory below .agentlang/typed-batch-failure-216/")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    evidence_root = validate_evidence_root(args.evidence_root)
    cli = resolve_repo_path(args.cli)
    actor_project = resolve_repo_path(args.actor_project)
    seed_project = resolve_repo_path(args.seed_project)
    baseline_project = resolve_repo_path(args.baseline_project or DEFAULT_BASELINE_PROJECTS[args.condition])
    if not cli.is_file() or not actor_project.is_dir() or not seed_project.is_dir() or not baseline_project.is_dir():
        raise FileNotFoundError("The pinned CLI, actor, seed, and accepted baseline projects must exist")

    accepted_213_pins = validate_accepted_213_pins(cli)
    seed_inventory, seed_projection = scorer.validate_seed_inventory(seed_project)
    baseline_fingerprint, baseline = validate_baseline(args.condition, baseline_project, seed_project)
    _, seed_manifest, _ = scorer.current_manifest(seed_project)
    actor_fingerprint = project_fingerprint(actor_project)
    _, actor_manifest, _ = scorer.current_manifest(actor_project)
    actor_projection = scorer.projection_for_manifest(actor_manifest)
    seed_preservation = scorer.preservation_diff(seed_projection, actor_projection)
    baseline_preservation = preservation_report(
        args.condition,
        baseline_project,
        baseline["manifest"],
        baseline["projection"],
        actor_project,
        actor_manifest,
        actor_projection,
    )
    batch_contract = inspect_batch_contract(actor_project, actor_manifest, baseline["manifest"], args.condition)
    examples = _manifest_examples(actor_manifest)
    baseline_examples = _manifest_examples(baseline["manifest"])
    cases = MODEL.make_cases()
    if len(cases) != CASE_COUNT:
        raise ValueError(f"Frozen typed failure model must contain exactly {CASE_COUNT} cases")
    creator_cases = scorer.load_frozen_cases("creator") if args.condition == "retained" else []
    if args.condition == "retained" and len(creator_cases) != CREATOR_CASE_COUNT:
        raise ValueError(f"Frozen retained creator set must contain exactly {CREATOR_CASE_COUNT} cases")
    requests = build_requests(cases, examples, args.condition, creator_cases)
    expected_response_count = len(requests)
    run_dir = create_run_directory(evidence_root, args.condition)
    scoring_project = run_dir / "project-copy"
    shutil.copytree(actor_project, scoring_project)
    scoring_copy_fingerprint = project_fingerprint(scoring_project)
    pins = {
        "cli": str(cli.resolve()),
        "accepted213Pins": accepted_213_pins,
        "condition": args.condition,
        "actorProject": str(actor_project.resolve()),
        "actorProjectFingerprint": actor_fingerprint,
        "seedProject": str(seed_project.resolve()),
        "seedProjectFingerprint": project_fingerprint(seed_project),
        "seedManifestHash": seed_inventory["manifestHash"],
        "baselineProject": str(baseline_project.resolve()),
        "baselineProjectFingerprint": baseline_fingerprint,
        "baselineManifestHash": baseline["manifestHash"],
        "scoringProjectCopy": str(scoring_project.resolve()),
        "scoringProjectCopyFingerprintBeforeRun": scoring_copy_fingerprint,
        "sourceHashes": _source_hashes(),
        "causeSourceProvenance": MODEL.cause_source_provenance(),
    }
    _write_json(run_dir / "pins.json", pins)
    (run_dir / "requests.jsonl").write_text(_jsonl(requests), encoding="utf-8")

    exit_code: int | None = None
    stdout = ""
    stderr = ""
    responses = []
    runner_error = None
    try:
        exit_code, stdout, stderr, responses = scorer.run_cli(cli, scoring_project, requests)
    except Exception as error:
        runner_error = {"type": type(error).__name__, "message": str(error)}
        stderr = f"{type(error).__name__}: {error}\n"
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    response_count = len(responses)
    example_start = 1
    example_responses = responses[example_start:example_start + len(examples)]
    define_index = example_start + len(examples)
    define_response = responses[define_index] if len(responses) > define_index else {}
    eval_start = define_index + 1
    batch_results = []
    for index, item in enumerate(cases):
        response_index = eval_start + index
        response = responses[response_index] if response_index < len(responses) else {}
        batch_results.append(safe_score_typed_eval(item, response))
    creator_start = eval_start + len(cases)
    creator_results = []
    for index, item in enumerate(creator_cases):
        response_index = creator_start + index
        response = responses[response_index] if response_index < len(responses) else {}
        creator_results.append(safe_score_creator_eval(item, response))

    observer_defined = scorer.is_successful_define_response(define_response)
    suite = score_suite(
        responses[0] if responses else {},
        examples,
        example_responses,
        baseline_examples,
        actor_manifest,
        seed_manifest,
        seed_projection,
    )
    final_actor_fingerprint = project_fingerprint(actor_project)
    final_baseline_fingerprint = project_fingerprint(baseline_project)
    final_copy_fingerprint = project_fingerprint(scoring_project)
    actor_unchanged = final_actor_fingerprint == actor_fingerprint
    baseline_unchanged = final_baseline_fingerprint == baseline_fingerprint
    scoring_copy_unchanged = final_copy_fingerprint == scoring_copy_fingerprint
    decision = evaluate_acceptance(
        condition=args.condition,
        cli_exit_code=exit_code,
        response_count=response_count,
        expected_response_count=expected_response_count,
        case_results=batch_results,
        creator_results=creator_results,
        suite=suite,
        observer_defined=observer_defined,
        batch_contract=batch_contract,
        baseline_binding_passed=baseline["passed"],
        baseline_seed_preservation_passed=baseline["seedPreservationPassed"],
        seed_preservation_passed=seed_preservation["passed"],
        baseline_preservation_passed=baseline_preservation["passed"],
        actor_unchanged=actor_unchanged,
        baseline_unchanged=baseline_unchanged,
        scoring_copy_unchanged=scoring_copy_unchanged,
    )
    report = {
        **decision,
        "runtimeInvocation": {"filesystemMode": "virtual", "capabilities": [], "testCapabilities": []},
        "actorProject": str(actor_project.resolve()),
        "actorProjectFingerprint": actor_fingerprint,
        "actorProjectUnchangedDuringScore": actor_unchanged,
        "scoringProject": str(scoring_project.resolve()),
        "scoringProjectCopyFingerprintBeforeRun": scoring_copy_fingerprint,
        "scoringProjectCopyUnchangedDuringScore": scoring_copy_unchanged,
        "evidenceDirectory": str(run_dir.resolve()),
        "seedManifestHash": seed_inventory["manifestHash"],
        "seedInventory": {"wordCount": len(seed_projection["words"]), "typeCount": len(seed_projection["types"])},
        "baseline": {key: value for key, value in baseline.items() if key not in {"projection", "manifest"}},
        "baselineProjectUnchangedDuringScore": baseline_unchanged,
        "cliExitCode": exit_code,
        "runnerError": runner_error,
        "responseCount": response_count,
        "expectedResponseCount": expected_response_count,
        "observationSetupPassed": observer_defined,
        "observationSetupResponse": define_response,
        "suite": suite,
        "behavior": {
            "caseCount": CASE_COUNT,
            "executedCaseCount": sum(row["executed"] for row in batch_results),
            "passedCaseCount": sum(row["executed"] and row["passed"] for row in batch_results),
            "failureIds": [row["caseId"] for row in batch_results if row["executed"] and not row["passed"]],
            "setupErrors": [row for row in batch_results if not row["executed"]],
            "caseResults": batch_results,
        },
        "retainedCreatorBehavior": {
            "caseCount": len(creator_cases),
            "executedCaseCount": sum(row["executed"] for row in creator_results),
            "passedCaseCount": sum(row["executed"] and row["passed"] for row in creator_results),
            "failureIds": [row["caseId"] for row in creator_results if row["executed"] and not row["passed"]],
            "setupErrors": [row for row in creator_results if not row["executed"]],
            "caseResults": creator_results,
        },
        "batchContract": batch_contract,
        "preservation": {
            "fromReport163Seed": seed_preservation,
            "fromAccepted213Baseline": baseline_preservation,
            "independentSourceReviewRequired": True,
        },
        "modelChecks": MODEL.model_checks(),
        "sourcePins": pins,
    }
    _write_json(run_dir / "score.json", report)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    # Exit zero means scoring completed and automated gates passed. The report
    # still withholds acceptedEditPassed until independent source review is recorded.
    return 0 if report["automatedPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
