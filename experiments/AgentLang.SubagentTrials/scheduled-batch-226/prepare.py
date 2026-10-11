#!/usr/bin/env python3
"""Create the pristine, undispatched scheduled-batch-226 inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[3]
EXPERIMENT = REPO / "experiments/AgentLang.SubagentTrials/scheduled-batch-226"
DATA = REPO / ".agentlang/scheduled-batch-226"
BASELINE_SOURCE = REPO / ".agentlang/typed-batch-comparison-220/baselines"
BASELINES = DATA / "baselines"
ACTORS = DATA / "actors"
PACKETS = DATA / "packets"
LAUNCHERS = DATA / "launchers"
TRACES = DATA / "traces"
RUNTIME = DATA / "runtime"
ROOT = DATA / "root"
SETUP = DATA / "setup.json"
EXPECTED_RETAINED_FINGERPRINT = "b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611"
ARMS = ("retained", "fsharp")
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "cache", "caches", "__pycache__"}
RUNTIME_FILES = {
    "agentlang": {
        "AgentLang.Cli.dll", "AgentLang.Cli.deps.json", "AgentLang.Cli.runtimeconfig.json",
        "AgentLang.Core.dll", "FSharp.Core.dll",
    },
    "conventional": {
        "AgentLang.Conventional.Cli.dll", "AgentLang.Conventional.Cli.deps.json",
        "AgentLang.Conventional.Cli.runtimeconfig.json", "AgentLang.Conventional.dll", "FSharp.Core.dll",
    },
}
AGENTLANG_OPS = (
    "help", "words", "source", "tests", "define", "test", "test-all", "commit",
    "storage.status", "task.begin", "task.status", "task.commit", "describe",
    "search", "eval", "context", "dependencies", "callers", "failed-tests",
    "examples", "effects", "type-of", "history", "task.abort",
)
FSHARP_OPS = ("inspect", "read", "search", "patch", "replace", "validate")
FSHARP_SOURCES = {
    "Domain.fs", "Operations.fs", "Fixtures.fs", "SelfTests.fs", "Program.fs",
    "SubscriptionBatch.Conventional.fsproj",
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def files(root: Path) -> list[Path]:
    result: list[Path] = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root)
        if any(part.casefold() in EXCLUDED_DIRS for part in relative.parts):
            continue
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in prepared tree: {path}")
        if path.is_file():
            result.append(path)
    return result


def assert_clean_source(root: Path) -> None:
    for path in root.rglob("*"):
        relative = path.relative_to(root)
        if path.is_dir() and any(part.casefold() in EXCLUDED_DIRS for part in relative.parts):
            raise RuntimeError(f"Generated or cache directory in baseline source: {path}")
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in baseline source: {path}")


def inventory(root: Path) -> list[dict[str, Any]]:
    return [
        {"path": path.relative_to(root).as_posix(), "bytes": len(data := path.read_bytes()), "sha256": sha256(data)}
        for path in files(root)
    ]


def tree_hash(rows: list[dict[str, Any]]) -> str:
    encoded = json.dumps(rows, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()
    return sha256(encoded)


def retained_fingerprint(root: Path) -> str:
    checksum = hashlib.sha256()
    for path in files(root):
        name = path.relative_to(root).as_posix().encode()
        content = path.read_bytes()
        checksum.update(len(name).to_bytes(8, "big"))
        checksum.update(name)
        checksum.update(len(content).to_bytes(8, "big"))
        checksum.update(content)
    return checksum.hexdigest()


def require_missing(paths: tuple[Path, ...]) -> None:
    existing = [str(path) for path in paths if path.exists()]
    if existing:
        raise RuntimeError("Refusing to overwrite prepared outputs: " + ", ".join(existing))


def packet_text(arm: str) -> str:
    bootstrap = (EXPERIMENT / "bootstrap.md").read_text(encoding="utf-8")
    task = (EXPERIMENT / "task.md").read_text(encoding="utf-8")
    common_at = bootstrap.index("# Common participant bootstrap")
    agent_at = bootstrap.index("# AgentLang interface")
    fsharp_at = bootstrap.index("# Conventional F# interface")
    common = bootstrap[common_at:agent_at].rstrip()
    interface = bootstrap[agent_at:fsharp_at].rstrip() if arm == "retained" else bootstrap[fsharp_at:].rstrip()
    actor = f".agentlang/scheduled-batch-226/actors/{arm}"
    launcher = f".agentlang/scheduled-batch-226/launchers/{arm}.ps1"
    launch = (
        "# Launch details\n\n"
        f"From the repository root, run: pwsh -NoProfile -ExecutionPolicy Bypass -File {launcher}\n\n"
        f"Assigned project path: {actor}\n"
    )
    if arm == "retained":
        access = "Use only this assigned project through the AgentLang broker. Do not edit runtime storage files directly."
    else:
        launch += (
            "\nFixed validation project: `SubscriptionBatch.Conventional.fsproj`, resolved inside the assigned project path."
            "\n\nPermitted files:\n- `Operations.fs`\n- `SelfTests.fs`\n\nAll other files are read-only."
        )
        access = ""
    parts = [launch.rstrip(), common, interface]
    if access:
        parts.append(access)
    parts.append(task.rstrip())
    return "\n\n".join(parts) + "\n"


def launcher_text(arm: str) -> str:
    runtime = "agentlang" if arm == "retained" else "conventional"
    cli = "AgentLang.Cli.dll" if arm == "retained" else "AgentLang.Conventional.Cli.dll"
    ops = AGENTLANG_OPS if arm == "retained" else FSHARP_OPS
    op_literals = ", ".join("'" + item + "'" for item in ops)
    extra = ""
    if arm == "fsharp":
        extra = (
            "$hostArguments.Add('AdditionalCliArguments', @('--validation-project', "
            "'SubscriptionBatch.Conventional.fsproj', '--max-requests', '100'))\n"
        )
    return (
        "#requires -Version 7.0\n"
        "$ErrorActionPreference = 'Stop'\n"
        "$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../'))\n"
        "$hostScriptPath = Join-Path $repositoryRoot 'scripts/Start-SubagentTrialHostV2.ps1'\n"
        f"$cliDll = Join-Path $repositoryRoot '.agentlang/scheduled-batch-226/runtime/{runtime}/{cli}'\n"
        f"$projectPath = Join-Path $repositoryRoot '.agentlang/scheduled-batch-226/actors/{arm}'\n"
        f"$tracePath = Join-Path $repositoryRoot '.agentlang/scheduled-batch-226/traces/{arm}.jsonl'\n"
        "$tempPath = Join-Path $repositoryRoot '.agentlang/scheduled-batch-226/root/scratch/temp'\n"
        "$tmpPath = Join-Path $repositoryRoot '.agentlang/scheduled-batch-226/root/scratch/tmp'\n"
        "[IO.Directory]::CreateDirectory($tempPath) | Out-Null\n"
        "[IO.Directory]::CreateDirectory($tmpPath) | Out-Null\n"
        "$env:MSBuildEnableWorkloadResolver = 'false'\n"
        "$env:TEMP = $tempPath\n"
        "$env:TMP = $tmpPath\n"
        "$hostArguments = @{\n"
        "    CliDll = $cliDll\n"
        "    ProjectPath = $projectPath\n"
        "    TracePath = $tracePath\n"
        f"    AllowedOperations = @({op_literals})\n"
        f"    Profile = '{'agentlang' if arm == 'retained' else 'conventional'}'\n"
        "    Capabilities = @()\n"
        "    ClockValue = '2000-01-01T00:00:00Z'\n"
        "    MaxRequestBytes = 262144\n"
        "    MaxResponseBytes = 524288\n"
        "    ExchangeTimeoutMilliseconds = 120000\n"
        "    MaxExchanges = 100\n"
        "}\n"
        + extra
        + "& $hostScriptPath @hostArguments\n"
    )


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="")


def copy_runtime(name: str, source: Path) -> dict[str, Any]:
    expected = RUNTIME_FILES[name]
    actual = {path.name for path in source.iterdir() if path.is_file()}
    missing = expected - actual
    if missing:
        raise RuntimeError(f"Fresh {name} build lacks runtime files: {sorted(missing)}")
    destination = RUNTIME / name
    destination.mkdir(parents=True, exist_ok=False)
    for filename in sorted(expected):
        shutil.copyfile(source / filename, destination / filename)
    copied = {path.name for path in destination.iterdir() if path.is_file()}
    if copied != expected:
        raise RuntimeError(f"Private {name} runtime inventory differs from the required set.")
    return {"source": source.relative_to(REPO).as_posix(), "files": inventory(destination)}


def prepare() -> dict[str, Any]:
    require_missing((BASELINES, ACTORS, PACKETS, LAUNCHERS, TRACES, RUNTIME, SETUP, ROOT / "preflight"))
    retained_source = BASELINE_SOURCE / "retained"
    fsharp_source = BASELINE_SOURCE / "fsharp"
    if not retained_source.is_dir() or not fsharp_source.is_dir():
        raise RuntimeError("The retained or matching F# 220 baseline is missing.")
    assert_clean_source(retained_source)
    assert_clean_source(fsharp_source)
    fingerprint = retained_fingerprint(retained_source)
    if fingerprint != EXPECTED_RETAINED_FINGERPRINT:
        raise RuntimeError(f"Retained baseline fingerprint mismatch: {fingerprint}")

    cli_log = DATA / "root-builds/cli.log"
    business_log = DATA / "root-builds/business.log"
    conventional_log = DATA / "root-builds/conventional-cli.log"
    for log in (cli_log, business_log, conventional_log):
        if not log.is_file() or "Build succeeded." not in log.read_text(encoding="utf-8"):
            raise RuntimeError(f"Missing successful fresh-build receipt: {log}")

    BASELINES.mkdir(parents=True)
    ACTORS.mkdir(parents=True)
    PACKETS.mkdir(parents=True)
    LAUNCHERS.mkdir(parents=True)
    TRACES.mkdir(parents=True)
    (ROOT / "scratch/temp").mkdir(parents=True)
    (ROOT / "scratch/tmp").mkdir(parents=True)
    shutil.copytree(retained_source, BASELINES / "retained", copy_function=shutil.copyfile)
    shutil.copytree(fsharp_source, BASELINES / "fsharp", copy_function=shutil.copyfile)
    shutil.copytree(BASELINES / "retained", ACTORS / "retained", copy_function=shutil.copyfile)
    shutil.copytree(BASELINES / "fsharp", ACTORS / "fsharp", copy_function=shutil.copyfile)

    runtime_sources = {
        "agentlang": REPO / "src/AgentLang.Cli/bin/Release/net9.0",
        "conventional": REPO / "experiments/AgentLang.Conventional.Cli/bin/Release/net9.0",
    }
    runtime_receipts = {name: copy_runtime(name, source) for name, source in runtime_sources.items()}
    baseline_receipts: dict[str, Any] = {}
    for arm in ARMS:
        baseline_rows = inventory(BASELINES / arm)
        actor_rows = inventory(ACTORS / arm)
        if baseline_rows != actor_rows:
            raise RuntimeError(f"The {arm} actor differs from its baseline copy.")
        baseline_receipts[arm] = {
            "source": (BASELINE_SOURCE / arm).relative_to(REPO).as_posix(),
            "baseline": (BASELINES / arm).relative_to(REPO).as_posix(),
            "actor": (ACTORS / arm).relative_to(REPO).as_posix(),
            "baselineTreeSha256": tree_hash(baseline_rows),
            "actorTreeSha256": tree_hash(actor_rows),
            "actorByteIdenticalToBaseline": True,
            "fileCount": len(actor_rows),
        }
        (PACKETS / f"{arm}.md").write_text(packet_text(arm), encoding="utf-8", newline="")
        (LAUNCHERS / f"{arm}.ps1").write_text(launcher_text(arm), encoding="utf-8", newline="")

    setup = {
        "study": "scheduled-batch-226",
        "status": "prepared226",
        "participantsDispatched": 0,
        "dispatchAllowed": False,
        "launchOrder": None,
        "capabilities": [],
        "protocol": {
            "maxExchanges": 100,
            "maxRequestBytes": 262144,
            "maxResponseBytes": 524288,
            "exchangeTimeoutMilliseconds": 120000,
            "clockValue": "2000-01-01T00:00:00Z",
        },
        "environment": {
            "MSBuildEnableWorkloadResolver": "false",
            "TEMP": (ROOT / "scratch/temp").relative_to(REPO).as_posix(),
            "TMP": (ROOT / "scratch/tmp").relative_to(REPO).as_posix(),
            "network": "disabled by empty broker capabilities and virtual I/O",
        },
        "retainedBaselineFingerprint": fingerprint,
        "baselines": baseline_receipts,
        "freshRuntimes": runtime_receipts,
        "buildLogs": [path.relative_to(REPO).as_posix() for path in (cli_log, business_log, conventional_log)],
        "preparedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "preparationOnly": True,
    }
    write_json(SETUP, setup)
    return setup


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare", action="store_true", help="Create baseline, actor, runtime, packet, and launcher copies.")
    parser.add_argument("--preflight", action="store_true", help="Run pristine baseline tests through the V2 brokers.")
    args = parser.parse_args()
    if args.prepare == args.preflight:
        parser.error("Choose exactly one of --prepare or --preflight.")
    result = prepare() if args.prepare else preflight()
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


def preflight() -> dict[str, Any]:
    setup = json.loads(SETUP.read_text(encoding="utf-8"))
    if setup.get("status") != "prepared226" or setup.get("participantsDispatched") != 0:
        raise RuntimeError("Pristine preparation state is required before baseline validation.")
    receipt_path = ROOT / "preflight.json"
    if receipt_path.exists():
        raise RuntimeError(f"Refusing to overwrite baseline preflight receipt: {receipt_path}")
    scratch = ROOT / "preflight"
    scratch.mkdir(parents=True, exist_ok=False)
    results: dict[str, Any] = {}
    for arm, operation in (("retained", "test-all"), ("fsharp", "validate")):
        work = scratch / arm
        work.mkdir()
        if arm == "retained":
            project = work / "project"
            shutil.copytree(BASELINES / arm, project, copy_function=shutil.copyfile)
        else:
            # Keep the fixed project reference valid at this path depth; only
            # generated obj/bin directories may appear beneath the actor.
            project = ACTORS / arm
        initial_source_hash = tree_hash(inventory(project))
        trace = work / "trace.jsonl"
        launcher_source = (LAUNCHERS / f"{arm}.ps1").read_text(encoding="utf-8")
        launcher = work / "launch.ps1"
        repository = str(REPO).replace("'", "''")
        project_literal = str(project).replace("'", "''")
        trace_literal = str(trace).replace("'", "''")
        scratch_root = str((ROOT / "scratch").resolve()).replace("'", "''")
        lines = launcher_source.splitlines()
        replacements = {
            "$repositoryRoot =": f"$repositoryRoot = '{repository}'",
            "$projectPath =": f"$projectPath = '{project_literal}'",
            "$tracePath =": f"$tracePath = '{trace_literal}'",
            "$tempPath =": f"$tempPath = Join-Path '{scratch_root}' 'temp'",
            "$tmpPath =": f"$tmpPath = Join-Path '{scratch_root}' 'tmp'",
        }
        for prefix, replacement in replacements.items():
            if sum(line.startswith(prefix) for line in lines) != 1:
                raise RuntimeError(f"Expected one launcher setting {prefix}.")
            lines = [replacement if line.startswith(prefix) else line for line in lines]
        launcher.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="")
        frames = json.dumps({"op": operation}, separators=(",", ":")) + "\n"
        frames += json.dumps({"op": "host.close"}, separators=(",", ":")) + "\n"
        run = subprocess.run(
            ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(launcher)],
            input=frames, text=True, capture_output=True, timeout=900,
        )
        (work / "stdout.jsonl").write_text(run.stdout, encoding="utf-8", newline="")
        (work / "stderr.txt").write_text(run.stderr, encoding="utf-8", newline="")
        if run.returncode != 0:
            raise RuntimeError(f"{arm} V2 broker exited {run.returncode}: {run.stderr[-2000:]}")
        responses = [json.loads(line) for line in run.stdout.splitlines() if line.strip()]
        # host.close is a V2 transport control and intentionally has no runtime
        # response frame; the termination audit proves it was acknowledged.
        if len(responses) != 1 or responses[0].get("ok") is not True:
            raise RuntimeError(f"{arm} baseline operation failed: {responses}")
        audit = subprocess.run(
            ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(REPO / "scripts/Audit-SubagentTrialTerminationV2.ps1"), "-TracePath", str(trace)],
            text=True, capture_output=True, timeout=120,
        )
        (work / "audit.stdout.json").write_text(audit.stdout, encoding="utf-8", newline="")
        (work / "audit.stderr.txt").write_text(audit.stderr, encoding="utf-8", newline="")
        if audit.returncode != 0:
            raise RuntimeError(f"{arm} normal host closure audit failed: {audit.stderr[-2000:]}")
        if tree_hash(inventory(project)) != initial_source_hash:
            raise RuntimeError(f"{arm} baseline source changed during preflight.")
        results[arm] = {
            "operation": operation,
            "responseCount": len(responses),
            "operationOk": responses[0].get("ok") is True,
            "hostCloseOk": audit.returncode == 0,
            "exitCode": run.returncode,
            "auditExitCode": audit.returncode,
            "trace": trace.relative_to(REPO).as_posix(),
            "stdout": (work / "stdout.jsonl").relative_to(REPO).as_posix(),
            "stderr": (work / "stderr.txt").relative_to(REPO).as_posix(),
        }
    receipt = {
        "status": "passed",
        "preparationOnly": True,
        "participantsDispatched": 0,
        "arms": results,
    }
    write_json(receipt_path, receipt)
    return receipt


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"PREPARATION_FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
