"""Execute organic-reuse-200 behavior controls on an explicit frozen CLI pin.

Do not run this runner until root has reviewed the source/request artifacts.
Each variant gets a fresh report-163 seed copy and a separate process. Receipts
are labeled preflight-only and never establish participant readiness.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

import control_preflight as controls
import score_organic_reuse as scorer


def runtime_hashes(cli: Path) -> dict[str, str]:
    files = [
        cli,
        cli.with_suffix(".deps.json"),
        cli.with_suffix(".runtimeconfig.json"),
        cli.parent / "AgentLang.Core.dll",
        cli.parent / "FSharp.Core.dll",
    ]
    return {path.name: scorer.sha256_file(path) for path in files if path.is_file()}


def validate_runtime_pin(cli: Path) -> dict:
    pin_path = scorer.STUDY_ROOT / "accepted-runtime-pin.json"
    if not pin_path.is_file():
        raise FileNotFoundError(f"Accepted runtime pin is missing: {pin_path}")
    pin = json.loads(pin_path.read_text(encoding="utf-8"))
    if "accepted-native198-runtime-and-source-pins" not in pin.get("status", ""):
        raise ValueError("accepted-runtime-pin.json does not record accepted native198 inputs")
    expected = {row["name"]: row["sha256"] for row in pin.get("runtimeFiles", [])}
    actual = runtime_hashes(cli)
    if len(expected) != 5 or actual != expected:
        raise ValueError("The explicit CLI directory does not match all five accepted runtime hashes")
    return {
        "path": str(pin_path.resolve()),
        "sha256": scorer.sha256_file(pin_path),
        "acceptedCommit": pin.get("acceptedCommit"),
        "sourceInputCount": pin.get("sourceInputCount"),
        "runtimeFiles": actual,
    }


def response_at(responses: list[dict], index: int) -> dict:
    return responses[index] if 0 <= index < len(responses) else {}


def write_run_files(run_dir: Path, requests: list[dict], stdout: str, stderr: str, report: dict) -> None:
    (run_dir / "requests.jsonl").write_text(controls.canonical_jsonl(requests), encoding="utf-8")
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")
    (run_dir / "score.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def run_variant(variant: str, cli: Path, seed_project: Path, evidence_root: Path, pin_receipt: dict) -> dict:
    stage, cases = controls.cases_for_variant(variant)
    run_dir = scorer.create_run_directory(evidence_root, variant)
    project = run_dir / "seed-project"
    shutil.copytree(seed_project, project)
    _, manifest, _ = scorer.current_manifest(project)
    current = scorer.current_revisions(manifest)
    example_owners = sorted(
        name for name, entry in current.items()
        if entry["revision"].get("examples")
    )
    requests = controls.requests_for_variant(variant, example_owners)
    exit_code, stdout, stderr, responses = scorer.run_cli(cli, project, requests)

    test_response = response_at(responses, 0)
    example_start = 1
    example_responses = responses[example_start:example_start + len(example_owners)]
    observer_index = example_start + len(example_owners)
    observer_response = response_at(responses, observer_index)
    control_response = response_at(responses, observer_index + 1)
    eval_responses = responses[observer_index + 2:]
    case_results = [
        scorer.score_eval(item, response)
        for item, response in zip(cases, eval_responses)
    ]
    test_pass = scorer.response_passed(test_response, "test")
    example_results = [
        {"owner": owner, "passed": scorer.response_passed(response, "example"), "response": response}
        for owner, response in zip(example_owners, example_responses)
    ]
    examples_pass = len(example_results) == len(example_owners) and all(row["passed"] for row in example_results)
    observer_defined = scorer.is_successful_define_response(observer_response)
    control_defined = scorer.is_successful_define_response(control_response)
    expected_count = len(requests)
    response_count_ok = len(responses) == expected_count
    setup_pass = (
        exit_code == 0 and response_count_ok and test_pass and examples_pass
        and observer_defined and control_defined
    )
    expected = controls.expected_control_result(variant, case_results)
    preflight_pass = setup_pass and expected["allCasesExecuted"] and expected["passed"]
    report = {
        "status": "preflight-passed" if preflight_pass else "preflight-failed",
        "stage": stage,
        "variant": variant,
        "scope": "behavior controls only; public library qualification and actor eligibility are scored separately",
        "runtime": str(cli.resolve()),
        "runtimeFiles": runtime_hashes(cli.resolve()),
        "runtimeInvocation": {"filesystemMode": "virtual", "capabilities": [], "testCapabilities": []},
        "acceptedRuntimePin": pin_receipt,
        "runtimePinStatus": "accepted runtime hash checked; this behavior preflight does not authorize participant dispatch",
        "controlSourceSha256": hashlib.sha256(controls.control_source(variant).encode("utf-8")).hexdigest(),
        "requestSha256": hashlib.sha256(controls.canonical_jsonl(requests).encode("utf-8")).hexdigest(),
        "seedProject": str(seed_project.resolve()),
        "seedProjectCopy": str(project.resolve()),
        "seedManifestHash": scorer.current_manifest(project)[0]["manifestHash"],
        "evidenceDirectory": str(run_dir.resolve()),
        "cliExitCode": exit_code,
        "responseCount": len(responses),
        "expectedResponseCount": expected_count,
        "suite": {
            "testAllPassed": test_pass,
            "testAllResponse": test_response,
            "exampleOwnerCount": len(example_owners),
            "examplesPassed": examples_pass,
            "exampleResults": example_results,
        },
        "definitions": {
            "observationDefined": observer_defined,
            "observationResponse": observer_response,
            "controlDefined": control_defined,
            "controlResponse": control_response,
        },
        "behavior": {
            "caseCount": len(cases),
            "executedCaseCount": sum(row["executed"] for row in case_results),
            "matchedCaseCount": sum(row["passed"] for row in case_results),
            "failureIds": [row["caseId"] for row in case_results if row["executed"] and not row["passed"]],
            "setupErrors": [row for row in case_results if not row["executed"]],
            "caseResults": case_results,
        },
        "controlExpectation": expected,
        "participantDispatchAllowed": False,
        "preflightLimit": "A passing receipt establishes only that this control set behaved on the accepted pinned CLI. Root must review it and separately decide participant readiness.",
    }
    write_run_files(run_dir, requests, stdout, stderr, report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Explicit hash-frozen current Release AgentLang.Cli.dll")
    parser.add_argument("--seed-project", type=Path, default=scorer.DEFAULT_SEED_PROJECT)
    parser.add_argument("--evidence-root", type=Path, required=True, help="Directory below .agentlang/organic-reuse-200/")
    parser.add_argument("--variants", nargs="+", choices=controls.ALL_VARIANTS, default=list(controls.ALL_VARIANTS))
    args = parser.parse_args()

    cli = args.cli if args.cli.is_absolute() else scorer.REPO / args.cli
    seed_project = args.seed_project if args.seed_project.is_absolute() else scorer.REPO / args.seed_project
    evidence_root = scorer.validate_evidence_root(args.evidence_root)
    if not cli.is_file() or not seed_project.is_dir():
        raise FileNotFoundError("The explicit CLI and frozen report-163 seed project must exist")
    pin_receipt = validate_runtime_pin(cli.resolve())
    scorer.validate_seed_inventory(seed_project)

    attempt_dir = scorer.create_run_directory(evidence_root, "attempts")
    reports = [run_variant(variant, cli.resolve(), seed_project.resolve(), attempt_dir, pin_receipt)
               for variant in args.variants]
    summary = {
        "status": "preflight-passed" if all(row["status"] == "preflight-passed" for row in reports) else "preflight-failed",
        "attemptDirectory": str(attempt_dir.resolve()),
        "variants": reports,
        "participantDispatchAllowed": False,
        "runtimeInvocation": {"filesystemMode": "virtual", "capabilities": [], "testCapabilities": []},
        "acceptedRuntimePin": pin_receipt,
        "runtimePinStatus": "accepted runtime hash checked; control review and participant decision remain pending",
    }
    summary_path = attempt_dir / "control-preflight-summary.json"
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": summary["status"],
        "summary": str(summary_path.resolve()),
        "variantStatuses": {row["variant"]: row["status"] for row in reports},
        "participantDispatchAllowed": False,
    }, indent=2, ensure_ascii=False))
    return 0 if summary["status"] == "preflight-passed" else 1


if __name__ == "__main__":
    sys.exit(main())
