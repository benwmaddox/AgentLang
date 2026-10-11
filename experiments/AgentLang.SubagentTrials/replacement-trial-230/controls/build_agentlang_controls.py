"""Build disposable AgentLang controls and preservation-fault evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

REPO = Path(__file__).resolve().parents[4]
STUDY = REPO / ".agentlang" / "replacement-trial-230"
ORACLE = REPO / "experiments" / "AgentLang.SubagentTrials" / "replacement-trial-230" / "oracle"
sys.path.insert(0, str(ORACLE))
import score_language as scorer  # noqa: E402
sys.path.remove(str(ORACLE))

SEED = STUDY / "baselines" / "retained"
SETUP = STUDY / "setup.json"
CLI = STUDY / "runtime" / "agentlang" / "AgentLang.Cli.dll"
CALIBRATION = STUDY / "calibration"
EXPECTED_REPLACEMENTS = (
    "subscription.renew-monthly",
    "subscription.renew-batch-step",
    "subscription.renew-scheduled-batch-step",
)


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def sha_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def split_arguments(text: str) -> list[str]:
    return scorer._split_top_level_arguments(text)


def rewrite_monthly_test(source: str) -> tuple[str, int]:
    needle = "subscription.renew-monthly("
    output = []
    cursor = 0
    count = 0
    while True:
        start = source.find(needle, cursor)
        if start < 0:
            output.append(source[cursor:])
            break
        output.append(source[cursor:start])
        opening = start + len(needle) - 1
        arguments, end = scorer._call_arguments(source, opening)
        if len(arguments) == 5:
            store, old_id, new_id, at, expires = arguments
        elif len(arguments) == 4:
            store, old_id, new_id, timing = arguments
            values = scorer._timing_values(timing)
            if values is None:
                raise ValueError("Existing monthly test call does not construct RenewalTiming")
            at, expires = values
        else:
            raise ValueError(f"Expected five old or four new monthly arguments, found {len(arguments)}")
        output.append(
            f"subscription.renew-monthly({store}, {old_id}, {new_id}, "
            f"renewalTiming.new(at = {at}, expires-at = {expires}))"
        )
        cursor = end
        count += 1
    return "".join(output), count


def monthly_test_source() -> str:
    return '''test subscription.renew-monthly/grouped-time-preserves-fields {
    let baseline = business.seed(unit)
    let oldId = SubscriptionId.new(\"50000000-0000-0000-0000-000000000070\")
    let newId = SubscriptionId.new(\"50000000-0000-0000-0000-000000000071\")
    let customerId = CustomerId.new(\"10000000-0000-0000-0000-000000000001\")
    let productId = ProductId.new(\"20000000-0000-0000-0000-000000000001\")
    let startedAt = Instant.new(\"2026-01-02T12:00:00.0000000+00:00\")
    let oldExpiry = Instant.new(\"2026-02-01T12:00:00.0000000+00:00\")
    let at = Instant.new(\"2026-01-15T12:03:00.0000000+00:00\")
    let expiry = Instant.new(\"2026-03-16T12:00:00.0000000+00:00\")
    let original = subscription.new(id = oldId, customer-id = customerId, product-id = productId, term = \"legacy\", started-at = startedAt, expires-at = oldExpiry, status = SubscriptionStatus.new(\"active\"), cancelled-at = option.none<Instant>())
    let initial = store.with-subscriptions(baseline, list.singleton<Subscription>(original))
    let timing = renewalTiming.new(at = at, expires-at = expiry)
    let result = subscription.renew-monthly(initial, oldId, newId, timing)
    match result {
        error problem => false
        ok updated => match store.subscription(oldId, updated) {
            none => false
            some cancelled => match store.subscription(newId, updated) {
                none => false
                some replacement => bool.and(
                    equals(cancelled.cancelled-at, option.some<Instant>(at)),
                    bool.and(equals(replacement.started-at, at),
                    bool.and(equals(replacement.expires-at, expiry),
                    bool.and(equals(initial.subscriptions, list.singleton<Subscription>(original)),
                             equals(updated.customers, initial.customers)))))
            }
        }
    }
    => true
}'''


def source_for(project: Path, *, swapped: bool = False, monthly_only: bool = False) -> tuple[str, dict]:
    _, manifest, _ = scorer.current_manifest(project)
    current = scorer.current_revisions(manifest)
    selected = ("subscription.renew-monthly",) if monthly_only else EXPECTED_REPLACEMENTS
    definitions = {}
    tests = {}
    direct_call_count = 0
    for name in selected:
        row = current[name]
        definition = scorer._object_text(project, row["revision"]["definition"])
        owner_tests = [
            scorer._object_text(project, reference)
            for reference in row["revision"]["tests"]
        ]
        if name == "subscription.renew-monthly":
            definition = definition.replace(
                "newId: SubscriptionId, at: Instant, expiry: Instant",
                "newId: SubscriptionId, timing: RenewalTiming",
            )
            definition = definition.replace(
                "subscription.cancel(store, oldId, at)",
                "subscription.cancel(store, oldId, timing.at)" if not swapped
                else "subscription.cancel(store, oldId, timing.expires-at)",
            )
            definition = definition.replace(
                'subscription.start(cancelled-store, newId, existing.customer-id, existing.product-id, \"monthly\", at, expiry)',
                'subscription.start(cancelled-store, newId, existing.customer-id, existing.product-id, \"monthly\", timing.at, timing.expires-at)'
                if not swapped else
                'subscription.start(cancelled-store, newId, existing.customer-id, existing.product-id, \"monthly\", timing.expires-at, timing.at)',
            )
            if "timing.at" not in definition or "timing.expires-at" not in definition:
                raise RuntimeError("Monthly helper did not migrate both timing fields")
        elif name == "subscription.renew-batch-step":
            old = "subscription.renew-monthly(current-store, change.old-id, change.new-id, state.at, change.expires-at)"
            new = (
                "subscription.renew-monthly(current-store, change.old-id, change.new-id, "
                "renewalTiming.new(at = state.at, expires-at = change.expires-at))"
            )
            definition = definition.replace(old, new)
        else:
            old = (
                "subscription.renew-monthly(current-store, scheduled.renewal.old-id, "
                "scheduled.renewal.new-id, scheduled.at, scheduled.renewal.expires-at)"
            )
            new = (
                "subscription.renew-monthly(current-store, scheduled.renewal.old-id, "
                "scheduled.renewal.new-id, "
                "renewalTiming.new(at = scheduled.at, expires-at = scheduled.renewal.expires-at))"
            )
            definition = definition.replace(old, new)
        definitions[name] = definition

        if name == "subscription.renew-monthly" and not monthly_only:
            updated_tests = []
            for source in owner_tests:
                revised, count = rewrite_monthly_test(source)
                direct_call_count += count
                updated_tests.append(revised)
            owner_tests = updated_tests
            owner_tests.append(monthly_test_source())
        elif name == "subscription.renew-scheduled-batch-step":
            updated_tests = []
            for source in owner_tests:
                revised, count = rewrite_monthly_test(source)
                direct_call_count += count
                updated_tests.append(revised)
            owner_tests = updated_tests
        tests[name] = owner_tests

    source_sections = ["// frontend: flow/2"]
    source_sections.extend(definitions[name].strip() for name in selected)
    for name in selected:
        source_sections.extend(item.strip() for item in tests[name])
    expected_revisions = {
        name: current[name]["revision"]["revision"]
        for name in selected
    }
    return "\n\n".join(source_sections) + "\n", {
        "expectedRevisions": expected_revisions,
        "directMonthlyCallsMigrated": direct_call_count,
        "selectedWords": list(selected),
    }


def run_cli(project: Path, requests: list[dict], log_dir: Path, name: str) -> dict:
    command = [
        "dotnet", str(CLI), "--project", str(project),
        "--filesystem", "virtual", "--jsonl",
    ]
    payload = "\n".join(
        json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests
    ) + "\n"
    try:
        process = subprocess.run(
            command, cwd=REPO, input=payload, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=900, check=False,
        )
        stdout, stderr, exit_code = process.stdout, process.stderr, process.returncode
        timed_out = False
    except subprocess.TimeoutExpired as error:
        stdout = _decode(error.stdout)
        stderr = _decode(error.stderr)
        exit_code = None
        timed_out = True
    log_dir.mkdir(parents=True, exist_ok=True)
    (log_dir / f"{name}.requests.jsonl").write_text(payload, encoding="utf-8")
    (log_dir / f"{name}.stdout.log").write_text(stdout, encoding="utf-8")
    (log_dir / f"{name}.stderr.log").write_text(stderr, encoding="utf-8")
    responses = []
    parse_errors = []
    for index, line in enumerate(stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError("not an object")
            responses.append(value)
        except (json.JSONDecodeError, ValueError) as error:
            parse_errors.append({"line": index, "error": str(error), "text": line[:300]})
    report = {
        "command": command,
        "exitCode": exit_code,
        "timedOut": timed_out,
        "requestCount": len(requests),
        "responseCount": len(responses),
        "parseErrors": parse_errors,
        "responses": responses,
        "stdout": stdout,
        "stderr": stderr,
    }
    write_json(log_dir / f"{name}.result.json", report)
    return report


def _decode(value) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def define_type(project: Path, log_dir: Path, label: str) -> dict:
    request = {
        "op": "define",
        "frontend": "flow",
        "syntaxVersion": 2,
        "source": (
            "// frontend: flow/2\n"
            "record RenewalTiming {\n"
            "    field at: Instant\n"
            "    field expires-at: Instant\n"
            "}\n"
        ),
    }
    requests = [request, {"op": "commit", "library": True}]
    result = run_cli(project, requests, log_dir, f"{label}-type")
    if (result["exitCode"] != 0 or len(result["responses"]) != len(requests)
            or any(response.get("ok") is not True for response in result["responses"])):
        raise RuntimeError(f"RenewalTiming type persistence failed for {label}; inspect {log_dir}")
    return result


def build_correct(destination: Path) -> dict:
    if destination.exists():
        if scorer.project_fingerprint(destination) != scorer.project_fingerprint(SEED):
            raise RuntimeError(f"Refusing to reuse a changed control project: {destination}")
        label = f"{destination.parent.name}-retry"
    else:
        shutil.copytree(SEED, destination)
        label = destination.parent.name
    log_dir = destination.parent / "logs"
    type_result = define_type(destination, log_dir, label)
    source, adaptation = source_for(destination)
    _, manifest, _ = scorer.current_manifest(destination)
    current = scorer.current_revisions(manifest)
    request = {
        "op": "define",
        "frontend": "flow",
        "syntaxVersion": 2,
        "source": source,
        "replace": True,
        "expectedRevisions": adaptation["expectedRevisions"],
    }
    requests = [
        request,
        {"op": "test-all"},
        {"op": "commit", "library": True},
        {"op": "test-all"},
    ]
    report = run_cli(destination, requests, log_dir, f"{label}-migration")
    required_ok = (
        report["exitCode"] == 0
        and report["responseCount"] == len(requests)
        and all(response.get("ok") is True for response in report["responses"])
    )
    source_fingerprint = scorer.project_fingerprint(destination)
    result = {
        "status": "built" if required_ok else "failure",
        "project": str(destination),
        "typeDefine": type_result["responses"],
        "migration": report["responses"],
        "adaptation": adaptation,
        "baselineExpectedRevisions": {
            name: current[name]["revision"]["revision"] for name in EXPECTED_REPLACEMENTS
        },
        "projectFingerprint": source_fingerprint,
    }
    write_json(destination.parent / "build-summary.json", result)
    if not required_ok:
        raise RuntimeError(f"Correct AgentLang control failed; inspect {log_dir}")
    return result


def update_monthly_body_for_swap(project: Path, log_dir: Path) -> dict:
    _, manifest, _ = scorer.current_manifest(project)
    current = scorer.current_revisions(manifest)
    definition = scorer._object_text(project, current["subscription.renew-monthly"]["revision"]["definition"])
    swapped = definition.replace(
        "subscription.cancel(store, oldId, timing.at)",
        "subscription.cancel(store, oldId, timing.expires-at)",
    ).replace(
        'subscription.start(cancelled-store, newId, existing.customer-id, existing.product-id, \"monthly\", timing.at, timing.expires-at)',
        'subscription.start(cancelled-store, newId, existing.customer-id, existing.product-id, \"monthly\", timing.expires-at, timing.at)',
    )
    if swapped == definition:
        raise RuntimeError("Could not make timing swap in the current monthly helper")
    source = "// frontend: flow/2\n" + swapped.strip() + "\n"
    revision_before = current["subscription.renew-monthly"]["revision"]["revision"]
    request = {
        "op": "define",
        "frontend": "flow",
        "syntaxVersion": 2,
        "source": source,
        "replace": True,
        "expectedRevision": revision_before,
    }
    define = run_cli(project, [request], log_dir, "swap-stage")
    stage_accepted = (
        define["exitCode"] == 0 and len(define["responses"]) == 1
        and define["responses"][0].get("ok") is True
    )
    after_revision = scorer.current_revisions(scorer.current_manifest(project)[1])[
        "subscription.renew-monthly"
    ]["revision"]["revision"]
    if stage_accepted:
        write_json(project.parent / "swap-request.json", request)
    result = {
        "stageAccepted": stage_accepted,
        "defineResponse": define["responses"],
        "candidateRequestPath": str(project.parent / "swap-request.json") if stage_accepted else None,
        "revisionBefore": revision_before,
        "revisionAfter": after_revision,
        "behaviorCandidateNotPublished": after_revision == revision_before,
    }
    return result


def _manifest_bytes(value: dict) -> bytes:
    # Match the runtime's compact JSON serializer, including its escaping of plus.
    text = json.dumps(value, ensure_ascii=False, separators=(",", ":")).replace("+", "\\u002B")
    return text.encode("utf-8")


def mutate_current_test_object(project: Path, owner: str, test_name: str) -> dict:
    store = project / ".agentlang" / "store"
    current_pointer = json.loads((store / "CURRENT").read_text(encoding="utf-8-sig"))
    old_manifest_hash = current_pointer["manifestHash"]
    manifest_path = store / "manifests" / f"{current_pointer['manifestHash']}.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    owner_head = next(row for row in manifest["words"] if row["currentName"] == owner)
    revision = next(row for row in manifest["revisions"]
                    if row["wordId"] == owner_head["wordId"]
                    and row["revision"] == owner_head["currentRevision"])
    old_ref = None
    for ref in revision["tests"]:
        text = (store / "objects" / f"{ref['hash']}.agent").read_text(encoding="utf-8-sig")
        if scorer._test_name(text) == test_name:
            old_ref = ref
            old_text = text
            break
    if old_ref is None:
        raise RuntimeError(f"Could not find {owner}/{test_name} in current revision")
    old_assertion = "SUBSCRIPTION_ALREADY_CANCELLED"
    new_assertion = "SUBSCRIPTION_NOT_FOUND"
    if old_text.count(old_assertion) != 1:
        raise RuntimeError("Expected one inherited cancellation assertion to mutate")
    new_text = old_text.replace(old_assertion, new_assertion, 1)
    new_hash = sha_bytes(new_text.encode("utf-8"))
    object_path = store / "objects" / f"{new_hash}.agent"
    object_path.write_bytes(new_text.encode("utf-8"))
    old_hash = old_ref["hash"]
    old_ref["hash"] = new_hash
    for binding in revision.get("callBindings", []):
        if binding.get("source", {}).get("hash") == old_hash:
            binding["source"]["hash"] = new_hash
    updated_manifest = _manifest_bytes(manifest)
    new_manifest_hash = sha_bytes(updated_manifest)
    (store / "manifests" / f"{new_manifest_hash}.json").write_bytes(updated_manifest)
    current_pointer["generation"] += 1
    current_pointer["manifestHash"] = new_manifest_hash
    pointer_bytes = json.dumps(current_pointer, ensure_ascii=False, separators=(",", ":")).replace("+", "\\u002B")
    (store / "CURRENT").write_text(pointer_bytes, encoding="utf-8")
    return {
        "owner": owner,
        "testName": test_name,
        "oldTestObjectHash": old_hash,
        "newTestObjectHash": new_hash,
        "oldManifestHash": old_manifest_hash,
        "newManifestHash": new_manifest_hash,
        "mutatedAssertion": {"from": old_assertion, "to": new_assertion},
    }


def build_arity_fault(destination: Path) -> dict:
    if destination.exists():
        _, existing_manifest, _ = scorer.current_manifest(destination)
        _, seed_manifest, _ = scorer.current_manifest(SEED)
        existing_words = {row["wordId"]: row for row in existing_manifest["words"]}
        seed_words = {row["wordId"]: row for row in seed_manifest["words"]}
        existing_types = {row["name"] for row in existing_manifest["types"]}
        seed_types = {row["name"] for row in seed_manifest["types"]}
        if existing_words != seed_words or existing_types != seed_types | {scorer.TYPE_NAME}:
            raise RuntimeError(f"Refusing to reuse a changed arity control: {destination}")
        label = "arity-retry"
    else:
        shutil.copytree(SEED, destination)
        label = "arity"
    log_dir = destination.parent / "logs"
    if label == "arity":
        define_type(destination, log_dir, label)
    before_manifest_hash = json.loads(
        (destination / ".agentlang" / "store" / "CURRENT").read_text(encoding="utf-8-sig")
    )["manifestHash"]
    _, manifest, _ = scorer.current_manifest(destination)
    current = scorer.current_revisions(manifest)
    source, adaptation = source_for(destination)
    old_caller = (
        "subscription.renew-monthly(current-store, change.old-id, change.new-id, "
        "renewalTiming.new(at = state.at, expires-at = change.expires-at))"
    )
    new_caller = (
        "subscription.renew-monthly(current-store, change.old-id, change.new-id, "
        "state.at, change.expires-at)"
    )
    if source.count(old_caller) != 1:
        raise RuntimeError("Arity fault requires exactly one batch-step caller to remain at five arguments")
    source = source.replace(old_caller, new_caller, 1)
    request = {
        "op": "define",
        "frontend": "flow",
        "syntaxVersion": 2,
        "source": source,
        "replace": True,
        "expectedRevisions": adaptation["expectedRevisions"],
    }
    requests = [request, {"op": "test-all"}, {"op": "commit", "library": True}]
    run = run_cli(destination, requests, log_dir, f"{label}-reject")
    after_current = json.loads(
        (destination / ".agentlang" / "store" / "CURRENT").read_text(encoding="utf-8-sig")
    )
    after_manifest = scorer.current_revisions(scorer.current_manifest(destination)[1])
    revision_before = current["subscription.renew-monthly"]["revision"]["revision"]
    revision_after = after_manifest["subscription.renew-monthly"]["revision"]["revision"]
    errors = [row.get("error", {}) for row in run["responses"] if row.get("ok") is not True]
    typecheck_diagnostic = any(
        row.get("code") == "FLOW_ARGUMENT_ARITY"
        and "subscription.renew-monthly" in row.get("message", "")
        for row in errors
    )
    rejected = (
        run["exitCode"] == 0
        and typecheck_diagnostic
        and after_current["manifestHash"] == before_manifest_hash
        and revision_after == revision_before
    )
    report = {
        "status": "old-caller-arity-rejected" if rejected else "control-failure",
        "expectedGate": "typecheck rejects a five-argument production call to the new four-argument helper",
        "beforeGroupManifestHash": before_manifest_hash,
        "afterGroupManifestHash": after_current["manifestHash"],
        "monthlyRevisionBefore": revision_before,
        "monthlyRevisionAfter": revision_after,
        "atomicTargetNames": sorted(adaptation["expectedRevisions"]),
        "intentionallyUnmigratedProductionCaller": "subscription.renew-batch-step",
        "diagnostics": errors,
        "request": request,
        "responses": run["responses"],
        "responseCount": run["responseCount"],
        "rejectedBeforePublication": rejected,
    }
    write_json(destination.parent / "arity-gate.json", report)
    if not rejected:
        raise RuntimeError(f"Old-arity typecheck control did not reject the intended call; inspect {log_dir}")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, default=CALIBRATION)
    parser.add_argument("--skip-build", action="store_true",
                        help="Assume the correct AgentLang project already exists under output-root/ok/l")
    args = parser.parse_args()
    output = args.output_root if args.output_root.is_absolute() else REPO / args.output_root
    if not SEED.is_dir() or not CLI.is_file() or not SETUP.is_file():
        raise FileNotFoundError("230 retained seed, setup, or pinned AgentLang CLI is missing")
    project = output / "ok" / "l"
    if args.skip_build and not project.is_dir():
        raise FileNotFoundError(f"Correct AgentLang control is missing: {project}")
    if project.is_dir() and not args.skip_build:
        prior_summary_path = project.parent / "build-summary.json"
        prior_summary = json.loads(prior_summary_path.read_text(encoding="utf-8")) if prior_summary_path.is_file() else {}
        prior_fingerprint = scorer.project_fingerprint(project)
        prior_preservation = scorer.replacement_preservation_diff(SEED, project)
        if (prior_summary.get("status") == "built"
                and prior_summary.get("projectFingerprint") == prior_fingerprint
                and prior_preservation.get("passed") is True):
            pass  # Reuse the already-built, verified correct control without rewriting its evidence.
        else:
            build_correct(project)
    elif not project.is_dir():
        build_correct(project)

    swap_project = output / "swap" / "l"
    if swap_project.exists():
        if scorer.project_fingerprint(swap_project) != scorer.project_fingerprint(project):
            raise RuntimeError(f"Refusing to reuse a changed timing-swap project: {swap_project}")
    else:
        shutil.copytree(project, swap_project)
    swap_logs = output / "swap" / "logs"
    swap = update_monthly_body_for_swap(swap_project, swap_logs)
    if not swap["stageAccepted"] or not swap["behaviorCandidateNotPublished"]:
        raise RuntimeError("The timing-swap definition did not stage as a behavior-only candidate; inspect logs")
    write_json(output / "swap" / "swap-build.json", swap)

    arity_project = output / "arity" / "l"
    arity = build_arity_fault(arity_project)

    assertion_project = output / "assert" / "l"
    if assertion_project.exists():
        assertion_report_path = output / "assert" / "assertion-gate.json"
        if not assertion_report_path.is_file():
            raise RuntimeError(f"Existing assertion control has no verified receipt: {assertion_project}")
        assertion_report = json.loads(assertion_report_path.read_text(encoding="utf-8"))
        detected = assertion_report.get("weakenedAssertionDetectedByPreservation") is True
    else:
        shutil.copytree(project, assertion_project)
        mutation = mutate_current_test_object(
            assertion_project,
            "subscription.renew-monthly",
            "subscription.renew-monthly/preserves-cancellation-errors",
        )
        preservation = scorer.replacement_preservation_diff(SEED, assertion_project)
        detected = not preservation["passed"] and bool(
            preservation["inheritedTestsAndExamples"]
            ["subscription.renew-monthly"]["normalizedInheritedTestDifferences"]
        )
        assertion_report = {
            "status": "assertion-change-rejected" if detected else "control-failure",
            "testMutation": mutation,
            "preservationGate": preservation,
            "weakenedAssertionDetectedByPreservation": detected,
        }
        write_json(output / "assert" / "assertion-gate.json", assertion_report)
    if not detected:
        raise RuntimeError("The weakened inherited assertion escaped the preservation gate")

    summary = {
        "status": "controls-built",
        "correctControl": str(project),
        "swappedTimingControl": str(swap_project),
        "swapStagedForIndependentBehaviorScoring": swap["stageAccepted"],
        "swapPublishedToProject": False,
        "oldArityFault": arity,
        "weakenedAssertionFault": {
            "project": str(assertion_project),
            "detected": detected,
        },
        "runtime": str(CLI),
        "runtimeSha256": sha_bytes(CLI.read_bytes()),
    }
    write_json(output / "agentlang-controls.json", summary)
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
