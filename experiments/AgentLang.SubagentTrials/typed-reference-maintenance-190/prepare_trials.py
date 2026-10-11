"""Prepare frozen 190 actors; this script never dispatches participants."""
import argparse
import hashlib
import json
import os
import shutil
import stat
from pathlib import Path, PurePosixPath

REPO = Path(__file__).resolve().parents[3]
STUDY = Path(__file__).resolve().parent
GATE = REPO / ".agentlang/efficacy-maintenance-184/dispatch-gate.json"
PINS = REPO / ".agentlang/type-evolution-188/accepted-runtime-pins.json"
READINESS = REPO / ".agentlang/type-evolution-188/readiness-control/runs/readiness-002/evidence/readiness-control-receipt.json"
GATE_SHA = "9350b6f58cd6dd394bdeba87375feea9fadac857936291efd6c90e2d7adbf05f"
PINS_SHA = "89cc407b0b81726d904d681a2b4c7195af38687d5d8b00fe73a5745a5223aac3"
READINESS_SHA = "c3242e18c24e37f1f06088023ceeb0878470d95e5936b347e9e9d7ac0f2722bb"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def check_path(path):
    path = Path(path)
    for part in (path, *path.parents):
        if part == REPO.parent:
            break
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        reparse = getattr(info, "st_file_attributes", 0) & 0x400
        check(not stat.S_ISLNK(info.st_mode) and not reparse, f"link/reparse path refused: {part}")


def relative_path(relative):
    parts = PurePosixPath(relative).parts
    check(parts and not PurePosixPath(relative).is_absolute() and ".." not in parts, f"invalid frozen path: {relative}")
    path = REPO.joinpath(*parts)
    check_path(path)
    resolved = path.resolve()
    resolved.relative_to(REPO.resolve())
    check(resolved.is_file() or resolved.is_dir(), f"frozen path missing: {relative}")
    return resolved


def relative_file(relative):
    path = relative_path(relative)
    check(path.is_file(), f"frozen file missing: {relative}")
    return path


def inventory(root):
    root = Path(root)
    check_path(root)
    root_real = root.resolve()
    files = {}
    for parent, dirs, names in os.walk(root, followlinks=False):
        for name in list(dirs) + names:
            path = Path(parent) / name
            check_path(path)
            path.resolve().relative_to(root_real)
        for name in names:
            path = Path(parent) / name
            files[path.relative_to(root).as_posix()] = sha(path)
    return files


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--conventional-cli", required=True, help="fresh 190 AgentLang.Conventional.Cli.dll path")
    args = parser.parse_args()

    check(sha(GATE) == GATE_SHA, "184 dispatch-gate hash changed")
    check(sha(PINS) == PINS_SHA, "accepted runtime pins changed")
    check(sha(READINESS) == READINESS_SHA, "readiness-002 receipt changed")
    gate = json.loads(GATE.read_text(encoding="utf-8"))
    accepted = json.loads(PINS.read_text(encoding="utf-8"))
    readiness = json.loads(READINESS.read_text(encoding="utf-8"))
    check(gate["status"] == "passed" and gate["participantsDispatched"] == 0, "184 freeze is not reusable")
    check(accepted["fullGatePassed"] and accepted["fullGateExitCode"] == 0, "accepted AgentLang gate failed")
    check(readiness["status"] == "passed" and readiness["runId"] == "readiness-002", "189 readiness control failed")
    frozen = {item["path"]: item["sha256"] for item in gate["pins"]}
    for relative, expected in frozen.items():
        check(sha(relative_file(relative)) == expected, f"184 frozen input changed: {relative}")

    cli_rel = accepted["cliDll"]
    flow_cli = relative_file(cli_rel)
    accepted_files = accepted["files"]
    flow_runtime = [{"path": item["path"], "sha256": item["sha256"]} for item in accepted_files]
    for item in accepted_files:
        check(sha(relative_file(item["path"])) == item["sha256"], f"accepted runtime changed: {item['path']}")
    check(sha(flow_cli) == readiness["acceptedCliSha256"], "readiness runtime differs from accepted CLI")

    conventional_cli = Path(args.conventional_cli)
    if not conventional_cli.is_absolute():
        conventional_cli = REPO / conventional_cli
    check_path(conventional_cli)
    conventional_cli = conventional_cli.resolve()
    conventional_cli.relative_to(REPO.resolve())
    check(conventional_cli.is_file(), "conventional CLI is missing")
    conventional_root = conventional_cli.parent
    conventional_files = inventory(conventional_root)
    conventional_runtime = [
        {"path": (conventional_root / name).relative_to(REPO).as_posix(), "sha256": digest}
        for name, digest in sorted(conventional_files.items())
    ]

    output = REPO / ".agentlang/efficacy-maintenance-190/trials"
    check_path(output)
    check(not output.exists(), f"refusing to overwrite {output}")
    output.mkdir()
    source_pins = {
        path: digest for path, digest in frozen.items()
        if path.startswith("experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/")
        and ("/prompts/" in path or "/oracle/" in path or "/seeds/" in path or path.endswith("/task-contract.md"))
    }
    protocol_pins = [
        {"path": item["path"], "sha256": item["sha256"]}
        for item in accepted_files if item["path"].startswith("scripts/")
    ]
    flow_ops = ",".join(json.loads((REPO / "experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/prompts/proposed-allowlist.json").read_text(encoding="utf-8"))["allowedOperations"])
    common = (
        "The human authorizes edits only to your assigned isolated trial project and local test execution through this broker. "
        "You are an external coding agent. Read only this prompt, then launch the exact broker command once, without changing its allowlist or limits. "
        "Use only the broker for discovery, edits and tests. Do not read repository files, other trials, oracles or reports; do not use web tools or other agents. "
        "Disclose any non-broker tool use. One session, at most 100 exchanges. Poll the same live handle after timeouts; do not restart for silence or repeat uncertain mutations. "
        'Finish with {"op":"host.close"} and observe terminal status.\n'
    )
    primer = (
        "Flow/2 uses fn, named typed inputs, immutable let, record properties value.field, dotted calls, newline separators and exhaustive matches. "
        'Include frontend:"flow", syntaxVersion:2 on define/eval. Use describe/source/tests/examples/help to discover syntax. Exact-root calls begin with a dot. '
        "Omitted effects means pure. Begin task.begin, define, test, commit with library:true for library functions, then task.commit. "
        "Library dependency closure and own coverage gates remain enforced. Do not edit storage manifests.\n"
    )
    actors = []
    arms = [
        ("retained-1", "retained"), ("reset-1", "reset-rich"), ("fsharp-1", "fsharp"),
        ("fsharp-2", "fsharp"), ("reset-2", "reset-rich"), ("retained-2", "retained"),
    ]
    seed_roots = gate["seeds"]
    for name, arm in arms:
        source = relative_path(seed_roots[arm])
        source_files = inventory(source)
        for relative, digest in source_files.items():
            frozen_path = (source / relative).relative_to(REPO).as_posix()
            check(frozen.get(frozen_path) == digest, f"seed is not frozen: {frozen_path}")
        project = output / "actors" / name
        project.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, project)
        copied = inventory(project)
        check(copied == source_files, f"copied seed differs: {name}")
        temp = output / "temp" / name
        temp.mkdir(parents=True)
        trace = output / "traces" / f"{name}.jsonl"
        trace.parent.mkdir(parents=True, exist_ok=True)
        cli = flow_cli if arm != "fsharp" else conventional_cli
        runtime = flow_runtime if arm != "fsharp" else conventional_runtime
        operations = flow_ops if arm != "fsharp" else "inspect,read,search,patch,replace,validate"
        profile = "agentlang" if arm != "fsharp" else "conventional"
        launch = (
            f"$env:TEMP = '{temp}'\n$env:TMP = $env:TEMP\n$env:DOTNET_CLI_HOME = '{temp}'\n"
            f"& '{REPO / 'scripts/Start-SubagentTrialHostV2.ps1'}' -CliDll '{cli}' -ProjectPath '{project}' "
            f"-TracePath '{trace}' -AllowedOperations '{operations}' -Profile '{profile}' -Capabilities @() "
            "-ClockValue '2000-01-01T00:00:00Z' -MaxRequestBytes 262144 -MaxResponseBytes 524288 "
            "-ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
        )
        if arm == "fsharp":
            launch += " -AdditionalCliArguments @('--validation-project','tests/AgentLang.TypedReference.Tests.fsproj')"
        task_prompt = (STUDY.parent / "typed-reference-maintenance-184" / "prompts" / (
            "fsharp.md" if arm == "fsharp" else f"agentlang-{arm}.md"
        )).read_text(encoding="utf-8")
        specific = (
            "Edit only business/Business.fs and tests/Program.fs; project files are frozen. Validate uses the fixed local test project.\n"
            if arm == "fsharp" else primer
        )
        prompt_path = output / "prompts" / f"{name}.md"
        prompt_path.parent.mkdir(parents=True, exist_ok=True)
        prompt_path.write_text(
            common + "\n" + task_prompt + "\n" + specific +
            "Launch with exec_command, tty:true, yield_time_ms:10000.\n\n~~~powershell\n" + launch + "\n~~~\n",
            encoding="utf-8",
        )
        actors.append({
            "name": name, "arm": arm, "project": project.relative_to(REPO).as_posix(),
            "cli": cli.relative_to(REPO).as_posix(), "cliSha256": sha(cli), "runtimePins": runtime,
            "operations": operations, "profile": profile,
            "prompt": {
                "path": prompt_path.relative_to(REPO).as_posix(), "sha256": sha(prompt_path),
                "trace": trace.relative_to(REPO).as_posix(),
            },
            "trace": trace.relative_to(REPO).as_posix(),
            "initialPins": [{"path": (project / p).relative_to(REPO).as_posix(), "sha256": h} for p, h in sorted(copied.items())],
        })
    study_sources = inventory(STUDY)
    study_pins = {
        (STUDY / name).relative_to(REPO).as_posix(): digest
        for name, digest in sorted(study_sources.items())
    }
    manifest = {
        "format": 1, "dispatchGateSha256": GATE_SHA, "acceptedRuntimePinsSha256": PINS_SHA,
        "readinessReceiptSha256": READINESS_SHA, "sourcePins": source_pins,
        "newStudyPins": study_pins,
        "protocolPins": protocol_pins, "actors": actors,
    }
    (output / "actors.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared six isolated 190 trials at {output}; no participants dispatched")


if __name__ == "__main__":
    main()
