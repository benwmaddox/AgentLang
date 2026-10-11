#!/usr/bin/env python3
"""Run public-interface migration controls on disposable copies of both 213 actors."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


REPO = Path(__file__).resolve().parents[4]
EXPERIMENT = REPO / "experiments" / "AgentLang.SubagentTrials" / "typed-batch-failure-216"
PREPARATION = EXPERIMENT / "preparation"
PINNED_ROOT = REPO / ".agentlang" / "paired-vocabulary-213"
ACTOR_ROOT = PINNED_ROOT / "actors"
RUNTIME = PINNED_ROOT / "runtime"
CONTROLS = REPO / ".agentlang" / "typed-batch-failure-216" / "controls"
ARM_NAMES = ("reset", "retained")
EXPECTED_OUTPUT = "Result<Store, SubscriptionRenewalFailure>"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def project_fingerprint(path: Path) -> str:
    """Use the accepted 213 scoring helper's content fingerprint for exact pins."""
    oracle = REPO / "experiments" / "AgentLang.SubagentTrials" / "paired-vocabulary-213" / "oracle"
    sys.path.insert(0, str(oracle))
    try:
        import score_batch as scoring  # type: ignore[import-not-found]

        return scoring.scorer.project_fingerprint(path)
    finally:
        try:
            sys.path.remove(str(oracle))
        except ValueError:
            pass


def verify_inputs() -> dict[str, Any]:
    source_pin = load_json(PINNED_ROOT / "source-pin.json")
    runtime_pin = load_json(PINNED_ROOT / "accepted-runtime-pin.json")
    failures: list[str] = []
    for item in source_pin["files"]:
        path = REPO / Path(item["path"])
        if not path.is_file():
            failures.append(f"missing source input: {item['path']}")
            continue
        if path.stat().st_size != item["bytes"] or sha256(path) != item["sha256"]:
            failures.append(f"source input pin mismatch: {item['path']}")
    for item in runtime_pin["runtimeFiles"]:
        path = RUNTIME / item["name"]
        if not path.is_file():
            failures.append(f"missing runtime file: {item['name']}")
            continue
        if path.stat().st_size != item["bytes"] or sha256(path) != item["sha256"]:
            failures.append(f"runtime pin mismatch: {item['name']}")
    if failures:
        raise RuntimeError("\n".join(failures))
    return {
        "sourceInputCount": len(source_pin["files"]),
        "runtimeFileCount": len(runtime_pin["runtimeFiles"]),
        "runtimeSha256": sha256(RUNTIME / "AgentLang.Cli.dll"),
        "runtimePath": str(RUNTIME / "AgentLang.Cli.dll"),
    }


def scan_brace_block(source: str, opening: int) -> int:
    depth = 0
    in_string = False
    escaped = False
    for index in range(opening, len(source)):
        character = source[index]
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
        elif character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return index + 1
    raise ValueError("unterminated Flow block")


def extract_blocks(source: str, keyword: str) -> list[tuple[str, str]]:
    pattern = re.compile(rf"(?m)^{re.escape(keyword)}\s+([^\s{{]+)\s*\{{")
    blocks: list[tuple[str, str]] = []
    for match in pattern.finditer(source):
        opening = source.find("{", match.start())
        end = scan_brace_block(source, opening)
        blocks.append((match.group(1), source[match.start():end]))
    return blocks


def add_fields_to_calls(source: str, call_name: str, fields: str) -> str:
    cursor = 0
    while True:
        match = re.search(rf"{re.escape(call_name)}\s*\(", source[cursor:])
        if not match:
            return source
        start = cursor + match.start()
        opening = source.find("(", start)
        depth = 0
        in_string = False
        escaped = False
        end = -1
        for index in range(opening, len(source)):
            character = source[index]
            if in_string:
                if escaped:
                    escaped = False
                elif character == "\\":
                    escaped = True
                elif character == '"':
                    in_string = False
                continue
            if character == '"':
                in_string = True
            elif character == "(":
                depth += 1
            elif character == ")":
                depth -= 1
                if depth == 0:
                    end = index
                    break
        if end < 0:
            raise ValueError(f"unterminated call: {call_name}")
        call_text = source[start:end + 1]
        if fields.split(" = ", 1)[0] not in call_text:
            source = source[:end] + ", " + fields + source[end:]
            end += len(", " + fields)
        cursor = end + 1


def change_call_field(source: str, call_name: str, field_name: str, value: str) -> str:
    match = re.search(rf"{re.escape(call_name)}\s*\(", source)
    if not match:
        return source
    start = match.start()
    opening = source.find("(", start)
    depth = 0
    in_string = False
    escaped = False
    end = -1
    for index in range(opening, len(source)):
        character = source[index]
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
        elif character == "(":
            depth += 1
        elif character == ")":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < 0:
        raise ValueError(f"unterminated call: {call_name}")
    call_text = source[start:end + 1]
    field_pattern = re.compile(rf"\b{re.escape(field_name)}\s*=\s*[^,)]+")
    replaced, count = field_pattern.subn(f"{field_name} = {value}", call_text, count=1)
    return source[:start] + (replaced if count else call_text[:-1] + f", {field_name} = {value})") + source[end + 1:]


def adapt_reset_tests(blocks: list[tuple[str, str]]) -> tuple[list[str], list[str]]:
    root: list[str] = []
    step: list[str] = []
    for full_name, block in blocks:
        owner = full_name.split("/", 1)[0]
        if owner not in {"subscription.renew-batch", "subscription.renew-batch-step"}:
            continue
        adapted = block.replace("problem.code", "problem.cause.code")
        if owner == "subscription.renew-batch-step":
            adapted = adapted.replace("option.none<BusinessError>()", "option.none<SubscriptionRenewalFailure>()")
            adapted = adapted.replace("option.some<BusinessError>(failure)", "option.some<SubscriptionRenewalFailure>(failure)")
            adapted = re.sub(
                r'let failure = businessError\.new\((code = "EARLIER_FAILURE", message = "[^"]+")\)',
                r'let cause = businessError.new(\1)\n            let failure = subscriptionRenewalFailure.new(index = 0, old-id = SubscriptionId.new("36000000-0000-0000-0000-000000000001"), cause = cause)',
                adapted,
            )
            adapted = add_fields_to_calls(adapted, "subscriptionRenewalState.new", "index = 0")
            if full_name.endswith("/preserves-earlier-failure"):
                adapted = change_call_field(adapted, "subscriptionRenewalState.new", "index", "1")
            step.append(adapted)
        else:
            root.append(adapted)
    return root, step


def adapt_retained_tests(blocks: list[tuple[str, str]]) -> tuple[list[str], list[str]]:
    root: list[str] = []
    step: list[str] = []
    for full_name, block in blocks:
        owner = full_name.split("/", 1)[0]
        if owner not in {"subscription.renew-batch", "subscription.renew-batch-step"}:
            continue
        adapted = block.replace("problem.code", "problem.cause.code")
        if owner == "subscription.renew-batch-step":
            adapted = adapted.replace("result.ok<Store, BusinessError>", "result.ok<Store, SubscriptionRenewalFailure>")
            adapted = adapted.replace("result.error<Store, BusinessError>(first-problem)", "result.error<Store, SubscriptionRenewalFailure>(first-failure)")
            adapted = re.sub(
                r'(let first-problem = businessError\.new\(code = "FIRST_BATCH_ERROR", message = "[^"]+"\))',
                r'\1\n    let first-failure = subscriptionRenewalFailure.new(index = 0, old-id = SubscriptionId.new("30000000-0000-0000-0000-000000000001"), cause = first-problem)',
                adapted,
            )
            adapted = add_fields_to_calls(adapted, "subscriptionRenewalBatchState.new", "staged = baseline, index = 0")
            if "let failed-state =" in adapted:
                failed_state_start = adapted.index("let failed-state =")
                adapted = (
                    adapted[:failed_state_start]
                    + change_call_field(adapted[failed_state_start:], "subscriptionRenewalBatchState.new", "index", "1")
                )
            step.append(adapted)
        else:
            root.append(adapted)
    return root, step


def migration_source(arm: str, actor_project: Path) -> tuple[str, dict[str, Any]]:
    dictionary = (actor_project / "dictionary.agent").read_text(encoding="utf-8")
    tests = extract_blocks(dictionary, "test")
    before = sorted(name for name, _ in tests if name.split("/", 1)[0] in {"subscription.renew-batch", "subscription.renew-batch-step"})
    if arm == "reset":
        inherited_root, inherited_step = adapt_reset_tests(tests)
    else:
        inherited_root, inherited_step = adapt_retained_tests(tests)
    template = (PREPARATION / f"control-{arm}.flow").read_text(encoding="utf-8")
    inherited = "\n\n".join(inherited_root + inherited_step)
    source = template + "\n\n" + inherited + "\n"
    after_tests = extract_blocks(source, "test")
    after = sorted(name for name, _ in after_tests if name.split("/", 1)[0] in {"subscription.renew-batch", "subscription.renew-batch-step"})
    added = sorted(set(after) - set(before))
    missing = sorted(set(before) - set(after))
    if missing or added != [
        "subscription.renew-batch/first-error-precedes-later-error",
        "subscription.renew-batch/later-item-failure-localized",
    ]:
        raise RuntimeError(f"unexpected inherited test identity delta for {arm}: missing={missing}, added={added}")
    if len(after) != len(set(after)):
        raise RuntimeError(f"duplicate inherited test identity in {arm} migration")
    return source, {
        "inheritedTestNamesBefore": before,
        "testNamesAfter": after,
        "addedTestNames": added,
        "missingTestNames": missing,
        "mechanicallyAdaptedExistingTestCount": len(before),
    }


def cli(project: Path, requests: Iterable[dict[str, Any]], timeout: int = 180) -> dict[str, Any]:
    command = [
        "dotnet",
        str(RUNTIME / "AgentLang.Cli.dll"),
        "--project",
        str(project),
        "--filesystem",
        "virtual",
        "--jsonl",
    ]
    payload = "".join(json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n" for request in requests)
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    try:
        completed = subprocess.run(
            command,
            input=payload,
            text=True,
            encoding="utf-8",
            capture_output=True,
            timeout=timeout,
            cwd=REPO,
            env=environment,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        return {
            "exitCode": None,
            "timedOut": True,
            "stdout": exc.stdout.decode("utf-8", errors="replace") if isinstance(exc.stdout, bytes) else exc.stdout,
            "stderr": exc.stderr.decode("utf-8", errors="replace") if isinstance(exc.stderr, bytes) else exc.stderr,
            "responses": [],
        }
    responses: list[Any] = []
    parse_errors: list[str] = []
    for line_number, line in enumerate(completed.stdout.splitlines(), start=1):
        if not line.strip():
            continue
        try:
            responses.append(json.loads(line))
        except json.JSONDecodeError as exc:
            parse_errors.append(f"stdout line {line_number}: {exc}")
    return {
        "exitCode": completed.returncode,
        "timedOut": False,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "responses": responses,
        "parseErrors": parse_errors,
    }


def one_response(result: dict[str, Any], index: int, label: str) -> dict[str, Any]:
    responses = result.get("responses", [])
    if index >= len(responses):
        raise RuntimeError(f"{label}: missing CLI response at index {index}; receipt={result}")
    response = responses[index]
    if not isinstance(response, dict):
        raise RuntimeError(f"{label}: non-object response at index {index}: {response!r}")
    return response


def assert_cli_ok(result: dict[str, Any], label: str) -> None:
    if result.get("exitCode") != 0 or result.get("timedOut") or result.get("parseErrors"):
        raise RuntimeError(f"{label}: CLI process failed; exit={result.get('exitCode')} timeout={result.get('timedOut')} parseErrors={result.get('parseErrors')}")
    for index, response in enumerate(result.get("responses", [])):
        if not isinstance(response, dict) or response.get("ok") is not True:
            raise RuntimeError(f"{label}: CLI operation {index} failed: {response}")


def test_summary(response: dict[str, Any]) -> dict[str, Any]:
    data = response.get("data", {})
    results = data.get("results", []) if isinstance(data, dict) else []
    return {
        "ok": response.get("ok") is True,
        "kind": response.get("kind"),
        "text": response.get("text"),
        "total": len(results),
        "passed": sum(1 for item in results if item.get("passed") is True),
        "failed": [item for item in results if item.get("passed") is not True],
    }


def source_owner_names(source: str) -> list[str]:
    return sorted({name.split("/", 1)[0] for name, _ in extract_blocks(source, "example")})


def examples_on(project: Path, owner_names: list[str]) -> dict[str, Any]:
    result = cli(project, [{"op": "example", "word": name} for name in owner_names])
    summaries = [test_summary(response) for response in result.get("responses", [])]
    return {
        "exitCode": result.get("exitCode"),
        "timedOut": result.get("timedOut"),
        "parseErrors": result.get("parseErrors", []),
        "ownerCount": len(owner_names),
        "owners": owner_names,
        "summaries": summaries,
        "allPassed": (
            result.get("exitCode") == 0
            and not result.get("timedOut")
            and not result.get("parseErrors")
            and len(summaries) == len(owner_names)
            and all(item["ok"] and not item["failed"] for item in summaries)
        ),
        "stdout": result.get("stdout"),
        "stderr": result.get("stderr"),
    }


def make_run_id() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + "-" + uuid.uuid4().hex[:8]


def write_receipt(run_root: Path, receipt: dict[str, Any]) -> None:
    receipt["finishedAtUtc"] = datetime.now(timezone.utc).isoformat()
    write_json(run_root / "receipt.json", receipt)


def run_positive(arm: str, pins: dict[str, Any], run_id: str) -> tuple[Path, bool]:
    actor = ACTOR_ROOT / arm
    expected_fingerprint = re.search(
        rf"(?im)^{('Reset' if arm == 'reset' else 'Retained')} terminal fingerprint:\s*([0-9a-f]{{64}})",
        (EXPERIMENT / "PLAN.md").read_text(encoding="utf-8"),
    )
    if not expected_fingerprint:
        raise RuntimeError(f"missing {arm} terminal fingerprint in PLAN.md")
    actor_before = project_fingerprint(actor)
    if actor_before != expected_fingerprint.group(1):
        raise RuntimeError(f"{arm} source actor fingerprint does not match PLAN.md: {actor_before}")

    run_root = CONTROLS / arm / f"positive-{run_id}"
    run_root.mkdir(parents=True, exist_ok=False)
    project = run_root / "project"
    shutil.copytree(actor, project)
    copied_fingerprint = project_fingerprint(project)
    if copied_fingerprint != actor_before:
        raise RuntimeError(f"{arm} disposable copy fingerprint mismatch: {copied_fingerprint} != {actor_before}")

    receipt: dict[str, Any] = {
        "arm": arm,
        "control": "positive-public-interface-migration",
        "status": "running",
        "pinnedRuntime": pins,
        "sourceActor": str(actor),
        "sourceActorFingerprintBefore": actor_before,
        "sourceActorFingerprintExpected": expected_fingerprint.group(1),
        "project": str(project),
        "projectFingerprintBefore": copied_fingerprint,
        "cliInvocation": ["dotnet", str(RUNTIME / "AgentLang.Cli.dll"), "--project", str(project), "--filesystem", "virtual", "--jsonl"],
        "grantedCapabilities": [],
        "publicListenerOrExternalService": False,
        "scopeDeviation": "Earlier read-only describe/test-all calls were made against the accepted source actor before the copies existed; no define, commit, or source write was issued, and exact full-project fingerprints remain unchanged. All runs recorded here use disposable control copies.",
    }
    try:
        initial_tests = cli(project, [{"op": "test-all"}])
        write_json(run_root / "baseline-test-all.json", initial_tests)
        receipt["baselineTestAll"] = test_summary(one_response(initial_tests, 0, "baseline test-all"))
        if not receipt["baselineTestAll"]["ok"] or receipt["baselineTestAll"]["failed"]:
            raise RuntimeError(f"{arm} copied baseline suite failed before migration")

        original_dictionary = (actor / "dictionary.agent").read_text(encoding="utf-8")
        example_owners = source_owner_names(original_dictionary)
        receipt["baselineExampleOwnerCount"] = len(example_owners)
        receipt["baselineExamples"] = examples_on(project, example_owners)
        write_json(run_root / "baseline-examples.json", receipt["baselineExamples"])
        if len(example_owners) != 39 or not receipt["baselineExamples"]["allPassed"]:
            raise RuntimeError(f"{arm} copied baseline examples did not pass for exactly 39 owners")

        description_requests = [
            {"op": "describe", "word": "subscription.renew-batch"},
            {"op": "describe", "word": "subscription.renew-batch-step"},
            {"op": "describe", "type": "SubscriptionRenewalState" if arm == "reset" else "SubscriptionRenewalBatchState"},
        ]
        described = cli(project, description_requests)
        assert_cli_ok(described, f"{arm} pre-migration descriptions")
        root_description = one_response(described, 0, f"{arm} root description")["data"]
        step_description = one_response(described, 1, f"{arm} step description")["data"]
        type_description = one_response(described, 2, f"{arm} state type description")["data"]
        receipt["preMigrationInterface"] = {
            "root": {key: root_description.get(key) for key in ("id", "revision", "outputs", "maturity", "status")},
            "step": {key: step_description.get(key) for key in ("id", "revision", "inputs", "outputs", "maturity", "status")},
            "stateType": {key: type_description.get(key) for key in ("name", "revision", "sourceHash")},
        }

        source, adaptation_receipt = migration_source(arm, actor)
        (run_root / "migration.flow").write_text(source, encoding="utf-8")
        write_json(run_root / "test-adaptation.json", adaptation_receipt)
        migration_request = {
            "op": "define",
            "frontend": "flow",
            "syntaxVersion": 2,
            "source": source,
            "replace": True,
            "expectedTypeSources": {type_description["name"]: type_description["sourceHash"]},
            "expectedRevisions": {
                root_description["name"]: root_description["revision"],
                step_description["name"]: step_description["revision"],
            },
        }
        # Candidate groups are scoped to one JSONL CLI session. Keep the
        # untargeted group commit in the same process that staged the define.
        defined = cli(project, [migration_request, {"op": "test-all"}, {"op": "commit", "library": True}])
        write_json(run_root / "define-and-test-all.json", defined)
        receipt["defineResponse"] = one_response(defined, 0, f"{arm} define response")
        receipt["stagedTestAll"] = test_summary(one_response(defined, 1, f"{arm} staged test-all")) if len(defined.get("responses", [])) > 1 else None
        if defined.get("exitCode") != 0 or defined.get("timedOut") or len(defined.get("responses", [])) != 3:
            raise RuntimeError(f"{arm} define/test/commit CLI process did not complete normally")
        if receipt["defineResponse"].get("ok") is not True:
            raise RuntimeError(f"{arm} public-interface define rejected: {receipt['defineResponse']}")
        if not receipt["stagedTestAll"]["ok"] or receipt["stagedTestAll"]["failed"]:
            raise RuntimeError(f"{arm} staged test suite did not pass")

        commits = dict(defined)
        commits["commitResponseIndex"] = 2
        write_json(run_root / "library-commits.json", commits)
        assert_cli_ok(commits, f"{arm} library commits")
        receipt["commitResponses"] = [one_response(defined, 2, f"{arm} atomic library group commit")]

        final_queries = [
            {"op": "describe", "word": "subscription.renew-batch"},
            {"op": "describe", "word": "subscription.renew-batch-step"},
            {"op": "describe", "type": "SubscriptionRenewalFailure"},
            {"op": "source", "type": "SubscriptionRenewalFailure"},
        ]
        final_inspection = cli(project, final_queries)
        assert_cli_ok(final_inspection, f"{arm} final interface inspection")
        final_root = one_response(final_inspection, 0, f"{arm} final root")["data"]
        final_step = one_response(final_inspection, 1, f"{arm} final step")["data"]
        final_failure_type = one_response(final_inspection, 2, f"{arm} final failure type")["data"]
        final_failure_source = one_response(final_inspection, 3, f"{arm} final failure source").get("data")
        receipt["postMigrationInterface"] = {
            "root": {key: final_root.get(key) for key in ("id", "revision", "outputs", "maturity", "status")},
            "step": {key: final_step.get(key) for key in ("id", "revision", "inputs", "outputs", "maturity", "status")},
            "failureType": {key: final_failure_type.get(key) for key in ("name", "kind", "status", "sourceHash")},
            "failureTypeSource": final_failure_source,
        }
        if (
            final_root.get("outputs") != [EXPECTED_OUTPUT]
            or final_root.get("id") != root_description.get("id")
            or final_root.get("maturity") != "library"
            or final_step.get("id") != step_description.get("id")
            or final_step.get("maturity") != "library"
            or final_failure_type.get("kind") != "type"
            or not isinstance(final_failure_source, str)
            or not all(field in final_failure_source for field in ("field index: Int", "field old-id: SubscriptionId", "field cause: BusinessError"))
        ):
            raise RuntimeError(f"{arm} final public interface, identity, maturity, or nominal fields failed inspection")

        persisted_suite = cli(project, [{"op": "test-all"}])
        write_json(run_root / "persisted-test-all.json", persisted_suite)
        receipt["persistedTestAll"] = test_summary(one_response(persisted_suite, 0, f"{arm} persisted test-all"))
        if not receipt["persistedTestAll"]["ok"] or receipt["persistedTestAll"]["failed"]:
            raise RuntimeError(f"{arm} persisted test suite failed after library commit")
        receipt["persistedExamples"] = examples_on(project, example_owners)
        write_json(run_root / "persisted-examples.json", receipt["persistedExamples"])
        if not receipt["persistedExamples"]["allPassed"]:
            raise RuntimeError(f"{arm} persisted examples failed after library commit")

        receipt["controlProjectFingerprintAfter"] = project_fingerprint(project)
        receipt["sourceActorFingerprintAfter"] = project_fingerprint(actor)
        receipt["sourceActorUnchanged"] = receipt["sourceActorFingerprintAfter"] == actor_before
        receipt["status"] = "positive-migration-passed"
        write_receipt(run_root, receipt)
        return project, True
    except Exception as exc:
        receipt["status"] = "positive-migration-failed"
        receipt["error"] = f"{type(exc).__name__}: {exc}"
        receipt["sourceActorFingerprintAfter"] = project_fingerprint(actor)
        receipt["sourceActorUnchanged"] = receipt["sourceActorFingerprintAfter"] == actor_before
        write_receipt(run_root, receipt)
        return project, False


def replace_function(source: str, name: str, replacement: str) -> str:
    match = re.search(rf"(?m)^fn\s+{re.escape(name)}\s*\(", source)
    if not match:
        raise ValueError(f"Flow function not found: {name}")
    opening = source.find("{", match.start())
    end = scan_brace_block(source, opening)
    return source[:match.start()] + replacement.strip() + source[end:]


RESET_LAST_ERROR_STEP = '''fn subscription.renew-batch-step(state: SubscriptionRenewalState, change: SubscriptionRenewal) -> SubscriptionRenewalState {
    doc "Continue after failures, preserving the last failure and last successfully staged Store."

    let next-index = add(state.index, 1)
    let term = if change.plan == SubscriptionPlan.monthly() {
        "monthly"
    } else {
        "annual"
    }
    match store.subscription(change.old-id, state.store) {
        some existing => {
            match subscription.cancel(state.store, change.old-id, state.at) {
                ok ended => {
                    match subscription.start(ended, change.new-id, existing.customer-id, existing.product-id, term, state.at, change.expires-at) {
                        ok renewed => subscriptionRenewalState.new(at = state.at, store = renewed, failure = state.failure, index = next-index)
                        error problem => subscriptionRenewalState.new(at = state.at, store = state.store, failure = option.some<SubscriptionRenewalFailure>(subscriptionRenewalFailure.new(index = state.index, old-id = change.old-id, cause = problem)), index = next-index)
                    }
                }
                error problem => subscriptionRenewalState.new(at = state.at, store = state.store, failure = option.some<SubscriptionRenewalFailure>(subscriptionRenewalFailure.new(index = state.index, old-id = change.old-id, cause = problem)), index = next-index)
            }
        }
        none => subscriptionRenewalState.new(at = state.at, store = state.store, failure = option.some<SubscriptionRenewalFailure>(subscriptionRenewalFailure.new(index = state.index, old-id = change.old-id, cause = businessError.new(code = "SUBSCRIPTION_NOT_FOUND", message = "The subscription does not exist."))), index = next-index)
    }
}'''


RETAINED_LAST_ERROR_STEP = '''fn subscription.renew-batch-step(state: SubscriptionRenewalBatchState, change: SubscriptionRenewal) -> SubscriptionRenewalBatchState {
    doc "Continue after failures, replacing the failure on later errors and retaining the last staged Store."

    let next-index = add(state.index, 1)
    let current-store = state.staged
    let attempt = if change.plan == SubscriptionPlan.monthly() {
        subscription.renew-monthly(current-store, change.old-id, change.new-id, state.at, change.expires-at)
    } else {
        subscription.renew-annual(current-store, change.old-id, change.new-id, state.at, change.expires-at)
    }
    match attempt {
        ok updated-store => {
            match state.outcome {
                error earlier-failure => subscriptionRenewalBatchState.new(outcome = result.error<Store, SubscriptionRenewalFailure>(earlier-failure), at = state.at, staged = updated-store, index = next-index)
                ok previous-store => subscriptionRenewalBatchState.new(outcome = result.ok<Store, SubscriptionRenewalFailure>(updated-store), at = state.at, staged = updated-store, index = next-index)
            }
        }
        error problem => subscriptionRenewalBatchState.new(outcome = result.error<Store, SubscriptionRenewalFailure>(subscriptionRenewalFailure.new(index = state.index, old-id = change.old-id, cause = problem)), at = state.at, staged = state.staged, index = next-index)
    }
}'''


def mutate_fault_source(source: str, arm: str, mutation: str) -> str:
    if mutation == "always-zero-index":
        changed, count = re.subn(
            r"subscriptionRenewalFailure\.new\(index = state\.index,",
            "subscriptionRenewalFailure.new(index = 0,",
            source,
        )
        if count < 1:
            raise ValueError(f"{arm} source had no indexed failure construction to mutate")
        return changed
    if mutation == "last-error-wins":
        step = RESET_LAST_ERROR_STEP if arm == "reset" else RETAINED_LAST_ERROR_STEP
        return replace_function(source, "subscription.renew-batch-step", step)
    raise ValueError(f"unknown fault mutation: {mutation}")


def load_failure_scorer():
    oracle = EXPERIMENT / "oracle"
    module_name = "typed_batch_failure_216_preflight_score_failure"
    spec = importlib.util.spec_from_file_location(module_name, oracle / "score_failure.py")
    if spec is None or spec.loader is None:
        raise ImportError("cannot load the typed failure scorer")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    old_path = list(sys.path)
    sys.path.insert(0, str(oracle))
    try:
        spec.loader.exec_module(module)
    finally:
        sys.path[:] = old_path
    return module


def mutated_expected_projection(scoring, item: dict[str, Any], mutation: str) -> dict[str, Any]:
    mutated_case = dict(item)
    mutated_case["expected"] = scoring.MODEL.batch_expected(item, mutation)
    return scoring.expected_projection(mutated_case)


def fault_gate_checks(receipt: dict[str, Any], case_count: int, predicted_correct: int) -> dict[str, bool]:
    behavior = receipt.get("behavior", {})
    return {
        "candidateDefineSucceeded": receipt.get("candidateDefineSucceeded") is True,
        "cliExitZero": receipt.get("cliExitCode") == 0,
        "responseCountExact": receipt.get("actualResponseCount") == receipt.get("expectedResponseCount"),
        "notTimedOut": receipt.get("cliTimedOut") is False,
        "parseErrorsAbsent": not receipt.get("cliParseErrors"),
        "allCasesExecuted": behavior.get("executedCaseCount") == case_count,
        "caseSetupErrorsAbsent": not behavior.get("setupErrors"),
        "allCasesMatchFaultPrediction": behavior.get("matchesFaultPredictionCount") == case_count,
        "correctBehaviorCountMatchesModel": behavior.get("matchesCorrectBehaviorCount") == predicted_correct,
        "controlProjectUnchanged": receipt.get("controlProjectUnchanged") is True,
        "positiveProjectUnchanged": receipt.get("positiveProjectUnchanged") is True,
        "sourceActorUnchanged": receipt.get("sourceActorUnchanged") is True,
    }


def fault_gate_self_checks() -> dict[str, Any]:
    sample = {
        "candidateDefineSucceeded": True,
        "cliExitCode": 0,
        "actualResponseCount": 11,
        "expectedResponseCount": 11,
        "cliTimedOut": False,
        "cliParseErrors": [],
        "behavior": {
            "executedCaseCount": 9,
            "setupErrors": [],
            "matchesFaultPredictionCount": 9,
            "matchesCorrectBehaviorCount": 6,
        },
        "controlProjectUnchanged": True,
        "positiveProjectUnchanged": True,
        "sourceActorUnchanged": True,
    }
    checks = {"acceptsCompleteNineOfNineFaultObservations": all(fault_gate_checks(sample, 9, 6).values())}
    variants = {
        "rejectsNonzeroExit": {"cliExitCode": 1},
        "rejectsMissingResponse": {"actualResponseCount": 10},
        "rejectsTimeout": {"cliTimedOut": True},
        "rejectsParseError": {"cliParseErrors": ["malformed JSONL"]},
        "rejectsSetupError": {"behavior": {**sample["behavior"], "setupErrors": [{"caseId": "case-1"}]}},
        "rejectsMissingCase": {"behavior": {**sample["behavior"], "executedCaseCount": 8}},
        "rejectsWrongFaultPredictionCount": {"behavior": {**sample["behavior"], "matchesFaultPredictionCount": 8}},
        "rejectsWrongCorrectBehaviorCount": {"behavior": {**sample["behavior"], "matchesCorrectBehaviorCount": 7}},
    }
    for name, changes in variants.items():
        variant = {**sample, **changes}
        checks[name] = not all(fault_gate_checks(variant, 9, 6).values())
    return {"passed": all(checks.values()), "checks": checks}


def run_fault(arm: str, mutation: str, pins: dict[str, Any], positive_project: Path,
              positive_flow: Path, run_id: str, scoring) -> tuple[Path, dict[str, Any]]:
    source_actor = ACTOR_ROOT / arm
    expected_fingerprint = re.search(
        rf"(?im)^{('Reset' if arm == 'reset' else 'Retained')} terminal fingerprint:\s*([0-9a-f]{{64}})",
        (EXPERIMENT / "PLAN.md").read_text(encoding="utf-8"),
    )
    if not expected_fingerprint:
        raise RuntimeError(f"missing {arm} terminal fingerprint in PLAN.md")
    source_fingerprint = project_fingerprint(source_actor)
    if source_fingerprint != expected_fingerprint.group(1):
        raise RuntimeError(f"{arm} source actor fingerprint changed before fault calibration")

    run_root = CONTROLS / arm / f"fault-{mutation}-{run_id}"
    run_root.mkdir(parents=True, exist_ok=False)
    project = run_root / "project"
    shutil.copytree(positive_project, project)
    project_before = project_fingerprint(project)
    positive_fingerprint = project_fingerprint(positive_project)
    if project_before != positive_fingerprint:
        raise RuntimeError(f"{arm} {mutation} fault copy did not match the positive actor")

    receipt: dict[str, Any] = {
        "arm": arm,
        "control": mutation,
        "status": "running",
        "purpose": "behavior-only calibration on an ephemeral public-CLI candidate",
        "publishedToLibrary": False,
        "commitAttempted": False,
        "pinnedRuntime": pins,
        "sourceActor": str(source_actor),
        "sourceActorFingerprintBefore": source_fingerprint,
        "expectedSourceActorFingerprint": expected_fingerprint.group(1),
        "positiveProject": str(positive_project.resolve()),
        "positiveProjectFingerprint": positive_fingerprint,
        "project": str(project.resolve()),
        "projectFingerprintBefore": project_before,
        "grantedCapabilities": [],
        "publicListenerOrExternalService": False,
    }
    try:
        root_description_result = cli(project, [
            {"op": "describe", "word": "subscription.renew-batch"},
            {"op": "describe", "word": "subscription.renew-batch-step"},
            {"op": "describe", "type": "SubscriptionRenewalState" if arm == "reset" else "SubscriptionRenewalBatchState"},
            {"op": "describe", "type": "SubscriptionRenewalFailure"},
        ])
        assert_cli_ok(root_description_result, f"{arm} {mutation} public descriptions")
        root = one_response(root_description_result, 0, "fault root description")["data"]
        step = one_response(root_description_result, 1, "fault step description")["data"]
        state = one_response(root_description_result, 2, "fault state description")["data"]
        failure_type = one_response(root_description_result, 3, "fault failure type description")["data"]
        source = mutate_fault_source(positive_flow.read_text(encoding="utf-8"), arm, mutation)
        (run_root / "fault.flow").write_text(source, encoding="utf-8")

        cases = scoring.MODEL.make_cases()
        define_request = {
            "op": "define",
            "frontend": "flow",
            "syntaxVersion": 2,
            "source": source + "\n\n" + scoring.typed_observer_source(False),
            "replace": True,
            "expectedTypeSources": {
                state["name"]: state["sourceHash"],
                failure_type["name"]: failure_type["sourceHash"],
            },
            "expectedRevisions": {root["name"]: root["revision"], step["name"]: step["revision"]},
        }
        requests = [define_request, {"op": "test-all"}]
        requests.extend({
            "op": "eval",
            "frontend": "flow",
            "syntaxVersion": 2,
            "structured": True,
            "code": scoring.observation_expression(item),
        } for item in cases)
        raw = cli(project, requests)
        write_json(run_root / "fault-cli.json", raw)
        responses = raw.get("responses", [])
        define_response = responses[0] if len(responses) > 0 and isinstance(responses[0], dict) else {}
        test_response = responses[1] if len(responses) > 1 and isinstance(responses[1], dict) else {}
        fault_suite = test_summary(test_response)
        write_json(run_root / "fault-test-all.json", test_response)

        results = []
        for index, item in enumerate(cases):
            response_index = index + 2
            response = responses[response_index] if response_index < len(responses) else {}
            result: dict[str, Any] = {"caseId": item["id"], "executed": False, "matchesFaultPrediction": False, "matchesCorrectBehavior": False}
            try:
                if not isinstance(response, dict) or response.get("ok") is not True or response.get("kind") != "eval":
                    raise ValueError(f"missing structured eval response: {response}")
                structured = response.get("data", {}).get("structuredStack", {})
                values = structured.get("values", []) if isinstance(structured, dict) else []
                if len(values) != 1:
                    raise ValueError("eval did not return exactly one structured observation")
                actual = scoring.validate_typed_observation_node(values[0])
                expected_fault = mutated_expected_projection(scoring, item, mutation)
                expected_correct = scoring.expected_projection(item)
                result.update(
                    executed=True,
                    matchesFaultPrediction=(actual == expected_fault),
                    matchesCorrectBehavior=(actual == expected_correct),
                    expectedFault=expected_fault,
                    actual=actual,
                )
            except Exception as error:
                result["setupError"] = f"{type(error).__name__}: {error}"
            results.append(result)
        write_json(run_root / "fault-case-results.json", results)

        receipt.update({
            "candidateDefineResponse": define_response,
            "candidateDefineSucceeded": define_response.get("ok") is True,
            "testAll": fault_suite,
            "testAllGatePassed": fault_suite["ok"] and not fault_suite["failed"],
            "expectedResponseCount": len(requests),
            "actualResponseCount": len(responses),
            "cliExitCode": raw.get("exitCode"),
            "cliTimedOut": raw.get("timedOut"),
            "cliParseErrors": raw.get("parseErrors", []),
            "behavior": {
                "caseCount": len(cases),
                "executedCaseCount": sum(row["executed"] for row in results),
                "matchesFaultPredictionCount": sum(row["executed"] and row["matchesFaultPrediction"] for row in results),
                "matchesCorrectBehaviorCount": sum(row["executed"] and row["matchesCorrectBehavior"] for row in results),
                "faultPredictionFailureIds": [row["caseId"] for row in results if row["executed"] and not row["matchesFaultPrediction"]],
                "correctBehaviorFailureIds": [row["caseId"] for row in results if row["executed"] and not row["matchesCorrectBehavior"]],
                "setupErrors": [row for row in results if not row["executed"]],
                "results": results,
            },
            "modelChecks": scoring.MODEL.model_checks(),
            "projectFingerprintAfter": project_fingerprint(project),
            "controlProjectUnchanged": project_fingerprint(project) == project_before,
            "positiveProjectFingerprintAfter": project_fingerprint(positive_project),
            "positiveProjectUnchanged": project_fingerprint(positive_project) == positive_fingerprint,
            "sourceActorFingerprintAfter": project_fingerprint(source_actor),
            "sourceActorUnchanged": project_fingerprint(source_actor) == source_fingerprint,
        })
        predicted_correct = scoring.MODEL.model_checks()["alwaysZeroIndex" if mutation == "always-zero-index" else "lastErrorWins"]["predictedPassed"]
        receipt["predictedFaultPredictionCount"] = len(cases)
        gate_checks = fault_gate_checks(receipt, len(cases), predicted_correct)
        receipt["calibrationGateChecks"] = gate_checks
        receipt["predictedCorrectBehaviorCount"] = predicted_correct
        receipt["status"] = "fault-behavior-calibrated" if (
            all(gate_checks.values())
        ) else "fault-behavior-calibration-failed"
        write_receipt(run_root, receipt)
        return project, receipt
    except Exception as exc:
        receipt["status"] = "fault-behavior-calibration-failed"
        receipt["error"] = f"{type(exc).__name__}: {exc}"
        receipt["projectFingerprintAfter"] = project_fingerprint(project)
        receipt["controlProjectUnchanged"] = receipt["projectFingerprintAfter"] == project_before
        receipt["sourceActorFingerprintAfter"] = project_fingerprint(source_actor)
        receipt["sourceActorUnchanged"] = receipt["sourceActorFingerprintAfter"] == source_fingerprint
        write_receipt(run_root, receipt)
        return project, receipt


def latest_score_for_arm(arm: str) -> tuple[Path | None, dict[str, Any] | None]:
    score_root = CONTROLS / arm / arm
    score_paths = list(score_root.glob("*/score.json")) if score_root.is_dir() else []
    if not score_paths:
        return None, None
    path = max(score_paths, key=lambda item: item.stat().st_mtime_ns)
    return path, load_json(path)


def failed_preparation_attempts(arm: str) -> list[dict[str, Any]]:
    attempts = []
    arm_root = CONTROLS / arm
    for receipt_path in sorted(arm_root.rglob("receipt.json")):
        receipt = load_json(receipt_path)
        if "failed" not in str(receipt.get("status", "")):
            continue
        run_root = receipt_path.parent
        cli_error = None
        commit_path = run_root / "library-commits.json"
        if commit_path.is_file():
            commit = load_json(commit_path)
            responses = commit.get("responses", [])
            if responses and isinstance(responses[0], dict):
                cli_error = responses[0].get("error")
        cli_error = cli_error or receipt.get("candidateDefineResponse", {}).get("error") or receipt.get("defineResponse", {}).get("error")
        attempts.append({
            "receiptPath": str(receipt_path.resolve()),
            "control": receipt.get("control"),
            "status": receipt.get("status"),
            "errorCode": cli_error.get("code") if isinstance(cli_error, dict) else None,
            "error": receipt.get("error") or (cli_error.get("message") if isinstance(cli_error, dict) else None),
            "executedCases": receipt.get("behavior", {}).get("executedCaseCount"),
            "setupErrorCount": len(receipt.get("behavior", {}).get("setupErrors", [])),
            "sourceActorUnchanged": receipt.get("sourceActorUnchanged"),
        })
    return attempts


def enrich_fault_summary(run_id: str, positive_run_id: str, pins: dict[str, Any]) -> dict[str, Any]:
    summaries = {}
    for arm in ARM_NAMES:
        path = CONTROLS / arm / f"fault-calibration-summary-{run_id}.json"
        if not path.is_file():
            continue
        summary = load_json(path)
        score_path, score = latest_score_for_arm(arm)
        if score:
            summary["positiveScorePath"] = str(score_path.resolve()) if score_path else None
            summary["positiveScore"] = {
                "status": score.get("status"),
                "automatedPassed": score.get("automatedPassed"),
                "testAllPassed": score.get("suite", {}).get("testAllPassed"),
                "allCurrentExamplesPassed": score.get("suite", {}).get("examplesPassed"),
                "batchExecuted": score.get("behavior", {}).get("executedCaseCount"),
                "batchPassed": score.get("behavior", {}).get("passedCaseCount"),
                "retainedCreatorExecuted": score.get("retainedCreatorBehavior", {}).get("executedCaseCount", 0),
                "retainedCreatorPassed": score.get("retainedCreatorBehavior", {}).get("passedCaseCount", 0),
            }
        summary["pinnedRuntime"] = pins
        summary["positiveRunId"] = positive_run_id
        summary["failedPreparationAttempts"] = failed_preparation_attempts(arm)
        summary["originalActorFingerprintAfter"] = project_fingerprint(ACTOR_ROOT / arm)
        summary["originalActorUnchanged"] = (
            summary.get("originalActorFingerprintBefore") == summary["originalActorFingerprintAfter"]
        )
        write_json(path, summary)
        summaries[arm] = summary
    return summaries


def run_fault_calibration(positive_projects: dict[str, Path], positive_run_id: str,
                          pins: dict[str, Any], scoring, run_id: str) -> dict[str, Any]:
    mutations = ("always-zero-index", "last-error-wins")
    gate_self_checks = fault_gate_self_checks()
    if not gate_self_checks["passed"]:
        raise RuntimeError(f"fault calibration gate self-checks failed: {gate_self_checks}")
    summary: dict[str, Any] = {
        "runId": run_id,
        "positiveRunId": positive_run_id,
        "pinnedRuntime": pins,
        "faultGateSelfChecks": gate_self_checks,
        "originalPathReadOnlyProbeDeviation": (
            "Earlier read-only describe/test-all calls were made against accepted 213 actor paths before copies existed. "
            "No define, commit, or source write targeted those paths; exact full-project actor fingerprints remain unchanged."
        ),
        "arms": {},
    }
    faults_failed = False
    for arm in ARM_NAMES:
        positive_root = CONTROLS / arm / f"positive-{positive_run_id}"
        positive_receipt = load_json(positive_root / "receipt.json")
        positive_project = positive_projects.get(arm) or positive_root / "project"
        if positive_receipt.get("status") != "positive-migration-passed":
            raise RuntimeError(f"cannot calibrate {arm}: positive receipt is not passed")
        score_path, score = latest_score_for_arm(arm)
        arm_summary: dict[str, Any] = {
            "positiveMigrationReceipt": str((positive_root / "receipt.json").resolve()),
            "positiveProject": str(positive_project.resolve()),
            "positiveProjectFingerprint": project_fingerprint(positive_project),
            "positiveScorePath": str(score_path.resolve()) if score_path else None,
            "positiveScore": {
                "status": score.get("status") if score else None,
                "automatedPassed": score.get("automatedPassed") if score else None,
                "testAllPassed": score.get("suite", {}).get("testAllPassed") if score else None,
                "allCurrentExamplesPassed": score.get("suite", {}).get("examplesPassed") if score else None,
                "batchExecuted": score.get("behavior", {}).get("executedCaseCount") if score else None,
                "batchPassed": score.get("behavior", {}).get("passedCaseCount") if score else None,
                "retainedCreatorExecuted": score.get("retainedCreatorBehavior", {}).get("executedCaseCount") if score else 0,
                "retainedCreatorPassed": score.get("retainedCreatorBehavior", {}).get("passedCaseCount") if score else 0,
            },
            "originalActorFingerprintBefore": project_fingerprint(ACTOR_ROOT / arm),
            "failedPreparationAttempts": failed_preparation_attempts(arm),
            "faults": {},
        }
        for mutation in mutations:
            _, receipt = run_fault(
                arm, mutation, pins, positive_project, positive_root / "migration.flow", run_id, scoring,
            )
            results = receipt.get("behavior", {})
            arm_summary["faults"][mutation] = {
                "receiptPath": str((CONTROLS / arm / f"fault-{mutation}-{run_id}" / "receipt.json").resolve()),
                "status": receipt.get("status"),
                "publishedToLibrary": receipt.get("publishedToLibrary"),
                "candidateDefineSucceeded": receipt.get("candidateDefineSucceeded"),
                "testAllGatePassed": receipt.get("testAllGatePassed"),
                "expected": results.get("caseCount"),
                "executed": results.get("executedCaseCount"),
                "matchedFaultPrediction": results.get("matchesFaultPredictionCount"),
                "matchedCorrectBehavior": results.get("matchesCorrectBehaviorCount"),
                "predictedCorrectBehavior": receipt.get("predictedCorrectBehaviorCount"),
                "faultPredictionFailureIds": results.get("faultPredictionFailureIds", []),
                "setupErrors": results.get("setupErrors", []),
                "calibrationGateChecks": receipt.get("calibrationGateChecks", {}),
                "projectFingerprintBefore": receipt.get("projectFingerprintBefore"),
                "projectFingerprintAfter": receipt.get("projectFingerprintAfter"),
                "controlProjectUnchanged": receipt.get("controlProjectUnchanged"),
                "sourceActorFingerprintBefore": receipt.get("sourceActorFingerprintBefore"),
                "sourceActorFingerprintAfter": receipt.get("sourceActorFingerprintAfter"),
                "sourceActorUnchanged": receipt.get("sourceActorUnchanged"),
            }
            if receipt.get("status") != "fault-behavior-calibrated":
                faults_failed = True
        arm_summary["originalActorFingerprintAfter"] = project_fingerprint(ACTOR_ROOT / arm)
        arm_summary["originalActorUnchanged"] = (
            arm_summary["originalActorFingerprintBefore"] == arm_summary["originalActorFingerprintAfter"]
        )
        summary["arms"][arm] = arm_summary
        write_json(CONTROLS / arm / f"fault-calibration-summary-{run_id}.json", {
            "runId": run_id,
            "positiveRunId": positive_run_id,
            "pinnedRuntime": pins,
            "faultGateSelfChecks": gate_self_checks,
            "originalPathReadOnlyProbeDeviation": summary["originalPathReadOnlyProbeDeviation"],
            "arm": arm,
            **arm_summary,
        })
    summary["allFaultsCalibrated"] = not faults_failed
    return summary


def main() -> int:
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--positive-only", action="store_true", help="stop after both positive migration controls")
    parser.add_argument("--faults-only", action="store_true", help="calibrate faults from a previously passed positive run")
    parser.add_argument("--positive-run-id", default=None, help="reuse this completed positive run for --faults-only")
    parser.add_argument("--run-id", default=None, help="use a supplied unique run id; existing receipts are never overwritten")
    args = parser.parse_args()
    pins = verify_inputs()
    run_id = args.run_id or make_run_id()
    if args.faults_only:
        if not args.positive_run_id:
            parser.error("--faults-only requires --positive-run-id")
        scoring = load_failure_scorer()
        projects = {
            arm: CONTROLS / arm / f"positive-{args.positive_run_id}" / "project"
            for arm in ARM_NAMES
        }
        summary = run_fault_calibration(projects, args.positive_run_id, pins, scoring, run_id)
        print(json.dumps(summary, indent=2, ensure_ascii=False))
        return 0 if summary["allFaultsCalibrated"] else 1

    failures: list[str] = []
    results: dict[str, Any] = {"runId": run_id, "pins": pins, "arms": {}}
    projects: dict[str, Path] = {}
    for arm in ARM_NAMES:
        project, passed = run_positive(arm, pins, run_id)
        projects[arm] = project
        results["arms"][arm] = {"positivePassed": passed, "project": str(project)}
        if not passed:
            failures.append(arm)
    summary_path = CONTROLS / "control-run-summary.json"
    write_json(summary_path, results)
    print(json.dumps({"summary": str(summary_path), **results}, indent=2))
    if failures:
        print(f"positive controls failed: {', '.join(failures)}", file=sys.stderr)
        return 1
    if args.positive_only:
        return 0
    scoring = load_failure_scorer()
    fault_summary = run_fault_calibration(projects, run_id, pins, scoring, make_run_id())
    print(json.dumps(fault_summary, indent=2, ensure_ascii=False))
    return 0 if fault_summary["allFaultsCalibrated"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
