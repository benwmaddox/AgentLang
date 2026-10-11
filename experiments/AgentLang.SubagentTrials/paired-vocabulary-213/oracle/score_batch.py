"""Score one saved paired-vocabulary actor project for paired-vocabulary-213.

The adapter reuses organic-reuse-210's frozen successor cases and low-level
projection helpers. It executes only a copied project through the pinned CLI,
using virtual filesystem mode, and stores each score under the explicitly
constrained .agentlang/paired-vocabulary-213 evidence root.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import sys
from time import gmtime, strftime


REPO = Path(__file__).resolve().parents[4]
SCORER_DIR = REPO / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210" / "oracle"
SCORER_PATH = SCORER_DIR / "score_organic_reuse.py"
STUDY_ROOT = REPO / ".agentlang" / "paired-vocabulary-213"
EXPECTED_RETAINED_BASELINE_FINGERPRINT = "390c0ebc697b735a21e5f12efe508b5e192f3d04c1a8947d9174f1835f62edd2"
BATCH_ROOT = "subscription.renew-batch"
CASE_COUNT = 9
SCORER_MODULE_NAME = "paired_vocabulary_213_organic_reuse_210_lowlevel"


def _import_210_scorer():
    """Import the frozen 210 module without leaving its helper path installed."""
    original_path = list(sys.path)
    had_model = "model" in sys.modules
    previous_model = sys.modules.pop("model", None)
    sys.path.insert(0, str(SCORER_DIR))
    try:
        spec = importlib.util.spec_from_file_location(SCORER_MODULE_NAME, SCORER_PATH)
        if spec is None or spec.loader is None:
            raise ImportError(f"Cannot load organic-reuse-210 scorer from {SCORER_PATH}")
        module = importlib.util.module_from_spec(spec)
        sys.modules[SCORER_MODULE_NAME] = module
        try:
            spec.loader.exec_module(module)
        except Exception:
            sys.modules.pop(SCORER_MODULE_NAME, None)
            raise
        return module
    finally:
        sys.path[:] = original_path
        if had_model:
            sys.modules["model"] = previous_model
        else:
            sys.modules.pop("model", None)


scorer = _import_210_scorer()
# The low-level validator is useful, but its default study root belongs to 210.
scorer.STUDY_ROOT = STUDY_ROOT.resolve()


def resolve_repo_path(path: Path) -> Path:
    return path.resolve() if path.is_absolute() else (REPO / path).resolve()


def validate_evidence_root(path: Path) -> Path:
    root = STUDY_ROOT.resolve()
    resolved = path if path.is_absolute() else REPO / path
    resolved = resolved.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"--evidence-root must be below {root}")
    return resolved


def validate_baseline(condition: str, baseline_project: Path, seed_project: Path,
                      seed_fingerprint: str) -> tuple[str, dict]:
    """Bind reset to the exact seed and retained to the accepted 210 snapshot."""
    baseline_fingerprint = scorer.project_fingerprint(baseline_project)
    if condition == "reset":
        expected = seed_fingerprint
        passed = baseline_fingerprint == expected
        rule = "baseline project fingerprint must equal the frozen report-163 seed project"
    elif condition == "retained":
        expected = EXPECTED_RETAINED_BASELINE_FINGERPRINT
        passed = baseline_fingerprint == expected
        rule = "baseline project fingerprint must equal the accepted organic-reuse-210 creator snapshot"
    else:
        raise ValueError(f"Unsupported condition: {condition}")
    if not passed:
        raise ValueError(f"Baseline project does not satisfy the {condition} condition: {rule}")
    _, manifest, _ = scorer.current_manifest(baseline_project)
    projection = scorer.projection_for_manifest(manifest)
    return baseline_fingerprint, {
        "passed": True,
        "condition": condition,
        "rule": rule,
        "expectedProjectFingerprint": expected,
        "projectFingerprint": baseline_fingerprint,
        "manifestHash": json.loads(
            (baseline_project / ".agentlang" / "store" / "CURRENT").read_text(encoding="utf-8-sig")
        )["manifestHash"],
        "projection": projection,
    }


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


def build_requests(cases: list[dict], example_owners: list[str]) -> list[dict]:
    if len(cases) != CASE_COUNT:
        raise ValueError(f"Expected exactly {CASE_COUNT} frozen successor cases, found {len(cases)}")
    return (
        [{"op": "test-all"}]
        + [{"op": "example", "word": owner} for owner in example_owners]
        + [{"op": "define", "frontend": "flow", "syntaxVersion": 2,
            "source": scorer.OBSERVATION_SOURCE}]
        + [{"op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
            "code": scorer.observation_expression(item, "successor")} for item in cases]
    )


def safe_score_eval(item: dict, response: dict) -> dict:
    """Keep malformed structured values in the setup-error category."""
    try:
        return scorer.score_eval(item, response)
    except Exception as error:
        return {
            "caseId": item["id"],
            "executed": False,
            "passed": False,
            "setupError": f"Could not project structured eval response: {type(error).__name__}: {error}",
        }


def _manifest_test_count(manifest: dict) -> int:
    current = scorer.current_revisions(manifest)
    return sum(len(entry["revision"].get("tests", [])) for entry in current.values())


def _seed_test_count(seed_manifest: dict) -> int:
    return _manifest_test_count(seed_manifest)


def _test_results(response: dict) -> list[dict] | None:
    data = response.get("data")
    if not isinstance(data, dict):
        return None
    results = data.get("results")
    return results if isinstance(results, list) else None


def _response_passed(response: dict, kind: str) -> bool:
    return isinstance(response, dict) and scorer.response_passed(response, kind)


def score_suite(test_response: dict, example_owners: list[str], example_responses: list[dict],
                actor_manifest: dict, seed_manifest: dict, seed_projection: dict) -> dict:
    results = _test_results(test_response)
    expected_total = _manifest_test_count(actor_manifest)
    expected_common_seed = _seed_test_count(seed_manifest)
    seed_names = set(seed_projection["words"])
    actual_total = len(results) if results is not None else None
    result_rows_are_objects = results is not None and all(isinstance(row, dict) for row in results)
    actual_common_seed = (
        sum(1 for row in results if isinstance(row, dict) and row.get("word") in seed_names)
        if results is not None else None
    )
    result_rows_complete = bool(
        result_rows_are_objects
        and len(results) == expected_total
        and all(row.get("passed") is True for row in results)
        and actual_common_seed == expected_common_seed
    )
    test_all_passed = bool(
        _response_passed(test_response, "test") and result_rows_complete
    )
    example_results = []
    for index, owner in enumerate(example_owners):
        response = example_responses[index] if index < len(example_responses) else {}
        example_results.append({
            "owner": owner,
            "passed": _response_passed(response, "example"),
            "response": response,
        })
    examples_passed = len(example_results) == len(example_owners) and all(
        row["passed"] for row in example_results
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
        "examplesPassed": examples_passed,
        "exampleResults": example_results,
    }


def batch_root_contract(signature: tuple[tuple[str, ...], str] | None, maturity: str | None,
                        own_test_count: int) -> dict:
    signature_json = {"inputs": list(signature[0]), "output": signature[1]} if signature else None
    signature_passed = signature == scorer.BATCH_SIGNATURE
    library_passed = maturity == "library"
    own_tests_passed = own_test_count > 0
    return {
        "signature": signature_json,
        "signaturePassed": signature_passed,
        "maturity": maturity,
        "libraryPassed": library_passed,
        "ownTestCount": own_test_count,
        "ownTestsPassed": own_tests_passed,
        "passed": signature_passed and library_passed and own_tests_passed,
    }


def inspect_batch_root(project: Path, manifest: dict) -> dict:
    current = scorer.current_revisions(manifest)
    entry = current.get(BATCH_ROOT)
    if entry is None:
        return {
            "present": False,
            "signature": None,
            "signaturePassed": False,
            "maturity": None,
            "libraryPassed": False,
            "ownTestCount": 0,
            "ownTestsPassed": False,
            "passed": False,
        }
    revision = entry["revision"]
    _, _, store = scorer.current_manifest(project)
    signature = scorer.flow_definition_signature(scorer.definition_source(store, revision))
    tests = revision.get("tests", [])
    contract = batch_root_contract(signature, revision.get("maturity"), len(tests))
    return {
        "present": True,
        "wordId": entry["word"]["wordId"],
        "revision": entry["word"]["currentRevision"],
        "definitionHash": revision["definition"]["hash"],
        **contract,
    }


def secondary_target_observations(manifest: dict, trace_path: Path | None) -> dict:
    """Report renewal names as secondary diagnostics, never acceptance gates."""
    target_names = ("subscription.renew-monthly", "subscription.renew-annual")
    graph = scorer.production_graph(manifest)
    paths = {
        name: scorer.path_to(graph, BATCH_ROOT, name)
        for name in target_names
    }
    try:
        discovery = scorer.trace_discovery(trace_path, set(target_names))
    except Exception as error:
        discovery = {
            "status": "secondary-observation-unavailable",
            "error": f"{type(error).__name__}: {error}",
            "discoveredHelpers": [],
            "metadataInspectedHelpers": [],
            "inspectionHits": [],
        }
    return {
        "targetNames": list(target_names),
        "discovery": discovery,
        "productionPathsFromBatchRoot": paths,
        "interpretation": "secondary observation only; these targets and paths are not acceptance requirements",
    }


def evaluate_acceptance(*, condition: str, exit_code: int | None, response_count: int,
                        expected_response_count: int, case_results: list[dict],
                        observation_setup_passed: bool, suite_passed: bool,
                        examples_passed: bool, batch_root_passed: bool,
                        baseline_binding_passed: bool, seed_preservation_passed: bool,
                        baseline_preservation_passed: bool, actor_unchanged: bool) -> dict:
    executed = sum(1 for row in case_results if row.get("executed") is True)
    passed = sum(1 for row in case_results if row.get("executed") is True and row.get("passed") is True)
    gates = {
        "cliExitZero": exit_code == 0,
        "responseCountExact": response_count == expected_response_count,
        "allNineCasesExecuted": len(case_results) == CASE_COUNT and executed == CASE_COUNT,
        "allNineCasesPassed": len(case_results) == CASE_COUNT and passed == CASE_COUNT,
        "observationSetupPassed": observation_setup_passed,
        "testAllPassed": suite_passed,
        "allCurrentExamplesPassed": examples_passed,
        "batchRootContractPassed": batch_root_passed,
        "baselineBindingPassed": baseline_binding_passed,
        "commonSeedPreserved": seed_preservation_passed,
        "armBaselinePreserved": baseline_preservation_passed,
        "actorProjectUnchanged": actor_unchanged,
    }
    return {
        "status": "passed" if all(gates.values()) else "score-failure",
        "condition": condition,
        "passed": all(gates.values()),
        "gates": gates,
        "caseCounts": {"expected": CASE_COUNT, "executed": executed, "passed": passed},
    }


def _runtime_pins(cli: Path) -> dict:
    runtime_files = {}
    for path in (cli, cli.with_suffix(".deps.json"), cli.with_suffix(".runtimeconfig.json")):
        if path.is_file():
            runtime_files[path.name] = scorer.sha256_file(path)
    return {
        "cli": str(cli.resolve()),
        "runtimeFiles": runtime_files,
        "scoreBatchSourceSha256": scorer.sha256_file(Path(__file__).resolve()),
        "organicReuse210ScorerSha256": scorer.sha256_file(SCORER_PATH),
        "organicReuse210ModelSha256": scorer.sha256_file(SCORER_DIR / "model.py"),
        "organicReuse210CasesSha256": scorer.sha256_file(SCORER_DIR / "cases.json"),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Pinned current Release AgentLang CLI")
    parser.add_argument("--actor-project", type=Path, required=True, help="Saved participant project to score")
    parser.add_argument("--condition", choices=("retained", "reset"), required=True,
                        help="Paired arm condition, used to bind the required baseline")
    parser.add_argument("--seed-project", type=Path, default=scorer.DEFAULT_SEED_PROJECT,
                        help="Frozen report-163 seed project")
    parser.add_argument("--baseline-project", type=Path, required=True,
                        help="Arm baseline project: seed for reset, accepted 210 snapshot for retained")
    parser.add_argument("--trace", type=Path, help="Optional assigned broker trace for secondary discovery observations")
    parser.add_argument("--evidence-root", type=Path, required=True,
                        help="Evidence root below .agentlang/paired-vocabulary-213/")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    evidence_root = validate_evidence_root(args.evidence_root)
    cli = resolve_repo_path(args.cli)
    actor_project = resolve_repo_path(args.actor_project)
    seed_project = resolve_repo_path(args.seed_project)
    baseline_project = resolve_repo_path(args.baseline_project)
    trace_path = resolve_repo_path(args.trace) if args.trace is not None else None
    if not cli.is_file() or not actor_project.is_dir() or not seed_project.is_dir() or not baseline_project.is_dir():
        raise FileNotFoundError("The pinned CLI, actor, seed, and arm baseline projects must exist")

    seed_inventory, seed_projection = scorer.validate_seed_inventory(seed_project)
    seed_fingerprint = scorer.project_fingerprint(seed_project)
    baseline_fingerprint, baseline_record = validate_baseline(
        args.condition, baseline_project, seed_project, seed_fingerprint
    )
    _, seed_manifest, _ = scorer.current_manifest(seed_project)
    actor_fingerprint = scorer.project_fingerprint(actor_project)
    _, actor_manifest, _ = scorer.current_manifest(actor_project)
    actor_projection = scorer.projection_for_manifest(actor_manifest)
    seed_preservation = scorer.preservation_diff(seed_projection, actor_projection)
    baseline_projection = baseline_record["projection"]
    baseline_preservation = scorer.preservation_diff(baseline_projection, actor_projection)
    baseline_binding_passed = baseline_record["passed"]

    cases = scorer.load_frozen_cases("successor")
    if len(cases) != CASE_COUNT:
        raise ValueError(f"Frozen successor case set must contain exactly {CASE_COUNT} cases")
    current = scorer.current_revisions(actor_manifest)
    example_owners = sorted({
        name for name, entry in current.items()
        if entry["revision"].get("examples")
    })
    requests = build_requests(cases, example_owners)
    expected_response_count = len(requests)
    run_dir = create_run_directory(evidence_root, args.condition)
    scoring_project = run_dir / "project-copy"
    shutil.copytree(actor_project, scoring_project)
    scoring_copy_fingerprint = scorer.project_fingerprint(scoring_project)
    pins = _runtime_pins(cli) | {
        "condition": args.condition,
        "actorProject": str(actor_project.resolve()),
        "actorProjectFingerprint": actor_fingerprint,
        "seedProject": str(seed_project.resolve()),
        "seedProjectFingerprint": seed_fingerprint,
        "seedManifestHash": seed_inventory["manifestHash"],
        "baselineProject": str(baseline_project.resolve()),
        "baselineProjectFingerprint": baseline_fingerprint,
        "baselineManifestHash": baseline_record["manifestHash"],
        "scoringProjectCopy": str(scoring_project.resolve()),
        "scoringProjectCopyFingerprintBeforeRun": scoring_copy_fingerprint,
        "trace": str(trace_path.resolve()) if trace_path else None,
    }
    _write_json(run_dir / "pins.json", pins)
    (run_dir / "requests.jsonl").write_text(_jsonl(requests), encoding="utf-8")

    exit_code: int | None = None
    stdout = ""
    stderr = ""
    runner_error = None
    try:
        exit_code, stdout, stderr, responses = scorer.run_cli(cli, scoring_project, requests)
    except Exception as error:
        responses = []
        runner_error = {"type": type(error).__name__, "message": str(error)}
        stderr = f"{type(error).__name__}: {error}\n"
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    _, final_actor_manifest, _ = scorer.current_manifest(actor_project)
    final_actor_fingerprint = scorer.project_fingerprint(actor_project)
    actor_unchanged = final_actor_fingerprint == actor_fingerprint
    response_count = len(responses)
    example_start = 1
    example_responses = responses[example_start:example_start + len(example_owners)]
    define_index = example_start + len(example_owners)
    define_response = responses[define_index] if len(responses) > define_index else {}
    eval_start = define_index + 1
    case_results = []
    for index, item in enumerate(cases):
        response_index = eval_start + index
        response = responses[response_index] if response_index < len(responses) else {}
        case_results.append(safe_score_eval(item, response))
    setup_errors = [row for row in case_results if not row["executed"]]
    failure_ids = [row["caseId"] for row in case_results if row["executed"] and not row["passed"]]
    observer_defined = (
        isinstance(define_response, dict)
        and scorer.is_successful_define_response(define_response)
    )

    suite = score_suite(
        responses[0] if responses else {},
        example_owners,
        example_responses,
        actor_manifest,
        seed_manifest,
        seed_projection,
    )
    batch_root = inspect_batch_root(actor_project, actor_manifest)
    secondary = secondary_target_observations(final_actor_manifest, trace_path)
    decision = evaluate_acceptance(
        condition=args.condition,
        exit_code=exit_code,
        response_count=response_count,
        expected_response_count=expected_response_count,
        case_results=case_results,
        observation_setup_passed=observer_defined,
        suite_passed=suite["testAllPassed"],
        examples_passed=suite["examplesPassed"],
        batch_root_passed=batch_root["passed"],
        baseline_binding_passed=baseline_binding_passed,
        seed_preservation_passed=seed_preservation["passed"],
        baseline_preservation_passed=baseline_preservation["passed"],
        actor_unchanged=actor_unchanged,
    )
    report = {
        **decision,
        "runtime": str(cli.resolve()),
        "runtimeInvocation": {"filesystemMode": "virtual", "capabilities": [], "testCapabilities": []},
        "actorProject": str(actor_project.resolve()),
        "actorProjectFingerprint": actor_fingerprint,
        "actorProjectUnchangedDuringScore": actor_unchanged,
        "scoringProject": str(scoring_project.resolve()),
        "scoringProjectCopyFingerprintBeforeRun": scoring_copy_fingerprint,
        "evidenceDirectory": str(run_dir.resolve()),
        "seedManifestHash": seed_inventory["manifestHash"],
        "seedInventoryPassed": True,
        "baseline": {key: value for key, value in baseline_record.items() if key != "projection"},
        "cliExitCode": exit_code,
        "runnerError": runner_error,
        "responseCount": response_count,
        "expectedResponseCount": expected_response_count,
        "observationSetupPassed": observer_defined,
        "observationSetupResponse": define_response,
        "suite": suite,
        "behavior": {
            "caseCount": CASE_COUNT,
            "executedCaseCount": sum(1 for row in case_results if row["executed"]),
            "passedCaseCount": sum(1 for row in case_results if row["executed"] and row["passed"]),
            "failureIds": failure_ids,
            "setupErrors": setup_errors,
            "caseResults": case_results,
        },
        "batchRoot": batch_root,
        "preservation": {
            "fromReport163Seed": seed_preservation,
            "fromArmBaseline": baseline_preservation,
        },
        "secondaryTargetObservations": secondary,
        "tracePath": str(trace_path.resolve()) if trace_path else None,
        "sourcePins": pins,
    }
    _write_json(run_dir / "score.json", report)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
