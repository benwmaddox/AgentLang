#!/usr/bin/env python3
"""Independent AgentLang scorer for scheduled-batch-226.

The submitted project is copied to a disposable directory and exercised by the
selected current AgentLang CLI over its JSONL interface. The scheduled cases
and expected results come from this oracle's model; 66 inherited creator cases
are scored through the earlier organic-reuse behavior adapter.
"""

from __future__ import annotations

import argparse
from datetime import timedelta
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from time import gmtime, strftime


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
STUDY_ROOT = REPO / ".agentlang" / "scheduled-batch-226"
DEFAULT_SEED = REPO / ".agentlang" / "typed-batch-comparison-220" / "baselines" / "retained"
DEFAULT_EVIDENCE = STUDY_ROOT / "oracle-work" / "language"
SCHEDULED_MODEL_PATH = HERE / "model.py"
SCHEDULED_CASES_PATH = HERE / "cases.json"
EXPECTED_SEED_FINGERPRINT = "b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611"
ROOT_WORD = "subscription.renew-scheduled-batch"
TYPE_NAME = "ScheduledSubscriptionRenewal"
STORE_ARRAYS = {"customers", "products", "subscriptions", "invoices", "payments"}
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
OBSERVATION_SOURCE = """// frontend: flow/2
record ScheduledBatch226Observation {
    field input: Store
    field outcome: Result<Store, BusinessError>
}
"""


def import_from_path(module_name: str, path: Path):
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {module_name} from {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_scheduled_model():
    model = import_from_path("scheduled_batch_226_language_model", SCHEDULED_MODEL_PATH)
    cases = json.loads(SCHEDULED_CASES_PATH.read_text(encoding="utf-8"))
    if cases != model.make_cases():
        raise ValueError("cases.json differs from the generated scheduled-batch model")
    return model, cases


def load_organic_adapter():
    """Load the 210 fixture and adapter without importing its generic `model` globally."""
    oracle_dir = REPO / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210" / "oracle"
    missing = object()
    saved_modules = {name: sys.modules.get(name, missing) for name in ("model", "cell_model_163")}
    for name in saved_modules:
        sys.modules.pop(name, None)
    sys.path.insert(0, str(oracle_dir))
    try:
        adapter = import_from_path("scheduled_batch_226_organic_reuse_adapter", oracle_dir / "score_organic_reuse.py")
    finally:
        sys.path.remove(str(oracle_dir))
        for name, module in saved_modules.items():
            if module is missing:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = module

    # 220 uses the canonical IDs below; reuse the old model/renderer while
    # keeping fixtures and projections internally consistent with this seed.
    adapter.OLD.SUBSCRIPTION_IDS.update({
        "old2": "30000000-0000-0000-0000-000000000006",
        "new2": "30000000-0000-0000-0000-000000000005",
    })
    return adapter


SCORER210 = load_organic_adapter()
OLD = SCORER210.OLD
sys.path.insert(0, str(HERE))
import type_gate  # noqa: E402
sys.path.remove(str(HERE))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def project_fingerprint(project: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(project.rglob("*"), key=lambda item: item.relative_to(project).as_posix()):
        if not path.is_file():
            continue
        relative = path.relative_to(project).as_posix().encode("utf-8")
        content = path.read_bytes()
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def current_manifest(project: Path) -> tuple[dict, dict, Path]:
    store = project / ".agentlang" / "store"
    current = json.loads((store / "CURRENT").read_text(encoding="utf-8-sig"))
    manifest_path = store / "manifests" / f"{current['manifestHash']}.json"
    return current, json.loads(manifest_path.read_text(encoding="utf-8-sig")), store


def current_revisions(manifest: dict) -> dict[str, dict]:
    result = {}
    for word in manifest["words"]:
        revision = next(row for row in manifest["revisions"]
                        if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"])
        result[word["currentName"]] = {"word": word, "revision": revision}
    return result


def projection_for_manifest(manifest: dict) -> dict:
    return {
        # Keep the complete manifest rows: the source-format, validator,
        # declaration, status, test/example references, fixtures, call bindings,
        # and revision metadata all participate in the inherited freeze.
        "types": {row["name"]: row for row in manifest["types"]},
        "words": {row["wordId"]: row for row in manifest["words"]},
        "revisions": {
            word_id: sorted(
                (row for row in manifest["revisions"] if row["wordId"] == word_id),
                key=lambda row: row["revision"],
            )
            for word_id in {row["wordId"] for row in manifest["words"]}
        },
    }


def referenced_assets(projection: dict) -> set[str]:
    hashes = {
        row["definition"]["hash"] for row in projection["types"].values()
    }
    for revisions in projection["revisions"].values():
        for revision in revisions:
            hashes.add(revision["definition"]["hash"])
            hashes.update(row["hash"] for row in revision["tests"])
            hashes.update(row["hash"] for row in revision["examples"])
    return hashes


def asset_hashes(project: Path, projection: dict) -> dict[str, str | None]:
    result = {}
    for object_hash in sorted(referenced_assets(projection)):
        path = project / ".agentlang" / "store" / "objects" / f"{object_hash}.agent"
        result[object_hash] = sha256_file(path) if path.is_file() else None
    return result


def preservation_diff(seed: dict, candidate: dict, *, seed_project: Path | None = None,
                      candidate_project: Path | None = None) -> dict:
    inherited_word_ids = set(seed["words"])
    changed_words = {
        name: {"expected": value, "actual": candidate["words"].get(name)}
        for name, value in seed["words"].items()
        if candidate["words"].get(name) != value
    }
    changed_types = {
        name: {"expected": value, "actual": candidate["types"].get(name)}
        for name, value in seed["types"].items()
        if candidate["types"].get(name) != value
    }
    changed_revisions = {
        word_id: {"expected": rows, "actual": candidate["revisions"].get(word_id)}
        for word_id, rows in seed["revisions"].items()
        if candidate["revisions"].get(word_id) != rows
    }
    missing_inherited_revisions = sorted(inherited_word_ids - set(candidate["revisions"]))
    inherited_assets = sorted(referenced_assets(seed))
    asset_diff = None
    if seed_project is not None and candidate_project is not None:
        expected_assets = asset_hashes(seed_project, seed)
        actual_assets = asset_hashes(candidate_project, seed)
        asset_diff = {
            "verifiedCount": sum(value == object_hash for object_hash, value in actual_assets.items()),
            "expectedCount": len(inherited_assets),
            "changedOrMissing": {
                object_hash: {"expectedSha256": expected_assets.get(object_hash),
                              "actualSha256": actual_assets.get(object_hash)}
                for object_hash in inherited_assets
                if expected_assets.get(object_hash) != object_hash or actual_assets.get(object_hash) != object_hash
            },
        }
    assets_passed = asset_diff is None or not asset_diff["changedOrMissing"]
    return {
        "passed": not changed_words and not changed_types and not changed_revisions
                   and not missing_inherited_revisions and assets_passed,
        "changedInheritedWords": changed_words,
        "changedInheritedTypes": changed_types,
        "changedOrAddedRevisionsForInheritedWords": changed_revisions,
        "missingInheritedRevisionGroups": missing_inherited_revisions,
        "referencedInheritedObjects": inherited_assets,
        "inheritedObjectIntegrity": asset_diff,
        "derivedCallerIndexExcluded": False,
    }


def validate_evidence_root(path: Path) -> Path:
    resolved = path if path.is_absolute() else REPO / path
    resolved = resolved.resolve()
    root = DEFAULT_EVIDENCE.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"Evidence must be inside {root}")
    return resolved


def create_run_directory(root: Path, label: str) -> Path:
    parent = root / label
    parent.mkdir(parents=True, exist_ok=True)
    stamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    index = 1
    while True:
        candidate = parent / f"{stamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _decode_output(value) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def run_cli(cli: Path, project: Path, requests: list[dict]) -> tuple[int | None, str, str, list[dict], list[str], str | None]:
    command = ["dotnet", str(cli), "--project", str(project), "--filesystem", "virtual", "--jsonl"]
    payload = "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n"
    try:
        process = subprocess.run(
            command,
            cwd=REPO,
            input=payload,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=900,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        return None, _decode_output(error.stdout), _decode_output(error.stderr), [], [], \
            f"CLI timed out after {error.timeout} seconds"
    except OSError as error:
        return None, "", f"{type(error).__name__}: {error}\n", [], [], f"CLI launch failed: {error}"
    responses = []
    parse_errors = []
    for line_number, line in enumerate(process.stdout.splitlines(), 1):
        try:
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError("response is not a JSON object")
            responses.append(row)
        except (json.JSONDecodeError, ValueError) as error:
            parse_errors.append(f"stdout line {line_number}: {error}: {line[:300]!r}")
    return process.returncode, process.stdout, process.stderr, responses, parse_errors, None


def type_control_source(separator: str, renewal_type: str = "SubscriptionRenewal") -> str:
    if separator == "semicolon":
        return (
            "// frontend: flow/2\n"
            "record ScheduledSubscriptionRenewal { "
            f"field renewal: {renewal_type}; field at: Instant; "
            "}\n"
        )
    return (
        "// frontend: flow/2\n"
        "record ScheduledSubscriptionRenewal {\n"
        f"    field renewal: {renewal_type}\n"
        "    field at: Instant\n"
        "}\n"
    )


def run_type_gate_controls(cli: Path, seed: Path, evidence_root: Path) -> dict:
    cases = [
        ("newline", "newline", "SubscriptionRenewal", "pass"),
        ("semicolon", "semicolon", "SubscriptionRenewal", "pass"),
        ("wrong-field-type", "newline", "String", "reject"),
    ]
    results = []
    seed_before = project_fingerprint(seed)
    for name, separator, renewal_type, expected in cases:
        run_dir = create_run_directory(evidence_root, f"type-gate-{name}")
        project = run_dir / "project-copy"
        shutil.copytree(seed, project)
        source_before = project_fingerprint(project)
        requests = [
            {"op": "define", "frontend": "flow", "syntaxVersion": 2,
             "source": type_control_source(separator, renewal_type)},
            {"op": "describe", "type": TYPE_NAME},
        ]
        exit_code, stdout, stderr, responses, parse_errors, launch_failure = run_cli(cli, project, requests)
        (run_dir / "requests.jsonl").write_text(
            "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
            encoding="utf-8",
        )
        (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
        (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")
        define_response = responses[0] if len(responses) > 0 else {}
        describe_response = responses[1] if len(responses) > 1 else {}
        gate = type_gate.type_gate(describe_response)
        described = type_gate.described_type(describe_response)
        semantic_record = (
            described is not None
            and described.get("name") == TYPE_NAME
            and described.get("typeKind") == "record"
            and isinstance(described.get("fields"), list)
        )
        expected_satisfied = semantic_record and gate["status"] == ("failure" if expected == "reject" else "pass")
        after_hash = project_fingerprint(project)
        well_formed = len(responses) == len(requests) and not parse_errors and launch_failure is None
        passed = (
            exit_code == 0 and well_formed
            and define_response.get("ok") is True and define_response.get("kind") == "defined"
            and expected_satisfied and source_before == after_hash
        )
        error_details = list(parse_errors)
        if launch_failure:
            error_details.append(launch_failure)
        if len(responses) != len(requests):
            error_details.append(f"expected {len(requests)} responses, got {len(responses)}")
        if exit_code != 0:
            error_details.append(f"CLI exit code {exit_code}")
        if source_before != after_hash:
            error_details.append("scoring copy changed during type-gate probe")
        result = {
            "name": name,
            "status": "passed" if passed else "failure",
            "expectedGate": expected,
            "definePassed": define_response.get("ok") is True and define_response.get("kind") == "defined",
            "semanticDescribeRecordObserved": semantic_record,
            "typeGate": gate,
            "typeGateMatchedExpected": expected_satisfied,
            "projectFingerprintBefore": source_before,
            "projectFingerprintAfter": after_hash,
            "cliExitCode": exit_code,
            "rawResponseCount": len(responses),
            "errors": error_details,
            "evidenceDirectory": str(run_dir.resolve()),
        }
        write_json(run_dir / "score.json", result)
        results.append(result)
    seed_after = project_fingerprint(seed)
    passed = seed_before == seed_after and all(row["status"] == "passed" for row in results)
    return {
        "status": "passed" if passed else "failure",
        "kind": "actual-cli-describe-probes",
        "seedFingerprintBefore": seed_before,
        "seedFingerprintAfter": seed_after,
        "seedUnchanged": seed_before == seed_after,
        "cases": results,
    }


def iso_minute(minutes: int) -> str:
    value = OLD.BASE_TIME + timedelta(minutes=minutes)
    return value.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"


def scheduled_item_expression(item: dict) -> str:
    renewal = item["renewal"]
    constructor = (
        "subscriptionRenewal.new("
        f"old-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, renewal['oldId']))}), "
        f"new-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, renewal['newId']))}), "
        f"plan = SubscriptionPlan.{renewal['plan']}(), "
        f"expires-at = Instant.new({OLD.flow_string(iso_minute(renewal['expiry']))}))"
    )
    return (
        "scheduledSubscriptionRenewal.new("
        f"renewal = {constructor}, at = Instant.new({OLD.flow_string(iso_minute(item['at']))}))"
    )


def scheduled_observation_expression(item: dict) -> str:
    original_store = OLD.store_expr(item["store"])
    changes = OLD.list_expr("ScheduledSubscriptionRenewal", [
        scheduled_item_expression(row) for row in item["request"]["items"]
    ])
    call = f"{item['operation']}({original_store}, {changes})"
    return f"scheduledBatch226Observation.new(input = {original_store}, outcome = {call})"


def canonical(value, key: str | None = None):
    if isinstance(value, dict):
        return {name: canonical(item, name) for name, item in value.items()}
    if isinstance(value, list):
        rows = [canonical(item) for item in value]
        if key in STORE_ARRAYS and rows and isinstance(rows[0], dict):
            rows.sort(key=lambda item: item.get("id", ""))
        return rows
    return value


def schedule_actual_projection(observation: dict) -> dict:
    input_store = observation["input"]
    outcome = observation["outcome"]
    if outcome["tag"] == "ok":
        return {"tag": "ok", "store": outcome["value"], "input": input_store}
    error = outcome.get("value") or {}
    return {"tag": "error", "code": error.get("code"), "message": error.get("message"), "input": input_store}


def schedule_expected_projection(item: dict, expected: dict | None = None) -> dict:
    expected = expected if expected is not None else item["expected"]
    original_iso_day = OLD.iso_day
    OLD.iso_day = iso_minute
    try:
        input_store = OLD.normalized_projection(item)
        if expected["tag"] == "ok":
            return {"tag": "ok", "store": OLD.normalized_projection({"store": expected["store"]}), "input": input_store}
        return {"tag": "error", "code": expected["code"],
                "message": expected.get("message", ERROR_MESSAGES[expected["code"]]), "input": input_store}
    finally:
        OLD.iso_day = original_iso_day


def score_schedule_case(item: dict, response: dict, expected: dict | None = None) -> dict:
    result = {"caseId": item["id"], "executed": False, "passed": False}
    if response.get("ok") is True and response.get("kind") == "eval":
        structured = response.get("data", {}).get("structuredStack")
        values = structured.get("values", []) if isinstance(structured, dict) else []
        if len(values) == 1:
            actual = schedule_actual_projection(OLD.unwrap_structured(values[0]))
            wanted = schedule_expected_projection(item, expected)
            actual = canonical(actual)
            wanted = canonical(wanted)
            result.update(executed=True, passed=(actual == wanted), actualTag=actual.get("tag"), expectedTag=wanted.get("tag"))
            if actual != wanted:
                result.update(expected=wanted, actual=actual)
        else:
            result["setupError"] = "eval response omitted one structured observation value"
    else:
        result["setupError"] = response.get("error", response.get("text", "missing eval response"))
    return result


def score_creator_case(item: dict, response: dict) -> dict:
    # This deliberately reuses only the previous study's behavior projection;
    # no old migration, manifest, effects, or eligibility gate is carried over.
    return SCORER210.score_eval(item, response)


def run_case_suite(cli: Path, project: Path, *, include_inherited: bool, full_suite: bool,
                   label: str, evidence_root: Path, mutation: str | None = None,
                   control_variant: str | None = None) -> dict:
    model, scheduled_cases = load_scheduled_model()
    if include_inherited:
        creator_cases = SCORER210.load_frozen_cases("creator")
    else:
        creator_cases = []

    before_source = project_fingerprint(project)
    _, manifest_before, _ = current_manifest(project)
    example_owners = sorted({
        word["currentName"] for word in manifest_before["words"]
        if next(row for row in manifest_before["revisions"]
                if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"])["examples"]
    }) if full_suite else []
    run_dir = create_run_directory(evidence_root, label)
    scoring_project = run_dir / "project-copy"
    shutil.copytree(project, scoring_project)
    copy_before = project_fingerprint(scoring_project)
    if copy_before != before_source:
        raise RuntimeError("Scoring copy fingerprint differs from source project")

    requests = []
    if full_suite:
        requests.append({"op": "test-all"})
        for owner in example_owners:
            requests.append({"op": "example", "word": owner})
    requests.append({"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": OBSERVATION_SOURCE})
    requests.append({"op": "describe", "type": TYPE_NAME})

    # Build the scheduled fixtures with minute precision, then restore the
    # historical day adapter for untouched 210 creator checks.
    original_iso_day = OLD.iso_day
    OLD.iso_day = iso_minute
    try:
        scheduled_expressions = [scheduled_observation_expression(item) for item in scheduled_cases]
    finally:
        OLD.iso_day = original_iso_day
    scheduled_expected = [item["expected"] for item in scheduled_cases]
    if mutation is not None:
        scheduled_expected = [model.expected(item, mutation=mutation) for item in model.CASES]
    for expression, item in zip(scheduled_expressions, scheduled_cases):
        requests.append({"op": "eval", "frontend": "flow", "syntaxVersion": 2,
                         "structured": True, "code": expression})

    for item in creator_cases:
        expression = SCORER210.observation_expression(item, "creator").replace(
            "organicReuse210Observation", "scheduledBatch226Observation"
        )
        requests.append({"op": "eval", "frontend": "flow", "syntaxVersion": 2,
                         "structured": True, "code": expression})

    exit_code, stdout, stderr, responses, json_parse_errors, launch_failure = run_cli(cli, scoring_project, requests)
    expected_request_count = len(requests)
    raw_response_count = len(responses)
    protocol_errors = list(json_parse_errors)
    if launch_failure:
        protocol_errors.append(launch_failure)
    if raw_response_count != expected_request_count:
        protocol_errors.append(f"CLI returned {raw_response_count} valid responses for {expected_request_count} requests")
    parse_failure = "\n".join(protocol_errors) or None

    (run_dir / "requests.jsonl").write_text(
        "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
        encoding="utf-8",
    )
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    if len(responses) < expected_request_count:
        responses.extend({} for _ in range(expected_request_count - len(responses)))

    cursor = 0
    test_response = {}
    if full_suite:
        test_response = responses[cursor]
        cursor += 1
    example_responses = responses[cursor:cursor + len(example_owners)]
    cursor += len(example_owners)
    define_response = responses[cursor]
    cursor += 1
    describe_response = responses[cursor]
    cursor += 1
    schedule_responses = responses[cursor:cursor + len(scheduled_cases)]
    cursor += len(scheduled_cases)
    creator_responses = responses[cursor:cursor + len(creator_cases)]

    schedule_results = [
        score_schedule_case(item, response, wanted)
        for item, response, wanted in zip(scheduled_cases, schedule_responses, scheduled_expected)
    ]
    creator_results = [score_creator_case(item, response) for item, response in zip(creator_cases, creator_responses)]
    define_pass = define_response.get("ok") is True and define_response.get("kind") == "defined"
    type_result = type_gate.type_gate(describe_response)
    type_pass = type_result["status"] == "pass"
    test_pass = SCORER210.response_passed(test_response, "test") if full_suite else True
    example_results = [
        {"owner": owner, "passed": SCORER210.response_passed(response, "example"), "response": response}
        for owner, response in zip(example_owners, example_responses)
    ]
    examples_pass = len(example_results) == len(example_owners) and all(row["passed"] for row in example_results)

    actor_after = project_fingerprint(project)
    copy_after = project_fingerprint(scoring_project)
    mutation_pass = True
    if mutation is not None:
        model_checks = model.model_checks()
        mutation_pass = all(
            item["caseId"] in model_checks["discrimination"].get(mutation, [])
            or canonical(schedule_expected_projection(scheduled_cases[index], expected=scheduled_expected[index]))
               == canonical(schedule_expected_projection(scheduled_cases[index]))
            for index, item in enumerate(schedule_results)
        )

    failures = []
    if exit_code != 0:
        failures.append({"kind": "cli-exit", "code": exit_code})
    if parse_failure:
        failures.append({"kind": "cli-output", "detail": parse_failure})
    if not define_pass:
        failures.append({"kind": "observer-define", "response": define_response})
    if not type_pass:
        failures.append({"kind": "scheduled-type", "gate": type_result, "response": describe_response})
    if not test_pass:
        failures.append({"kind": "test-all", "response": test_response})
    if not examples_pass:
        failures.append({"kind": "examples", "results": example_results})
    if len(schedule_results) != len(scheduled_cases) or any(not row["executed"] for row in schedule_results):
        failures.append({"kind": "scheduled-case-setup", "cases": [row for row in schedule_results if not row["executed"]]})
    if mutation is None and any(not row["passed"] for row in schedule_results):
        failures.append({"kind": "scheduled-case-mismatch", "cases": [row for row in schedule_results if not row["passed"]]})
    if mutation is not None and not mutation_pass:
        failures.append({"kind": "mutation-discrimination", "mutation": mutation})
    if include_inherited and (len(creator_results) != 66 or any(not row["executed"] or not row["passed"] for row in creator_results)):
        failures.append({"kind": "inherited-creator-behavior", "cases": [row for row in creator_results if not row["executed"] or not row["passed"]]})
    if actor_after != before_source:
        failures.append({"kind": "source-project-mutated", "before": before_source, "after": actor_after})
    if copy_after != copy_before:
        failures.append({"kind": "scoring-copy-mutated", "before": copy_before, "after": copy_after})

    report = {
        "status": (("control-calibrated" if not failures else "control-failure") if control_variant
                   else ("passed" if not failures else "score-failure")),
        "scoreKind": "calibration-control" if control_variant else "participant-acceptance",
        "label": label,
        "controlVariant": control_variant,
        "mutationModel": mutation,
        "project": str(project.resolve()),
        "sourceProjectFingerprintBefore": before_source,
        "sourceProjectFingerprintAfter": actor_after,
        "scoringProject": str(scoring_project.resolve()),
        "scoringProjectFingerprintBefore": copy_before,
        "scoringProjectFingerprintAfter": copy_after,
        "cli": str(cli.resolve()),
        "cliSha256": sha256_file(cli),
        "cliExitCode": exit_code,
        "rawValidResponseCount": raw_response_count,
        "responseCount": len(responses),
        "expectedResponseCount": expected_request_count,
        "requestParseFailure": parse_failure,
        "jsonParseErrors": json_parse_errors,
        "launchFailure": launch_failure,
        "testAllPassed": test_pass,
        "testAllResponse": test_response,
        "exampleOwnerCount": len(example_owners),
        "examplesPassed": examples_pass,
        "exampleResults": example_results,
        "observerDefined": define_pass,
        "typeGate": {**type_result, "response": describe_response},
        "scheduledCases": {
            "count": len(schedule_results),
            "executed": sum(row["executed"] for row in schedule_results),
            "matched": sum(row["passed"] for row in schedule_results),
            "results": schedule_results,
        },
        "inheritedCreatorCases": {
            "count": len(creator_results),
            "executed": sum(row["executed"] for row in creator_results),
            "matched": sum(row["passed"] for row in creator_results),
            "source": "organic-reuse-210 creator cases through its behavior-only adapter",
        },
        "mutationDiscriminationPassed": mutation_pass,
        "failures": failures,
        "evidenceDirectory": str(run_dir.resolve()),
        "requests": "requests.jsonl",
        "responses": "responses.jsonl",
        "stderr": "stderr.txt",
    }
    write_json(run_dir / "score.json", report)
    return report


def parse_named_project(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected NAME=PROJECT_PATH")
    name, raw_path = value.split("=", 1)
    if not name or not raw_path:
        raise argparse.ArgumentTypeError("expected nonempty NAME=PROJECT_PATH")
    path = Path(raw_path)
    return name, path if path.is_absolute() else REPO / path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path, help="Fresh AgentLang.Cli.dll for this study")
    parser.add_argument("--actor-project", required=True, type=Path, help="Project root to score")
    parser.add_argument("--seed-project", type=Path, default=DEFAULT_SEED)
    parser.add_argument("--evidence-root", type=Path, default=DEFAULT_EVIDENCE)
    parser.add_argument("--mode", choices=("preflight", "frozen"), default="preflight")
    parser.add_argument("--source-purpose", choices=("participant-acceptance", "calibration"),
                        default="participant-acceptance",
                        help="Label whether actor-project is a participant submission or a calibration control")
    parser.add_argument("--control", action="append", type=parse_named_project, default=[],
                        help="Optional calibrated control as NAME=PROJECT_PATH; names: correct, shared-instant, sort-by-at, last-error, partial-success")
    args = parser.parse_args()

    cli = args.cli if args.cli.is_absolute() else REPO / args.cli
    actor = args.actor_project if args.actor_project.is_absolute() else REPO / args.actor_project
    seed = args.seed_project if args.seed_project.is_absolute() else REPO / args.seed_project
    evidence = validate_evidence_root(args.evidence_root)
    if not cli.is_file() or not actor.is_dir() or not seed.is_dir():
        raise FileNotFoundError("CLI, actor project, and retained seed must exist")
    seed_before = project_fingerprint(seed)
    if seed_before != EXPECTED_SEED_FINGERPRINT:
        raise ValueError(f"Retained seed fingerprint changed: {seed_before}")
    type_gate_controls = run_type_gate_controls(cli.resolve(), seed, evidence)
    _, seed_manifest, _ = current_manifest(seed)
    seed_projection = projection_for_manifest(seed_manifest)
    actor_before = project_fingerprint(actor)
    _, actor_manifest, _ = current_manifest(actor)
    current = current_revisions(actor_manifest)
    root_library = ROOT_WORD in current and current[ROOT_WORD]["revision"]["maturity"] == "library"
    root_tests = bool(current.get(ROOT_WORD, {}).get("revision", {}).get("tests"))
    root_available = ROOT_WORD in current

    result_root = create_run_directory(evidence, "cal" if args.source_purpose == "calibration" else "participant")
    # Use the participant run folder as the common evidence root. The evaluator
    # creates run-specific copies below its child folder.
    suite_label = "correct" if args.source_purpose == "calibration" else "evaluation"
    suite = run_case_suite(cli.resolve(), actor.resolve(), include_inherited=True,
                           full_suite=True, label=suite_label, evidence_root=result_root)
    control_reports = []
    model, _ = load_scheduled_model()
    expected_controls = {"correct": None, "shared-instant": "shared-instant", "sort-by-at": "sort-by-at",
                         "last-error": "last-error", "partial-success": "partial-success"}
    control_names = [name for name, _ in args.control]
    control_set_complete = set(control_names) == set(expected_controls) and len(control_names) == len(set(control_names))
    for name, project in args.control:
        if name not in expected_controls:
            raise ValueError(f"Unknown control name {name!r}")
        if not project.is_dir():
            raise FileNotFoundError(f"Control project does not exist: {project}")
        control = run_case_suite(cli.resolve(), project.resolve(), include_inherited=False,
                                 full_suite=False, label=f"control-{name}", evidence_root=result_root,
                                 mutation=expected_controls[name], control_variant=name)
        # Calibration compares exact neutral Store projections for every case.
        matches_predicted = control["scheduledCases"]["matched"] == len(model.CASES)
        control["matchesPredictedModel"] = matches_predicted
        if not matches_predicted:
            control["status"] = "control-failure"
            control["failures"].append({"kind": "control-model-mismatch", "expectedMutation": expected_controls[name]})
        control_reports.append(control)

    seed_after = project_fingerprint(seed)
    actor_after = project_fingerprint(actor)
    _, actor_manifest_after, _ = current_manifest(actor)
    candidate_projection_after = projection_for_manifest(actor_manifest_after)
    inherited_after = preservation_diff(seed_projection, candidate_projection_after,
                                        seed_project=seed, candidate_project=actor)
    preflight_mode = args.mode == "preflight"

    failures = []
    if suite["status"] != "passed":
        failures.append({"kind": "behavior-suite", "directory": suite["evidenceDirectory"]})
    if not inherited_after["passed"]:
        failures.append({"kind": "inherited-preservation", "diff": inherited_after})
    if not root_available:
        failures.append({"kind": "new-root-missing", "word": ROOT_WORD})
    if not root_library or not root_tests:
        failures.append({"kind": "root-qualification", "library": root_library, "hasOwnTests": root_tests})
    if seed_after != seed_before or actor_after != actor_before:
        failures.append({"kind": "source-project-mutated", "actorBefore": actor_before,
                         "actorAfter": actor_after, "seedBefore": seed_before, "seedAfter": seed_after})
    if type_gate_controls["status"] != "passed":
        failures.append({"kind": "type-gate-controls", "details": type_gate_controls})
    if not control_set_complete:
        failures.append({"kind": "control-set-incomplete", "expected": sorted(expected_controls),
                         "actual": control_names})
    if any(row["status"] != "control-calibrated" for row in control_reports):
        failures.append({"kind": "control-calibration", "controls": [row["label"] for row in control_reports
                                                                           if row["status"] != "control-calibrated"]})

    report = {
        "status": "passed" if not failures else "score-failure",
        "mode": args.mode,
        "sourcePurpose": args.source_purpose,
        "runtime": str(cli.resolve()),
        "runtimeSha256": sha256_file(cli),
        "seedProject": str(seed.resolve()),
        "seedProjectFingerprint": seed_before,
        "seedManifestHash": json.loads((seed / ".agentlang/store/CURRENT").read_text(encoding="utf-8-sig"))["manifestHash"],
        "expectedSeedFingerprint": EXPECTED_SEED_FINGERPRINT,
        "actorProject": str(actor.resolve()),
        "actorProjectFingerprintBefore": actor_before,
        "actorProjectFingerprintAfter": actor_after,
        "actorProjectUnchanged": actor_before == actor_after,
        "inheritedProjectionPreserved": inherited_after,
        "rootQualification": {"name": ROOT_WORD, "available": root_available,
                              "library": root_library, "hasOwnTests": root_tests},
        "suite": suite,
        "controls": control_reports,
        "controlSetComplete": control_set_complete,
        "typeGateControls": type_gate_controls,
        "frozenPinStatus": "preflight-only; no frozen CLI pin is claimed" if preflight_mode else "freeze pin must be recorded by the coordinator",
        "failures": failures,
    }
    top_dir = Path(suite["evidenceDirectory"]).parents[1]
    write_json(top_dir / "language-score.json", report)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
