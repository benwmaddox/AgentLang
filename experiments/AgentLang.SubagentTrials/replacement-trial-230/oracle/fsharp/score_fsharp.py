#!/usr/bin/env python3
"""Build a disposable F# source copy and score monthly replacement timing."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[4]
ORACLE = HERE.parent
PRESERVATION_PATH = HERE / "preservation.py"
BASELINE_PATH = REPOSITORY / ".agentlang" / "replacement-trial-230" / "baselines" / "fsharp"
CASES_PATH = ORACLE / "cases.json"
MODEL_PATH = ORACLE / "model.py"
DEFAULT_EVIDENCE = (
    REPOSITORY / ".agentlang" / "replacement-trial-230" / "oracle-work" / "f"
)
BUILD_FLAGS = ["-m:1", "-nr:false", "-p:MSBuildEnableWorkloadResolver=false",
               "-p:NuGetAudit=true", "-p:WarningsNotAsErrors=NU1900"]
STORE_ARRAYS = {"customers", "products", "subscriptions", "invoices", "payments"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value) -> None:
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


def participant_sources(source: Path) -> tuple[Path, Path]:
    source = source.resolve()
    if source.is_file():
        if source.name.casefold() != "operations.fs":
            raise FileNotFoundError(f"Expected Operations.fs or a source directory: {source}")
        return source.with_name("Domain.fs"), source
    return source / "Domain.fs", source / "Operations.fs"


def canonical(value, key: str | None = None):
    if isinstance(value, dict):
        return {name: canonical(item, name) for name, item in value.items()}
    if isinstance(value, list):
        values = [canonical(item) for item in value]
        if key in STORE_ARRAYS:
            if values and isinstance(values[0], dict):
                values.sort(key=lambda item: item.get("id", ""))
            else:
                values.sort()
        return values
    return value


def parse_jsonl(stdout: str) -> tuple[list[dict], str | None]:
    rows = []
    for line_number, line in enumerate(stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as error:
            return rows, f"stdout line {line_number} is not JSONL: {error}"
        if not isinstance(row, dict):
            return rows, f"stdout line {line_number} is not a JSON object"
        rows.append(row)
    return rows, None


def _decode_output(value) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def run_process(command: list[str], *, timeout: int) -> tuple[subprocess.CompletedProcess, bool]:
    try:
        return subprocess.run(
            command, cwd=REPOSITORY, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout, check=False,
        ), False
    except subprocess.TimeoutExpired as error:
        partial = subprocess.CompletedProcess(
            command, -1, _decode_output(error.stdout), _decode_output(error.stderr)
        )
        return partial, True


def score_rows(rows: list[dict], cases: list[dict], *, verify_preservation: bool = True) -> dict:
    expected_ids = [item["id"] for item in cases]
    row_ids = [row.get("id") for row in rows]
    failures = []
    if row_ids != expected_ids:
        failures.append({"kind": "row-ids", "expected": expected_ids, "actual": row_ids})
    per_case = []
    for index, item in enumerate(cases):
        if index >= len(rows):
            break
        row = rows[index]
        if "setupFailure" in row:
            failures.append({"kind": "setup-failure", "caseId": item["id"], "detail": row["setupFailure"]})
            continue
        expected = canonical(item["expected"])
        actual = canonical(row.get("actual"))
        matching = actual == expected
        flags = {
            name: row.get(name) is True
            for name in ("inputPreserved", "entityMembershipPreserved", "instantPrecisionPreserved", "collateralPreserved")
        }
        if not matching:
            failures.append({"kind": "result-mismatch", "caseId": item["id"], "expected": expected, "actual": actual})
        if verify_preservation and not all(flags.values()):
            failures.append({"kind": "preservation-check", "caseId": item["id"], "flags": flags})
        per_case.append({"id": item["id"], "matches": matching, "preservation": flags})
    return {"status": "pass" if not failures else "failure", "caseResults": per_case, "failures": failures}


def run(participant: Path, evidence_root: Path, control_variant: str | None = None) -> dict:
    import importlib.util

    spec = importlib.util.spec_from_file_location("replacement_trial_230_model", MODEL_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load independent model at {MODEL_PATH}")
    model = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(model)

    cases = json.loads(CASES_PATH.read_text(encoding="utf-8"))
    if cases != model.make_cases():
        raise ValueError("Frozen cases.json differs from the independent model")
    mutation = "swap-monthly-timing" if control_variant == "swapped-timing" else None
    if control_variant is not None and control_variant not in {"correct", "swapped-timing"}:
        raise ValueError(f"Unknown calibration control variant: {control_variant}")
    scored_cases = [
        {**item, "expected": model.expected(item, mutation=mutation)}
        for item in cases
    ]
    source_domain, source_operations = participant_sources(participant)
    for source in (source_domain, source_operations):
        if not source.is_file():
            raise FileNotFoundError(f"Required participant source is missing: {source}")
    source_before = {path.name: sha256(path) for path in (source_domain, source_operations)}

    run_dir = next_run_directory(evidence_root)
    copied_source = run_dir / "participant-source"
    copied_source.mkdir()
    copied_domain = copied_source / "Domain.fs"
    copied_operations = copied_source / "Operations.fs"
    shutil.copyfile(source_domain, copied_domain)
    shutil.copyfile(source_operations, copied_operations)
    copied_before = {path.name: sha256(path) for path in (copied_domain, copied_operations)}
    if copied_before != source_before:
        raise RuntimeError("Disposable F# source copy does not match the participant source hashes")

    project = HERE / "ParticipantControl.fsproj"
    artifacts = run_dir / "artifacts"
    build_command = [
        "dotnet", "build", str(project), "--configuration", "Release",
        "--artifacts-path", str(artifacts), f"-p:ParticipantSourceDir={copied_source}",
        *BUILD_FLAGS,
    ]
    assembly = artifacts / "bin" / "ParticipantControl" / "release" / "ParticipantControl.dll"
    run_command = ["dotnet", str(assembly), "--cases", str(CASES_PATH)]
    write_json(run_dir / "invocation.json", {
        "buildCommand": build_command,
        "runCommand": run_command,
        "participantSource": str(participant.resolve()),
        "sourceSha256": source_before,
        "scoringCopySha256": copied_before,
        "selfTestsIncluded": False,
        "scoreKind": "calibration-control" if control_variant is not None else "participant-acceptance",
        "expectedVariant": control_variant or "correct",
    })
    build, build_timed_out = run_process(build_command, timeout=300)
    (run_dir / "build.stdout.log").write_text(build.stdout, encoding="utf-8")
    (run_dir / "build.stderr.log").write_text(build.stderr, encoding="utf-8")
    if build.returncode == 0 and not build_timed_out:
        completed, runtime_timed_out = run_process(run_command, timeout=120)
    else:
        completed = subprocess.CompletedProcess(run_command, build.returncode, "", "build failed; runtime not started\n")
        runtime_timed_out = False
    (run_dir / "stdout.log").write_text(completed.stdout, encoding="utf-8")
    (run_dir / "stderr.log").write_text(completed.stderr, encoding="utf-8")
    source_after = {path.name: sha256(path) for path in (source_domain, source_operations)}
    copied_after = {path.name: sha256(path) for path in (copied_domain, copied_operations)}
    if source_after != source_before:
        raise RuntimeError("Participant source changed while the independent grader was running")
    preservation_spec = importlib.util.spec_from_file_location(
        "replacement_trial_230_fsharp_preservation", PRESERVATION_PATH
    )
    if preservation_spec is None or preservation_spec.loader is None:
        raise RuntimeError(f"Cannot load F# preservation audit at {PRESERVATION_PATH}")
    preservation_module = importlib.util.module_from_spec(preservation_spec)
    preservation_spec.loader.exec_module(preservation_module)
    participant_directory = participant if participant.is_dir() else participant.parent
    preservation_audit = preservation_module.audit(participant_directory, BASELINE_PATH)
    expected_preservation_status = "pass"
    preservation_matches_expectation = preservation_audit.get("status") == expected_preservation_status
    rows, parse_error = parse_jsonl(completed.stdout)
    score = score_rows(rows, scored_cases,
                       verify_preservation=(control_variant in (None, "correct"))) if parse_error is None else {
        "status": "failure", "caseResults": [], "failures": [{"kind": "invalid-jsonl", "detail": parse_error}]
    }
    discrimination = None
    if control_variant is not None:
        predicted = [
            item["id"] for item, scored in zip(cases, scored_cases)
            if canonical(item["expected"]) != canonical(scored["expected"])
        ]
        observed = [
            item["id"] for item, row in zip(cases, rows)
            if canonical(item["expected"]) != canonical(row.get("actual"))
        ]
        discrimination = {
            "expectedVariant": control_variant,
            "predictedCaseIds": predicted,
            "observedCaseIds": observed,
            "matchesCanonicalModel": observed == predicted,
        }
        if observed != predicted:
            score["status"] = "failure"
            score["failures"].append({"kind": "control-discrimination", **discrimination})
    if build.returncode != 0 or completed.returncode != 0:
        score["status"] = "failure"
        score["failures"].append({
            "kind": "process-exit", "buildReturnCode": build.returncode,
            "runtimeReturnCode": completed.returncode,
        })
    if build_timed_out or runtime_timed_out:
        score["status"] = "failure"
        score["failures"].append({
            "kind": "process-timeout", "buildTimedOut": build_timed_out,
            "runtimeTimedOut": runtime_timed_out,
            "partialBuildStdoutSaved": bool(build.stdout),
            "partialBuildStderrSaved": bool(build.stderr),
            "partialRuntimeStdoutSaved": bool(completed.stdout),
            "partialRuntimeStderrSaved": bool(completed.stderr),
        })
    if copied_after != copied_before:
        score["status"] = "failure"
        score["failures"].append({"kind": "scoring-copy-mutated", "before": copied_before, "after": copied_after})
    if not preservation_matches_expectation:
        score["status"] = "failure"
        score["failures"].append({
            "kind": "source-preservation",
            "expectedStatus": expected_preservation_status,
            "actualStatus": preservation_audit.get("status"),
            "details": preservation_audit,
        })
    if control_variant is not None:
        score["status"] = "control-calibrated" if score["status"] == "pass" else "control-failure"
    report = {
        **score,
        "scoreKind": "calibration-control" if control_variant is not None else "participant-acceptance",
        "expectedVariant": control_variant or "correct",
        "discrimination": discrimination,
        "runDirectory": str(run_dir),
        "rowCount": len(rows),
        "sourceSha256": source_before,
        "sourceAfterSha256": source_after,
        "scoringCopySha256": copied_before,
        "selfTestsIncluded": False,
        "sourcePreservation": preservation_audit,
        "sourcePreservationExpectedStatus": expected_preservation_status,
        "sourcePreservationMatchesExpectation": preservation_matches_expectation,
        "buildReturnCode": build.returncode,
        "runtimeReturnCode": completed.returncode,
        "buildTimedOut": build_timed_out,
        "runtimeTimedOut": runtime_timed_out,
        "dependencyAudit": {
            "enabled": True,
            "status": "unavailable-network-warning" if "NU1900" in build.stdout + build.stderr else "no-NU1900-warning-observed",
            "nugetAuditWarningCount": (build.stdout + build.stderr).count("NU1900"),
            "note": "A NU1900 service-index warning means vulnerability data could not be fetched; build success does not certify an audit pass.",
        },
        "canonicalStoreComparison": "all keyed Store collections sorted by ID before complete projection comparison",
    }
    write_json(run_dir / "result.json", report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--participant", required=True, type=Path,
                        help="directory with Domain.fs and Operations.fs, or Operations.fs")
    parser.add_argument("--evidence-root", type=Path, default=DEFAULT_EVIDENCE)
    parser.add_argument("--control-variant", choices=("correct", "swapped-timing"),
                        help="Score a calibrated control against its predicted model outcome, separately from participant acceptance")
    args = parser.parse_args()
    report = run(args.participant, args.evidence_root.resolve(), args.control_variant)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["status"] in ("pass", "control-calibrated") else 1


if __name__ == "__main__":
    raise SystemExit(main())
