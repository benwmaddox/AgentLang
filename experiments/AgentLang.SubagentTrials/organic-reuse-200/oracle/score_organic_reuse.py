"""Independent AgentLang adapter for organic-reuse-200.

This scores a saved project copy. Hidden cases and model expectations remain in
the oracle directory, while all execution evidence is written below the
explicit --evidence-root under .agentlang/organic-reuse-200/.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from time import gmtime, strftime

import model as MODEL


REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
STUDY_ROOT = REPO / ".agentlang" / "organic-reuse-200"
SEED_INVENTORY_PATH = HERE / "seed-inventory.json"
OLD_ORACLE = REPO / "experiments" / "AgentLang.SubagentTrials" / "subscription-handoff-161" / "oracle"
DEFAULT_SEED_PROJECT = REPO / ".agentlang" / "subscription-handoff-161" / "seeds" / "agentlang-project"
CREATOR_ROOTS = ("subscription.renew-monthly", "subscription.renew-annual")
BATCH_ROOT = "subscription.renew-batch"
CREATOR_SIGNATURE = (("Store", "SubscriptionId", "SubscriptionId", "Instant", "Instant"), "Result<Store,BusinessError>")
BATCH_SIGNATURE = (("Store", "List<SubscriptionRenewal>", "Instant"), "Result<Store,BusinessError>")
TASK_SIGNATURES = {CREATOR_SIGNATURE, BATCH_SIGNATURE}
FROZEN_CASES_PATH = HERE / "cases.json"
INSPECTION_OPS = {
    "words", "describe", "source", "tests", "examples", "dependencies", "callers", "search",
    "search-dependency", "search-output", "search-type", "transitive-dependencies",
    "transitive-callers", "context", "graph",
}
INSPECTION_NAME_KEYS = {
    "name", "currentname", "wordname", "ownername", "calleename", "targetname",
    "word", "owner", "callee", "target",
}
INSPECTION_COLLECTION_KEYS = {
    "words", "dependencies", "callers", "callees", "results", "items", "matches",
    "targets", "calls",
}
FLOW_WORD_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_?!-]*(?:\.[A-Za-z_][A-Za-z0-9_?!-]*)*")
FLOW_DECLARATION = re.compile(r"^\s*(?:fn|word)\s+([^\s(:]+)", re.MULTILINE)
OBSERVATION_SOURCE = """// frontend: flow/2
record OrganicReuse200Observation {
    field input: Store
    field outcome: Result<Store, BusinessError>
}
"""


def import_from_path(module_name: str, path: Path):
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {module_name} from {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# Reuse report 163's typed Store constructors and structured-stack projection.
OLD = import_from_path("organic_reuse_200_handoff_adapter", OLD_ORACLE / "score_agentlang.py")
OLD.SUBSCRIPTION_IDS.update({
    "old2": "30000000-0000-0000-0000-000000000005",
    "new2": "30000000-0000-0000-0000-000000000006",
})


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_fingerprint(project: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted((p for p in project.rglob("*") if p.is_file()), key=lambda p: p.relative_to(project).as_posix()):
        relative = path.relative_to(project).as_posix().encode("utf-8")
        content = path.read_bytes()
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def current_manifest(project: Path) -> tuple[dict, dict, Path]:
    store = project / ".agentlang" / "store"
    current = json.loads((store / "CURRENT").read_text(encoding="utf-8-sig"))
    path = store / "manifests" / f"{current['manifestHash']}.json"
    return current, json.loads(path.read_text(encoding="utf-8-sig")), store


def current_revisions(manifest: dict) -> dict[str, dict]:
    rows = {}
    for word in manifest["words"]:
        revision = next(
            item for item in manifest["revisions"]
            if item["wordId"] == word["wordId"] and item["revision"] == word["currentRevision"]
        )
        rows[word["currentName"]] = {"word": word, "revision": revision}
    return rows


def projection_for_manifest(manifest: dict) -> dict:
    return {
        "types": {row["name"]: row["definition"]["hash"] for row in manifest["types"]},
        "words": {
            word["currentName"]: {
                "wordId": word["wordId"],
                "revision": word["currentRevision"],
                "definitionHash": revision["definition"]["hash"],
                "maturity": revision["maturity"],
                "tests": [row["hash"] for row in revision["tests"]],
                "examples": [row["hash"] for row in revision["examples"]],
            }
            for word in manifest["words"]
            for revision in manifest["revisions"]
            if revision["wordId"] == word["wordId"] and revision["revision"] == word["currentRevision"]
        },
    }


def preservation_diff(baseline: dict, candidate: dict) -> dict:
    candidate_words = candidate["words"]
    candidate_types = candidate["types"]
    changed_words = {
        name: {"expected": row, "actual": candidate_words.get(name)}
        for name, row in baseline["words"].items()
        if candidate_words.get(name) != row
    }
    changed_types = {
        name: {"expected": hash_value, "actual": candidate_types.get(name)}
        for name, hash_value in baseline["types"].items()
        if candidate_types.get(name) != hash_value
    }
    return {"passed": not changed_words and not changed_types,
            "changedInheritedWords": changed_words,
            "changedInheritedTypes": changed_types}


def validate_seed_inventory(seed_project: Path) -> tuple[dict, dict]:
    inventory = json.loads(SEED_INVENTORY_PATH.read_text(encoding="utf-8"))
    actual_files = {
        path.relative_to(seed_project).as_posix(): {
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        for path in seed_project.rglob("*")
        if path.is_file()
    }
    if actual_files != inventory["fileHashes"]:
        raise ValueError("The report-163 seed file inventory differs from the frozen inventory")
    current, manifest, _ = current_manifest(seed_project)
    projected = projection_for_manifest(manifest)
    if current["manifestHash"] != inventory["manifestHash"]:
        raise ValueError("The report-163 seed manifest hash no longer matches the frozen inventory")
    if projected["types"] != {row["name"]: row["definitionHash"] for row in inventory["types"]}:
        raise ValueError("The report-163 seed type inventory differs from the frozen inventory")
    return inventory, projected


def _normalize_flow_type(type_text: str) -> str:
    compact = re.sub(r"\s*([<>,])\s*", r"\1", type_text.strip())
    return re.sub(r"\s+", " ", compact)


def flow_definition_signature(source: str) -> tuple[tuple[str, ...], str] | None:
    lines = [line.strip() for line in source.splitlines()
             if line.strip() and not line.lstrip().startswith(("//", "#"))]
    if not lines or not re.match(r"(?:fn|word)\s+[^ (]+\s*\(", lines[0]):
        return None
    header_lines = []
    header = ""
    for line in lines:
        header_lines.append(line)
        header = " ".join(header_lines)
        arrow = header.find("->")
        if arrow < 0:
            continue
        body_start = header.find("{", arrow + 2)
        if body_start >= 0:
            header = header[:body_start].strip()
            break
    else:
        return None

    match = re.match(r"(?:fn|word)\s+[^ (]+\s*\((.*)\)\s*->\s*(.*?)\s*$", header, re.DOTALL)
    if not match:
        return None
    parameters = []
    depth = 0
    current = []
    fields = []
    for char in match.group(1):
        if char == "," and depth == 0:
            fields.append("".join(current).strip())
            current = []
            continue
        if char == "<":
            depth += 1
        elif char == ">":
            depth -= 1
        current.append(char)
    if current:
        fields.append("".join(current).strip())
    for field in fields:
        if not field:
            continue
        if ":" not in field:
            return None
        parameters.append(_normalize_flow_type(field.split(":", 1)[1]))
    return tuple(parameters), _normalize_flow_type(match.group(2))


def definition_source(store: Path, revision: dict) -> str:
    return (store / "objects" / f"{revision['definition']['hash']}.agent").read_text(encoding="utf-8-sig")


def production_graph(manifest: dict) -> dict[str, set[str]]:
    by_identity = {row["wordId"]: row["currentName"] for row in manifest["words"]}
    graph: dict[str, set[str]] = {name: set() for name in by_identity.values()}
    for word in manifest["words"]:
        revision = next(
            row for row in manifest["revisions"]
            if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"]
        )
        body_hash = revision["definition"]["hash"]
        for binding in revision.get("callBindings", []):
            source = binding.get("source", {})
            target = binding.get("target", {})
            if source.get("kind") != "word-definition" or source.get("hash") != body_hash:
                continue
            if target.get("kind") != "userWord":
                continue
            callee = by_identity.get(target.get("identity"))
            if callee:
                graph[word["currentName"]].add(callee)
    return graph


def path_to(graph: dict[str, set[str]], start: str, target: str) -> list[str] | None:
    stack = [(start, [start])]
    seen = set()
    while stack:
        node, path = stack.pop()
        if node == target:
            return path
        if node in seen:
            continue
        seen.add(node)
        for next_node in sorted(graph.get(node, ()), reverse=True):
            if next_node not in seen:
                stack.append((next_node, path + [next_node]))
    return None


def find_eligible_helpers(project: Path, manifest: dict, seed_inventory: dict) -> dict:
    _, _, store = current_manifest(project)
    current = current_revisions(manifest)
    seed_names = {row["name"] for row in seed_inventory["words"]}
    seed_ids = {row["wordId"] for row in seed_inventory["words"]}
    excluded_names = set(CREATOR_ROOTS) | {BATCH_ROOT}
    graph = production_graph(manifest)
    eligible = []
    for name, entry in sorted(current.items()):
        revision = entry["revision"]
        if (name in seed_names or entry["word"]["wordId"] in seed_ids
                or name in excluded_names or revision["maturity"] != "library"):
            continue
        source = definition_source(store, revision)
        signature = flow_definition_signature(source)
        if not signature or signature in TASK_SIGNATURES:
            continue
        paths = {root: path_to(graph, root, name) for root in CREATOR_ROOTS}
        if all(paths.values()):
            eligible.append({
                "name": name,
                "wordId": entry["word"]["wordId"],
                "revision": entry["word"]["currentRevision"],
                "definitionHash": revision["definition"]["hash"],
                "signature": {"inputs": list(signature[0]), "output": signature[1]},
                "maturity": revision["maturity"],
                "ownTestHashes": [row["hash"] for row in revision["tests"]],
                "productionPaths": paths,
            })
    return {
        "rule": "new authored function absent from seed and task, published as library, and reachable from both fixed-term roots through definition call bindings only",
        "eligibleHelpers": eligible,
        "creationOutcome": "eligible-helper-created" if eligible else "no-eligible-helper",
        "successorDispatchAllowedByProvenance": bool(eligible),
    }


def _add_inspection_name(value, names: set[str]) -> None:
    if isinstance(value, str) and FLOW_WORD_NAME.fullmatch(value):
        names.add(value)


def _returned_inspection_names(value, names: set[str], *, collection_item: bool = False) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            normalized = str(key).casefold()
            if normalized in INSPECTION_NAME_KEYS:
                _add_inspection_name(child, names)
            elif normalized == "source" and isinstance(child, str):
                names.update(match.group(1) for match in FLOW_DECLARATION.finditer(child))
            else:
                _returned_inspection_names(
                    child, names, collection_item=normalized in INSPECTION_COLLECTION_KEYS
                )
    elif isinstance(value, list):
        for child in value:
            if collection_item and isinstance(child, str):
                _add_inspection_name(child, names)
            else:
                _returned_inspection_names(child, names, collection_item=collection_item)
    elif collection_item:
        _add_inspection_name(value, names)


def inspection_response_word_names(operation: str, response_json: dict) -> set[str]:
    """Extract exact names returned as inspection metadata, never text substrings."""
    if operation not in INSPECTION_OPS or response_json.get("ok") is not True:
        return set()
    names: set[str] = set()
    data = response_json.get("data")
    _returned_inspection_names(data, names, collection_item=operation in {
        "words", "dependencies", "callers", "search", "search-dependency",
        "search-output", "search-type", "transitive-dependencies",
        "transitive-callers", "context", "graph",
    })
    if operation == "source":
        source_text = data if isinstance(data, str) else None
        if isinstance(data, dict) and isinstance(data.get("source"), str):
            source_text = data["source"]
        if source_text:
            names.update(match.group(1) for match in FLOW_DECLARATION.finditer(source_text))
    return names


def inspection_request_word_names(operation: str, request_json: dict) -> set[str]:
    """Return exact word selectors for successful case-metadata inspections.

    The `tests` and `examples` protocol responses contain case names, not the
    owner word. Preserve those responses as inspections without treating a
    test/example case name as a discovered word name.
    """
    if operation not in {"tests", "examples"}:
        return set()
    name = request_json.get("word")
    return {name} if isinstance(name, str) and FLOW_WORD_NAME.fullmatch(name) else set()


def trace_discovery(trace_path: Path | None, eligible_names: set[str]) -> dict:
    if trace_path is None:
        return {"status": "trace-not-provided", "discoveredHelpers": [],
                "metadataInspectedHelpers": [], "inspectionHits": []}
    if not trace_path.is_file():
        return {"status": "trace-missing", "path": str(trace_path), "discoveredHelpers": [],
                "metadataInspectedHelpers": [], "inspectionHits": []}
    hits = []
    for line_no, raw in enumerate(trace_path.read_text(encoding="utf-8-sig").splitlines(), 1):
        try:
            event = json.loads(raw)
        except json.JSONDecodeError:
            continue
        if event.get("event") != "exchange":
            continue
        operation = event.get("operation")
        if operation not in INSPECTION_OPS:
            continue
        request = event.get("request", {})
        response = event.get("response", {})
        request_text = request.get("canonical") or request.get("rawLine") or ""
        response_text = response.get("canonical") or response.get("rawLine") or ""
        observed = event.get("observedRuntimeResponse") or {}
        response_text += observed.get("canonical", "") + observed.get("rawLine", "")
        response_json = None
        for candidate in (response.get("canonical"), response.get("rawLine"),
                          observed.get("canonical"), observed.get("rawLine")):
            if candidate:
                try:
                    response_json = json.loads(candidate)
                    break
                except json.JSONDecodeError:
                    continue
        if not response_json or response_json.get("ok") is not True:
            continue
        returned_names = inspection_response_word_names(operation, response_json)
        request_json = None
        for candidate in (request.get("canonical"), request.get("rawLine")):
            if candidate:
                try:
                    request_json = json.loads(candidate)
                    break
                except json.JSONDecodeError:
                    continue
        requested_names = inspection_request_word_names(operation, request_json or {})
        returned_eligible = sorted(eligible_names & returned_names)
        requested_eligible = sorted(eligible_names & requested_names)
        if returned_eligible or requested_eligible:
            hits.append({"line": line_no, "exchange": event.get("index"), "operation": operation,
                         "requestSha256": hashlib.sha256(request_text.encode("utf-8")).hexdigest(),
                         "responseSha256": hashlib.sha256(response_text.encode("utf-8")).hexdigest(),
                         "helperNamesReturned": returned_eligible,
                         "helperNamesRequestedForMetadata": requested_eligible})
    found = sorted({name for hit in hits for name in hit["helperNamesReturned"]})
    inspected = sorted({name for hit in hits for name in hit["helperNamesRequestedForMetadata"]})
    return {"status": "scored", "path": str(trace_path.resolve()), "sha256": sha256_file(trace_path),
            "discoveredHelpers": found, "inspectionHits": hits,
            "metadataInspectedHelpers": inspected,
            "discoveredAnyEligibleHelper": bool(found)}


def run_cli(cli: Path, project: Path, requests: list[dict]) -> tuple[int, str, str, list[dict]]:
    process = subprocess.run(
        ["dotnet", str(cli), "--project", str(project), "--filesystem", "virtual", "--jsonl"],
        cwd=REPO,
        input="\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=900,
    )
    responses = []
    for line_number, line in enumerate(process.stdout.splitlines(), 1):
        try:
            responses.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise RuntimeError(f"CLI stdout line {line_number} is not JSON: {line[:500]!r}") from error
    return process.returncode, process.stdout, process.stderr, responses


def create_run_directory(evidence_root: Path, stage: str) -> Path:
    timestamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    parent = evidence_root / stage
    parent.mkdir(parents=True, exist_ok=True)
    index = 1
    while True:
        candidate = parent / f"{timestamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def observation_expression(item: dict, stage: str) -> str:
    store = OLD.store_expr(item["store"])
    if stage == "creator":
        request = item["request"]
        function = item["operation"]
        call = (
            f"{function}({store}, "
            f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['oldId']))}), "
            f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['newId']))}), "
            f"Instant.new({OLD.flow_string(OLD.iso_day(request['at']))}), "
            f"Instant.new({OLD.flow_string(OLD.iso_day(request['expiry']))}))"
        )
    else:
        request = item["request"]
        changes = []
        for change in request["changes"]:
            changes.append(
                "subscriptionRenewal.new("
                f"old-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, change['oldId']))}), "
                f"new-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, change['newId']))}), "
                f"plan = SubscriptionPlan.{change['plan']}(), "
                f"expires-at = Instant.new({OLD.flow_string(OLD.iso_day(change['expiry']))}))"
            )
        change_list = OLD.list_expr("SubscriptionRenewal", changes)
        call = (
            f"{BATCH_ROOT}({store}, {change_list}, "
            f"Instant.new({OLD.flow_string(OLD.iso_day(request['at']))}))"
        )
    return f"organicReuse200Observation.new(input = {store}, outcome = {call})"


def expected_projection(item: dict) -> dict:
    expected = item["expected"]
    input_store = OLD.normalized_projection(item)
    if expected["tag"] == "ok":
        return {"tag": "ok", "store": OLD.normalized_projection({"store": expected["store"]}), "input": input_store}
    return {"tag": "error", "code": expected["code"], "input": input_store}


def score_eval(item: dict, response: dict) -> dict:
    result = {"caseId": item["id"], "executed": False, "passed": False}
    if response.get("ok") and response.get("kind") == "eval":
        structured = response.get("data", {}).get("structuredStack")
        if structured and len(structured.get("values", [])) == 1:
            actual = OLD.result_projection(OLD.unwrap_structured(structured["values"][0]))
            expected = expected_projection(item)
            result.update(executed=True, passed=(actual == expected), actualTag=actual.get("tag"), expectedTag=expected.get("tag"))
            if actual != expected:
                result.update(expected=expected, actual=actual)
        else:
            result["setupError"] = "eval response omitted one structured observation value"
    else:
        result["setupError"] = response.get("error", response.get("text", "missing eval response"))
    return result


def is_successful_define_response(response: dict) -> bool:
    return response.get("ok") is True and response.get("kind") == "defined"


def response_passed(response: dict, kind: str) -> bool:
    if not response.get("ok") or response.get("kind") != kind:
        return False
    text = response.get("text", "")
    counts = re.search(r"(\d+)\s*/\s*(\d+)", text)
    return counts is not None and counts.group(1) == counts.group(2)


def validate_evidence_root(path: Path) -> Path:
    root = STUDY_ROOT.resolve()
    resolved = path if path.is_absolute() else REPO / path
    resolved = resolved.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"--evidence-root must be below {root}")
    return resolved


def load_eligible_set(path: Path) -> dict:
    payload = json.loads(path.read_text(encoding="utf-8"))
    return payload.get("helperEligibility", payload)


def load_frozen_cases(stage: str) -> list[dict]:
    exported = json.loads(FROZEN_CASES_PATH.read_text(encoding="utf-8"))
    generated = MODEL.make_cases()
    if exported != generated:
        raise ValueError("cases.json differs from the model-generated audit export")
    if stage not in exported or not isinstance(exported[stage], list):
        raise ValueError(f"cases.json has no frozen {stage!r} case list")
    return copy.deepcopy(exported[stage])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=("creator", "successor"), required=True)
    parser.add_argument("--cli", type=Path, required=True, help="Pinned current Release AgentLang.Cli.dll")
    parser.add_argument("--actor-project", type=Path, required=True, help="Saved participant project to score")
    parser.add_argument("--evidence-root", type=Path, required=True, help="Evidence directory below .agentlang/organic-reuse-200/")
    parser.add_argument("--seed-project", type=Path, default=DEFAULT_SEED_PROJECT)
    parser.add_argument("--creator-project", type=Path, help="Accepted creator snapshot, required for successor scoring")
    parser.add_argument("--eligible-helpers", type=Path, help="Creator score's helper-eligibility.json, required for successor scoring")
    parser.add_argument("--trace", type=Path, help="Assigned broker trace; used only to score successor discovery")
    args = parser.parse_args()

    evidence_root = validate_evidence_root(args.evidence_root)
    cli = args.cli if args.cli.is_absolute() else REPO / args.cli
    actor_project = args.actor_project if args.actor_project.is_absolute() else REPO / args.actor_project
    seed_project = args.seed_project if args.seed_project.is_absolute() else REPO / args.seed_project
    if not cli.is_file() or not actor_project.is_dir() or not seed_project.is_dir():
        raise FileNotFoundError("The selected CLI, actor project, and seed project must exist")
    if args.stage == "successor" and (args.creator_project is None or args.eligible_helpers is None):
        parser.error("successor scoring requires --creator-project and --eligible-helpers")

    seed_inventory, seed_projection = validate_seed_inventory(seed_project)
    actor_fingerprint = project_fingerprint(actor_project)
    _, actor_manifest, _ = current_manifest(actor_project)
    actor_projection = projection_for_manifest(actor_manifest)
    preservation_from_seed = preservation_diff(seed_projection, actor_projection)

    accepted_creator_projection = None
    helper_set = {"eligibleHelpers": []}
    helper_source_path = None
    creator_fingerprint = None
    if args.stage == "successor":
        creator_project = args.creator_project if args.creator_project.is_absolute() else REPO / args.creator_project
        helper_source_path = args.eligible_helpers if args.eligible_helpers.is_absolute() else REPO / args.eligible_helpers
        if not creator_project.is_dir() or not helper_source_path.is_file():
            raise FileNotFoundError("The accepted creator snapshot and its helper eligibility record must exist")
        creator_fingerprint = project_fingerprint(creator_project)
        helper_set = load_eligible_set(helper_source_path)
        if not helper_set.get("eligibleHelpers"):
            raise ValueError("The accepted creator has no eligible helper; successor dispatch/scoring is not permitted")
        if helper_set.get("acceptedProjectFingerprint") != creator_fingerprint:
            raise ValueError("The creator snapshot does not match the hash bound in helper eligibility")
        _, creator_manifest, _ = current_manifest(creator_project)
        accepted_creator_projection = projection_for_manifest(creator_manifest)

    inherited_creator_preservation = (
        preservation_diff(accepted_creator_projection, actor_projection)
        if accepted_creator_projection is not None else None
    )

    cases = load_frozen_cases(args.stage)

    run_dir = create_run_directory(evidence_root, args.stage)
    scoring_project = run_dir / "project-copy"
    shutil.copytree(actor_project, scoring_project)

    _, scoring_manifest, _ = current_manifest(scoring_project)
    example_owners = sorted({
        word["currentName"] for word in scoring_manifest["words"]
        if next(row for row in scoring_manifest["revisions"]
                if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"])["examples"]
    })
    requests = [{"op": "test-all"}]
    requests.extend({"op": "example", "word": owner} for owner in example_owners)
    requests.append({"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": OBSERVATION_SOURCE})
    requests.extend({"op": "eval", "frontend": "flow", "syntaxVersion": 2, "structured": True,
                     "code": observation_expression(item, args.stage)} for item in cases)
    exit_code, stdout, stderr, responses = run_cli(cli.resolve(), scoring_project, requests)
    (run_dir / "requests.jsonl").write_text("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n", encoding="utf-8")
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    test_response = responses[0] if responses else {}
    example_start = 1
    example_responses = responses[example_start:example_start + len(example_owners)]
    define_index = example_start + len(example_owners)
    define_response = responses[define_index] if len(responses) > define_index else {}
    eval_responses = responses[define_index + 1:]
    case_results = [score_eval(item, response) for item, response in zip(cases, eval_responses)]
    case_failures = [row["caseId"] for row in case_results if row["executed"] and not row["passed"]]
    case_setup_errors = [row for row in case_results if not row["executed"]]
    test_pass = response_passed(test_response, "test")
    example_results = [
        {"owner": owner, "passed": response_passed(response, "example"), "response": response}
        for owner, response in zip(example_owners, example_responses)
    ]
    examples_pass = len(example_results) == len(example_owners) and all(row["passed"] for row in example_results)
    observer_defined = is_successful_define_response(define_response)
    behavior_pass = len(case_results) == len(cases) and not case_setup_errors and not case_failures

    current = current_revisions(actor_manifest)
    required_roots = CREATOR_ROOTS if args.stage == "creator" else (BATCH_ROOT,)
    roots_library = {
        name: name in current and current[name]["revision"]["maturity"] == "library"
        for name in required_roots
    }
    roots_with_tests = {
        name: bool(current.get(name, {}).get("revision", {}).get("tests"))
        for name in required_roots
    }

    helper_eligibility = None
    helper_reuse = None
    discovery = None
    if args.stage == "creator":
        helper_eligibility_record = find_eligible_helpers(
            actor_project, actor_manifest, seed_inventory
        )
        helper_eligibility = helper_eligibility_record
        helper_eligibility_record["acceptedProjectFingerprint"] = actor_fingerprint
        helper_eligibility_record["seedManifestHash"] = seed_inventory["manifestHash"]
        (run_dir / "helper-eligibility.json").write_text(json.dumps(helper_eligibility_record, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    else:
        eligible_names = {row["name"] for row in helper_set["eligibleHelpers"]}
        graph = production_graph(actor_manifest)
        reused = []
        paths = {}
        for name in sorted(eligible_names):
            found_path = path_to(graph, BATCH_ROOT, name)
            if found_path:
                reused.append(name)
                paths[name] = found_path
        helper_reuse = {"eligibleHelpers": sorted(eligible_names), "reusedHelpers": reused,
                        "productionPaths": paths, "reusedAny": bool(reused)}
        trace_path = args.trace if args.trace is None or args.trace.is_absolute() else REPO / args.trace
        discovery = trace_discovery(trace_path, eligible_names)

    actor_still_unchanged = project_fingerprint(actor_project) == actor_fingerprint
    scored_status = "passed" if (
        exit_code == 0 and test_pass and examples_pass and observer_defined and behavior_pass
        and all(roots_library.values()) and all(roots_with_tests.values())
        and preservation_from_seed["passed"] and actor_still_unchanged
        and (inherited_creator_preservation is None or inherited_creator_preservation["passed"])
    ) else "score-failure"
    creator_ready_for_successor = (
        args.stage == "creator" and scored_status == "passed"
        and bool(helper_eligibility and helper_eligibility["eligibleHelpers"])
    )
    report = {
        "status": scored_status,
        "stage": args.stage,
        "runtime": str(cli.resolve()),
        "runtimeFiles": {
            path.name: sha256_file(path)
            for path in [cli.resolve(), cli.resolve().with_suffix(".deps.json"), cli.resolve().with_suffix(".runtimeconfig.json")]
            if path.is_file()
        },
        "runtimeSourceSha256": sha256_file(Path(__file__).resolve()),
        "runtimeInvocation": {"filesystemMode": "virtual", "capabilities": [], "testCapabilities": []},
        "modelSourceSha256": sha256_file(HERE / "model.py"),
        "cellModelSourceSha256": sha256_file(HERE / "cell_model_163.py"),
        "caseSourceSha256": sha256_file(HERE / "cases.json"),
        "caseSourceKind": "frozen audit export validated against model.make_cases() and used for this score",
        "report163ModelSha256": sha256_file(OLD_ORACLE / "model.py"),
        "report163AdapterSha256": sha256_file(OLD_ORACLE / "score_agentlang.py"),
        "maintenance166AdapterReferenceSha256": sha256_file(
            REPO / "experiments" / "AgentLang.SubagentTrials" / "subscription-handoff-maintenance-166" / "oracle" / "score_agentlang.py"
        ),
        "seedManifestHash": seed_inventory["manifestHash"],
        "actorProject": str(actor_project.resolve()),
        "actorProjectFingerprint": actor_fingerprint,
        "actorProjectUnchangedDuringScore": actor_still_unchanged,
        "scoringProject": str(scoring_project.resolve()),
        "evidenceDirectory": str(run_dir.resolve()),
        "cliExitCode": exit_code,
        "responseCount": len(responses),
        "expectedResponseCount": len(requests),
        "suite": {"testAllPassed": test_pass, "testAllResponse": test_response,
                  "exampleOwnerCount": len(example_owners), "examplesPassed": examples_pass,
                  "exampleResults": example_results},
        "observationSetupPassed": observer_defined,
        "behavior": {"caseCount": len(cases), "executedCaseCount": len(case_results) - len(case_setup_errors),
                     "passedCaseCount": len(case_results) - len(case_setup_errors) - len(case_failures),
                     "failureIds": case_failures, "setupErrors": case_setup_errors,
                     "caseResults": case_results},
        "libraryQualification": {"rootMaturity": roots_library, "rootHasOwnTests": roots_with_tests},
        "preservation": {"fromReport163Seed": preservation_from_seed,
                         "fromAcceptedCreator": inherited_creator_preservation},
        "helperEligibility": helper_eligibility,
        "creatorReadyForSuccessor": creator_ready_for_successor,
        "helperReuse": helper_reuse,
        "discovery": discovery,
        "successorDispatchRule": "Do not dispatch unless creator behavior, preservation, library gates, and a nonempty eligible-helper set are all accepted.",
        "preflightLimit": "This score is meaningful only after the exact current pinned Release CLI and frozen controls pass their separate preflight.",
    }
    (run_dir / "score.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key not in {"behavior", "suite"}}, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
