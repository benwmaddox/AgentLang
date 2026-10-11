"""Score task-required vocabulary reuse using the frozen 210 model and scorer.

The 210 scorer's successor interface requires an eligible-helper file. This
adapter supplies the two predeclared, task-required creator functions through
that interface solely to reuse its held-out behavior, preservation, suite,
production-graph, and exact-trace-discovery checks. The compatibility file and
raw score reports are stored under an explicitly internal 210-adapter path; the
published 211 summary makes no optional-helper or spontaneous-creation claim.
"""

from __future__ import annotations

import argparse
from contextlib import redirect_stdout
import hashlib
import io
import json
from pathlib import Path
import sys


REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
SCORER_DIR = REPO / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210" / "oracle"
sys.path.insert(0, str(SCORER_DIR))
import score_organic_reuse as scorer  # noqa: E402


STUDY_ROOT = REPO / ".agentlang" / "public-vocabulary-reuse-211"
scorer.STUDY_ROOT = STUDY_ROOT
EXPECTED_PROJECT_FINGERPRINT = "390c0ebc697b735a21e5f12efe508b5e192f3d04c1a8947d9174f1835f62edd2"
TARGET_KIND = "task-required-creator-authored-library-functions"
TARGET_NAMES = ("subscription.renew-monthly", "subscription.renew-annual")
TARGET_SCHEMA_VERSION = 1
ADAPTER_NOTE = (
    "Internal compatibility input for organic-reuse-210's successor scorer only. "
    "These are task-required functions already present in the accepted creator snapshot; "
    "the 211 endpoint is vocabulary discovery and production reuse, not optional-helper eligibility."
)


def resolve_repo_path(path: Path) -> Path:
    return path.resolve() if path.is_absolute() else (REPO / path).resolve()


def validate_target_record(payload: dict) -> dict:
    if not isinstance(payload, dict) or set(payload) != {
        "schemaVersion", "targetKind", "acceptedProjectFingerprint", "targets"
    }:
        raise ValueError("Target record must contain only the version, kind, accepted fingerprint, and targets fields")
    if type(payload["schemaVersion"]) is not int or payload["schemaVersion"] != TARGET_SCHEMA_VERSION:
        raise ValueError("Unsupported vocabulary target schemaVersion")
    if payload["targetKind"] != TARGET_KIND:
        raise ValueError("Target record kind does not identify task-required creator-authored library functions")
    if payload["acceptedProjectFingerprint"] != EXPECTED_PROJECT_FINGERPRINT:
        raise ValueError("Target record is not bound to the predeclared accepted creator fingerprint")
    targets = payload["targets"]
    if not isinstance(targets, list) or any(not isinstance(row, dict) or set(row) != {"name"} for row in targets):
        raise ValueError("Each target must contain exactly one name field")
    names = [row["name"] for row in targets]
    if tuple(names) != TARGET_NAMES:
        raise ValueError(f"Targets must be exactly {list(TARGET_NAMES)} in the predeclared order")
    return payload


def inspect_targets(project: Path, names: tuple[str, ...] = TARGET_NAMES) -> dict:
    current, manifest, store = scorer.current_manifest(project)
    revisions = scorer.current_revisions(manifest)
    result = {
        "manifestHash": current["manifestHash"],
        "targets": {},
    }
    for name in names:
        entry = revisions.get(name)
        if entry is None:
            result["targets"][name] = {"present": False}
            continue
        revision = entry["revision"]
        source = scorer.definition_source(store, revision)
        declarations = [match.group(1) for match in scorer.FLOW_DECLARATION.finditer(source)]
        signature = scorer.flow_definition_signature(source)
        result["targets"][name] = {
            "present": True,
            "wordId": entry["word"]["wordId"],
            "revision": entry["word"]["currentRevision"],
            "definitionHash": revision["definition"]["hash"],
            "declarations": declarations,
            "signature": {"inputs": list(signature[0]), "output": signature[1]} if signature else None,
            "maturity": revision.get("maturity"),
            "ownTestHashes": [row["hash"] for row in revision.get("tests", []) if row.get("hash")],
        }
    return result


def bind_targets(creator_project: Path, payload: dict, seed_inventory: dict) -> dict:
    payload = validate_target_record(payload)
    fingerprint = scorer.project_fingerprint(creator_project)
    if fingerprint != payload["acceptedProjectFingerprint"]:
        raise ValueError("Accepted creator project fingerprint does not match the target record")
    seed_names = {row["name"] for row in seed_inventory["words"]}
    seed_ids = {row["wordId"] for row in seed_inventory["words"]}
    snapshot = inspect_targets(creator_project)
    bound = []
    for name in TARGET_NAMES:
        row = snapshot["targets"][name]
        if not row["present"]:
            raise ValueError(f"Accepted creator snapshot is missing required target {name}")
        if name in seed_names or row["wordId"] in seed_ids:
            raise ValueError(f"Required target {name} is not new relative to the frozen report-163 seed")
        if row["declarations"] != [name]:
            raise ValueError(f"Accepted source declaration does not exactly match target name {name}")
        signature = row["signature"]
        expected = scorer.CREATOR_SIGNATURE
        if not signature or (tuple(signature["inputs"]), signature["output"]) != expected:
            raise ValueError(f"Accepted source signature does not match the predeclared creator operation for {name}")
        if row["maturity"] != "library" or not row["ownTestHashes"]:
            raise ValueError(f"Accepted target {name} must be library vocabulary with its own tests")
        bound.append({key: row[key] for key in (
            "wordId", "revision", "definitionHash", "signature", "maturity", "ownTestHashes"
        )} | {"name": name})
    return {
        "targetKind": TARGET_KIND,
        "acceptedProjectFingerprint": fingerprint,
        "manifestHash": snapshot["manifestHash"],
        "targets": bound,
    }


def compare_target_preservation(binding: dict, actor_snapshot: dict) -> dict:
    differences = {}
    for expected in binding["targets"]:
        name = expected["name"]
        actual = actor_snapshot["targets"].get(name, {"present": False})
        keys = ("wordId", "revision", "definitionHash", "signature", "maturity", "ownTestHashes")
        changed = {key: {"expected": expected.get(key), "actual": actual.get(key)}
                   for key in keys if expected.get(key) != actual.get(key)}
        if not actual.get("present") or changed:
            differences[name] = {"missing": not actual.get("present"), "changed": changed}
    return {"passed": not differences, "changedTargets": differences,
            "checkedTargets": [row["name"] for row in binding["targets"]]}


def build_compatibility_record(binding: dict) -> dict:
    return {
        "acceptedProjectFingerprint": binding["acceptedProjectFingerprint"],
        "adapterSemanticNote": ADAPTER_NOTE,
        "eligibleHelpers": [{"name": name} for name in TARGET_NAMES],
    }


def _stage_summary(return_code: int | None, report: dict | None) -> dict:
    if report is None:
        return {"status": "score-unavailable", "returnCode": return_code}
    behavior = report["behavior"]
    suite = report["suite"]
    return {
        "status": report["status"],
        "returnCode": return_code,
        "behavior": {
            "caseCount": behavior["caseCount"],
            "executedCaseCount": behavior["executedCaseCount"],
            "passedCaseCount": behavior["passedCaseCount"],
        },
        "suiteAndExamples": {
            "testAllPassed": suite["testAllPassed"],
            "exampleOwnerCount": suite["exampleOwnerCount"],
            "examplesPassed": suite["examplesPassed"],
        },
        "libraryQualification": report["libraryQualification"],
        "preservation": report["preservation"],
        "actorProjectUnchangedDuringScore": report["actorProjectUnchangedDuringScore"],
    }


def assemble_summary(
    binding: dict,
    creator_code: int | None,
    creator_report: dict | None,
    successor_code: int | None,
    successor_report: dict | None,
    actor_snapshot: dict,
    compatibility_path: Path,
) -> dict:
    creator = _stage_summary(creator_code, creator_report)
    successor = _stage_summary(successor_code, successor_report)
    target_preservation = compare_target_preservation(binding, actor_snapshot)
    reuse_score = successor_report or {}
    discovery = reuse_score.get("discovery") or {}
    discovered = sorted(set(discovery.get("discoveredHelpers", [])) & set(TARGET_NAMES))
    raw_paths = (reuse_score.get("helperReuse") or {}).get("productionPaths", {})
    paths = {name: raw_paths[name] for name in TARGET_NAMES if name in raw_paths}
    all_discovered = set(discovered) == set(TARGET_NAMES)
    all_have_paths = all(name in paths and paths[name] for name in TARGET_NAMES)
    targets_ready = all(
        row.get("present") and row.get("maturity") == "library" and bool(row.get("ownTestHashes"))
        for row in actor_snapshot["targets"].values()
    )
    actor_unchanged = bool(
        creator_report and creator_report.get("actorProjectUnchangedDuringScore")
        and successor_report and successor_report.get("actorProjectUnchangedDuringScore")
    )
    stages_passed = bool(
        creator_code == 0 and creator_report and creator_report.get("status") == "passed"
        and successor_code == 0 and successor_report and successor_report.get("status") == "passed"
    )
    reuse_passed = all_discovered and all_have_paths
    status = "passed" if (
        stages_passed and target_preservation["passed"] and targets_ready
        and actor_unchanged and reuse_passed
    ) else "score-failure"
    return {
        "status": status,
        "endpoint": "task-required-vocabulary-reuse",
        "targetKind": TARGET_KIND,
        "interpretationLimit": (
            "This is discovery and production reuse of task-required functions already present in the accepted creator project. "
            "It does not measure spontaneous helper creation, causal benefit, or comparative advantage."
        ),
        "acceptedCreator": {
            "projectFingerprint": binding["acceptedProjectFingerprint"],
            "manifestHash": binding["manifestHash"],
            "targets": binding["targets"],
        },
        "creatorBehavior66": creator,
        "heldOutBatchBehavior9": successor,
        "targetFunctionStateOnParticipant": actor_snapshot["targets"],
        "targetFunctionsPreservedFromAcceptedCreator": target_preservation,
        "vocabularyReuse": {
            "targetNames": list(TARGET_NAMES),
            "discoveredTargetNames": discovered,
            "discoveryTraceStatus": discovery.get("status"),
            "metadataInspectedTargetNames": sorted(
                set(discovery.get("metadataInspectedHelpers", [])) & set(TARGET_NAMES)
            ),
            "productionPathsFromBatchRoot": paths,
            "allTargetsDiscovered": all_discovered,
            "allTargetsHaveProductionPath": all_have_paths,
            "passed": reuse_passed,
        },
        "participantProjectUnchangedAcrossScoring": actor_unchanged,
        "internal210Adapter": {
            "note": ADAPTER_NOTE,
            "compatibilityInput": str(compatibility_path.resolve()),
            "rawScorePaths": {
                "creator": creator_report.get("evidenceDirectory") if creator_report else None,
                "successor": successor_report.get("evidenceDirectory") if successor_report else None,
            },
        },
    }


def run_internal_stage(stage: str, args: argparse.Namespace, evidence_root: Path,
                       compatibility_path: Path | None) -> tuple[int, dict, Path]:
    stage_evidence = evidence_root / "internal-210-adapter" / stage
    stage_parent = stage_evidence / stage
    before = set(stage_parent.iterdir()) if stage_parent.is_dir() else set()
    command_args = [
        "--stage", stage,
        "--cli", str(resolve_repo_path(args.cli)),
        "--actor-project", str(resolve_repo_path(args.actor_project)),
        "--seed-project", str(resolve_repo_path(args.seed_project)),
        "--evidence-root", str(stage_evidence),
    ]
    if stage == "successor":
        command_args.extend([
            "--creator-project", str(resolve_repo_path(args.creator_project)),
            "--eligible-helpers", str(compatibility_path),
            "--trace", str(resolve_repo_path(args.trace)),
        ])
    old_argv = sys.argv
    captured = io.StringIO()
    try:
        sys.argv = [str(SCORER_DIR / "score_organic_reuse.py"), *command_args]
        with redirect_stdout(captured):
            return_code = scorer.main()
    finally:
        sys.argv = old_argv
    created = sorted(
        (path for path in stage_parent.iterdir() if path.is_dir() and path not in before),
        key=lambda path: path.name,
    ) if stage_parent.is_dir() else []
    if len(created) != 1:
        raise RuntimeError(f"Expected exactly one internal {stage} scorer output, found {len(created)}")
    report_path = created[0] / "score.json"
    return return_code, json.loads(report_path.read_text(encoding="utf-8")), report_path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Root-approved current pinned Release CLI")
    parser.add_argument("--actor-project", type=Path, required=True, help="Final saved participant project")
    parser.add_argument("--creator-project", type=Path, required=True, help="Accepted organic-reuse-210 creator snapshot")
    parser.add_argument("--targets", type=Path, required=True, help="Predeclared task-required vocabulary target record")
    parser.add_argument("--trace", type=Path, required=True, help="Assigned 211 broker trace")
    parser.add_argument("--seed-project", type=Path, default=scorer.DEFAULT_SEED_PROJECT)
    parser.add_argument("--evidence-root", type=Path, required=True, help="Evidence root below .agentlang/public-vocabulary-reuse-211/")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    evidence_root = scorer.validate_evidence_root(args.evidence_root)
    cli = resolve_repo_path(args.cli)
    actor_project = resolve_repo_path(args.actor_project)
    creator_project = resolve_repo_path(args.creator_project)
    seed_project = resolve_repo_path(args.seed_project)
    targets_path = resolve_repo_path(args.targets)
    if not cli.is_file() or not actor_project.is_dir() or not creator_project.is_dir() or not seed_project.is_dir():
        raise FileNotFoundError("The pinned CLI and participant, accepted creator, and seed projects must exist")
    payload = validate_target_record(json.loads(targets_path.read_text(encoding="utf-8")))
    seed_inventory, _ = scorer.validate_seed_inventory(seed_project)
    binding = bind_targets(creator_project, payload, seed_inventory)
    initial_actor_fingerprint = scorer.project_fingerprint(actor_project)
    compatibility_path = evidence_root / "internal-210-adapter" / "inputs" / "task-required-targets-as-compatibility-input.json"
    compatibility_path.parent.mkdir(parents=True, exist_ok=True)
    compatibility_path.write_text(json.dumps(build_compatibility_record(binding), indent=2) + "\n", encoding="utf-8")

    creator_code, creator_report, _ = run_internal_stage("creator", args, evidence_root, None)
    successor_code, successor_report, _ = run_internal_stage("successor", args, evidence_root, compatibility_path)
    actor_snapshot = inspect_targets(actor_project)
    final_actor_fingerprint = scorer.project_fingerprint(actor_project)
    unchanged = initial_actor_fingerprint == final_actor_fingerprint
    summary = assemble_summary(binding, creator_code, creator_report, successor_code,
                               successor_report, actor_snapshot, compatibility_path)
    summary["participantProjectUnchangedAcrossScoring"] = bool(
        summary["participantProjectUnchangedAcrossScoring"] and unchanged
    )
    if not unchanged:
        summary["status"] = "score-failure"
    summary["participantProjectFingerprint"] = final_actor_fingerprint
    summary_path = evidence_root / "vocabulary-reuse-summary.json"
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
