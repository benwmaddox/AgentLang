#!/usr/bin/env python3
"""Score the typed F# batch renewal against the independent 216 model."""

from __future__ import annotations

import argparse
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from typing import Any


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[4]
TRIAL = HERE.parents[1]
MODEL_PATH = REPOSITORY / "experiments/AgentLang.SubagentTrials/typed-batch-failure-216/oracle/model.py"
DEFAULT_SEED = TRIAL / "seed/fsharp"
DEFAULT_OUTPUT = REPOSITORY / ".agentlang/typed-batch-comparison-220/fsharp/attempts"
CONTROL_UNKNOWN_CUSTOMER_ID = "90000000-0000-0000-0000-000000000001"
FIXTURE_CUSTOMER_IDS = {
    "10000000-0000-0000-0000-000000000001",
    "10000000-0000-0000-0000-000000000002",
    "10000000-0000-0000-0000-000000000003",
}
BUILD_FLAGS = ["-m:1", "-nr:false", "-p:MSBuildEnableWorkloadResolver=false",
               "-p:WarningsNotAsErrors=NU1900"]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


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


def load_model():
    spec = importlib.util.spec_from_file_location("typed_batch_failure_216_model", MODEL_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load independent model at {MODEL_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_rows(stdout: str) -> tuple[list[dict[str, Any]], str | None]:
    rows = []
    for line_number, line in enumerate(stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            return rows, f"stdout line {line_number} is not JSONL: {error}"
    return rows, None


def row_id_failure(rows: list[dict[str, Any]], cases: list[dict[str, Any]]) -> str | None:
    row_ids = [row.get("id") for row in rows]
    expected_ids = [str(case["id"]) for case in cases]
    if any(not isinstance(identifier, str) for identifier in row_ids):
        return "one or more result rows have no string id"
    if len(set(row_ids)) != len(row_ids):
        return "result row IDs are not unique"
    if row_ids != expected_ids:
        return "result row IDs do not exactly match the expected case order"
    return None


def check_row_id_gate(cases: list[dict[str, Any]]) -> dict[str, Any]:
    expected = [{"id": str(case["id"])} for case in cases]
    duplicate = deepcopy(expected)
    duplicate[-1]["id"] = duplicate[0]["id"]
    missing = expected[:-1]
    extra = deepcopy(expected)
    extra[-1]["id"] = "unexpected-case"
    reordered = list(reversed(expected))
    checks = {
        "validRowsAccepted": row_id_failure(expected, cases) is None,
        "duplicateRowsRejected": row_id_failure(duplicate, cases) is not None,
        "missingRowsRejected": row_id_failure(missing, cases) is not None,
        "extraRowsRejected": row_id_failure(extra, cases) is not None,
        "reorderedRowsRejected": row_id_failure(reordered, cases) is not None,
    }
    return {"status": "pass" if all(checks.values()) else "control-failure", **checks}


def check_scoring_copy_gate() -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="copy-gate-", dir=REPOSITORY / ".agentlang/typed-batch-comparison-220/fsharp/tmp") as temporary:
        source_dir = Path(temporary)
        for name, contents in (("Domain.fs", "module Domain\n"), ("Operations.fs", "module Operations\n")):
            (source_dir / name).write_text(contents, encoding="utf-8")
        manifest = {"sourceCopySha256": {
            name: sha256(source_dir / name) for name in ("Domain.fs", "Operations.fs")
        }}
        intact_accepted = scoring_copy_unchanged(source_dir, manifest)
        (source_dir / "Operations.fs").write_text("module MutatedOperations\n", encoding="utf-8")
        mutated_rejected = not scoring_copy_unchanged(source_dir, manifest)
    checks = {"intactCopyAccepted": intact_accepted, "mutatedCopyRejected": mutated_rejected}
    return {"status": "pass" if all(checks.values()) else "control-failure", **checks}


def run_process(command: list[str], cwd: Path, env: dict[str, str], timeout: int = 300):
    return subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout, check=False)


def prepare_source(source: Path, destination: Path, calibration: str | None = None) -> dict[str, Any]:
    source = source.resolve()
    if source.is_file():
        if source.name.lower() != "operations.fs":
            raise FileNotFoundError(f"Expected Operations.fs or a source directory: {source}")
        operations = source
        domain = source.with_name("Domain.fs")
        source_root = source.parent
    else:
        operations = source / "Operations.fs"
        domain = source / "Domain.fs"
        source_root = source
    for required in (domain, operations):
        if not required.is_file():
            raise FileNotFoundError(f"Required participant source is missing: {required}")
    before = {str(path): sha256(path) for path in (domain, operations)}
    destination.mkdir(parents=True)
    domain_text = domain.read_text(encoding="utf-8-sig")
    operations_text = operations.read_text(encoding="utf-8-sig")
    if calibration is not None:
        domain_text += calibration_failure_type(calibration)
        marker = "    /// Renew subscriptions in list order and return the first error without partial Store state."
        if marker not in operations_text:
            raise ValueError("Calibration source does not contain the baseline renewBatch boundary")
        operations_text = operations_text[:operations_text.index(marker)] + calibration_operations(calibration)
    (destination / "Domain.fs").write_text(domain_text, encoding="utf-8")
    (destination / "Operations.fs").write_text(operations_text, encoding="utf-8")
    copied = {"Domain.fs": sha256(destination / "Domain.fs"),
              "Operations.fs": sha256(destination / "Operations.fs")}
    return {
        "sourceRoot": str(source_root),
        "sourceBeforeSha256": before,
        "sourceCopySha256": copied,
        "selfTestsIncluded": False,
    }


def scoring_copy_unchanged(source_dir: Path, manifest: dict[str, Any]) -> bool:
    return all(
        (source_dir / name).is_file() and sha256(source_dir / name) == digest
        for name, digest in manifest["sourceCopySha256"].items()
    )


def calibration_failure_type(mode: str) -> str:
    if mode == "wrongrecord":
        name = "UnexpectedSubscriptionFailure"
    else:
        name = "SubscriptionRenewalFailure"
    old_id_type = "string" if mode == "wrongoldid" else "SubscriptionId"
    cause_type = "obj" if mode == "wrongcause" else "DomainError"
    return f'''\n/// Typed failure payload added by an isolated calibration candidate.\ntype {name} =\n    {{ Index: int\n      OldId: {old_id_type}\n      Cause: {cause_type} }}\n'''


def calibration_operations(mode: str) -> str:
    failure_type = "UnexpectedSubscriptionFailure" if mode == "wrongrecord" else "SubscriptionRenewalFailure"
    old_id_value = "SubscriptionId.toString change.OldId" if mode == "wrongoldid" else "change.OldId"
    cause_value = "box cause" if mode == "wrongcause" else "cause"
    operation_at = "(at.AddMinutes(1.0))" if mode == "subday" else "at"
    add_extra = "Ok (addUnexpectedCustomer at current)" if mode == "extracustomer" else "Ok current"
    if mode == "alwayszero":
        failure_result = "Error { failure with Index = 0 }"
        on_failure = failure_result
    elif mode == "changedmessage":
        failure_result = 'Error { failure with Cause = { failure.Cause with Message = failure.Cause.Message + " (changed)" } }'
        on_failure = failure_result
    elif mode == "correct":
        failure_result = "Error failure"
        on_failure = failure_result
    else:
        failure_result = "Error failure"
        on_failure = "apply current (index + 1) (Some failure) tail"
    extra_helper = '''
    let private addUnexpectedCustomer (at: Instant) (store: Store) =
        let customerId =
            CustomerId.parse "__CONTROL_UNKNOWN_CUSTOMER_ID__"
            |> Result.defaultWith (fun error -> failwithf "control customer id: %s" (AgentLang.Business.Domain.DomainError.message error))
        let email =
            Email.create "unexpected@example.test"
            |> Result.defaultWith (fun error -> failwithf "control email: %s" (AgentLang.Business.Domain.DomainError.message error))
        let customer =
            Customer.create customerId email "regular" (Money.ofMinorUnits 1L) at
            |> Result.defaultWith (fun error -> failwithf "control customer: %s" (AgentLang.Business.Domain.DomainError.message error))
        AgentLang.Business.Domain.Store.addCustomer customer store
        |> Result.defaultWith (fun error -> failwithf "control store customer: %s" (AgentLang.Business.Domain.DomainError.message error))
'''
    extra_helper = extra_helper.replace("__CONTROL_UNKNOWN_CUSTOMER_ID__", CONTROL_UNKNOWN_CUSTOMER_ID)
    return f'''{extra_helper if mode == "extracustomer" else ""}    /// Isolated typed batch controls used to calibrate the independent scorer.
    let renewBatch (store: Store) (changes: SubscriptionRenewal list) (at: Instant)
                   : Result<Store, {failure_type}> =
        let rec apply current index latestFailure remaining =
            match remaining with
            | [] ->
                match latestFailure with
                | None -> {add_extra}
                | Some failure -> {failure_result}
            | change :: tail ->
                let step =
                    match change.Plan with
                    | Monthly -> renewMonthly current change.OldId change.NewId {operation_at} change.ExpiresAt
                    | Annual -> renewAnnual current change.OldId change.NewId {operation_at} change.ExpiresAt
                match step with
                | Ok next -> apply next (index + 1) latestFailure tail
                | Error cause ->
                    let failure = {{ Index = index; OldId = {old_id_value}; Cause = {cause_value} }}
                    {on_failure}
        apply store 0 None changes
'''


def score_rows(label: str, cases: list[dict[str, Any]], process, evidence: Path,
               command: list[str], predicted: dict[str, Any] | None = None) -> dict[str, Any]:
    (evidence / f"{label}-stdout.txt").write_text(process.stdout, encoding="utf-8")
    (evidence / f"{label}-stderr.txt").write_text(process.stderr, encoding="utf-8")
    rows, parse_error = parse_rows(process.stdout)
    setup_failure = None
    if process.returncode != 0:
        setup_failure = f"runner exited {process.returncode}"
    elif parse_error:
        setup_failure = parse_error
    elif len(rows) != len(cases):
        setup_failure = f"runner emitted {len(rows)} rows for {len(cases)} cases"
    elif row_id_failure(rows, cases):
        setup_failure = row_id_failure(rows, cases)
    results = []
    if setup_failure is None:
        for case, row in zip(cases, rows, strict=True):
            case_id = str(case["id"])
            if row.get("setupFailure"):
                results.append({"caseId": case_id, "executed": False, "passed": False,
                                "setupFailure": row["setupFailure"]})
                continue
            actual = row.get("actual")
            input_preserved = row.get("inputPreserved") is True
            collateral_preserved = row.get("collateralPreserved") is True
            membership_preserved = row.get("entityMembershipPreserved") is True
            precision_preserved = row.get("instantPrecisionPreserved") is True
            passed = (actual == case["expected"] and input_preserved and collateral_preserved
                      and membership_preserved and precision_preserved)
            result = {"caseId": case_id, "executed": True, "passed": passed,
                      "inputPreserved": input_preserved,
                      "entityMembershipPreserved": membership_preserved,
                      "instantPrecisionPreserved": precision_preserved,
                      "collateralPreserved": collateral_preserved}
            if not passed:
                result["expected"] = case["expected"]
                result["actual"] = actual
            results.append(result)
    write_json(evidence / f"{label}-results.json", results)
    passed = sum(item["passed"] for item in results)
    failures = [item["caseId"] for item in results if item.get("executed") and not item["passed"]]
    setup_failures = [item["caseId"] for item in results if not item.get("executed")]
    report = {
        "label": label,
        "status": "setup-failure" if setup_failure or setup_failures else
                  "pass" if passed == len(cases) else "behavioral-mismatch",
        "caseCount": len(cases),
        "executedCaseCount": sum(item.get("executed", False) for item in results),
        "passedCaseCount": passed,
        "failingCaseIds": failures,
        "setupFailureCaseIds": setup_failures,
        "setupFailure": setup_failure,
        "runnerExitCode": process.returncode,
        "runnerCommand": command,
        "evidence": {"results": str(evidence / f"{label}-results.json"),
                     "stdout": str(evidence / f"{label}-stdout.txt"),
                     "stderr": str(evidence / f"{label}-stderr.txt")},
    }
    if predicted is not None:
        report["predictedPassedCaseCount"] = predicted["passedCaseCount"]
        report["calibrationMatchesPrediction"] = (
            report["status"] == "pass" if predicted["passedCaseCount"] == len(cases)
            else report["status"] == "behavioral-mismatch"
                 and report["passedCaseCount"] == predicted["passedCaseCount"]
                 and failures == predicted["failingCaseIds"]
        )
    return report


def run_candidate(run_dir: Path, source: Path, cases: list[dict[str, Any]],
                  cases_path: Path, env: dict[str, str], label: str,
                  calibration: str | None = None,
                  predicted: dict[str, Any] | None = None) -> dict[str, Any]:
    source_dir = run_dir / f"{label}-participant-copy"
    manifest = prepare_source(source, source_dir, calibration)
    write_json(run_dir / f"{label}-source-manifest.json", manifest)
    project = HERE / "ParticipantControl.fsproj"
    build_command = ["dotnet", "build", str(project), *BUILD_FLAGS,
                     f"-p:ParticipantSourceDir={source_dir}"]
    build = run_process(build_command, HERE, env)
    (run_dir / f"{label}-build-stdout.txt").write_text(build.stdout, encoding="utf-8")
    (run_dir / f"{label}-build-stderr.txt").write_text(build.stderr, encoding="utf-8")
    copy_after_build = scoring_copy_unchanged(source_dir, manifest)
    if build.returncode != 0:
        source_after_build = all(
            sha256(Path(path)) == digest for path, digest in manifest["sourceBeforeSha256"].items()
        )
        return {"label": label, **manifest,
                "status": "setup-failure" if source_after_build and copy_after_build else "source-changed",
                "participantSourceUnchanged": source_after_build,
                "buildExitCode": build.returncode, "buildCommand": build_command,
                "scoringCopyBuildUnchanged": copy_after_build,
                "scoringCopyRunUnchanged": copy_after_build,
                "scoringCopyUnchanged": copy_after_build,
                "buildEvidence": {"stdout": str(run_dir / f"{label}-build-stdout.txt"),
                                 "stderr": str(run_dir / f"{label}-build-stderr.txt")}}
    dll = HERE / "bin/Debug/net9.0/ParticipantControl.dll"
    command = ["dotnet", str(dll), "--cases", str(cases_path)]
    process = run_process(command, HERE, env)
    copy_after_run = scoring_copy_unchanged(source_dir, manifest)
    result = score_rows(label, cases, process, run_dir, command, predicted)
    result.update(manifest)
    result["buildExitCode"] = build.returncode
    result["buildCommand"] = build_command
    result["buildEvidence"] = {"stdout": str(run_dir / f"{label}-build-stdout.txt"),
                               "stderr": str(run_dir / f"{label}-build-stderr.txt")}
    result["participantSourceUnchanged"] = all(
        sha256(Path(path)) == digest for path, digest in manifest["sourceBeforeSha256"].items()
    )
    result["scoringCopyBuildUnchanged"] = copy_after_build
    result["scoringCopyRunUnchanged"] = copy_after_run
    result["scoringCopyUnchanged"] = copy_after_build and copy_after_run
    if not result["participantSourceUnchanged"] or not result["scoringCopyUnchanged"]:
        result["status"] = "source-changed"
    return result


def _load_results(report: dict[str, Any], _run_dir: Path) -> list[dict[str, Any]]:
    evidence = report.get("evidence", {})
    path = evidence.get("results")
    if not path:
        return []
    return json.loads(Path(path).read_text(encoding="utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--participant", type=Path,
                        help="Saved participant source directory or its Operations.fs; source is copied before build.")
    parser.add_argument("--seed-directory", type=Path, default=DEFAULT_SEED,
                        help="Conventional source used only for isolated scorer calibration.")
    parser.add_argument("--output-directory", type=Path, default=DEFAULT_OUTPUT,
                        help="Evidence root; each run gets a retained fresh directory.")
    parser.add_argument("--calibrate", action="store_true",
                        help="Run the isolated correct, always-zero, last-error, and changed-message controls.")
    args = parser.parse_args()
    if args.calibrate == (args.participant is not None):
        parser.error("choose exactly one of --participant or --calibrate")

    model = load_model()
    checks = model.model_checks()
    cases = model.make_cases()
    if len(cases) != 9 or len({str(case["id"]) for case in cases}) != len(cases):
        raise ValueError(f"Expected nine frozen 216 cases, got {len(cases)}")
    if args.calibrate and CONTROL_UNKNOWN_CUSTOMER_ID in FIXTURE_CUSTOMER_IDS:
        raise ValueError("Extra-customer calibration ID must be absent from fixture and known customer IDs")
    run_dir = next_run_directory(args.output_directory.resolve())
    cases_path = run_dir / "cases.json"
    write_json(cases_path, cases)
    temp_dir = (REPOSITORY / ".agentlang/typed-batch-comparison-220/fsharp/tmp").resolve()
    temp_dir.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["TEMP"] = str(temp_dir)
    env["TMP"] = str(temp_dir)

    if args.calibrate:
        predicted = {
            "correct": {"passedCaseCount": 9, "failingCaseIds": []},
            "alwayszero": {"passedCaseCount": checks["alwaysZeroIndex"]["predictedPassed"],
                           "failingCaseIds": checks["alwaysZeroIndex"]["failureIds"]},
            "lasterror": {"passedCaseCount": checks["lastErrorWins"]["predictedPassed"],
                          "failingCaseIds": checks["lastErrorWins"]["failureIds"]},
            "changedmessage": {"passedCaseCount": checks["changedMessageSameCode"]["predictedPassed"],
                               "failingCaseIds": checks["changedMessageSameCode"]["failureIds"]},
        }
        controls = {}
        for name in ("correct", "alwayszero", "lasterror", "changedmessage"):
            controls[name] = run_candidate(run_dir, args.seed_directory.resolve(), cases,
                                            cases_path, env, f"control-{name}", name,
                                            predicted[name])
        row_id_checks = check_row_id_gate(cases)
        copy_gate_checks = check_scoring_copy_gate()
        coverage_controls = {}
        for name in ("subday", "extracustomer", "wrongrecord", "wrongoldid", "wrongcause"):
            coverage_controls[name] = run_candidate(
                run_dir, args.seed_directory.resolve(), cases, cases_path, env,
                f"coverage-{name}", name,
            )
        coverage_ok = (
            coverage_controls["subday"].get("status") == "behavioral-mismatch"
            and any(not item.get("instantPrecisionPreserved", True)
                    for item in _load_results(coverage_controls["subday"], run_dir))
            and coverage_controls["extracustomer"].get("status") == "behavioral-mismatch"
            and any(not item.get("entityMembershipPreserved", True)
                    for item in _load_results(coverage_controls["extracustomer"], run_dir))
            and all(coverage_controls[name].get("status") == "setup-failure"
                    and bool(coverage_controls[name].get("setupFailureCaseIds"))
                    for name in ("wrongrecord", "wrongoldid", "wrongcause"))
            and all(row.get("scoringCopyUnchanged") is True
                    for row in coverage_controls.values())
        )
        all_calibrated = (
            row_id_checks["status"] == "pass"
            and copy_gate_checks["status"] == "pass"
            and coverage_ok
            and all(row.get("calibrationMatchesPrediction") is True
                    and row.get("participantSourceUnchanged") is True
                    and row.get("scoringCopyUnchanged") is True
                    for row in controls.values())
            and all(row.get("participantSourceUnchanged") is True
                    and row.get("scoringCopyUnchanged") is True
                    for row in coverage_controls.values())
        )
        report = {"status": "pass" if all_calibrated else "control-failure",
                  "mode": "calibration", "runDirectory": str(run_dir),
                  "caseCount": len(cases), "modelChecks": checks, "controls": controls,
                  "rowIdGateChecks": row_id_checks, "scoringCopyGateChecks": copy_gate_checks,
                  "coverageControls": coverage_controls,
                  "extraCustomerControlId": CONTROL_UNKNOWN_CUSTOMER_ID,
                  "extraCustomerIdIsUnknown": CONTROL_UNKNOWN_CUSTOMER_ID not in FIXTURE_CUSTOMER_IDS,
                  "coverageControlsRejected": coverage_ok}
    else:
        result = run_candidate(run_dir, args.participant.resolve(), cases,
                               cases_path, env, "participant")
        report = {"status": "pass" if result.get("status") == "pass" and
                  result.get("participantSourceUnchanged") and result.get("scoringCopyUnchanged")
                  else "participant-rejected-or-setup-failure",
                  "mode": "participant", "runDirectory": str(run_dir),
                  "caseCount": len(cases), "modelChecks": checks,
                  "participant": result}
    write_json(run_dir / "score-summary.json", report)
    write_json(args.output_directory.resolve() / "latest-score-summary.json", report)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"scorer setup error: {error}", file=sys.stderr)
        sys.exit(2)
