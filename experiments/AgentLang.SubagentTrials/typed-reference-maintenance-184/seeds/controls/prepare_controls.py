"""Create isolated Flow control projects for study184 oracle calibration."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any


STUDY = "typed-reference-maintenance-184"
STORE_SETUP_WORDS = [
    "store.shipment-lookup-step",
    "store.shipment",
    "store.shipment-replacement-step",
    "store.with-shipment",
]
INGEST_WORDS = [
    "shipment.ingest-rule",
    "shipment.ingest",
    "shipment.batch-step",
    "shipment.ingest-batch",
]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_function(source: str, name: str, replacement: str) -> str:
    start = source.index(f"fn {name}(")
    open_brace = source.index("{", start)
    depth = 0
    for index in range(open_brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[:start] + replacement + source[index + 1 :]
    raise ValueError(f"Unclosed function body for {name}")


def nominal_seed(common_path: Path, retained_path: Path) -> str:
    common = common_path.read_text(encoding="utf-8")
    retained = retained_path.read_text(encoding="utf-8")

    common = common.replace(
        "type ShipmentId : String { }",
        "type ShipmentId : String { }\n\ntype TrackingReference : String { }",
        1,
    )
    if common.count("field reference: String") != 2:
        raise ValueError("Expected reference fields on Scan and ScanInput")
    common = common.replace("field reference: String", "field reference: TrackingReference")
    common = common.replace("field target: String", "field target: TrackingReference", 1)
    common = re.sub(
        r'reference = "([^"\\]*)"',
        lambda match: f"reference = TrackingReference.new({json.dumps(match.group(1))})",
        common,
    )
    common = re.sub(
        r'target = "([^"\\]*)"',
        lambda match: f"target = TrackingReference.new({json.dumps(match.group(1))})",
        common,
    )

    if retained.count("reference: String") != 1:
        raise ValueError("Expected one retained helper reference parameter")
    retained = retained.replace("reference: String", "reference: TrackingReference", 1)
    retained = re.sub(
        r'reference = "([^"\\]*)"',
        lambda match: f"reference = TrackingReference.new({json.dumps(match.group(1))})",
        retained,
    )
    retained = re.sub(
        r'shipment\.find-scan\(([^,]+), "([^"\\]*)"\)',
        lambda match: (
            f"shipment.find-scan({match.group(1)}, "
            f"TrackingReference.new({json.dumps(match.group(2))}))"
        ),
        retained,
    )
    retained = re.sub(
        r'selected\.reference, "([^"\\]*)"',
        lambda match: f"selected.reference, TrackingReference.new({json.dumps(match.group(1))})",
        retained,
    )
    return common.rstrip() + "\n\n" + retained.strip() + "\n"


CORRECT_RULE = '''fn shipment.ingest-rule(shipment: Shipment, input: ScanInput) -> Result<Shipment, ScanError> {
    effects none
    doc "Append the first scan after validating status; an existing exact reference is idempotent."

    let status-ok = bool.or(
        equals(input.status, "received"),
        bool.or(equals(input.status, "in-transit"), equals(input.status, "delivered")));
    if status-ok {
        match .shipment.find-scan(shipment, input.reference) {
            some existing => { result.ok<Shipment, ScanError>(shipment) }
            none => {
                let scan = .scan.new(reference = input.reference, status = input.status);
                let updated = .shipment.new(
                    id = shipment.id,
                    label = shipment.label,
                    scans = list.append(shipment.scans, scan));
                result.ok<Shipment, ScanError>(updated)
            }
        }
    } else {
        result.error<Shipment, ScanError>(ScanError.invalid-status())
    }
}'''


ALWAYS_APPEND_RULE = '''fn shipment.ingest-rule(shipment: Shipment, input: ScanInput) -> Result<Shipment, ScanError> {
    effects none
    doc "Append each valid scan after validating status."

    let status-ok = bool.or(
        equals(input.status, "received"),
        bool.or(equals(input.status, "in-transit"), equals(input.status, "delivered")));
    if status-ok {
        let scan = .scan.new(reference = input.reference, status = input.status);
        let updated = .shipment.new(
            id = shipment.id,
            label = shipment.label,
            scans = list.append(shipment.scans, scan));
        result.ok<Shipment, ScanError>(updated)
    } else {
        result.error<Shipment, ScanError>(ScanError.invalid-status())
    }
}'''


GLOBAL_LOOKUP = '''fn store.scan-lookup-step(state: ShipmentScanLookup, candidate: Shipment) -> ShipmentScanLookup {
    effects none
    doc "Fold each shipment's scans into the store-wide exact-reference lookup."

    candidate.scans.fold(state, .shipment.scan-lookup-step)
}

fn store.find-scan(store: Store, reference: TrackingReference) -> Option<Scan> {
    effects none
    doc "Return the first exact-reference match anywhere in the Store."

    let initial = shipmentScanLookup.new(target = reference, found = option.none<Scan>());
    store.shipments.fold(initial, .store.scan-lookup-step).found
}

test store.scan-lookup-step/folds-shipment-scans {
    let reference = TrackingReference.new("scan-1");
    let expected = .scan.new(reference = reference, status = "received");
    let item = .shipment.new(
        id = ShipmentId.new("shipment-1"),
        label = "Box",
        scans = list.singleton<Scan>(expected));
    let state = shipmentScanLookup.new(target = reference, found = option.none<Scan>());
    match store.scan-lookup-step(state, item).found {
        some selected => { equals(selected, expected) }
        none => { false }
    }
    => true
}

test store.find-scan/returns-first-store-match {
    let reference = TrackingReference.new("scan-1");
    let first = .scan.new(reference = reference, status = "received");
    let later = .scan.new(reference = reference, status = "delivered");
    let first-shipment = .shipment.new(
        id = ShipmentId.new("shipment-1"), label = "First",
        scans = list.singleton<Scan>(first));
    let later-shipment = .shipment.new(
        id = ShipmentId.new("shipment-2"), label = "Later",
        scans = list.singleton<Scan>(later));
    let current = .store.new(
        shipments = list.append(list.singleton<Shipment>(first-shipment), later-shipment),
        audit = "keep", generation = 1);
    equals(store.find-scan(current, reference), option.some<Scan>(first))
    => true
}
'''


GLOBAL_INGEST = '''fn shipment.ingest(store: Store, input: ScanInput) -> Result<Store, ScanError> {
    effects none
    doc "Ingest one scan after checking shipment existence and status; this control deduplicates store-wide."

    match .store.shipment(input.shipment-id, store) {
        some selected => {
            match shipment.ingest-rule(selected, input) {
                error problem => { result.error<Store, ScanError>(problem) }
                ok updated-shipment => {
                    match .store.find-scan(store, input.reference) {
                        some existing => { result.ok<Store, ScanError>(store) }
                        none => {
                            let updated-store = .store.with-shipment(store, updated-shipment);
                            result.ok<Store, ScanError>(updated-store)
                        }
                    }
                }
            }
        }
        none => {
            result.error<Store, ScanError>(ScanError.unknown-shipment())
        }
    }
}'''


CORRECT_TESTS = '''
test shipment.ingest/replay-keeps-first-scan {
    let key = ShipmentId.new("shipment-1");
    let first = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let item = .shipment.new(id = key, label = "Box", scans = list.singleton<Scan>(first));
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 11);
    let input = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "delivered");
    equals(shipment.ingest(current, input), result.ok<Store, ScanError>(current))
    => true
}

test shipment.ingest/same-reference-on-another-shipment-is-independent {
    let first-id = ShipmentId.new("shipment-1");
    let second-id = ShipmentId.new("shipment-2");
    let first-scan = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let first = .shipment.new(id = first-id, label = "Box", scans = list.singleton<Scan>(first-scan));
    let second = .shipment.new(id = second-id, label = "Envelope", scans = list.empty<Scan>());
    let current = .store.new(shipments = list.append(list.singleton<Shipment>(first), second), audit = "sentinel", generation = 12);
    let updated-second = .shipment.new(
        id = second-id,
        label = "Envelope",
        scans = list.singleton<Scan>(.scan.new(reference = TrackingReference.new("scan-1"), status = "delivered")));
    let expected = .store.new(
        shipments = list.append(list.singleton<Shipment>(first), updated-second),
        audit = "sentinel", generation = 12);
    let input = scanInput.new(shipment-id = second-id, reference = TrackingReference.new("scan-1"), status = "delivered");
    equals(shipment.ingest(current, input), result.ok<Store, ScanError>(expected))
    => true
}

test shipment.ingest/invalid-status-on-replay-still-errors {
    let key = ShipmentId.new("shipment-1");
    let existing = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let item = .shipment.new(id = key, label = "Box", scans = list.singleton<Scan>(existing));
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 13);
    let input = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "invalid");
    match shipment.ingest(current, input) {
        ok updated => { false }
        error problem => { equals(problem, ScanError.invalid-status()) }
    }
    => true
}

test shipment.ingest-batch/replay-keeps-first-event {
    let key = ShipmentId.new("shipment-1");
    let item = .shipment.new(id = key, label = "Box", scans = list.empty<Scan>());
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 14);
    let first = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "received");
    let replay = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "delivered");
    let events = list.append(list.singleton<ScanInput>(first), replay);
    let updated = .shipment.new(
        id = key,
        label = "Box",
        scans = list.singleton<Scan>(.scan.new(reference = TrackingReference.new("scan-1"), status = "received")));
    let expected = .store.new(shipments = list.singleton<Shipment>(updated), audit = "sentinel", generation = 14);
    equals(shipment.ingest-batch(current, events), result.ok<Store, ScanError>(expected))
    => true
}
'''


GLOBAL_SAFE_TESTS = '''
test shipment.ingest/replay-keeps-first-scan {
    let key = ShipmentId.new("shipment-1");
    let first = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let item = .shipment.new(id = key, label = "Box", scans = list.singleton<Scan>(first));
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 11);
    let input = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "delivered");
    equals(shipment.ingest(current, input), result.ok<Store, ScanError>(current))
    => true
}

test shipment.ingest/invalid-status-on-replay-still-errors {
    let key = ShipmentId.new("shipment-1");
    let existing = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let item = .shipment.new(id = key, label = "Box", scans = list.singleton<Scan>(existing));
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 13);
    let input = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "invalid");
    match shipment.ingest(current, input) {
        ok updated => { false }
        error problem => { equals(problem, ScanError.invalid-status()) }
    }
    => true
}
'''


ALWAYS_APPEND_TEST = '''
test shipment.ingest/replay-appends-second-scan {
    let key = ShipmentId.new("shipment-1");
    let first = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let item = .shipment.new(id = key, label = "Box", scans = list.singleton<Scan>(first));
    let current = .store.new(shipments = list.singleton<Shipment>(item), audit = "sentinel", generation = 11);
    let second = .scan.new(reference = TrackingReference.new("scan-1"), status = "delivered");
    let updated = .shipment.new(id = key, label = "Box", scans = list.append(list.singleton<Scan>(first), second));
    let expected = .store.new(shipments = list.singleton<Shipment>(updated), audit = "sentinel", generation = 11);
    let input = scanInput.new(shipment-id = key, reference = TrackingReference.new("scan-1"), status = "delivered");
    equals(shipment.ingest(current, input), result.ok<Store, ScanError>(expected))
    => true
}
'''


GLOBAL_WRONG_TEST = '''
test shipment.ingest/same-reference-on-another-shipment-is-globally-deduplicated {
    let first-id = ShipmentId.new("shipment-1");
    let second-id = ShipmentId.new("shipment-2");
    let first-scan = .scan.new(reference = TrackingReference.new("scan-1"), status = "received");
    let first = .shipment.new(id = first-id, label = "Box", scans = list.singleton<Scan>(first-scan));
    let second = .shipment.new(id = second-id, label = "Envelope", scans = list.empty<Scan>());
    let current = .store.new(shipments = list.append(list.singleton<Shipment>(first), second), audit = "sentinel", generation = 12);
    let input = scanInput.new(shipment-id = second-id, reference = TrackingReference.new("scan-1"), status = "delivered");
    equals(shipment.ingest(current, input), result.ok<Store, ScanError>(current))
    => true
}
'''


def source_variants(common_path: Path, retained_path: Path) -> dict[str, str]:
    typed = nominal_seed(common_path, retained_path)
    correct = replace_function(typed, "shipment.ingest-rule", CORRECT_RULE)
    correct += CORRECT_TESTS

    always = replace_function(typed, "shipment.ingest-rule", ALWAYS_APPEND_RULE)
    always += ALWAYS_APPEND_TEST

    global_source = replace_function(typed, "shipment.ingest-rule", CORRECT_RULE)
    global_source = replace_function(global_source, "shipment.ingest", GLOBAL_INGEST)
    global_source += "\n" + GLOBAL_LOOKUP + GLOBAL_SAFE_TESTS
    wrong_selftests = global_source + GLOBAL_WRONG_TEST

    return {
        "correct": correct,
        "always-append": always,
        "global-dedup": global_source,
        "global-dedup-wrong-selftests": wrong_selftests,
    }


def run_cli(cli: Path, project: Path, requests: list[dict[str, Any]], temp: Path) -> dict[str, Any]:
    env = dict(os.environ)
    env.update(TEMP=str(temp), TMP=str(temp), DOTNET_NOLOGO="1")
    command = ["dotnet", str(cli), "--project", str(project), "--jsonl"]
    payload = "".join(json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n"
                      for request in requests)
    process = subprocess.run(command, cwd=project, input=payload, capture_output=True,
                             text=True, encoding="utf-8", errors="replace", timeout=240,
                             env=env)
    responses: list[dict[str, Any]] = []
    parse_error = None
    for line_number, line in enumerate(process.stdout.splitlines(), 1):
        if not line.strip():
            continue
        try:
            response = json.loads(line)
        except json.JSONDecodeError as error:
            parse_error = f"stdout line {line_number}: {error}"
            break
        if not isinstance(response, dict):
            parse_error = f"stdout line {line_number} is not an object"
            break
        responses.append(response)
    return {
        "command": command,
        "exitCode": process.returncode,
        "responseCount": len(responses),
        "parseError": parse_error,
        "stdout": process.stdout,
        "stderr": process.stderr,
        "responses": responses,
    }


def requests_for(source: str, include_global: bool) -> list[dict[str, Any]]:
    requests: list[dict[str, Any]] = [
        {"op": "task.begin", "goal": "Prepare isolated study184 oracle calibration control"},
        {"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": source},
        {"op": "test-all"},
        {"op": "commit", "word": "shipment.scan-lookup-step", "library": True},
    ]
    for word in STORE_SETUP_WORDS:
        requests.append({"op": "commit", "word": word, "library": False})
    requests.append({"op": "commit", "word": "shipment.find-scan", "library": True})
    if include_global:
        for word in ("store.scan-lookup-step", "store.find-scan"):
            requests.append({"op": "commit", "word": word, "library": False})
    for word in INGEST_WORDS:
        requests.append({"op": "commit", "word": word, "library": False})
    requests.extend([{"op": "test-all"}, {"op": "task.commit"}])
    return requests


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[5])
    args = parser.parse_args()
    root = args.root.resolve()
    cli = args.cli.resolve()
    if not cli.is_file():
        parser.error(f"Fresh accepted-revision Flow CLI DLL is missing: {cli}")

    seeds = root / "experiments/AgentLang.SubagentTrials" / STUDY / "seeds"
    controls_source = seeds / "controls"
    common_path = seeds / "agentlang/common.flow"
    retained_path = seeds / "agentlang/retained-reference.flow"
    artifact_root = root / ".agentlang/efficacy-maintenance-184/seed-draft/controls"
    generated_root = controls_source / "generated"
    if artifact_root.exists() and any(artifact_root.iterdir()):
        raise FileExistsError(f"Refusing to overwrite control artifacts: {artifact_root}")
    generated_root.mkdir(parents=True, exist_ok=True)
    artifact_root.mkdir(parents=True, exist_ok=True)

    variants = source_variants(common_path, retained_path)
    manifest: dict[str, Any] = {
        "study": STUDY,
        "status": "draft calibration only; no participants dispatched",
        "cli": str(cli.relative_to(root)),
        "cliSha256": sha256(cli),
        "seedInputs": {
            str(common_path.relative_to(root)): sha256(common_path),
            str(retained_path.relative_to(root)): sha256(retained_path),
        },
        "controls": [],
    }

    for name, source in variants.items():
        source_path = generated_root / f"{name}.flow"
        source_path.write_text(source, encoding="utf-8", newline="\n")
        control_root = artifact_root / name
        project = control_root / "project"
        temp = control_root / "tmp"
        project.mkdir(parents=True)
        temp.mkdir(parents=True)
        include_global = name.startswith("global-dedup")
        request_list = requests_for(source, include_global)
        receipt = run_cli(cli, project, request_list, temp)
        (control_root / "setup-requests.jsonl").write_text(
            "".join(json.dumps(item, ensure_ascii=False, separators=(",", ":")) + "\n"
                    for item in request_list), encoding="utf-8")
        (control_root / "setup-stdout.txt").write_text(receipt["stdout"], encoding="utf-8")
        (control_root / "setup-stderr.txt").write_text(receipt["stderr"], encoding="utf-8")
        control = {
            "name": name,
            "source": str(source_path.relative_to(root)),
            "sourceSha256": sha256(source_path),
            "project": str(project.relative_to(root)),
            "projectInventory": {
                path.relative_to(project).as_posix(): sha256(path)
                for path in sorted(project.rglob("*")) if path.is_file()
            },
            "setup": {key: value for key, value in receipt.items() if key not in {"stdout", "stderr"}},
            "responses": receipt["responses"],
        }
        (control_root / "control-manifest.json").write_text(
            json.dumps(control, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        manifest["controls"].append(control)

    manifest_path = artifact_root / "control-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
                             encoding="utf-8")
    print(manifest_path)
    setup_failures = [
        control["name"] for control in manifest["controls"]
        if control["setup"]["exitCode"] != 0
        or control["setup"]["parseError"] is not None
        or control["setup"]["responseCount"] != len(requests_for("", control["name"].startswith("global-dedup")))
        or any(response.get("ok") is not True for response in control["responses"])
    ]
    return 1 if setup_failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"control preparation failed: {error}", file=sys.stderr)
        raise
