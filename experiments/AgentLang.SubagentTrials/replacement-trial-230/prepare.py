#!/usr/bin/env python3
"""Prepare pristine, undispatched replacement-trial-230 inputs."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any


REPO = Path(__file__).resolve().parents[3]
EXPERIMENT = REPO / "experiments/AgentLang.SubagentTrials/replacement-trial-230"
DATA = REPO / ".agentlang/replacement-trial-230"
ARCHIVE_DIR = REPO / "reports/evidence/228-scheduled-renewal-interactive-comparison"
ARCHIVE = ARCHIVE_DIR / "participant-evidence.zip"
ARCHIVE_MANIFEST = ARCHIVE_DIR / "participant-evidence.manifest.json"
SOURCE_STUDY = REPO / ".agentlang/scheduled-batch-228"
SETUP = DATA / "setup.json"
BASELINES = DATA / "baselines"
ACTORS = DATA / "actors"
PACKETS = DATA / "packets"
LAUNCHERS = DATA / "launchers"
PREFLIGHT = DATA / "preflight"
ROOT = DATA / "root"
RUNTIME = DATA / "runtime"

SOURCE_REVISION = "74cc644cd1fb29290435d332fecabfc5833ad6e9"
ARMS = ("retained", "fsharp")
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "cache", "caches", "__pycache__"}
FSHARP_FILES = {
    "Domain.fs",
    "Operations.fs",
    "Fixtures.fs",
    "SelfTests.fs",
    "Program.fs",
    "SubscriptionBatch.Conventional.fsproj",
}
ENDPOINT_GROUPS = {
    "retained": "retained-terminal-project-and-durable-store",
    "fsharp": "fsharp-terminal-source-only",
}
SETUP_SOURCE_PATH = ".agentlang/scheduled-batch-228/setup.json"
ACCEPTANCE_REVIEW_PATH = ".agentlang/scheduled-batch-228/review/participant-review.md"
FREEZE_PATH = ".agentlang/scheduled-batch-228/freeze.json"
DISPATCH_PATH = ".agentlang/scheduled-batch-228/dispatch.json"
PARTICIPANT_FINALS_PATH = ".agentlang/scheduled-batch-228/participant-finals.json"

BUILD_TARGETS = {
    "agentlang": {
        "project": "src/AgentLang.Cli/AgentLang.Cli.fsproj",
        "output": "src/AgentLang.Cli/bin/Release/net9.0",
        "cli": "AgentLang.Cli.dll",
        "required": {
            "AgentLang.Cli.deps.json",
            "AgentLang.Cli.dll",
            "AgentLang.Cli.runtimeconfig.json",
            "AgentLang.Core.dll",
            "FSharp.Core.dll",
        },
    },
    "conventional": {
        "project": "experiments/AgentLang.Conventional.Cli/AgentLang.Conventional.Cli.fsproj",
        "output": "experiments/AgentLang.Conventional.Cli/bin/Release/net9.0",
        "cli": "AgentLang.Conventional.Cli.dll",
        "required": {
            "AgentLang.Conventional.Cli.deps.json",
            "AgentLang.Conventional.Cli.dll",
            "AgentLang.Conventional.Cli.runtimeconfig.json",
            "AgentLang.Conventional.dll",
            "FSharp.Core.dll",
        },
    },
}

STARTUP = (
    "Launch the packet's exact PowerShell command through `exec_command` with "
    "`tty:true`. Do not pipe requests or redirect stdin. Keep the returned "
    "`session_id`; send requests with `write_stdin` to that same handle. If an "
    "observation times out, continue with that session and do not restart the "
    "process. Send `{\"op\":\"host.close\"}` through `write_stdin` to the same "
    "handle and observe terminal exit; the coordinator will audit termination."
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_inventory(root: Path, *, excluded_dirs: set[str] | None = None) -> list[dict[str, Any]]:
    if not root.is_dir():
        raise RuntimeError(f"Expected directory is missing: {root}")
    excluded = {part.casefold() for part in (excluded_dirs or set())}
    result: list[dict[str, Any]] = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root)
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in prepared tree: {path}")
        if any(part.casefold() in excluded for part in relative.parts):
            continue
        if path.is_file():
            raw = path.read_bytes()
            result.append({"path": relative.as_posix(), "bytes": len(raw), "sha256": sha256(raw)})
    return result


def tree_hash(rows: list[dict[str, Any]]) -> str:
    encoded = json.dumps(rows, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return sha256(encoded)


def json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(json_bytes(value))


def write_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8", newline="")


def repo_path(relative: str) -> Path:
    path = (REPO / relative).resolve()
    try:
        path.relative_to(REPO.resolve())
    except ValueError as exc:
        raise RuntimeError(f"Pinned path escapes the repository: {relative}") from exc
    return path


def verify_source_revision() -> dict[str, Any]:
    revision = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=REPO, check=True, capture_output=True, text=True
    ).stdout.strip()
    if revision != SOURCE_REVISION:
        raise RuntimeError(f"Expected accepted 229 source revision {SOURCE_REVISION}, found {revision}.")
    changed = subprocess.run(
        ["git", "status", "--porcelain=v1", "--untracked-files=no"],
        cwd=REPO,
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    if changed.strip():
        raise RuntimeError("Refusing runtime build from a checkout with tracked changes.")
    return {"revision": revision, "trackedWorktreeClean": True}


def safe_archive_path(name: str) -> PurePosixPath:
    path = PurePosixPath(name)
    if path.is_absolute() or not path.parts or any(part in {"", ".", ".."} for part in path.parts):
        raise RuntimeError(f"Unsafe archive member path: {name}")
    return path


def verify_archive() -> tuple[zipfile.ZipFile, dict[str, Any], dict[str, Any], dict[str, Any]]:
    sidecar_raw = ARCHIVE_MANIFEST.read_bytes()
    sidecar = json.loads(sidecar_raw.decode("utf-8"))
    archive_raw = ARCHIVE.read_bytes()
    archive_hash = sha256(archive_raw)
    if len(archive_raw) != sidecar["archive"]["bytes"] or archive_hash != sidecar["archive"]["sha256"]:
        raise RuntimeError("The accepted 228 participant archive no longer matches its manifest receipt.")
    if sidecar.get("format") != "scheduled-batch-228-participant-evidence-v1":
        raise RuntimeError("Unexpected 228 participant archive format.")
    if sidecar.get("freezeStatus") != "frozen-ready" or sidecar.get("sourceRecheck") != "unchanged":
        raise RuntimeError("The 228 archive lacks its frozen, unchanged-source receipt.")
    if sidecar.get("verified") is not True:
        raise RuntimeError("The 228 archive sidecar is not marked verified.")
    if len(sidecar.get("members", [])) != sidecar.get("archiveMemberCount"):
        raise RuntimeError("The 228 sidecar archive member count is inconsistent.")

    names: set[str] = set()
    for row in sidecar["members"]:
        name = row["path"]
        safe_archive_path(name)
        if name in names:
            raise RuntimeError(f"Duplicate 228 archive member: {name}")
        names.add(name)

    archive = zipfile.ZipFile(ARCHIVE, "r")
    if archive.testzip() is not None:
        archive.close()
        raise RuntimeError("The accepted 228 archive contains a corrupt ZIP member.")
    zip_names = set(archive.namelist())
    if len(zip_names) != len(archive.namelist()):
        archive.close()
        raise RuntimeError("The accepted 228 archive has duplicate ZIP member names.")
    if zip_names != names:
        archive.close()
        raise RuntimeError("The accepted 228 ZIP members differ from its external manifest.")
    for row in sidecar["members"]:
        name = row["path"]
        if name not in zip_names:
            archive.close()
            raise RuntimeError(f"The 228 archive is missing manifest member {name}.")
        info = archive.getinfo(name)
        mode = info.external_attr >> 16
        if stat.S_IFMT(mode) == stat.S_IFLNK:
            archive.close()
            raise RuntimeError(f"Refusing symlink archive member: {name}")
        raw = archive.read(name)
        if len(raw) != row["bytes"] or sha256(raw) != row["sha256"]:
            archive.close()
            raise RuntimeError(f"The 228 archive member does not match its pin: {name}")

    inner_raw = archive.read("MANIFEST.json")
    inner = json.loads(inner_raw.decode("utf-8"))
    inner_rows = inner.get("members", [])
    side_rows = [row for row in sidecar["members"] if row["group"] != "archive-member-manifest"]
    if inner_rows != side_rows:
        archive.close()
        raise RuntimeError("The 228 inner and external archive member manifests disagree.")
    manifest_pin = next(
        (row for row in sidecar["members"] if row["group"] == "archive-member-manifest" and row["path"] == "MANIFEST.json"),
        None,
    )
    if manifest_pin is None or len(inner_raw) != manifest_pin["bytes"] or sha256(inner_raw) != manifest_pin["sha256"]:
        archive.close()
        raise RuntimeError("The 228 internal archive manifest does not match its pin.")

    receipt = {
        "archive": {
            "path": ARCHIVE.relative_to(REPO).as_posix(),
            "bytes": len(archive_raw),
            "sha256": archive_hash,
        },
        "sidecarManifest": {
            "path": ARCHIVE_MANIFEST.relative_to(REPO).as_posix(),
            "bytes": len(sidecar_raw),
            "sha256": sha256(sidecar_raw),
        },
        "internalManifest": {"path": "MANIFEST.json", "bytes": len(inner_raw), "sha256": sha256(inner_raw)},
        "memberCount": len(inner_rows),
        "verified": True,
    }
    return archive, sidecar, inner, receipt


def rows_for_group(sidecar: dict[str, Any], group: str, prefix: str) -> list[dict[str, Any]]:
    rows = [
        row
        for row in sidecar["members"]
        if row["group"] == group and row["path"].startswith(prefix)
    ]
    if not rows:
        raise RuntimeError(f"No archived endpoint members for {group} at {prefix}.")
    return sorted(rows, key=lambda item: item["path"])


def verify_setup_and_reviews(
    archive: zipfile.ZipFile, sidecar: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any]]:
    by_path = {row["path"]: row for row in sidecar["members"]}

    def archived_json(path: str, group: str) -> tuple[dict[str, Any], dict[str, Any]]:
        row = by_path.get(path)
        if row is None or row["group"] != group:
            raise RuntimeError(f"Missing archived 228 receipt {path} ({group}).")
        raw = archive.read(path)
        local = repo_path(path).read_bytes()
        if sha256(local) != row["sha256"] or len(local) != row["bytes"]:
            raise RuntimeError(f"The current 228 receipt changed since archiving: {path}")
        return json.loads(raw.decode("utf-8")), row

    source_setup, setup_pin = archived_json(SETUP_SOURCE_PATH, "study-setup")
    freeze, freeze_pin = archived_json(FREEZE_PATH, "frozen-public-inputs")
    dispatch, dispatch_pin = archived_json(DISPATCH_PATH, "dispatch-record")
    finals, finals_pin = archived_json(PARTICIPANT_FINALS_PATH, "participant-final-reports")
    review_path = ACCEPTANCE_REVIEW_PATH
    review_pin = by_path.get(review_path)
    if review_pin is None or review_pin["group"] != "independent-reviews":
        raise RuntimeError("The 228 independent participant acceptance review is missing from the archive.")
    review_raw = archive.read(review_path)
    if sha256(repo_path(review_path).read_bytes()) != review_pin["sha256"]:
        raise RuntimeError("The current 228 participant review changed since archiving.")
    review = review_raw.decode("utf-8")
    if "Disposition: BOTH ARMS ACCEPTED" not in review:
        raise RuntimeError("The archived 228 review does not accept both participant endpoints.")
    if source_setup.get("study") != "scheduled-batch-228" or source_setup.get("status") != "prepared228":
        raise RuntimeError("The 228 setup receipt does not describe the expected prepared study.")
    if freeze.get("study") != "scheduled-batch-228" or freeze.get("status") != "frozen-ready":
        raise RuntimeError("The 228 frozen receipt is not ready.")
    terminal_arms = {
        row.get("arm")
        for row in dispatch.get("events", [])
        if row.get("event") == "participant-terminal"
    }
    if terminal_arms != set(ARMS):
        raise RuntimeError("The archived 228 dispatch receipt does not show two terminal participants.")
    if set(finals) != set(ARMS):
        raise RuntimeError("The archived 228 final participant reports do not contain both arms.")

    baseline_receipts: dict[str, Any] = {}
    for arm in ARMS:
        prefix = f".agentlang/scheduled-batch-228/baselines/{arm}/"
        rows = rows_for_group(sidecar, f"baseline-{arm}-once", prefix)
        relative_rows = [
            {"path": row["path"][len(prefix) :], "bytes": row["bytes"], "sha256": row["sha256"]}
            for row in rows
        ]
        expected = source_setup["baselines"][arm]["treeSha256"]
        actual = tree_hash(relative_rows)
        if expected != actual:
            raise RuntimeError(f"The 228 {arm} archived baseline differs from its setup receipt.")
        baseline_receipts[arm] = {
            "sourceSetupPath": SETUP_SOURCE_PATH,
            "setupSha256": setup_pin["sha256"],
            "archiveGroup": f"baseline-{arm}-once",
            "treeSha256": actual,
            "fileCount": len(relative_rows),
        }

    receipts = {
        "sourceSetup": {"path": SETUP_SOURCE_PATH, "sha256": setup_pin["sha256"]},
        "freeze": {"path": FREEZE_PATH, "sha256": freeze_pin["sha256"], "status": freeze["status"]},
        "dispatch": {"path": DISPATCH_PATH, "sha256": dispatch_pin["sha256"]},
        "participantFinalReports": {"path": PARTICIPANT_FINALS_PATH, "sha256": finals_pin["sha256"]},
        "independentAcceptanceReview": {
            "path": review_path,
            "sha256": review_pin["sha256"],
            "disposition": "both arms accepted",
        },
        "baselineReceipts": baseline_receipts,
    }
    return source_setup, receipts


def verify_endpoints(
    archive: zipfile.ZipFile, sidecar: dict[str, Any], source_setup: dict[str, Any]
) -> dict[str, dict[str, Any]]:
    verified: dict[str, dict[str, Any]] = {}
    for arm in ARMS:
        prefix = f".agentlang/scheduled-batch-228/actors/{arm}/"
        rows = rows_for_group(sidecar, ENDPOINT_GROUPS[arm], prefix)
        relative_rows = [
            {"path": row["path"][len(prefix) :], "bytes": row["bytes"], "sha256": row["sha256"]}
            for row in rows
        ]
        if arm == "fsharp" and {row["path"] for row in relative_rows} != FSHARP_FILES:
            raise RuntimeError("The accepted F# endpoint archive must contain its exact six source/project files.")

        local_source = repo_path(f".agentlang/scheduled-batch-228/actors/{arm}")
        current_rows = file_inventory(local_source, excluded_dirs=EXCLUDED_DIRS)
        if current_rows != relative_rows:
            raise RuntimeError(f"The current archived 228 {arm} endpoint differs from its final-source pins.")

        final_tree_hash = tree_hash(relative_rows)
        setup_baseline_hash = source_setup["baselines"][arm]["treeSha256"]
        if final_tree_hash == setup_baseline_hash:
            raise RuntimeError(f"The accepted 228 {arm} endpoint unexpectedly equals its original baseline.")
        verified[arm] = {
            "sourceStudy": "scheduled-batch-228",
            "sourcePath": f".agentlang/scheduled-batch-228/actors/{arm}",
            "archiveGroup": ENDPOINT_GROUPS[arm],
            "archivePathPrefix": prefix,
            "endpointTreeSha256": final_tree_hash,
            "fileCount": len(relative_rows),
            "files": relative_rows,
            "currentEndpointMatchesArchive": True,
            "acceptanceReviewPath": ACCEPTANCE_REVIEW_PATH,
            "acceptanceReviewSha256": next(
                row["sha256"] for row in sidecar["members"] if row["path"] == ACCEPTANCE_REVIEW_PATH
            ),
        }
    return verified


def next_build_run_id(log_dir: Path) -> str:
    base = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    candidate = base
    suffix = 1
    while any(log_dir.glob(f"{candidate}-*")):
        candidate = f"{base}-{suffix:02d}"
        suffix += 1
    return candidate


def run_command(command: list[str], *, log_path: Path, env: dict[str, str]) -> dict[str, Any]:
    completed = subprocess.run(command, cwd=REPO, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_bytes(completed.stdout)
    return {
        "command": command,
        "exitCode": completed.returncode,
        "log": log_path.relative_to(REPO).as_posix(),
        "logBytes": len(completed.stdout),
        "logSha256": sha256(completed.stdout),
    }


def build_fresh_runtimes(source_receipt: dict[str, Any]) -> tuple[dict[str, Any], dict[str, list[dict[str, Any]]]]:
    runtime_log_dir = RUNTIME / "build-logs"
    runtime_receipt_dir = RUNTIME / "build-receipts"
    runtime_log_dir.mkdir(parents=True, exist_ok=True)
    runtime_receipt_dir.mkdir(parents=True, exist_ok=True)
    run_id = next_build_run_id(runtime_log_dir)
    env = os.environ.copy()
    env["MSBuildEnableWorkloadResolver"] = "false"
    scratch = ROOT / "scratch"
    temp = scratch / "temp"
    tmp = scratch / "tmp"
    temp.mkdir(parents=True, exist_ok=True)
    tmp.mkdir(parents=True, exist_ok=True)
    env["TEMP"] = str(temp)
    env["TMP"] = str(tmp)

    try:
        dotnet_version = subprocess.run(
            ["dotnet", "--version"], cwd=REPO, env=env, check=True, capture_output=True, text=True
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as exc:
        raise RuntimeError(f"Cannot read the installed .NET SDK version: {exc}") from exc

    builds: dict[str, Any] = {}
    output_rows: dict[str, list[dict[str, Any]]] = {}
    for name, target in BUILD_TARGETS.items():
        project = target["project"]
        clean_log = runtime_log_dir / f"{run_id}-{name}-clean.log"
        build_log = runtime_log_dir / f"{run_id}-{name}-build.log"
        clean = run_command(
            ["dotnet", "clean", project, "--configuration", "Release", "-m:1"],
            log_path=clean_log,
            env=env,
        )
        if clean["exitCode"] != 0:
            raise RuntimeError(f"Fresh {name} Release clean failed; raw output is preserved at {clean['log']}.")
        build_command = [
            "dotnet",
            "build",
            project,
            "--configuration",
            "Release",
            "--no-incremental",
            "-m:1",
            "-p:NuGetAudit=true",
        ]
        build = run_command(build_command, log_path=build_log, env=env)
        builds[name] = {
            "project": project,
            "output": target["output"],
            "cli": target["cli"],
            "configuration": "Release",
            "restorePackageAudit": "enabled",
            "clean": clean,
            "build": build,
            "requiredFiles": sorted(target["required"]),
        }
        if build["exitCode"] != 0:
            write_json(
                runtime_receipt_dir / f"{run_id}-{name}-failed.json",
                {"source": source_receipt, "dotnetVersion": dotnet_version, "runId": run_id, **builds[name]},
            )
            raise RuntimeError(f"Fresh {name} Release build failed; package audit remains enabled; raw output is preserved at {build['log']}.")
        output = repo_path(target["output"])
        rows = file_inventory(output)
        found = {row["path"] for row in rows}
        if not target["required"].issubset(found):
            missing = sorted(target["required"] - found)
            raise RuntimeError(f"Fresh {name} runtime is missing required files: {missing}")
        output_rows[name] = rows

    build_receipt = {
        "runId": run_id,
        "sourceRevision": source_receipt["revision"],
        "sourceTrackedWorktreeClean": source_receipt["trackedWorktreeClean"],
        "dotnetVersion": dotnet_version,
        "buildOrder": list(BUILD_TARGETS),
        "serialBuilds": True,
        "restorePackageAudit": "enabled via -p:NuGetAudit=true; no audit-disabling override used",
        "builds": builds,
    }
    write_json(runtime_receipt_dir / f"{run_id}-complete.json", build_receipt)
    return build_receipt, output_rows


def copy_runtime_outputs(output_rows: dict[str, list[dict[str, Any]]]) -> dict[str, dict[str, Any]]:
    receipts: dict[str, dict[str, Any]] = {}
    for name, target in BUILD_TARGETS.items():
        source = repo_path(target["output"])
        destination = RUNTIME / name
        if destination.exists():
            raise RuntimeError(f"Refusing to overwrite private runtime output: {destination}")
        shutil.copytree(source, destination, copy_function=shutil.copyfile)
        copied = file_inventory(destination)
        if copied != output_rows[name]:
            raise RuntimeError(f"Copied {name} private runtime differs from its fresh Release build output.")
        receipts[name] = {
            "path": destination.relative_to(REPO).as_posix(),
            "sourceOutput": target["output"],
            "project": target["project"],
            "cli": target["cli"],
            "sourceRevision": SOURCE_REVISION,
            "treeSha256": tree_hash(copied),
            "fileCount": len(copied),
            "files": copied,
            "matchesFreshBuildOutput": True,
        }
    return receipts


def copy_archived_endpoint(
    archive: zipfile.ZipFile, rows: list[dict[str, Any]], prefix: str, destination: Path
) -> list[dict[str, Any]]:
    destination.mkdir(parents=True)
    for row in rows:
        relative_name = row["path"][len(prefix) :]
        relative = safe_archive_path(relative_name)
        target = destination.joinpath(*relative.parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() or target.is_symlink():
            raise RuntimeError(f"Refusing to overwrite accepted endpoint file: {target}")
        raw = archive.read(row["path"])
        target.write_bytes(raw)
    result = file_inventory(destination)
    expected = [
        {"path": row["path"][len(prefix) :], "bytes": row["bytes"], "sha256": row["sha256"]}
        for row in rows
    ]
    if result != expected:
        raise RuntimeError(f"Copied endpoint differs from the accepted 228 archive source: {destination}")
    return result


def assert_pristine_equal(baseline: Path, actor: Path) -> dict[str, Any]:
    baseline_rows = file_inventory(baseline)
    actor_rows = file_inventory(actor)
    if baseline_rows != actor_rows:
        raise RuntimeError(f"The fresh actor does not match its accepted endpoint baseline: {actor}")
    return {
        "treeSha256": tree_hash(actor_rows),
        "fileCount": len(actor_rows),
        "actorByteIdenticalToBaseline": True,
    }


def replace_exact(data: bytes, old: bytes, new: bytes, label: str, count: int = 1) -> bytes:
    actual = data.count(old)
    if actual != count:
        raise RuntimeError(f"Expected {count} {label} occurrence(s), found {actual}.")
    return data.replace(old, new)


def prepare_packet(arm: str, task: str, bootstrap: str) -> bytes:
    if arm == "retained":
        interface = """# AgentLang interface

Allowed operations:
`help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort`.

Use Flow/2. Include `frontend: \"flow\"` and `syntaxVersion: 2` on every
`define` and `eval`. Keep the new record and words library-qualified. Record
staging and publication must include the attached tests in the same live
session. Use `replace: true` and exact observed `expectedRevisions` for the
monthly operation and its two production callers. Do not edit runtime storage
files directly.
"""
        task_section = task.strip() + "\n"
    else:
        interface = """# Conventional F# interface

Allowed operations: `inspect,read,search,patch,replace,validate`.

The fixed validation project is `SubscriptionBatch.Conventional.fsproj` in
the assigned project. Only `Operations.fs` and `SelfTests.fs` may be edited;
all other files are read-only. Local validation uses that project. Keep the
implementation pure and add no packages.
"""
        task_section = task.strip() + "\n"
    project_line = f"Assigned project path: `.agentlang/replacement-trial-230/actors/{arm}`\n"
    launch_line = (
        "From the repository root, run: `pwsh -NoProfile -ExecutionPolicy Bypass "
        f"-File .agentlang/replacement-trial-230/launchers/{arm}.ps1`\n"
    )
    if arm == "fsharp":
        project_line += "Fixed validation project: `SubscriptionBatch.Conventional.fsproj`, resolved in the assigned project.\n"
        task_line = "# Launch details\n\n" + launch_line + "\n" + project_line + "\n"
    else:
        task_line = "# Launch details\n\n" + launch_line + "\n" + project_line + "\n"
    return (
        task_line
        + bootstrap.strip()
        + "\n\n"
        + interface.strip()
        + "\n\n"
        + task_section
    ).encode("utf-8")


def launcher_text(arm: str, *, preflight: bool) -> str:
    project = (
        f".agentlang/replacement-trial-230/preflight/projects/{arm}"
        if preflight
        else f".agentlang/replacement-trial-230/actors/{arm}"
    )
    trace = (
        f".agentlang/replacement-trial-230/preflight/traces/{arm}.jsonl"
        if preflight
        else f".agentlang/replacement-trial-230/root/traces/{arm}.jsonl"
    )
    runtime = ".agentlang/replacement-trial-230/runtime/agentlang" if arm == "retained" else ".agentlang/replacement-trial-230/runtime/conventional"
    cli = "AgentLang.Cli.dll" if arm == "retained" else "AgentLang.Conventional.Cli.dll"
    operations = (
        "'help', 'words', 'source', 'tests', 'define', 'test', 'test-all', 'commit', 'storage.status', "
        "'task.begin', 'task.status', 'task.commit', 'describe', 'search', 'eval', 'context', 'dependencies', "
        "'callers', 'failed-tests', 'examples', 'effects', 'type-of', 'history', 'task.abort'"
        if arm == "retained"
        else "'inspect', 'read', 'search', 'patch', 'replace', 'validate'"
    )
    profile = "agentlang" if arm == "retained" else "conventional"
    additional = (
        "$hostArguments.Add('AdditionalCliArguments', @('--validation-project', 'SubscriptionBatch.Conventional.fsproj', '--max-requests', '100'))\n"
        if arm == "fsharp"
        else ""
    )
    return f"""#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../'))
$hostScriptPath = Join-Path $repositoryRoot 'scripts/Start-SubagentTrialHostV2.ps1'
$cliDll = Join-Path $repositoryRoot '{runtime}/{cli}'
$projectPath = Join-Path $repositoryRoot '{project}'
$tracePath = Join-Path $repositoryRoot '{trace}'
$tempPath = Join-Path $repositoryRoot '.agentlang/replacement-trial-230/root/scratch/temp'
$tmpPath = Join-Path $repositoryRoot '.agentlang/replacement-trial-230/root/scratch/tmp'
[IO.Directory]::CreateDirectory($tempPath) | Out-Null
[IO.Directory]::CreateDirectory($tmpPath) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($tracePath)) | Out-Null
$env:MSBuildEnableWorkloadResolver = 'false'
$env:TEMP = $tempPath
$env:TMP = $tmpPath
$hostArguments = @{{
    CliDll = $cliDll
    ProjectPath = $projectPath
    TracePath = $tracePath
    AllowedOperations = @({operations})
    Profile = '{profile}'
    Capabilities = @()
    ClockValue = '2000-01-01T00:00:00Z'
    MaxRequestBytes = 262144
    MaxResponseBytes = 524288
    ExchangeTimeoutMilliseconds = 120000
    MaxExchanges = 100
}}
{additional}& $hostScriptPath @hostArguments
"""


def prepare_preflight_projects() -> dict[str, Any]:
    receipts: dict[str, Any] = {}
    for arm in ARMS:
        source = BASELINES / arm
        destination = PREFLIGHT / "projects" / arm
        if destination.exists():
            raise RuntimeError(f"Refusing to overwrite disposable preflight project: {destination}")
        shutil.copytree(source, destination, copy_function=shutil.copyfile)
        rows = file_inventory(destination)
        if rows != file_inventory(source):
            raise RuntimeError(f"Disposable {arm} preflight project differs from the accepted seed.")
        receipts[arm] = {
            "path": destination.relative_to(REPO).as_posix(),
            "treeSha256": tree_hash(rows),
            "fileCount": len(rows),
            "disposableCopyOfBaseline": True,
        }
    return receipts


def ensure_empty_output_paths() -> None:
    outputs = (
        BASELINES,
        ACTORS,
        PACKETS,
        LAUNCHERS,
        PREFLIGHT / "projects",
        PREFLIGHT / "traces",
        PREFLIGHT / "retained.ps1",
        PREFLIGHT / "fsharp.ps1",
        ROOT / "scratch",
        ROOT / "traces",
        RUNTIME / "agentlang",
        RUNTIME / "conventional",
        SETUP,
    )
    existing = [str(path.relative_to(REPO)) for path in outputs if path.exists() or path.is_symlink()]
    if existing:
        raise RuntimeError("Refusing to overwrite existing 230 preparation outputs: " + ", ".join(existing))


def prepare() -> dict[str, Any]:
    if not REPO.is_dir() or not EXPERIMENT.is_dir():
        raise RuntimeError("The repository or replacement-trial-230 experiment directory is missing.")
    ensure_empty_output_paths()
    source_revision = verify_source_revision()
    archive, archive_sidecar, _, archive_receipt = verify_archive()
    try:
        source_setup, source_receipts = verify_setup_and_reviews(archive, archive_sidecar)
        accepted_endpoints = verify_endpoints(archive, archive_sidecar, source_setup)
        build_receipt, build_output_rows = build_fresh_runtimes(source_revision)

        for arm in ARMS:
            prefix = f".agentlang/scheduled-batch-228/actors/{arm}/"
            rows = rows_for_group(archive_sidecar, ENDPOINT_GROUPS[arm], prefix)
            baseline = BASELINES / arm
            actor = ACTORS / arm
            accepted_rows = copy_archived_endpoint(archive, rows, prefix, baseline)
            shutil.copytree(baseline, actor, copy_function=shutil.copyfile)
            pristine = assert_pristine_equal(baseline, actor)
            accepted_endpoints[arm]["actorTreeSha256"] = pristine["treeSha256"]
            accepted_endpoints[arm]["baselineTreeSha256"] = pristine["treeSha256"]
            accepted_endpoints[arm]["actorByteIdenticalToBaseline"] = pristine["actorByteIdenticalToBaseline"]
            accepted_endpoints[arm]["fileCount"] = len(accepted_rows)

        runtimes = copy_runtime_outputs(build_output_rows)
        preflight_projects = prepare_preflight_projects()

        task = (EXPERIMENT / "task.md").read_text(encoding="utf-8")
        bootstrap = (EXPERIMENT / "bootstrap.md").read_text(encoding="utf-8")
        packet_receipts: dict[str, Any] = {}
        launcher_receipts: dict[str, Any] = {"participant": {}, "preflight": {}}
        for arm in ARMS:
            packet = prepare_packet(arm, task, bootstrap)
            packet_path = PACKETS / f"{arm}.md"
            packet_path.parent.mkdir(parents=True, exist_ok=True)
            packet_path.write_bytes(packet)
            packet_receipts[arm] = {
                "path": packet_path.relative_to(REPO).as_posix(),
                "bytes": len(packet),
                "sha256": sha256(packet),
            }
            for mode in ("participant", "preflight"):
                is_preflight = mode == "preflight"
                directory = LAUNCHERS if not is_preflight else PREFLIGHT
                launcher_path = directory / f"{arm}.ps1"
                raw = launcher_text(arm, preflight=is_preflight).encode("utf-8")
                launcher_path.parent.mkdir(parents=True, exist_ok=True)
                launcher_path.write_bytes(raw)
                launcher_receipts[mode][arm] = {
                    "path": launcher_path.relative_to(REPO).as_posix(),
                    "bytes": len(raw),
                    "sha256": sha256(raw),
                    "projectPath": (
                        f".agentlang/replacement-trial-230/preflight/projects/{arm}"
                        if is_preflight
                        else f".agentlang/replacement-trial-230/actors/{arm}"
                    ),
                    "tracePath": (
                        f".agentlang/replacement-trial-230/preflight/traces/{arm}.jsonl"
                        if is_preflight
                        else f".agentlang/replacement-trial-230/root/traces/{arm}.jsonl"
                    ),
                    "cliRuntime": runtimes["agentlang" if arm == "retained" else "conventional"]["path"],
                }

        for path in (ROOT / "scratch/temp", ROOT / "scratch/tmp", ROOT / "traces", PREFLIGHT / "traces"):
            path.mkdir(parents=True, exist_ok=True)

        source_manifest_row = next(
            row for row in archive_sidecar["members"] if row["path"] == SETUP_SOURCE_PATH
        )
        setup = {
            "study": "replacement-trial-230",
            "status": "prepared230",
            "participantsDispatched": 0,
            "dispatchAllowed": False,
            "freezeAllowed": False,
            "calibrationComplete": False,
            "independentReviewComplete": False,
            "launchOrder": ["fsharp", "retained"],
            "capabilities": [],
            "externalEffectGrants": [],
            "protocol": source_setup["protocol"],
            "environment": {
                "MSBuildEnableWorkloadResolver": "false",
                "TEMP": (ROOT / "scratch/temp").relative_to(REPO).as_posix(),
                "TMP": (ROOT / "scratch/tmp").relative_to(REPO).as_posix(),
                "packages": "No participant packages; the trusted runtime build kept NuGet audit enabled.",
                "networkBoundary": "No-web and broker-only rules are participant instructions; OS-level network confinement is not claimed.",
            },
            "task": {
                "path": (EXPERIMENT / "task.md").relative_to(REPO).as_posix(),
                "sha256": sha256((EXPERIMENT / "task.md").read_bytes()),
            },
            "bootstrap": {
                "path": (EXPERIMENT / "bootstrap.md").relative_to(REPO).as_posix(),
                "sha256": sha256((EXPERIMENT / "bootstrap.md").read_bytes()),
            },
            "sourceStudy": {
                "study": "scheduled-batch-228",
                "archive": archive_receipt["archive"],
                "archiveSidecar": archive_receipt["sidecarManifest"],
                "archiveInternalManifest": archive_receipt["internalManifest"],
                "archiveMemberCount": archive_receipt["memberCount"],
                "setupMember": source_manifest_row,
                "receipts": source_receipts,
                "acceptedEndpoints": accepted_endpoints,
            },
            "baselines": {
                arm: {
                    "sourceStudy": "scheduled-batch-228",
                    "baseline": (BASELINES / arm).relative_to(REPO).as_posix(),
                    "actor": (ACTORS / arm).relative_to(REPO).as_posix(),
                    "acceptedEndpoint": accepted_endpoints[arm],
                    "treeSha256": accepted_endpoints[arm]["baselineTreeSha256"],
                    "fileCount": accepted_endpoints[arm]["fileCount"],
                    "actorByteIdenticalToBaseline": True,
                }
                for arm in ARMS
            },
            "privateRuntimes": runtimes,
            "runtimeBuild": build_receipt,
            "preflight": {
                "projects": preflight_projects,
                "traces": {
                    arm: f".agentlang/replacement-trial-230/preflight/traces/{arm}.jsonl" for arm in ARMS
                },
                "participantTraces": {
                    arm: f".agentlang/replacement-trial-230/root/traces/{arm}.jsonl" for arm in ARMS
                },
                "disposableProjectsSeparatedFromActors": True,
                "interactiveSmokeStatus": "pending-coordinator",
            },
            "packets": packet_receipts,
            "launchers": launcher_receipts,
            "architectureDisclosure": (
                "F# routes both batch paths through renewBatchStep while AgentLang has separate common and scheduled step callers."
            ),
            "preparationOnly": True,
            "preparedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        }
        write_json(SETUP, setup)
        return setup
    finally:
        archive.close()


if __name__ == "__main__":
    try:
        result = prepare()
        print(
            json.dumps(
                {
                    "study": result["study"],
                    "status": result["status"],
                    "participantsDispatched": result["participantsDispatched"],
                    "dispatchAllowed": result["dispatchAllowed"],
                    "launchOrder": result["launchOrder"],
                    "baselines": {
                        arm: {
                            "treeSha256": result["baselines"][arm]["treeSha256"],
                            "fileCount": result["baselines"][arm]["fileCount"],
                        }
                        for arm in ARMS
                    },
                    "privateRuntimes": {
                        name: {
                            "path": pin["path"],
                            "treeSha256": pin["treeSha256"],
                            "fileCount": pin["fileCount"],
                        }
                        for name, pin in result["privateRuntimes"].items()
                    },
                    "setupPath": SETUP.relative_to(REPO).as_posix(),
                },
                indent=2,
                ensure_ascii=False,
            )
        )
    except Exception as exc:
        print(f"replacement-trial-230 preparation failed: {exc}", file=sys.stderr)
        raise
