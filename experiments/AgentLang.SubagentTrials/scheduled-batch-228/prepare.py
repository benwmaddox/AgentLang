#!/usr/bin/env python3
"""Prepare fresh, undispatched scheduled-batch-228 inputs from frozen 226 assets."""

from __future__ import annotations

import hashlib
import json
import random
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[3]
EXPERIMENT = REPO / "experiments/AgentLang.SubagentTrials/scheduled-batch-228"
SOURCE_DATA = REPO / ".agentlang/scheduled-batch-226"
DATA = REPO / ".agentlang/scheduled-batch-228"
BASELINES = DATA / "baselines"
ACTORS = DATA / "actors"
PACKETS = DATA / "packets"
LAUNCHERS = DATA / "launchers"
TRACES = DATA / "traces"
ROOT = DATA / "root"
SETUP = DATA / "setup.json"
ARMS = ("retained", "fsharp")
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "cache", "caches", "__pycache__"}
FSHARP_FILES = {
    "Domain.fs", "Operations.fs", "Fixtures.fs", "SelfTests.fs", "Program.fs",
    "SubscriptionBatch.Conventional.fsproj",
}
STARTUP = (
    "Launch the packet's exact PowerShell command through `exec_command` with "
    "`tty:true`. Do not pipe requests or redirect stdin. Keep the returned "
    "`session_id`; send requests with `write_stdin` to that same session. If an "
    "observation times out, continue with that session and do not restart the "
    "process. Send `{\"op\":\"host.close\"}` through `write_stdin` to the same "
    "handle and observe terminal exit; the coordinator will audit termination."
)


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


def assert_no_generated_dirs(root: Path) -> None:
    for path in root.rglob("*"):
        relative = path.relative_to(root)
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in baseline source: {path}")
        if path.is_dir() and any(part.casefold() in EXCLUDED_DIRS for part in relative.parts):
            raise RuntimeError(f"Generated or cache directory in baseline source: {path}")


def inventory(root: Path) -> list[dict[str, Any]]:
    return [
        {
            "path": path.relative_to(root).as_posix(),
            "bytes": len(data := path.read_bytes()),
            "sha256": sha256(data),
        }
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


def repo_path(relative: str) -> Path:
    path = (REPO / relative).resolve()
    try:
        path.relative_to(REPO.resolve())
    except ValueError as exc:
        raise RuntimeError(f"Pinned path escapes the repository: {relative}") from exc
    return path


def verify_file_pin(pin: dict[str, Any]) -> dict[str, Any]:
    path = repo_path(pin["path"])
    if not path.is_file():
        raise RuntimeError(f"Pinned input is missing: {pin['path']}")
    data = path.read_bytes()
    actual = {"path": pin["path"], "bytes": len(data), "sha256": sha256(data)}
    if actual["bytes"] != pin["bytes"] or actual["sha256"] != pin["sha256"]:
        raise RuntimeError(f"Pinned input changed: {pin['path']}")
    return actual


def verify_runtime_pins(candidate: dict[str, Any]) -> dict[str, Any]:
    receipts: dict[str, Any] = {}
    for name, pin in candidate["privateRuntimes"].items():
        root = repo_path(pin["path"])
        rows = inventory(root)
        if rows != pin["files"] or tree_hash(rows) != pin["treeSha256"]:
            raise RuntimeError(f"226 private runtime changed: {name}")
        receipts[name] = {
            "path": pin["path"],
            "treeSha256": tree_hash(rows),
            "files": rows,
        }
    return receipts


def verify_baseline_pins(candidate: dict[str, Any], source_setup: dict[str, Any]) -> dict[str, Path]:
    sources: dict[str, Path] = {}
    for arm in ARMS:
        pin = candidate["baselineCopies"][arm]
        source = repo_path(pin["baseline"])
        if not source.is_dir():
            raise RuntimeError(f"226 {arm} baseline is missing: {pin['baseline']}")
        assert_no_generated_dirs(source)
        rows = inventory(source)
        if len(rows) != pin["fileCount"] or tree_hash(rows) != pin["treeSha256"]:
            raise RuntimeError(f"226 {arm} baseline copy changed")
        setup_pin = source_setup["baselines"][arm]
        if setup_pin["baselineTreeSha256"] != pin["treeSha256"]:
            raise RuntimeError(f"226 {arm} baseline setup receipt differs from the freeze candidate")
        if arm == "fsharp" and {row["path"] for row in rows} != FSHARP_FILES:
            raise RuntimeError("The 226 F# baseline must contain exactly its six source/project files.")
        sources[arm] = source
    fingerprint = retained_fingerprint(sources["retained"])
    if fingerprint != candidate["retainedBaselineFingerprint"]:
        raise RuntimeError("226 retained baseline fingerprint differs from the frozen candidate.")
    return sources


def replace_exact(data: bytes, old: bytes, new: bytes, label: str, expected_count: int = 1) -> bytes:
    count = data.count(old)
    if count != expected_count:
        raise RuntimeError(f"Expected {expected_count} {label} occurrence(s) in a 226 launcher/packet, found {count}.")
    return data.replace(old, new)


def prepare_packet(arm: str) -> bytes:
    source = SOURCE_DATA / f"packets/{arm}.md"
    data = source.read_bytes()
    newline = b"\r\n" if b"\r\n" in data else b"\n"
    data = replace_exact(
        data,
        b".agentlang/scheduled-batch-226/",
        b".agentlang/scheduled-batch-228/",
        "study launch path",
        expected_count=2,
    )
    heading = b"# Common participant bootstrap" + newline + newline
    data = replace_exact(data, heading, heading + STARTUP.encode("utf-8") + newline + newline, "common bootstrap heading")
    return data


def prepare_launcher(arm: str, cli_runtime_path: str) -> bytes:
    path = SOURCE_DATA / f"launchers/{arm}.ps1"
    data = path.read_bytes()
    replacements = (
        (f".agentlang/scheduled-batch-226/actors/{arm}".encode(), f".agentlang/scheduled-batch-228/actors/{arm}".encode(), "ProjectPath"),
        (f".agentlang/scheduled-batch-226/traces/{arm}.jsonl".encode(), f".agentlang/scheduled-batch-228/traces/{arm}.jsonl".encode(), "TracePath"),
        (b".agentlang/scheduled-batch-226/root/scratch/temp", b".agentlang/scheduled-batch-228/root/scratch/temp", "TEMP"),
        (b".agentlang/scheduled-batch-226/root/scratch/tmp", b".agentlang/scheduled-batch-228/root/scratch/tmp", "TMP"),
    )
    for old, new, label in replacements:
        data = replace_exact(data, old, new, label)
    cli_name = "AgentLang.Cli.dll" if arm == "retained" else "AgentLang.Conventional.Cli.dll"
    expected_cli = f"{cli_runtime_path}/{cli_name}".encode()
    if expected_cli not in data:
        raise RuntimeError(f"The {arm} launcher no longer points at its pinned 226 CLI runtime.")
    if b".agentlang/scheduled-batch-228/runtime/" in data:
        raise RuntimeError(f"The {arm} launcher must continue using the pinned 226 CLI runtime.")
    return data


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="")


def prepare() -> dict[str, Any]:
    outputs = (BASELINES, ACTORS, PACKETS, LAUNCHERS, TRACES, ROOT, SETUP)
    existing = [str(path) for path in outputs if path.exists()]
    if existing:
        raise RuntimeError("Refusing to overwrite 228 preparation outputs: " + ", ".join(existing))

    candidate_path = SOURCE_DATA / "freeze-candidate.json"
    supplement_path = SOURCE_DATA / "freeze-supplement.json"
    freeze_path = SOURCE_DATA / "freeze.json"
    candidate_bytes = candidate_path.read_bytes()
    supplement_bytes = supplement_path.read_bytes()
    freeze = json.loads(freeze_path.read_text(encoding="utf-8"))
    candidate = json.loads(candidate_bytes)
    supplement = json.loads(supplement_bytes)
    source_setup = json.loads((SOURCE_DATA / "setup.json").read_text(encoding="utf-8"))

    if freeze.get("study") != "scheduled-batch-226" or freeze.get("status") != "frozen-ready":
        raise RuntimeError("The source study must be the frozen 226 study.")
    if candidate.get("study") != "scheduled-batch-226" or supplement.get("study") != "scheduled-batch-226":
        raise RuntimeError("The frozen 226 candidate and supplement are required.")
    candidate_hash = sha256(candidate_bytes)
    supplement_hash = sha256(supplement_bytes)
    if freeze.get("candidate", {}).get("sha256") != candidate_hash:
        raise RuntimeError("The 226 freeze candidate hash does not match its final freeze receipt.")
    if freeze.get("supplement", {}).get("sha256") != supplement_hash:
        raise RuntimeError("The 226 freeze supplement hash does not match its final freeze receipt.")
    if supplement.get("extends", {}).get("sha256") != candidate_hash:
        raise RuntimeError("The 226 supplement does not extend the pinned candidate.")

    candidate_pins = candidate.get("inputHashes", [])
    supplement_pins = supplement.get("additionalPinnedInputs", [])
    if not candidate_pins or not supplement_pins:
        raise RuntimeError("The frozen candidate and supplement input pins are required.")
    for pin in candidate_pins + supplement_pins:
        verify_file_pin(pin)
    if candidate.get("model", {}).get("status") != "model-controls-passed":
        raise RuntimeError("The pinned 226 model checks did not pass.")
    if candidate.get("semanticTypeGate", {}).get("status") != "passed":
        raise RuntimeError("The pinned 226 semantic type gate did not pass.")
    if candidate.get("pristinePreflight", {}).get("status") != "passed":
        raise RuntimeError("The pinned 226 pristine preflight did not pass.")

    baseline_sources = verify_baseline_pins(candidate, source_setup)
    runtime_receipts = verify_runtime_pins(candidate)
    for output in outputs:
        output.parent.mkdir(parents=True, exist_ok=True)
    BASELINES.mkdir()
    ACTORS.mkdir()
    PACKETS.mkdir()
    LAUNCHERS.mkdir()
    TRACES.mkdir()
    (ROOT / "scratch/temp").mkdir(parents=True)
    (ROOT / "scratch/tmp").mkdir(parents=True)

    baseline_receipts: dict[str, Any] = {}
    packet_receipts: dict[str, Any] = {}
    launcher_receipts: dict[str, Any] = {}
    for arm in ARMS:
        source = baseline_sources[arm]
        baseline = BASELINES / arm
        actor = ACTORS / arm
        shutil.copytree(source, baseline, copy_function=shutil.copyfile)
        shutil.copytree(baseline, actor, copy_function=shutil.copyfile)
        baseline_rows = inventory(baseline)
        actor_rows = inventory(actor)
        expected = candidate["baselineCopies"][arm]
        if baseline_rows != actor_rows or tree_hash(actor_rows) != expected["treeSha256"]:
            raise RuntimeError(f"The new {arm} actor differs from its 226 baseline copy.")
        baseline_receipts[arm] = {
            "source226Baseline": expected["baseline"],
            "baseline": baseline.relative_to(REPO).as_posix(),
            "actor": actor.relative_to(REPO).as_posix(),
            "treeSha256": tree_hash(actor_rows),
            "fileCount": len(actor_rows),
            "actorByteIdenticalToBaseline": True,
        }

        packet = prepare_packet(arm)
        packet_path = PACKETS / f"{arm}.md"
        packet_path.write_bytes(packet)
        packet_receipts[arm] = {
            "source226Packet": f".agentlang/scheduled-batch-226/packets/{arm}.md",
            "sha256": sha256(packet),
            "bytes": len(packet),
        }

        runtime_name = "agentlang" if arm == "retained" else "conventional"
        launcher = prepare_launcher(arm, runtime_receipts[runtime_name]["path"])
        launcher_path = LAUNCHERS / f"{arm}.ps1"
        launcher_path.write_bytes(launcher)
        launcher_receipts[arm] = {
            "source226Launcher": f".agentlang/scheduled-batch-226/launchers/{arm}.ps1",
            "sha256": sha256(launcher),
            "bytes": len(launcher),
            "cliRuntime": runtime_receipts[runtime_name]["path"],
        }

    launch_order = list(ARMS)
    random.SystemRandom().shuffle(launch_order)
    setup = {
        "study": "scheduled-batch-228",
        "status": "prepared228",
        "retryReason": "The F# arm in scheduled-batch-226 ended at startup with input EOF.",
        "participantsDispatched": 0,
        "dispatchAllowed": False,
        "launchOrder": launch_order,
        "capabilities": [],
        "externalEffectGrants": [],
        "protocol": source_setup["protocol"],
        "environment": {
            "MSBuildEnableWorkloadResolver": "false",
            "TEMP": (ROOT / "scratch/temp").relative_to(REPO).as_posix(),
            "TMP": (ROOT / "scratch/tmp").relative_to(REPO).as_posix(),
            "packages": "F# validation is local and no packages may be added.",
            "networkBoundary": "No-web and broker-only rules are participant instructions; OS-level network confinement is not claimed.",
        },
        "sourceStudy": {
            "freeze": {"path": freeze_path.relative_to(REPO).as_posix(), "sha256": sha256(freeze_path.read_bytes())},
            "candidate": {"path": candidate_path.relative_to(REPO).as_posix(), "sha256": candidate_hash},
            "supplement": {"path": supplement_path.relative_to(REPO).as_posix(), "sha256": supplement_hash},
            "candidateInputPinsVerified": len(candidate_pins),
            "supplementInputPinsVerified": len(supplement_pins),
            "model": candidate["model"],
            "scoreScripts": [pin for pin in candidate_pins if pin["path"].endswith(("score_language.py", "score_fsharp.py"))],
            "calibrationReceiptsPinnedByCandidate": True,
        },
        "retainedBaselineFingerprint": candidate["retainedBaselineFingerprint"],
        "baselines": baseline_receipts,
        "privateRuntimesReusedFrom226": runtime_receipts,
        "packets": packet_receipts,
        "launchers": launcher_receipts,
        "preparationOnly": True,
        "preparedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    }
    write_json(SETUP, setup)
    return setup


if __name__ == "__main__":
    print(json.dumps(prepare(), indent=2, ensure_ascii=False))
