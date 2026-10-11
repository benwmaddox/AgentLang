#!/usr/bin/env python3
"""Prepare the isolated, undispatched typed-batch-comparison-220 pair."""

from __future__ import annotations

import hashlib
import json
import os
import re
import secrets
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[3]
EXPERIMENT = REPO / "experiments" / "AgentLang.SubagentTrials" / "typed-batch-comparison-220"
DATA = REPO / ".agentlang" / "typed-batch-comparison-220"
BASELINES = DATA / "baselines"
ACTORS = DATA / "actors"
PACKETS = DATA / "packets"
LAUNCHERS = DATA / "launchers"
TRACES = DATA / "traces"
SETUP = DATA / "setup.json"
RUNTIME = DATA / "runtime"
SCRATCH = DATA / "root" / "scratch"
EXPECTED_RETAINED_FINGERPRINT = "b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611"
ARMS = ("retained", "fsharp")
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
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "cache", "caches", "__pycache__"}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_inventory(root: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root)
        if any(part.casefold() in EXCLUDED_DIRS for part in relative.parts):
            if path.is_dir():
                raise RuntimeError(f"Generated or cache directory in prepared tree: {path}")
            continue
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in the prepared tree: {path}")
        if path.is_file():
            data = path.read_bytes()
            rows.append({"path": relative.as_posix(), "bytes": len(data), "sha256": digest(data)})
    return rows


def tree_hash(rows: list[dict[str, Any]]) -> str:
    encoded = json.dumps(rows, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return digest(encoded)


def retained_fingerprint(project: Path) -> str:
    oracle = REPO / "experiments" / "AgentLang.SubagentTrials" / "paired-vocabulary-213" / "oracle"
    sys.path.insert(0, str(oracle))
    try:
        import score_batch  # type: ignore[import-not-found]
        return score_batch.scorer.project_fingerprint(project)
    finally:
        sys.path.remove(str(oracle))


def copy_fsharp_sources(seed: Path, destination: Path) -> list[str]:
    destination.mkdir(parents=True, exist_ok=False)
    copied: list[str] = []
    for path in sorted(seed.rglob("*"), key=lambda item: item.relative_to(seed).as_posix()):
        relative = path.relative_to(seed)
        if any(part.casefold() in EXCLUDED_DIRS for part in relative.parts):
            continue
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in the F# seed: {path}")
        if not path.is_file() or path.suffix.casefold() not in {".fs", ".fsproj"}:
            continue
        output = destination / relative
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, output)
        copied.append(relative.as_posix())
    if set(copied) != FSHARP_SOURCES:
        raise RuntimeError(f"Unexpected F# source inventory: {sorted(copied)}")
    return copied


def rewrite_business_reference(project_file: Path, seed: Path, business_project: Path) -> str:
    content = project_file.read_text(encoding="utf-8")
    pattern = re.compile(r'(<ProjectReference\s+Include=")([^"]+)(")')
    matches = list(pattern.finditer(content))
    if len(matches) != 1:
        raise RuntimeError("Expected exactly one F# project reference.")
    if (seed / matches[0].group(2)).resolve() != business_project.resolve():
        raise RuntimeError("The seed project reference no longer points to AgentLang.Business.")
    reference = Path(os.path.relpath(business_project, project_file.parent)).as_posix()
    start, end = matches[0].span(2)
    project_file.write_text(content[:start] + reference + content[end:], encoding="utf-8", newline="")
    return reference


def packet_text(arm: str) -> str:
    bootstrap = (EXPERIMENT / "bootstrap.md").read_text(encoding="utf-8")
    task = (EXPERIMENT / "task.md").read_text(encoding="utf-8")
    common_start = bootstrap.index("# Common participant bootstrap")
    language_start = bootstrap.index("# AgentLang interface")
    fsharp_start = bootstrap.index("# Conventional F# interface")
    common = bootstrap[common_start:language_start].rstrip()
    interface = (
        bootstrap[language_start:fsharp_start].rstrip()
        if arm == "retained"
        else bootstrap[fsharp_start:].rstrip()
    )
    interface = re.sub(
        r"The final generated\s+packet will name the exact permitted file\s+paths and validation project\s+after\s+seed readiness and freeze;\s*this template is not a launch instruction\.",
        "",
        interface,
    ).rstrip()
    actor = f".agentlang/typed-batch-comparison-220/actors/{arm}"
    launcher = f".agentlang/typed-batch-comparison-220/launchers/{arm}.ps1"
    launch = (
        "# Launch details\n\n"
        f"From the repository root, run: pwsh -NoProfile -ExecutionPolicy Bypass -File {launcher}\n\n"
        f"Assigned project path: {actor}\n"
    )
    if arm == "retained":
        access = (
            "Use only this assigned project through the AgentLang broker. Make all "
            "changes through the broker-managed language session; do not directly "
            "edit project or runtime storage files."
        )
    else:
        launch += (
            "\nFixed validation project: SubscriptionBatch.Conventional.fsproj, "
            "resolved inside the assigned project path.\n\n"
            "Permitted files:\n"
            "- Domain.fs: only the related nominal failure type. Preserve the "
            "existing DomainError record and all error-to-code/message mappings unchanged.\n"
            "- Operations.fs: only the batch result, batch state/helper, and "
            "related implementation.\n"
            "- SelfTests.fs: only necessary adaptations and new regressions.\n\n"
            "All other project files are read-only."
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
    profile = "agentlang" if arm == "retained" else "conventional"
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
        f"$cliDll = Join-Path $repositoryRoot '.agentlang/typed-batch-comparison-220/runtime/{runtime}/{cli}'\n"
        f"$projectPath = Join-Path $repositoryRoot '.agentlang/typed-batch-comparison-220/actors/{arm}'\n"
        f"$tracePath = Join-Path $repositoryRoot '.agentlang/typed-batch-comparison-220/traces/{arm}.jsonl'\n"
        "$tempPath = Join-Path $repositoryRoot '.agentlang/typed-batch-comparison-220/root/scratch/temp'\n"
        "$tmpPath = Join-Path $repositoryRoot '.agentlang/typed-batch-comparison-220/root/scratch/tmp'\n"
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
        f"    Profile = '{profile}'\n"
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


def require_missing(path: Path) -> None:
    if path.exists():
        raise RuntimeError(f"Output already exists; refusing to reset the study: {path}")


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    retained = BASELINES / "retained"
    fsharp_seed = EXPERIMENT / "seed" / "fsharp"
    fsharp_baseline = BASELINES / "fsharp"
    business_project = REPO / "experiments" / "AgentLang.Business" / "AgentLang.Business.fsproj"

    for output in (ACTORS, PACKETS, LAUNCHERS, TRACES, fsharp_baseline, SETUP):
        require_missing(output)
    if not retained.is_dir() or not fsharp_seed.is_dir() or not business_project.is_file():
        raise RuntimeError("A required baseline, seed, or fixed Business project is missing.")
    fingerprint = retained_fingerprint(retained)
    if fingerprint != EXPECTED_RETAINED_FINGERPRINT:
        raise RuntimeError(f"Retained baseline fingerprint mismatch: {fingerprint}")
    retained_rows = file_inventory(retained)

    runtime_files = {
        "agentlang": {
            "AgentLang.Cli.dll", "AgentLang.Cli.deps.json", "AgentLang.Cli.runtimeconfig.json",
            "AgentLang.Core.dll", "FSharp.Core.dll",
        },
        "conventional": {
            "AgentLang.Conventional.Cli.dll", "AgentLang.Conventional.Cli.deps.json",
            "AgentLang.Conventional.Cli.runtimeconfig.json", "AgentLang.Conventional.dll",
            "FSharp.Core.dll",
        },
    }
    for name, expected in runtime_files.items():
        directory = RUNTIME / name
        actual = {item.name for item in directory.iterdir() if item.is_file()} if directory.is_dir() else set()
        if actual != expected:
            raise RuntimeError(f"Private {name} runtime mismatch: expected {sorted(expected)}, found {sorted(actual)}")

    order = list(ARMS)
    secrets.SystemRandom().shuffle(order)
    ACTORS.mkdir(parents=True, exist_ok=False)
    PACKETS.mkdir(parents=True, exist_ok=False)
    LAUNCHERS.mkdir(parents=True, exist_ok=False)
    TRACES.mkdir(parents=True, exist_ok=False)
    shutil.copytree(retained, ACTORS / "retained", copy_function=shutil.copyfile)

    copied = copy_fsharp_sources(fsharp_seed, fsharp_baseline)
    reference = rewrite_business_reference(
        fsharp_baseline / "SubscriptionBatch.Conventional.fsproj", fsharp_seed, business_project
    )
    shutil.copytree(fsharp_baseline, ACTORS / "fsharp", copy_function=shutil.copyfile)

    arms: dict[str, Any] = {}
    for arm, baseline in (("retained", retained), ("fsharp", fsharp_baseline)):
        actor = ACTORS / arm
        baseline_rows = retained_rows if arm == "retained" else file_inventory(baseline)
        actor_rows = file_inventory(actor)
        if baseline_rows != actor_rows:
            raise RuntimeError(f"The {arm} actor is not byte-identical to its baseline.")
        arms[arm] = {
            "baseline": baseline.relative_to(REPO).as_posix(),
            "actor": actor.relative_to(REPO).as_posix(),
            "packet": (PACKETS / f"{arm}.md").relative_to(REPO).as_posix(),
            "launcher": (LAUNCHERS / f"{arm}.ps1").relative_to(REPO).as_posix(),
            "allowedOperations": list(AGENTLANG_OPS if arm == "retained" else FSHARP_OPS),
            "baselineTreeSha256": tree_hash(baseline_rows),
            "actorTreeSha256": tree_hash(actor_rows),
            "actorBytesEqualBaseline": True,
            "fileCount": len(actor_rows),
        }
    arms["retained"]["projectFingerprint"] = fingerprint
    arms["fsharp"]["fixedProjectReference"] = reference
    arms["fsharp"]["sourceFiles"] = copied

    for arm in ARMS:
        (PACKETS / f"{arm}.md").write_text(packet_text(arm), encoding="utf-8", newline="")
        (LAUNCHERS / f"{arm}.ps1").write_text(launcher_text(arm), encoding="utf-8", newline="")
    (SCRATCH / "temp").mkdir(parents=True)
    (SCRATCH / "tmp").mkdir(parents=True)

    setup = {
        "study": "typed-batch-comparison-220",
        "status": "prepared220",
        "participantsDispatched": 0,
        "launchOrder": order,
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
            "TEMP": (SCRATCH / "temp").relative_to(REPO).as_posix(),
            "TMP": (SCRATCH / "tmp").relative_to(REPO).as_posix(),
        },
        "arms": arms,
        "preparedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "preparationOnly": True,
    }
    write_json(SETUP, setup)
    print(json.dumps(setup, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"PREPARATION_FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
