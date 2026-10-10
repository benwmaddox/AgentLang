"""Score the maintenance-166 handoff contract against a saved AgentLang project.

This reuses report 163's typed fixtures, cell model, and canonical projection.
The runtime project is copied for each run; the Flow source submitted here only
defines observers and never declares or commits the target or retained callers.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
from time import gmtime, strftime


REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
OLD_ORACLE = REPO / "experiments" / "AgentLang.SubagentTrials" / "subscription-handoff-161" / "oracle"
EVIDENCE_ROOT = REPO / ".agentlang" / "subscription-handoff-maintenance-166"
DEFAULT_CLI = REPO / "src" / "AgentLang.Cli" / "bin" / "Release" / "net9.0" / "AgentLang.Cli.dll"


def import_from_path(module_name: str, path: Path):
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {module_name} from {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


OLD = import_from_path("handoff_163_score_agentlang", OLD_ORACLE / "score_agentlang.py")
MODEL = import_from_path("handoff_163_model", OLD_ORACLE / "model.py")


OBSERVER_SOURCE = r'''// frontend: flow/2

record HandoffScoringObservation {
    field input: Store
    field outcome: Result<Store, BusinessError>
}

fn handoff.control.scoring-observe(input: Store, oldId: SubscriptionId, replacementId: SubscriptionId, term: String, handoffAt: Instant, expiresAt: Instant, dryRun: Bool) -> HandoffScoringObservation {
    handoffScoringObservation.new(input = input, outcome = subscription.handoff(input, oldId, replacementId, term, handoffAt, expiresAt, dryRun))
}

fn handoff.control.scoring-observe-renew-annual(input: Store, oldId: SubscriptionId, replacementId: SubscriptionId, handoffAt: Instant, expiresAt: Instant) -> HandoffScoringObservation {
    handoffScoringObservation.new(input = input, outcome = subscription.renew-annual(input, oldId, replacementId, handoffAt, expiresAt))
}

fn handoff.control.scoring-observe-renew-monthly(input: Store, oldId: SubscriptionId, replacementId: SubscriptionId, handoffAt: Instant, expiresAt: Instant) -> HandoffScoringObservation {
    handoffScoringObservation.new(input = input, outcome = subscription.renew-monthly(input, oldId, replacementId, handoffAt, expiresAt))
}
'''

CALLERS = {
    "renew-annual": "annual",
    "renew-monthly": "monthly",
}
CANCELLATION_ERROR_CODES = {
    "SUBSCRIPTION_NOT_FOUND",
    "SUBSCRIPTION_ALREADY_CANCELLED",
    "CANCEL_BEFORE_START",
}


def load_cases() -> list[dict]:
    path = OLD_ORACLE / "cases.json"
    rows = json.loads(path.read_text(encoding="utf-8"))
    modeled = {item["id"]: item for item in MODEL.CASES}
    if len(rows) != 33 or len(modeled) != 33:
        raise ValueError(f"Expected the frozen report-163 33-case set; found {len(rows)} JSON and {len(modeled)} model cases")
    if {item["id"] for item in rows} != set(modeled):
        raise ValueError("The report-163 frozen case IDs differ from its independent model")
    for row in rows:
        if row.get("expected") != MODEL.expected(modeled[row["id"]]):
            raise ValueError(f"Frozen expected value disagrees with the report-163 model for {row['id']}")
    return rows


def target_observation_expr(item: dict, dry_run: bool) -> str:
    request = item["request"]
    return (
        "handoff.control.scoring-observe("
        f"{OLD.store_expr(item['store'])}, "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['oldId']))}), "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['newId']))}), "
        f"{OLD.flow_string(request['term'])}, "
        f"Instant.new({OLD.flow_string(OLD.iso_day(request['at']))}), "
        f"Instant.new({OLD.flow_string(OLD.iso_day(request['expiry']))}), "
        f"{str(dry_run).lower()})"
    )


def caller_observation_expr(item: dict, caller: str) -> str:
    request = item["request"]
    observer = f"handoff.control.scoring-observe-{caller}"
    return (
        f"{observer}({OLD.store_expr(item['store'])}, "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['oldId']))}), "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['newId']))}), "
        f"Instant.new({OLD.flow_string(OLD.iso_day(request['at']))}), "
        f"Instant.new({OLD.flow_string(OLD.iso_day(request['expiry']))}))"
    )


def expected_projection(item: dict, dry_run: bool = False) -> dict:
    expected_model = item["expected"]
    expected_input = OLD.normalized_projection(item)
    if expected_model["tag"] == "ok":
        store_model = item["store"] if dry_run else expected_model["store"]
        return {
            "tag": "ok",
            "store": OLD.normalized_projection({"store": store_model}),
            "input": expected_input,
        }
    return {"tag": "error", "code": expected_model["code"], "input": expected_input}


def score_eval(item: dict, response: dict, *, dry_run: bool | None = None,
               caller: str | None = None) -> dict:
    label = {"caseId": item["id"]}
    if dry_run is not None:
        label["dryRun"] = dry_run
    if caller is not None:
        label["caller"] = caller
    result = {**label, "executed": False, "passed": False}
    if response.get("ok") and response.get("kind") == "eval":
        structured = response.get("data", {}).get("structuredStack")
        if structured and len(structured.get("values", [])) == 1:
            actual = OLD.result_projection(OLD.unwrap_structured(structured["values"][0]))
            expected = expected_projection(item, dry_run=(dry_run is True))
            result.update(executed=True, passed=(actual == expected))
            result["actualTag"] = actual.get("tag")
            result["expectedTag"] = expected.get("tag")
            if actual.get("tag") == "error":
                result["actualErrorCode"] = actual.get("code")
            if expected.get("tag") == "error":
                result["expectedErrorCode"] = expected.get("code")
            if actual != expected:
                result["expected"] = expected
                result["actual"] = actual
        else:
            result["setupError"] = "eval response omitted one structured observation value"
    else:
        result["setupError"] = response.get("error", response.get("text", "missing eval response"))
    return result


def json_line(value: dict) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def run_cli(cli: Path, project: Path, requests: list[dict]) -> tuple[int, str, str, list[dict]]:
    process = subprocess.run(
        ["dotnet", str(cli), "--project", str(project), "--jsonl"],
        cwd=REPO,
        input="\n".join(json_line(item) for item in requests) + "\n",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=600,
    )
    rows = []
    for index, line in enumerate(process.stdout.splitlines(), 1):
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise RuntimeError(f"CLI stdout line {index} is not JSON: {line[:500]!r}") from error
    return process.returncode, process.stdout, process.stderr, rows


def next_run_dir(label: str) -> Path:
    run_root = EVIDENCE_ROOT / "flow-oracle-runs"
    run_root.mkdir(parents=True, exist_ok=True)
    stamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    index = 1
    while True:
        candidate = run_root / f"{stamp}-{label}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def target_identity(project: Path) -> dict:
    store = project / ".agentlang" / "store"
    current = json.loads((store / "CURRENT").read_text(encoding="utf-8-sig"))
    manifest = json.loads((store / "manifests" / f"{current['manifestHash']}.json").read_text(encoding="utf-8-sig"))
    word = next((row for row in manifest["words"] if row["currentName"] == "subscription.handoff"), None)
    if word is None:
        raise ValueError(f"No saved subscription.handoff word in {project}")
    revision = next((
        row for row in manifest["revisions"]
        if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"]
    ), None)
    if revision is None:
        raise ValueError(f"No current saved revision for subscription.handoff in {project}")
    return {
        "wordId": word["wordId"],
        "name": word["currentName"],
        "revision": word["currentRevision"],
        "definitionHash": revision["definition"]["hash"],
    }


def persistent_fingerprint(project: Path) -> str:
    digest = hashlib.sha256()
    candidates = []
    store = project / ".agentlang" / "store"
    if store.is_dir():
        candidates.extend(path for path in store.rglob("*") if path.is_file() and path.name != "WRITE.lock")
    dictionary = project / "dictionary.agent"
    if dictionary.is_file():
        candidates.append(dictionary)
    for path in sorted(set(candidates), key=lambda value: str(value.relative_to(project)).lower()):
        relative = path.relative_to(project).as_posix().encode("utf-8")
        content = path.read_bytes()
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fixture_populations(cases: list[dict], dry_modes: tuple[bool, ...]) -> dict:
    base = OLD.case_fixture_populations(cases)
    expanded = {}
    for group, values in base.items():
        expanded[group] = {
            "count": values["count"] * len(dry_modes),
            "caseIds": [f"{case_id}:dryRun={str(mode).lower()}" for mode in dry_modes for case_id in values["caseIds"]],
        }
    return expanded


def population_for_caller(cases: list[dict]) -> dict:
    return OLD.case_fixture_populations(cases)


def tally(results: list[dict]) -> dict:
    executed = sum(result.get("executed", False) for result in results)
    failures = [result for result in results if result.get("executed") and not result.get("passed")]
    setup_errors = [result for result in results if not result.get("executed")]
    return {
        "expectedCaseCount": len(results),
        "executedCaseCount": executed,
        "passedCaseCount": executed - len(failures),
        "behaviorFailureCount": len(failures),
        "behaviorFailureIds": [
            f"{item.get('caller', 'target')}:{item['caseId']}"
            + (f":dryRun={str(item['dryRun']).lower()}" if "dryRun" in item else "")
            for item in failures
        ],
        "setupErrorCount": len(setup_errors),
    }


def behavior_expectation(role: str, target_results: list[dict], caller_results: dict[str, list[dict]]) -> dict:
    target_tally = tally(target_results)
    caller_tallies = {caller: tally(results) for caller, results in caller_results.items()}
    all_caller_cases_pass = all(
        row["executedCaseCount"] == row["expectedCaseCount"]
        and row["passedCaseCount"] == row["expectedCaseCount"]
        for row in caller_tallies.values()
    )
    fully_executed = target_tally["executedCaseCount"] == 66 and target_tally["setupErrorCount"] == 0
    if role == "correct":
        passed = (
            fully_executed and target_tally["passedCaseCount"] == 66
            and all_caller_cases_pass
        )
        return {"passed": passed, "classification": "correct-control" if passed else "correct-control-failure",
                "targetFullyExecuted": fully_executed, "retainedCallersPass": all_caller_cases_pass}

    wrappers_ready = all(
        row["executedCaseCount"] == row["expectedCaseCount"]
        and row["passedCaseCount"] == row["expectedCaseCount"]
        and row["setupErrorCount"] == 0
        for row in caller_tallies.values()
    )
    failures = [item for item in target_results if item.get("executed") and not item.get("passed")]
    if role == "skip-dry-run-creation-validation":
        demonstrated = any(
            item.get("dryRun") is True
            and item.get("expectedTag") == "error"
            and item.get("expectedErrorCode") not in CANCELLATION_ERROR_CODES
            and item.get("actualTag") != "error"
            for item in failures
        )
    else:
        demonstrated = any(
            item.get("dryRun") is True
            and item.get("expectedTag") == "ok"
            and item.get("actualTag") == "ok"
            for item in failures
        )
    passed = fully_executed and wrappers_ready and demonstrated
    return {
        "passed": passed,
        "classification": "fault-behavior-rejected" if passed else "fault-control-not-demonstrated",
        "targetFullyExecuted": fully_executed,
        "retainedCallersPass": wrappers_ready,
        "expectedFaultBehaviorDemonstrated": demonstrated,
    }


def score_actor(actor_project: Path, cli: Path, role: str, label: str) -> dict:
    actor_project = actor_project.expanduser().resolve()
    cli = cli.expanduser().resolve()
    if not actor_project.is_dir():
        raise FileNotFoundError(f"Saved actor project is missing: {actor_project}")
    if not cli.is_file():
        raise FileNotFoundError(f"Release CLI is missing: {cli}")
    cases = load_cases()
    run_dir = next_run_dir(label)
    project = run_dir / "actor-project-copy"
    identity_before_copy = target_identity(actor_project)
    actor_fingerprint = persistent_fingerprint(actor_project)
    shutil.copytree(actor_project, project)
    identity_before = target_identity(project)
    if identity_before != identity_before_copy:
        raise RuntimeError("Copied scoring project did not preserve the saved handoff identity/revision/source")
    fingerprint_before = persistent_fingerprint(project)

    observer_path = run_dir / "scoring-observers.flow"
    observer_path.write_text(OBSERVER_SOURCE, encoding="utf-8")
    requests = [{"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": OBSERVER_SOURCE}]
    scored_slots: list[tuple[str, dict, bool | None, str | None]] = []
    for dry_run in (False, True):
        for item in cases:
            requests.append({
                "op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
                "code": target_observation_expr(item, dry_run),
            })
            scored_slots.append(("target", item, dry_run, None))
    for caller in CALLERS:
        for item in cases:
            requests.append({
                "op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
                "code": caller_observation_expr(item, caller),
            })
            scored_slots.append(("caller", item, None, caller))

    exit_code, stdout, stderr, responses = run_cli(cli, project, requests)
    (run_dir / "requests.jsonl").write_text("\n".join(json_line(row) for row in requests) + "\n", encoding="utf-8")
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    define_response = responses[0] if responses else {}
    defined_names = [row.get("name") for row in define_response.get("data", {}).get("words", [])]
    protected_names = {"subscription.handoff", "subscription.renew-annual", "subscription.renew-monthly"}
    redefined = sorted(protected_names.intersection(defined_names))
    define_failed = not define_response.get("ok", False) or bool(redefined)
    setup_failure = None
    if len(responses) != len(requests):
        setup_failure = {
            "stage": "response-count", "exitCode": exit_code,
            "expectedResponseCount": len(requests), "actualResponseCount": len(responses),
            "stderr": stderr,
        }
    elif not define_response.get("ok", False):
        setup_failure = {"stage": "observer-define", "response": define_response}
    elif redefined:
        setup_failure = {"stage": "observer-define", "message": "Observer source redeclared a protected saved word.",
                         "redefinedWords": redefined}
    elif exit_code != 0:
        setup_failure = {"stage": "cli-exit-code", "exitCode": exit_code, "stderr": stderr}

    target_results = []
    caller_results = {caller: [] for caller in CALLERS}
    for slot, response in zip(scored_slots, responses[1:]):
        section, item, dry_run, caller = slot
        result = score_eval(item, response, dry_run=dry_run, caller=caller)
        if section == "target":
            target_results.append(result)
        else:
            caller_item = copy.deepcopy(item)
            caller_item["request"]["term"] = CALLERS[caller]
            caller_item["expected"] = MODEL.expected(caller_item)
            # Score the wrapper against the same model and projection, with its fixed term and false mode.
            result = score_eval(caller_item, response, caller=caller)
            caller_results[caller].append(result)

    identity_after = target_identity(project)
    fingerprint_after = persistent_fingerprint(project)
    target_unchanged = identity_before == identity_after
    persistent_state_unchanged = fingerprint_before == fingerprint_after
    target_tally = tally(target_results)
    caller_tallies = {caller: tally(rows) for caller, rows in caller_results.items()}
    behavior = behavior_expectation(role, target_results, caller_results)
    report = {
        "status": "passed" if setup_failure is None and target_unchanged and persistent_state_unchanged and behavior["passed"] else "control-failure",
        "mode": "wrapper-only scoring against a saved target",
        "controlRole": role,
        "runtime": str(cli),
        "runtimeSha256": hashlib.sha256(cli.read_bytes()).hexdigest(),
        "cliExitCode": exit_code,
        "actorProject": str(actor_project),
        "copiedScoringProject": str(project),
        "runDirectory": str(run_dir),
        "oldCaseSource": str((OLD_ORACLE / "cases.json").relative_to(REPO)),
        "oldCaseSourceSha256": sha256_file(OLD_ORACLE / "cases.json"),
        "oldModelSource": str((OLD_ORACLE / "model.py").relative_to(REPO)),
        "oldModelSourceSha256": sha256_file(OLD_ORACLE / "model.py"),
        "scorerSha256": sha256_file(Path(__file__).resolve()),
        "observerSource": str(observer_path),
        "observerSourceSha256": sha256_file(observer_path),
        "validationCommand": f"python {Path(__file__).resolve().relative_to(REPO)} --cli <Release-AgentLang.Cli.dll> --actor-project <saved-project> --role {role}",
        "targetIdentityBefore": identity_before,
        "targetIdentityAfter": identity_after,
        "targetWordRedefined": bool(redefined),
        "definedWordNames": defined_names,
        "targetIdentityUnchanged": target_unchanged,
        "persistentStateUnchanged": persistent_state_unchanged,
        "actorPersistentFingerprint": actor_fingerprint,
        "copiedPersistentFingerprintBefore": fingerprint_before,
        "copiedPersistentFingerprintAfter": fingerprint_after,
        "targetCaseCount": len(target_results),
        "targetFixturePopulations": fixture_populations(cases, (False, True)),
        "target": target_tally,
        "retainedCallerCaseCount": sum(len(rows) for rows in caller_results.values()),
        "retainedCallers": {
            caller: {"term": term, "fixturePopulations": population_for_caller(cases), "score": caller_tallies[caller]}
            for caller, term in CALLERS.items()
        },
        "setupFailure": setup_failure,
        "behaviorCheck": behavior,
        "caseResults": {"target": target_results, "retainedCallers": caller_results},
    }
    (run_dir / "score.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, default=DEFAULT_CLI, help="Release AgentLang.Cli.dll to execute")
    parser.add_argument("--actor-project", type=Path, required=True, help="Saved project already containing the target and retained callers")
    parser.add_argument("--role", choices=("correct", "skip-dry-run-creation-validation", "return-updated-store-on-dry-run"),
                        default="correct", help="Expected behavior for control qualification")
    parser.add_argument("--label", help="Short evidence-directory label; defaults to the role")
    args = parser.parse_args()
    label = args.label or args.role
    report = score_actor(args.actor_project, args.cli, args.role, label)
    print(json.dumps({key: value for key, value in report.items() if key != "caseResults"}, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
