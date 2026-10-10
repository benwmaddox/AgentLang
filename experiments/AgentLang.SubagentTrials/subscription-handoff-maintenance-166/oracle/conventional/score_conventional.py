#!/usr/bin/env python3
"""Score the 7-argument F# handoff and its retained five-argument callers.

The runner reuses the frozen 33 fixtures and independent model from trial 161.
Participant Operations.fs is copied byte-for-byte into a disposable project;
the seed library and Domain.fs only provide its original compile context.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Any


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[4]
TRIAL = HERE.parents[1]
HISTORICAL_ORACLE = REPOSITORY / "experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle"
DEFAULT_CASES = HISTORICAL_ORACLE / "cases.json"
DEFAULT_SEED = REPOSITORY / ".agentlang/subscription-handoff-maintenance-166/seeds/conventional"
DEFAULT_OUTPUT = REPOSITORY / ".agentlang/subscription-handoff-maintenance-166/control/conventional"
CONTROL_FILES = (
    "ConventionalControl.fsproj",
    "Handoff.fs",
    "Scorer.fs",
    "Program.fs",
)
PARTICIPANT_FILES = (
    "ParticipantControl.fsproj",
    "Handoff.fs",
    "Scorer.fs",
    "ParticipantProgram.fs",
)
CONTROL_ARMS = (
    "composed",
    "skip-creation-validation-dry-run",
    "return-modified-dry-run",
)
ORPHAN_REFERENCE_CASES = {
    "old-customer-missing",
    "old-product-missing",
    "old-customer-and-product-missing",
    "blank-term-precedes-missing-customer",
    "bad-period-precedes-missing-customer",
    "missing-product-precedes-overlap",
    "duplicate-precedes-missing-customer",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def json_line(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def next_run_directory(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    index = 1
    while True:
        candidate = root / f"run-{stamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def fixture_populations(cases: list[dict[str, Any]]) -> dict[str, Any]:
    valid = [case["id"] for case in cases if case["id"] not in ORPHAN_REFERENCE_CASES]
    orphan = [case["id"] for case in cases if case["id"] in ORPHAN_REFERENCE_CASES]
    return {
        "common-valid-reference": {"count": len(valid), "caseIds": valid},
        "adversarial-orphan-reference": {"count": len(orphan), "caseIds": orphan},
    }


def expected_projection(case: dict[str, Any], dry_run: bool | None) -> dict[str, Any]:
    source = case["store"]
    expected = case["expected"]
    if expected["tag"] == "error":
        return {"tag": "error", "code": expected["code"], "input": source}
    store = source if dry_run is True else expected["store"]
    return {"tag": "ok", "store": store, "input": source}


def parse_rows(stdout: str) -> tuple[list[dict[str, Any]], str | None]:
    rows: list[dict[str, Any]] = []
    for line_number, line in enumerate(stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            return rows, f"stdout line {line_number} is not JSONL: {error}"
    return rows, None


def score_rows(
    label: str,
    cases: list[dict[str, Any]],
    process: subprocess.CompletedProcess[str],
    dry_run: bool | None,
    evidence_dir: Path,
    command: list[str],
) -> dict[str, Any]:
    (evidence_dir / f"{label}-stdout.txt").write_text(process.stdout, encoding="utf-8")
    (evidence_dir / f"{label}-stderr.txt").write_text(process.stderr, encoding="utf-8")
    rows, parse_error = parse_rows(process.stdout)
    setup_failure = None
    if process.returncode != 0:
        setup_failure = f"runner exited {process.returncode}"
    elif parse_error:
        setup_failure = parse_error
    elif len(rows) != len(cases):
        setup_failure = f"runner emitted {len(rows)} rows for {len(cases)} cases"

    results: list[dict[str, Any]] = []
    if setup_failure is None:
        for case, row in zip(cases, rows, strict=True):
            case_id = str(case["id"])
            if row.get("setupFailure"):
                results.append({"caseId": case_id, "executed": False, "passed": False,
                                "setupFailure": row["setupFailure"]})
                continue
            expected = expected_projection(case, dry_run)
            actual = row.get("actual")
            input_preserved = row.get("inputPreserved") is True
            collateral_preserved = row.get("collateralPreserved") is True
            passed = actual == expected and input_preserved and collateral_preserved
            result = {
                "caseId": case_id,
                "executed": True,
                "passed": passed,
                "inputPreserved": input_preserved,
                "collateralPreserved": collateral_preserved,
            }
            if not passed:
                result["expected"] = expected
                result["actual"] = actual
            results.append(result)
    executed = sum(result["executed"] for result in results)
    failures = [result["caseId"] for result in results if result["executed"] and not result["passed"]]
    setup_cases = [result["caseId"] for result in results if not result["executed"]]
    write_json(evidence_dir / f"{label}-results.json", results)
    return {
        "label": label,
        "dryRun": dry_run,
        "status": "setup-failure" if setup_failure or setup_cases else "pass" if not failures else "behavioral-mismatch",
        "expectedOutcomeCount": len(cases),
        "executedCaseCount": executed,
        "passedCaseCount": sum(result["passed"] for result in results),
        "failingCaseIds": failures,
        "setupFailureCaseIds": setup_cases,
        "setupFailure": setup_failure or ("one or more cases failed to construct" if setup_cases else None),
        "runnerExitCode": process.returncode,
        "runnerCommand": command,
        "evidence": {
            "results": str(evidence_dir / f"{label}-results.json"),
            "stdout": str(evidence_dir / f"{label}-stdout.txt"),
            "stderr": str(evidence_dir / f"{label}-stderr.txt"),
        },
    }


def run_process(command: list[str], cwd: Path, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=cwd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout, check=False)


def copy_control_project(project_dir: Path, seed_dir: Path) -> Path:
    project_dir.mkdir(parents=True)
    (project_dir / "lib").mkdir()
    for filename in CONTROL_FILES:
        shutil.copy2(HERE / filename, project_dir / filename)
    shutil.copy2(seed_dir / "lib/AgentLang.Business.dll", project_dir / "lib/AgentLang.Business.dll")
    return project_dir / "ConventionalControl.fsproj"


def score_controls(seed_dir: Path, evidence_dir: Path, cases_path: Path,
                   cases: list[dict[str, Any]], selected: list[str]) -> dict[str, Any]:
    project = evidence_dir / "control-project"
    project_file = copy_control_project(project, seed_dir)
    build_command = ["dotnet", "build", str(project_file), "--configuration", "Release",
                     "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
    build = run_process(build_command, project)
    (evidence_dir / "control-build-stdout.txt").write_text(build.stdout, encoding="utf-8")
    (evidence_dir / "control-build-stderr.txt").write_text(build.stderr, encoding="utf-8")
    dll = project / "bin/Release/net9.0/ConventionalControl.dll"
    report: dict[str, Any] = {
        "buildCommand": build_command,
        "buildExitCode": build.returncode,
        "buildEvidence": {
            "stdout": str(evidence_dir / "control-build-stdout.txt"),
            "stderr": str(evidence_dir / "control-build-stderr.txt"),
        },
        "arms": {},
    }
    if build.returncode != 0:
        report["status"] = "setup-failure"
        report["setupFailure"] = "control project build failed"
        return report

    for name in selected:
        modes: dict[str, Any] = {}
        for dry_run in (False, True):
            label = f"{name}-dry-run-{str(dry_run).lower()}"
            command = ["dotnet", str(dll), "--control", name, "--dry-run", str(dry_run).lower(),
                       "--cases", str(cases_path)]
            process = run_process(command, project)
            modes[str(dry_run).lower()] = score_rows(label, cases, process, dry_run, evidence_dir, command)
        if name == "composed":
            arm_ok = all(mode["status"] == "pass" and mode["passedCaseCount"] == len(cases)
                         for mode in modes.values())
        else:
            normal = modes["false"]
            dry = modes["true"]
            arm_ok = (
                normal["status"] == "pass" and normal["passedCaseCount"] == len(cases)
                and dry["status"] == "behavioral-mismatch" and bool(dry["failingCaseIds"])
                and dry["executedCaseCount"] == len(cases)
            )
        report["arms"][name] = {"status": "pass" if arm_ok else "control-failure", "modes": modes}
    all_arms_ran = all(name in report["arms"] for name in CONTROL_ARMS)
    report["allRequiredArmsRan"] = all_arms_ran
    report["status"] = (
        "pass"
        if all_arms_ran and all(arm["status"] == "pass" for arm in report["arms"].values())
        else "control-failure"
    )
    report["caseCountPerMode"] = len(cases)
    report["expectedCaseExecutions"] = len(cases) * 2 * len(selected)
    report["allFaultControlsRejected"] = all(
        report["arms"].get(name, {}).get("modes", {}).get("true", {}).get("status") == "behavioral-mismatch"
        and bool(report["arms"].get(name, {}).get("modes", {}).get("true", {}).get("failingCaseIds"))
        for name in ("skip-creation-validation-dry-run", "return-modified-dry-run")
        if name in report["arms"]
    ) and all(name in report["arms"] for name in ("skip-creation-validation-dry-run", "return-modified-dry-run"))
    report["casesPath"] = str(cases_path)
    return report


def load_model_module():
    model_path = HISTORICAL_ORACLE / "model.py"
    spec = importlib.util.spec_from_file_location("subscription_handoff_161_model", model_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load historical model: {model_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def wrapper_cases(cases: list[dict[str, Any]], term: str, model: Any) -> list[dict[str, Any]]:
    converted = []
    for case in cases:
        item = deepcopy(case)
        item["request"]["term"] = term
        item["expected"] = model.expected(item)
        converted.append(item)
    return converted


def participant_operations(path: Path) -> Path:
    source = path.resolve()
    operations = source / "Operations.fs" if source.is_dir() else source
    if not operations.is_file() or operations.name.lower() != "operations.fs":
        raise FileNotFoundError(f"Expected a saved project directory or Operations.fs: {source}")
    return operations


def copy_participant_project(project_dir: Path, seed_dir: Path, operations: Path) -> dict[str, Any]:
    project_dir.mkdir(parents=True)
    (project_dir / "lib").mkdir()
    shutil.copy2(seed_dir / "Domain.fs", project_dir / "Domain.fs")
    shutil.copy2(seed_dir / "lib/AgentLang.Business.dll", project_dir / "lib/AgentLang.Business.dll")
    shutil.copy2(operations, project_dir / "Operations.fs")
    for filename in PARTICIPANT_FILES:
        shutil.copy2(HERE / filename, project_dir / filename)
    copied = [
        project_dir / "Domain.fs",
        project_dir / "Operations.fs",
        project_dir / "Handoff.fs",
        project_dir / "Scorer.fs",
        project_dir / "ParticipantProgram.fs",
        project_dir / "lib/AgentLang.Business.dll",
    ]
    return {
        "target": "AgentLang.SubscriptionHandoff.Subscription.handoff",
        "targetSignature": "Store -> oldId -> replacementId -> term -> handoffAt -> expiresAt -> dryRun -> Result<Store, DomainError>",
        "participantOperationsSource": str(operations),
        "participantOperationsSourceSha256": sha256(operations),
        "disposableProjectPath": str(project_dir),
        "compileInputs": [
            {"path": str(path.relative_to(project_dir)), "sha256": sha256(path)}
            for path in copied
        ],
        "selfTestsIncluded": False,
    }


def score_participant(seed_dir: Path, evidence_dir: Path, cases_path: Path,
                      cases: list[dict[str, Any]], source_path: Path) -> dict[str, Any]:
    operations = participant_operations(source_path)
    source_hash_before = sha256(operations)
    project = evidence_dir / "participant-project"
    input_manifest = copy_participant_project(project, seed_dir, operations)
    write_json(evidence_dir / "participant-source-manifest.json", input_manifest)
    project_file = project / "ParticipantControl.fsproj"
    build_command = ["dotnet", "build", str(project_file), "--configuration", "Release",
                     "--verbosity", "minimal", "-m:1", "-p:NuGetAudit=false"]
    build = run_process(build_command, project)
    (evidence_dir / "participant-build-stdout.txt").write_text(build.stdout, encoding="utf-8")
    (evidence_dir / "participant-build-stderr.txt").write_text(build.stderr, encoding="utf-8")
    dll = project / "bin/Release/net9.0/ParticipantControl.dll"
    report: dict[str, Any] = {
        **input_manifest,
        "buildCommand": build_command,
        "buildExitCode": build.returncode,
        "buildEvidence": {
            "stdout": str(evidence_dir / "participant-build-stdout.txt"),
            "stderr": str(evidence_dir / "participant-build-stderr.txt"),
        },
        "handoffModes": {},
        "retainedCallers": {},
    }
    if build.returncode != 0:
        report["status"] = "setup-failure"
        report["setupFailure"] = "participant Operations.fs failed to compile in the disposable scorer project"
        report["participantSourceUnchanged"] = sha256(operations) == source_hash_before
        return report

    for dry_run in (False, True):
        label = f"participant-handoff-dry-run-{str(dry_run).lower()}"
        command = ["dotnet", str(dll), "--operation", "handoff", "--dry-run", str(dry_run).lower(),
                   "--cases", str(cases_path)]
        process = run_process(command, project)
        report["handoffModes"][str(dry_run).lower()] = score_rows(
            label, cases, process, dry_run, evidence_dir, command
        )

    model = load_model_module()
    for display_name, operation, term in (
        ("renewAnnual", "renew-annual", "annual"),
        ("renewMonthly", "renew-monthly", "monthly"),
    ):
        derived = wrapper_cases(cases, term, model)
        wrapper_cases_path = evidence_dir / f"{display_name}-derived-cases.json"
        write_json(wrapper_cases_path, derived)
        label = f"participant-{display_name}"
        command = ["dotnet", str(dll), "--operation", operation, "--cases", str(wrapper_cases_path)]
        process = run_process(command, project)
        wrapper_report = score_rows(label, derived, process, False, evidence_dir, command)
        wrapper_report["fiveArgumentWrapper"] = True
        wrapper_report["expectedForwardedTerm"] = term
        wrapper_report["dryRunPassedToHandoff"] = False
        wrapper_report["derivedFromHistoricalCaseCount"] = len(cases)
        wrapper_report["derivedCasesPath"] = str(wrapper_cases_path)
        report["retainedCallers"][display_name] = wrapper_report

    report["participantSourceUnchanged"] = sha256(operations) == source_hash_before
    handoff_ok = all(
        result["status"] == "pass" and result["passedCaseCount"] == len(cases)
        for result in report["handoffModes"].values()
    )
    wrappers_ok = all(
        result["status"] == "pass" and result["passedCaseCount"] == len(cases)
        for result in report["retainedCallers"].values()
    )
    report["status"] = "pass" if handoff_ok and wrappers_ok and report["participantSourceUnchanged"] else "behavioral-rejection"
    report["caseCountPerHandoffMode"] = len(cases)
    report["wrapperCaseCountEach"] = len(cases)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seed-directory", type=Path, default=DEFAULT_SEED,
                        help="Coordinator-staged conventional seed containing Domain.fs and lib/AgentLang.Business.dll.")
    parser.add_argument("--cases", type=Path, default=DEFAULT_CASES,
                        help="Frozen report-161 cases.json; defaults to the historical oracle.")
    parser.add_argument("--output-directory", type=Path, default=DEFAULT_OUTPUT,
                        help="Ignored evidence root; each invocation creates a fresh run directory.")
    parser.add_argument("--control", choices=CONTROL_ARMS, action="append",
                        help="Run a control arm (repeatable); defaults to all three controls.")
    parser.add_argument("--participant", type=Path,
                        help="Saved participant project or its actual Operations.fs file; source is copied unchanged.")
    args = parser.parse_args()

    cases_path = args.cases.resolve()
    seed_dir = args.seed_directory.resolve()
    output_root = args.output_directory.resolve()
    for required in (cases_path, seed_dir / "Domain.fs", seed_dir / "lib/AgentLang.Business.dll"):
        if not required.is_file():
            raise FileNotFoundError(f"Required scorer input is missing: {required}")
    cases = json.loads(cases_path.read_text(encoding="utf-8"))
    if len(cases) != 33 or len({case["id"] for case in cases}) != 33:
        raise ValueError("Expected the unchanged 33 unique report-161 handoff fixtures")
    populations = fixture_populations(cases)
    if populations["common-valid-reference"]["count"] != 26 or populations["adversarial-orphan-reference"]["count"] != 7:
        raise ValueError("Historical case classification changed; expected 26 valid-reference and 7 orphan-reference fixtures")

    run_dir = next_run_directory(output_root)
    selected = args.control or list(CONTROL_ARMS)
    control_report = score_controls(seed_dir, run_dir, cases_path, cases, selected)
    participant_report = None
    if args.participant is not None:
        participant_report = score_participant(seed_dir, run_dir, cases_path, cases, args.participant)

    status = control_report["status"]
    if participant_report is not None and participant_report["status"] != "pass":
        status = "participant-rejected-or-setup-failure"
    report = {
        "status": status,
        "runDirectory": str(run_dir),
        "caseCount": len(cases),
        "fixturePopulations": populations,
        "expectedSuccesses": sum(case["expected"]["tag"] == "ok" for case in cases),
        "caseSource": str(cases_path),
        "caseSourceSha256": sha256(cases_path),
        "seedDirectory": str(seed_dir),
        "seedBusinessDllSha256": sha256(seed_dir / "lib/AgentLang.Business.dll"),
        "controlSuite": control_report,
        "participant": participant_report,
    }
    write_json(run_dir / "score-summary.json", report)
    write_json(output_root / "latest-score-summary.json", report)
    printable = deepcopy(report)
    if printable["participant"] is not None:
        for section in ("handoffModes", "retainedCallers"):
            printable["participant"].pop(section, None)
    print(json.dumps(printable, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"scorer setup error: {error}", file=sys.stderr)
        sys.exit(2)
