"""Independent preview acceptance, reusing existing fixture encoders and runners.

No participant dispatch. Source submissions are copied unchanged into disposable
scoring projects. The 33 historical fixtures are extended by four zero-period
precedence combinations; expected behavior comes from the frozen integer model.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import traceback
from uuid import uuid4

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
EVIDENCE = REPO / ".agentlang/efficacy-maintenance-178/oracle"
OLD = REPO / "experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def cases():
    model = load_module("preview178_model", OLD / "model.py")
    historical = json.loads((OLD / "cases.json").read_text(encoding="utf-8"))
    by_id = {row["id"]: row for row in model.CASES}
    if (len(historical) != 33 or {row["id"] for row in historical} != set(by_id)
            or any({key: value for key, value in row.items() if key != "expected"} != by_id[row["id"]]
                   or row["expected"] != model.expected(by_id[row["id"]]) for row in historical)):
        raise ValueError("Historical cases disagree with their independent model")
    result = deepcopy(historical)
    for source_id, new_id in [
        ("missing-old", "zero-period-missing-old"),
        ("already-cancelled-old", "zero-period-cancelled-old"),
        ("before-original-start", "zero-period-before-old-start"),
        ("duplicate-precedes-blank-term", "zero-period-duplicate-and-blank-term"),
    ]:
        row = deepcopy(by_id[source_id])
        row["id"] = new_id
        row["request"]["expiry"] = row["request"]["at"]
        before = deepcopy(row)
        row["expected"] = model.expected(row)
        if {key: value for key, value in row.items() if key != "expected"} != before:
            raise ValueError("Acceptance model mutated its input")
        result.append(row)
    return result


def flow_score(project, cli, label, rows):
    adapter_path = REPO / ".agentlang/preview-retention-171/drafts/score_adapter.py"
    adapter = load_module("preview178_flow_adapter", adapter_path)
    adapter.EVIDENCE_ROOT = EVIDENCE / "flow"
    adapter.ORACLE = HERE
    adapter.load_cases = lambda: deepcopy(rows)
    result = adapter.score_actor(project, cli, "control", label)
    result["adapterSource"] = str(adapter_path.relative_to(REPO))
    result["adapterSha256"] = sha(adapter_path)
    return result


def fsharp_score(project, label, rows):
    adapter_path = REPO / "experiments/AgentLang.SubagentTrials/subscription-handoff-maintenance-166/oracle/conventional/score_conventional.py"
    adapter = load_module("preview178_fsharp_adapter", adapter_path)
    run = adapter.next_run_directory(EVIDENCE / "fsharp" / label)
    scoring = run / "project"
    operations = project / "Operations.fs"
    original_hash = sha(operations)
    def inventory():
        return {path.relative_to(project).as_posix(): sha(path)
                for path in sorted(project.rglob("*")) if path.is_file()
                and not any(part in ("bin", "obj", "temp") for part in path.relative_to(project).parts)}
    original_inventory = inventory()
    manifest = adapter.copy_participant_project(scoring, project, operations)
    # Keep the original fixture decoder/projection. Route its one observation
    # through the actual six-input preview function; no original caller scores.
    (scoring / "ParticipantProgram.fs").write_text("""namespace AgentLang.SubscriptionHandoff.Control

module ParticipantProgram =
    [<EntryPoint>]
    let main args =
        try
            let observe store oldId newId term at expiry _ =
                AgentLang.SubscriptionHandoff.Subscription.previewReplacement store oldId newId term at expiry
            Scorer.runFileWith observe true args.[0]
            0
        with error ->
            System.Console.Error.WriteLine(error.Message)
            2
""", encoding="utf-8")
    manifest["target"] = "AgentLang.SubscriptionHandoff.Subscription.previewReplacement"
    manifest["targetSignature"] = "Store -> oldId -> replacementId -> term -> handoffAt -> expiresAt -> Result<Store, DomainError>"
    for row in manifest["compileInputs"]:
        row["sha256"] = sha(scoring / row["path"])
    local_cases = run / "cases.json"
    write(local_cases, rows)
    temp = run / "temp"
    temp.mkdir()
    environment = dict(os.environ, TEMP=str(temp), TMP=str(temp), DOTNET_CLI_HOME=str(temp),
                       DOTNET_NOLOGO="1")
    command = ["dotnet", "build", str(scoring / "ParticipantControl.fsproj"), "-c", "Release",
               "-m:1", "-p:NuGetAudit=false"]
    build = subprocess.run(command, cwd=REPO, env=environment, capture_output=True,
                           text=True, encoding="utf-8", timeout=600)
    (run / "build.stdout.txt").write_text(build.stdout, encoding="utf-8")
    (run / "build.stderr.txt").write_text(build.stderr, encoding="utf-8")
    result = {"buildCommand": command, "buildExitCode": build.returncode,
              "adapterSource": str(adapter_path.relative_to(REPO)), "adapterSha256": sha(adapter_path),
              "participantSourceSha256": original_hash, "fixtureCount": len(rows),
              "participantInputInventory": original_inventory,
              "successProjection": "Original complete Store, using preview/dry-run expected projection",
              "fixturePopulations": adapter.fixture_populations(rows),
              "scoringManifest": manifest,
              "previewEntrypointSha256": sha(scoring / "ParticipantProgram.fs")}
    if build.returncode:
        result["setupFailure"] = "Fresh scorer build failed"
    else:
        command = ["dotnet", str(scoring / "bin/Release/net9.0/ParticipantControl.dll"), str(local_cases)]
        process = subprocess.run(command, cwd=REPO, env=environment, capture_output=True,
                                 text=True, encoding="utf-8", timeout=600)
        result["score"] = adapter.score_rows(label, rows, process, True, run, command)
        emitted, parse_error = adapter.parse_rows(process.stdout)
        if parse_error or [row.get("id") for row in emitted] != [row["id"] for row in rows]:
            result["setupFailure"] = "Scorer observation IDs differ from frozen case order"
        if result["score"]["setupFailure"]:
            result["setupFailure"] = result["score"]["setupFailure"]
    result["participantSourceUnchanged"] = sha(operations) == original_hash
    result["participantInputInventoryUnchanged"] = inventory() == original_inventory
    write(run / "result.json", result)
    return result


def main():
    script_hash = sha(Path(__file__))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm", choices=["flow", "fsharp"], required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--cli", type=Path)
    parser.add_argument("--label", required=True)
    args = parser.parse_args()
    if not args.label or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-"
                             for c in args.label) or len(args.label) > 48:
        raise ValueError("Label must be one bounded path component")
    if (EVIDENCE / f"{args.label}.json").exists():
        raise FileExistsError("Refusing to overwrite an existing score label")
    rows = cases()
    frozen_path = HERE / "cases.json"
    frozen = json.dumps(rows, indent=2, ensure_ascii=False) + "\n"
    if frozen_path.read_text(encoding="utf-8") != frozen:
        raise ValueError("Frozen acceptance cases differ from independent derivation")
    if args.arm == "flow":
        if args.cli is None:
            parser.error("--cli is required for flow")
        result = flow_score((REPO / args.project).resolve(), (REPO / args.cli).resolve(), args.label, rows)
    else:
        result = fsharp_score((REPO / args.project).resolve(), args.label, rows)
    result["caseSourceSha256"] = sha(frozen_path)
    result["scoreScriptSha256"] = script_hash
    if sha(Path(__file__)) != script_hash:
        raise RuntimeError("Scorer source changed during execution")
    write(EVIDENCE / f"{args.label}.json", result)
    print(json.dumps({key: value for key, value in result.items()
                      if key not in ("caseResults", "scoringManifest", "score")}, indent=2))
    # Behavioral rejections are evidence, not setup failures. Callers must audit
    # complete counts and case failures rather than treat this exit as acceptance.
    return 1 if (result.get("setupFailure") or not result.get("participantSourceUnchanged", True)
                 or not result.get("participantInputInventoryUnchanged", True)) else 0


if __name__ == "__main__":
    try:
        exit_code = main()
    except Exception as error:
        failure = {"status": "setup-exception", "exceptionType": type(error).__name__,
                   "message": str(error), "traceback": traceback.format_exc()}
        write(EVIDENCE / f"setup-failure-{uuid4().hex}.json", failure)
        raise
    raise SystemExit(exit_code)
