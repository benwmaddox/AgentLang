#!/usr/bin/env python3
"""Create a reviewable, undispatched freeze candidate for scheduled-batch-226."""

from __future__ import annotations

import hashlib
import json
import random
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[3]
EXPERIMENT = REPO / "experiments/AgentLang.SubagentTrials/scheduled-batch-226"
DATA = REPO / ".agentlang/scheduled-batch-226"
SETUP_PATH = DATA / "setup.json"
CANDIDATE_PATH = DATA / "freeze-candidate.json"
SUPPLEMENT_PATH = DATA / "freeze-supplement.json"
VARIANTS = ("correct", "shared-instant", "sort-by-at", "last-error", "partial-success")
EXCLUDED = {".git", ".vs", "bin", "obj", "__pycache__", "cache", "caches"}
AGENTLANG_CONTROLS = {
    "correct": "controls/agentlang-candidate09/correct",
    "shared-instant": "controls/agentlang-candidate10/shared-instant",
    "sort-by-at": "controls/agentlang-candidate10/sort-by-at",
    "last-error": "controls/agentlang-candidate12/last-error",
    "partial-success": "controls/agentlang-candidate14/partial-success",
}
FSharp_CONTROLS = {name: f"controls/fsharp-calibrated/{name}" for name in VARIANTS}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rel(path: Path) -> str:
    return path.relative_to(REPO).as_posix()


def inventory(root: Path) -> list[dict[str, Any]]:
    if not root.is_dir():
        raise RuntimeError(f"Missing input directory: {root}")
    rows: list[dict[str, Any]] = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root)
        if any(part.casefold() in EXCLUDED for part in relative.parts):
            continue
        if path.is_symlink():
            raise RuntimeError(f"Refusing symlink in frozen input: {path}")
        if path.is_file():
            content = path.read_bytes()
            rows.append({"path": relative.as_posix(), "bytes": len(content), "sha256": digest(content)})
    return rows


def tree_hash(rows: list[dict[str, Any]]) -> str:
    raw = json.dumps(rows, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    return digest(raw)


def source_fingerprint(root: Path) -> str:
    checksum = hashlib.sha256()
    for row in inventory(root):
        name = row["path"].encode()
        content = (root / row["path"]).read_bytes()
        checksum.update(len(name).to_bytes(8, "big"))
        checksum.update(name)
        checksum.update(len(content).to_bytes(8, "big"))
        checksum.update(content)
    return checksum.hexdigest()


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def result_files(root: Path) -> list[Path]:
    if not root.is_dir():
        return []
    found = []
    for path in root.rglob("result.json"):
        if any(part.casefold() in EXCLUDED for part in path.relative_to(root).parts):
            continue
        if path.is_file():
            found.append(path)
    return sorted(found, key=lambda path: rel(path))


def validate_control_result(path: Path, variant: str, expected_cases: set[str]) -> dict[str, Any]:
    result = load_json(path)
    if result.get("status") != "control-calibrated":
        raise RuntimeError(f"{rel(path)} is not a completed calibration result.")
    if result.get("expectedVariant") != variant or result.get("rowCount") != 7:
        raise RuntimeError(f"{rel(path)} is not the expected seven-case {variant} result.")
    if result.get("buildReturnCode") != 0 or result.get("runtimeReturnCode") != 0:
        raise RuntimeError(f"{rel(path)} reports a failed build or control execution.")
    if result.get("buildTimedOut") or result.get("runtimeTimedOut"):
        raise RuntimeError(f"{rel(path)} reports a timeout.")
    rows = result.get("caseResults")
    if not isinstance(rows, list) or len(rows) != 7 or any(row.get("matches") is not True for row in rows):
        raise RuntimeError(f"{rel(path)} did not match all seven model outcomes.")
    if result.get("failures"):
        raise RuntimeError(f"{rel(path)} contains failures.")
    discrimination = result.get("discrimination", {})
    predicted = set(discrimination.get("predictedCaseIds", []))
    observed = set(discrimination.get("observedCaseIds", []))
    if predicted != expected_cases or observed != expected_cases or discrimination.get("matchesCanonicalModel") is not True:
        raise RuntimeError(f"{rel(path)} discrimination does not match the model for {variant}.")
    before = result.get("sourceSha256")
    after = result.get("sourceAfterSha256")
    if before is not None and after != before:
        raise RuntimeError(f"{rel(path)} reports participant source changes during scoring.")
    return {
        "result": rel(path),
        "resultSha256": digest(path.read_bytes()),
        "variant": variant,
        "rowCount": 7,
        "predictedCaseIds": sorted(predicted),
        "observedCaseIds": sorted(observed),
        "runDirectory": result.get("runDirectory"),
    }


def validate_language_calibration(path: Path, expected_cases: dict[str, set[str]]) -> dict[str, Any]:
    result = load_json(path)
    if result.get("status") != "passed" or result.get("mode") != "preflight" or result.get("sourcePurpose") != "calibration":
        raise RuntimeError("The AgentLang receipt must be the completed calibration run, not participant acceptance.")
    if result.get("seedProjectFingerprint") != result.get("expectedSeedFingerprint") or result.get("seedProjectFingerprint") != "b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611":
        raise RuntimeError("AgentLang calibration used an unexpected retained seed.")
    if result.get("actorProjectUnchanged") is not True or result.get("actorProjectFingerprintBefore") != result.get("actorProjectFingerprintAfter"):
        raise RuntimeError("AgentLang correct control changed during independent scoring.")
    if result.get("inheritedProjectionPreserved", {}).get("passed") is not True or result.get("failures"):
        raise RuntimeError("AgentLang correct control did not preserve inherited projections cleanly.")
    if result.get("controlSetComplete") is not True:
        raise RuntimeError("The AgentLang receipt does not contain the complete five-control set.")
    type_gate = result.get("typeGateControls", {})
    if type_gate.get("status") != "passed" or type_gate.get("seedUnchanged") is not True:
        raise RuntimeError("The AgentLang calibration's semantic type controls did not pass.")

    suite = result.get("suite", {})
    if suite.get("status") != "passed" or suite.get("scoreKind") != "participant-acceptance" or suite.get("controlVariant") is not None:
        raise RuntimeError("The correct AgentLang control did not complete its full suite.")
    if suite.get("cliExitCode") != 0 or suite.get("testAllPassed") is not True or suite.get("failures"):
        raise RuntimeError("The correct AgentLang control has a build, test, or runtime failure.")
    if suite.get("sourceProjectFingerprintBefore") != suite.get("sourceProjectFingerprintAfter"):
        raise RuntimeError("The correct AgentLang control source changed during scoring.")
    scheduled = suite.get("scheduledCases", {})
    inherited = suite.get("inheritedCreatorCases", {})
    if (scheduled.get("count"), scheduled.get("executed"), scheduled.get("matched")) != (7, 7, 7):
        raise RuntimeError("The correct AgentLang control did not match all seven scheduled cases.")
    if (inherited.get("count"), inherited.get("executed"), inherited.get("matched")) != (66, 66, 66):
        raise RuntimeError("The correct AgentLang control did not match all 66 inherited cases.")

    controls = {item.get("controlVariant"): item for item in result.get("controls", [])}
    if set(controls) != set(VARIANTS):
        raise RuntimeError("The AgentLang receipt is missing one or more bounded mutation controls.")
    evidence: dict[str, Any] = {}
    for variant in VARIANTS:
        item = controls[variant]
        cases = item.get("scheduledCases", {})
        if item.get("status") != "control-calibrated" or item.get("scoreKind") != "calibration-control":
            raise RuntimeError(f"AgentLang {variant} is not marked as a calibration control.")
        if (cases.get("count"), cases.get("executed"), cases.get("matched")) != (7, 7, 7):
            raise RuntimeError(f"AgentLang {variant} did not match all seven scheduled cases.")
        if item.get("cliExitCode") != 0 or item.get("testAllPassed") is not True or item.get("failures"):
            raise RuntimeError(f"AgentLang {variant} control failed execution or inherited tests.")
        if item.get("matchesPredictedModel") is not True or item.get("mutationDiscriminationPassed") is not True:
            raise RuntimeError(f"AgentLang {variant} did not match and discriminate its canonical model.")
        if item.get("sourceProjectFingerprintBefore") != item.get("sourceProjectFingerprintAfter"):
            raise RuntimeError(f"AgentLang {variant} source changed during scoring.")
        if not expected_cases[variant] and variant != "correct":
            raise RuntimeError(f"Model unexpectedly predicts no discrimination for {variant}.")
        evidence[variant] = {
            "result": rel(path),
            "resultSha256": digest(path.read_bytes()),
            "variant": variant,
            "rowCount": 7,
            "predictedModelMatched": True,
            "evidenceDirectory": item.get("evidenceDirectory"),
        }
    correct_evidence = {
        "result": rel(path),
        "resultSha256": digest(path.read_bytes()),
        "variant": "correct-suite",
        "scheduledCases": 7,
        "inheritedCases": 66,
        "inheritedProjectionPreserved": True,
        "evidenceDirectory": suite.get("evidenceDirectory"),
    }
    return {"controls": evidence, "correctSuite": correct_evidence}


def write_supplement() -> dict[str, Any]:
    if SUPPLEMENT_PATH.exists():
        raise RuntimeError(f"Refusing to overwrite immutable supplement: {SUPPLEMENT_PATH}")
    if not CANDIDATE_PATH.is_file():
        raise RuntimeError("Create and review the original freeze candidate before adding supplemental pins.")
    candidate = load_json(CANDIDATE_PATH)
    if candidate.get("status") != "awaiting-independent-review" or candidate.get("participantsDispatched") != 0:
        raise RuntimeError("Supplement requires the unchanged undispatched review candidate.")

    # These are transitive inputs loaded by the existing scorer and actual V2
    # broker, plus the source compiled by the F# scorer's Business project ref.
    additional = [
        EXPERIMENT / "freeze.py",
        REPO / "scripts/Start-SubagentTrialHostV2.ps1",
        REPO / "scripts/Audit-SubagentTrialTerminationV2.ps1",
        REPO / "experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/cell_model_163.py",
        REPO / "experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/model.py",
        REPO / "experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/cases.json",
        REPO / "experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/score_organic_reuse.py",
        REPO / "experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle/score_agentlang.py",
        REPO / "experiments/AgentLang.Business/AgentLang.Business.fsproj",
        REPO / "experiments/AgentLang.Business/Business.fs",
    ]
    missing = [path for path in additional if not path.is_file()]
    if missing:
        raise RuntimeError("Missing transitive input(s): " + ", ".join(map(str, missing)))

    # Record any MSBuild or NuGet configuration inherited by either the scorer
    # or its project reference. No matching config files currently exist.
    config_names = {"Directory.Build.props", "Directory.Build.targets", "global.json", "NuGet.Config", "nuget.config"}
    roots = [
        EXPERIMENT / "oracle/fsharp",
        REPO / "experiments/AgentLang.Business",
        REPO,
    ]
    config_paths: set[Path] = set()
    for start in roots:
        current = start.resolve()
        while current == REPO or REPO in current.parents:
            for name in config_names:
                path = current / name
                if path.is_file():
                    config_paths.add(path)
            if current == REPO:
                break
            current = current.parent

    rows = []
    for path in sorted([*additional, *config_paths], key=rel):
        raw = path.read_bytes()
        rows.append({"path": rel(path), "bytes": len(raw), "sha256": digest(raw)})
    supplement = {
        "study": "scheduled-batch-226",
        "status": "awaiting-independent-review",
        "extends": {
            "candidate": rel(CANDIDATE_PATH),
            "sha256": digest(CANDIDATE_PATH.read_bytes()),
            "launchOrder": candidate["launchOrder"],
        },
        "additionalPinnedInputs": rows,
        "applicableBuildConfigFiles": [row["path"] for row in rows if Path(row["path"]).name in config_names],
        "note": "Supplemental transitive dependency pins only; participant results were not rebuilt and the sampled launch order was reused.",
    }
    SUPPLEMENT_PATH.write_text(json.dumps(supplement, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="")
    return {
        "status": supplement["status"],
        "supplement": rel(SUPPLEMENT_PATH),
        "candidateSha256": supplement["extends"]["sha256"],
        "launchOrder": supplement["extends"]["launchOrder"],
        "additionalPins": len(rows),
        "buildConfigFiles": supplement["applicableBuildConfigFiles"],
    }


def main() -> int:
    if "--supplement" in sys.argv[1:]:
        result = write_supplement()
        print(json.dumps(result, indent=2))
        return 0
    if CANDIDATE_PATH.exists():
        raise RuntimeError(f"Refusing to overwrite immutable candidate: {CANDIDATE_PATH}")

    setup = load_json(SETUP_PATH)
    if setup.get("status") != "prepared226" or setup.get("participantsDispatched") != 0 or setup.get("dispatchAllowed") is not False:
        raise RuntimeError("Candidate creation requires the pristine prepared, undispatched state.")

    preflight_path = DATA / "root/preflight.json"
    preflight = load_json(preflight_path)
    if preflight.get("status") != "passed" or preflight.get("participantsDispatched") != 0:
        raise RuntimeError("Pristine V2 broker validation has not passed.")
    if set(preflight.get("arms", {})) != {"retained", "fsharp"}:
        raise RuntimeError("Preflight receipt does not cover both arms.")
    if any(arm.get("operationOk") is not True or arm.get("hostCloseOk") is not True or arm.get("exitCode") != 0 or arm.get("auditExitCode") != 0 for arm in preflight["arms"].values()):
        raise RuntimeError("A pristine broker operation or normal-close audit failed.")

    type_path = DATA / "root-type-gates/result.json"
    type_receipt = load_json(type_path)
    type_cases = type_receipt.get("cases", [])
    if type_receipt.get("status") != "passed" or type_receipt.get("seedUnchanged") is not True:
        raise RuntimeError("Actual semantic type-gate probes have not passed with the seed unchanged.")
    if {case.get("name") for case in type_cases} != {"newline", "semicolon", "wrong-field-type"} or any(case.get("status") != "passed" or case.get("cliExitCode") != 0 for case in type_cases):
        raise RuntimeError("Actual semantic type-gate probes are incomplete or failed.")

    model_path = EXPERIMENT / "oracle/model-checks.json"
    model_checks = load_json(model_path)
    if model_checks.get("status") != "model-controls-passed" or model_checks.get("caseCount") != 7:
        raise RuntimeError("Seven-case oracle model checks are not complete.")
    expected_cases = {name: set(model_checks["discrimination"].get(name, [])) for name in VARIANTS}

    fsharp_root = DATA / "oracle-work/fsharp-calibrated-final"
    fsharp_results: dict[str, dict[str, Any]] = {}
    for variant in VARIANTS:
        paths = [path for path in result_files(fsharp_root) if load_json(path).get("expectedVariant") == variant]
        valid = []
        for path in paths:
            try:
                valid.append(validate_control_result(path, variant, expected_cases[variant]))
            except (RuntimeError, OSError, json.JSONDecodeError):
                continue
        if not valid:
            raise RuntimeError(f"No completed, matching F# calibration receipt found for {variant}.")
        fsharp_results[variant] = valid[-1]

    language_root = DATA / "oracle-work/language"
    language_paths = sorted(language_root.rglob("language-score.json"), key=lambda path: path.stat().st_mtime_ns)
    language_candidates = []
    for path in language_paths:
        try:
            result = load_json(path)
            if result.get("sourcePurpose") == "calibration" and result.get("status") == "passed":
                language_candidates.append(path)
        except (OSError, json.JSONDecodeError):
            continue
    if not language_candidates:
        raise RuntimeError("No completed AgentLang calibration language-score.json was found.")
    language_receipt = language_candidates[-1]
    language_summary = validate_language_calibration(language_receipt, expected_cases)
    language_results = language_summary["controls"]

    baseline_rows: dict[str, dict[str, Any]] = {}
    for arm in ("retained", "fsharp"):
        baseline = DATA / "baselines" / arm
        actor = DATA / "actors" / arm
        baseline_inventory = inventory(baseline)
        actor_inventory = inventory(actor)
        if baseline_inventory != actor_inventory:
            raise RuntimeError(f"Pristine actor and baseline diverged for {arm}.")
        recorded = setup.get("baselines", {}).get(arm, {})
        if tree_hash(baseline_inventory) != recorded.get("baselineTreeSha256"):
            raise RuntimeError(f"Baseline inventory differs from prepared setup for {arm}.")
        baseline_rows[arm] = {
            "baseline": rel(baseline),
            "actor": rel(actor),
            "fileCount": len(baseline_inventory),
            "treeSha256": tree_hash(baseline_inventory),
            "byteIdentical": True,
        }

    retained_source = REPO / ".agentlang/typed-batch-comparison-220/baselines/retained"
    if source_fingerprint(retained_source) != setup.get("retainedBaselineFingerprint"):
        raise RuntimeError("Pinned 220 retained baseline fingerprint has changed.")

    runtime_rows: dict[str, Any] = {}
    for runtime_name, runtime_record in setup.get("freshRuntimes", {}).items():
        runtime_dir = DATA / "runtime" / runtime_name
        rows = inventory(runtime_dir)
        if rows != runtime_record.get("files"):
            raise RuntimeError(f"Private fresh {runtime_name} runtime changed after preparation.")
        runtime_rows[runtime_name] = {"path": rel(runtime_dir), "files": rows, "treeSha256": tree_hash(rows)}

    active_control_rows: dict[str, Any] = {}
    for language, roots in (("agentlang", AGENTLANG_CONTROLS), ("fsharp", FSharp_CONTROLS)):
        active_control_rows[language] = {}
        for variant, relative in roots.items():
            root = DATA / relative
            rows = inventory(root)
            active_control_rows[language][variant] = {
                "path": rel(root),
                "fileCount": len(rows),
                "treeSha256": tree_hash(rows),
                "files": rows,
            }

    public_inputs = [
        EXPERIMENT / name for name in (
            "PLAN.md", "task.md", "bootstrap.md", "prepare.py",
            "controls/build_agentlang_controls.py", "controls/build_fsharp_controls.py",
            "oracle/cases.json", "oracle/model.py", "oracle/model-checks.json",
            "oracle/score_language.py", "oracle/type_gate.py", "oracle/unit_checks.py",
            "oracle/fsharp/ParticipantControl.fsproj", "oracle/fsharp/ParticipantProgram.fs",
            "oracle/fsharp/Scorer.fs", "oracle/fsharp/StoreReflection.fs", "oracle/fsharp/score_fsharp.py",
        )
    ]
    missing_public = [path for path in public_inputs if not path.is_file()]
    if missing_public:
        raise RuntimeError("Missing public freeze input(s): " + ", ".join(map(str, missing_public)))

    tracked_and_hidden_producers = [
        EXPERIMENT / "controls/build_agentlang_controls.py",
        EXPERIMENT / "controls/build_fsharp_controls.py",
        DATA / "controls/build_agentlang_controls.py",
        DATA / "controls/build_fsharp_controls.py",
    ]
    if any(not path.is_file() for path in tracked_and_hidden_producers):
        raise RuntimeError("A reproducible public or as-run control producer is missing.")

    files_to_hash = [
        *public_inputs,
        *tracked_and_hidden_producers,
        DATA / "packets/retained.md",
        DATA / "packets/fsharp.md",
        DATA / "launchers/retained.ps1",
        DATA / "launchers/fsharp.ps1",
        DATA / "root/preflight.json",
        DATA / "root-type-gates/result.json",
        DATA / "root-builds/cli.log",
        DATA / "root-builds/business.log",
        DATA / "root-builds/conventional-cli.log",
        DATA / "root-builds/broker-verification.log",
        DATA / "oracle-work/unit-checks.log",
        language_receipt,
    ]
    input_hashes = []
    for path in files_to_hash:
        if not path.is_file():
            raise RuntimeError(f"Missing required freeze input: {path}")
        raw = path.read_bytes()
        input_hashes.append({"path": rel(path), "bytes": len(raw), "sha256": digest(raw)})

    calibration_dirs: dict[str, list[dict[str, Any]]] = {}
    for name, result_map in (("agentlang", language_results), ("fsharp", fsharp_results)):
        calibration_dirs[name] = []
        for evidence in result_map.values():
            result_path = REPO / evidence["result"]
            result = load_json(result_path)
            run_text = evidence.get("evidenceDirectory") or result.get("runDirectory")
            run_dir = Path(run_text) if run_text else result_path.parent
            if not run_dir.is_absolute():
                run_dir = REPO / run_dir
            calibration_dirs[name].append({
                "variant": evidence["variant"],
                "path": str(run_dir),
                "files": inventory(run_dir),
            })

    arms = ["retained", "fsharp"]
    launch_order = random.SystemRandom().sample(arms, k=len(arms))
    candidate = {
        "study": "scheduled-batch-226",
        "status": "awaiting-independent-review",
        "dispatchAllowed": False,
        "participantsDispatched": 0,
        "launchOrder": launch_order,
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "retainedBaselineFingerprint": setup["retainedBaselineFingerprint"],
        "baselineCopies": baseline_rows,
        "privateRuntimes": runtime_rows,
        "pristinePreflight": {"path": rel(preflight_path), "sha256": digest(preflight_path.read_bytes()), "status": preflight["status"]},
        "semanticTypeGate": {"path": rel(type_path), "sha256": digest(type_path.read_bytes()), "status": type_receipt["status"], "cases": [case["name"] for case in type_cases]},
        "model": {"path": rel(model_path), "sha256": digest(model_path.read_bytes()), "status": model_checks["status"], "caseCount": model_checks["caseCount"]},
        "controlCalibration": {"agentlang": language_summary, "fsharp": fsharp_results},
        "activeControlSources": active_control_rows,
        "calibrationEvidence": calibration_dirs,
        "inputHashes": input_hashes,
        "setupPreparedSha256": digest(SETUP_PATH.read_bytes()),
        "awaiting": "independent review of this candidate and actual control receipts; coordinator finalization is required before dispatch",
    }
    CANDIDATE_PATH.write_text(json.dumps(candidate, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="")
    print(json.dumps({
        "status": candidate["status"],
        "candidate": rel(CANDIDATE_PATH),
        "sha256": digest(CANDIDATE_PATH.read_bytes()),
        "launchOrder": launch_order,
        "agentlangControls": sorted(language_results),
        "fsharpControls": sorted(fsharp_results),
        "dispatchAllowed": False,
    }, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"FREEZE_CANDIDATE_FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
