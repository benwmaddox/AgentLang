#!/usr/bin/env python3
"""Independent AgentLang scorer for the monthly replacement trial.

The submitted project is copied to a disposable directory and exercised by the
selected current AgentLang CLI over its JSONL interface. The scheduled cases
and expected results come from this oracle's model; 66 inherited creator cases
are scored through the earlier organic-reuse behavior adapter.
"""

from __future__ import annotations

import argparse
from datetime import timedelta
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from time import gmtime, strftime


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
STUDY_ROOT = REPO / ".agentlang" / "replacement-trial-230"
SETUP_PATH = STUDY_ROOT / "setup.json"
DEFAULT_SEED = STUDY_ROOT / "baselines" / "retained"
DEFAULT_EVIDENCE = STUDY_ROOT / "oracle-work" / "l"
SCHEDULED_MODEL_PATH = HERE / "model.py"
SCHEDULED_CASES_PATH = HERE / "cases.json"
TYPE_NAME = "RenewalTiming"
ALLOWED_REPLACEMENTS = (
    "subscription.renew-monthly",
    "subscription.renew-batch-step",
    "subscription.renew-scheduled-batch-step",
)
STORE_ARRAYS = {"customers", "products", "subscriptions", "invoices", "payments"}
EXCLUDED_SOURCE_DIRS = {".git", ".vs", "bin", "obj", "cache", "caches", "__pycache__"}
ERROR_MESSAGES = {
    "SUBSCRIPTION_NOT_FOUND": "The subscription does not exist.",
    "SUBSCRIPTION_ALREADY_CANCELLED": "The subscription is already cancelled.",
    "CANCEL_BEFORE_START": "A subscription cannot be cancelled before it starts.",
    "DUPLICATE_SUBSCRIPTION": "A subscription with this identifier already exists.",
    "INVALID_SUBSCRIPTION_TERM": "Subscription term must contain non-whitespace text.",
    "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START": "Subscription expiry must be later than its start.",
    "CUSTOMER_NOT_FOUND": "The customer does not exist.",
    "PRODUCT_NOT_FOUND": "The product does not exist.",
    "SUBSCRIPTION_OVERLAP": "This customer already has an overlapping subscription for the product.",
}
OBSERVATION_SOURCE = """// frontend: flow/2
record ReplacementTrial230Observation {
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


def load_scheduled_model():
    model = import_from_path("scheduled_batch_226_language_model", SCHEDULED_MODEL_PATH)
    cases = json.loads(SCHEDULED_CASES_PATH.read_text(encoding="utf-8"))
    if cases != model.make_cases():
        raise ValueError("cases.json differs from the generated scheduled-batch model")
    return model, cases


def load_organic_adapter():
    """Load the 210 fixture and adapter without importing its generic `model` globally."""
    oracle_dir = REPO / "experiments" / "AgentLang.SubagentTrials" / "organic-reuse-210" / "oracle"
    missing = object()
    saved_modules = {name: sys.modules.get(name, missing) for name in ("model", "cell_model_163")}
    for name in saved_modules:
        sys.modules.pop(name, None)
    sys.path.insert(0, str(oracle_dir))
    try:
        adapter = import_from_path("scheduled_batch_226_organic_reuse_adapter", oracle_dir / "score_organic_reuse.py")
    finally:
        sys.path.remove(str(oracle_dir))
        for name, module in saved_modules.items():
            if module is missing:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = module

    # 220 uses the canonical IDs below; reuse the old model/renderer while
    # keeping fixtures and projections internally consistent with this seed.
    adapter.OLD.SUBSCRIPTION_IDS.update({
        "old2": "30000000-0000-0000-0000-000000000006",
        "new2": "30000000-0000-0000-0000-000000000005",
    })
    return adapter


SCORER210 = load_organic_adapter()
OLD = SCORER210.OLD
sys.path.insert(0, str(HERE))
import type_gate  # noqa: E402
sys.path.remove(str(HERE))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def project_fingerprint(project: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(project.rglob("*"), key=lambda item: item.relative_to(project).as_posix()):
        if not path.is_file():
            continue
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
    manifest_path = store / "manifests" / f"{current['manifestHash']}.json"
    return current, json.loads(manifest_path.read_text(encoding="utf-8-sig")), store


def current_revisions(manifest: dict) -> dict[str, dict]:
    result = {}
    for word in manifest["words"]:
        revision = next(row for row in manifest["revisions"]
                        if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"])
        result[word["currentName"]] = {"word": word, "revision": revision}
    return result


def projection_for_manifest(manifest: dict) -> dict:
    return {
        # Keep the complete manifest rows: the source-format, validator,
        # declaration, status, test/example references, fixtures, call bindings,
        # and revision metadata all participate in the inherited freeze.
        "types": {row["name"]: row for row in manifest["types"]},
        "words": {row["wordId"]: row for row in manifest["words"]},
        "revisions": {
            word_id: sorted(
                (row for row in manifest["revisions"] if row["wordId"] == word_id),
                key=lambda row: row["revision"],
            )
            for word_id in {row["wordId"] for row in manifest["words"]}
        },
    }


def referenced_assets(projection: dict) -> set[str]:
    hashes = {
        row["definition"]["hash"] for row in projection["types"].values()
    }
    for revisions in projection["revisions"].values():
        for revision in revisions:
            hashes.add(revision["definition"]["hash"])
            hashes.update(row["hash"] for row in revision["tests"])
            hashes.update(row["hash"] for row in revision["examples"])
    return hashes


def asset_hashes(project: Path, projection: dict) -> dict[str, str | None]:
    result = {}
    for object_hash in sorted(referenced_assets(projection)):
        path = project / ".agentlang" / "store" / "objects" / f"{object_hash}.agent"
        result[object_hash] = sha256_file(path) if path.is_file() else None
    return result


def preservation_diff(seed: dict, candidate: dict, *, seed_project: Path | None = None,
                      candidate_project: Path | None = None) -> dict:
    inherited_word_ids = set(seed["words"])
    changed_words = {
        name: {"expected": value, "actual": candidate["words"].get(name)}
        for name, value in seed["words"].items()
        if candidate["words"].get(name) != value
    }
    changed_types = {
        name: {"expected": value, "actual": candidate["types"].get(name)}
        for name, value in seed["types"].items()
        if candidate["types"].get(name) != value
    }
    changed_revisions = {
        word_id: {"expected": rows, "actual": candidate["revisions"].get(word_id)}
        for word_id, rows in seed["revisions"].items()
        if candidate["revisions"].get(word_id) != rows
    }
    missing_inherited_revisions = sorted(inherited_word_ids - set(candidate["revisions"]))
    inherited_assets = sorted(referenced_assets(seed))
    asset_diff = None
    if seed_project is not None and candidate_project is not None:
        expected_assets = asset_hashes(seed_project, seed)
        actual_assets = asset_hashes(candidate_project, seed)
        asset_diff = {
            "verifiedCount": sum(value == object_hash for object_hash, value in actual_assets.items()),
            "expectedCount": len(inherited_assets),
            "changedOrMissing": {
                object_hash: {"expectedSha256": expected_assets.get(object_hash),
                              "actualSha256": actual_assets.get(object_hash)}
                for object_hash in inherited_assets
                if expected_assets.get(object_hash) != object_hash or actual_assets.get(object_hash) != object_hash
            },
        }
    assets_passed = asset_diff is None or not asset_diff["changedOrMissing"]
    return {
        "passed": not changed_words and not changed_types and not changed_revisions
                   and not missing_inherited_revisions and assets_passed,
        "changedInheritedWords": changed_words,
        "changedInheritedTypes": changed_types,
        "changedOrAddedRevisionsForInheritedWords": changed_revisions,
        "missingInheritedRevisionGroups": missing_inherited_revisions,
        "referencedInheritedObjects": inherited_assets,
        "inheritedObjectIntegrity": asset_diff,
        "derivedCallerIndexExcluded": False,
    }


def prepared_tree_fingerprint(project: Path) -> tuple[str, list[dict]]:
    rows = []
    for path in sorted(project.rglob("*"), key=lambda item: item.relative_to(project).as_posix()):
        relative = path.relative_to(project)
        if any(part.casefold() in EXCLUDED_SOURCE_DIRS for part in relative.parts):
            continue
        if path.is_symlink():
            raise RuntimeError(f"Refusing a symlink in the pinned source tree: {path}")
        if path.is_file():
            data = path.read_bytes()
            rows.append({"path": relative.as_posix(), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    encoded = json.dumps(rows, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest(), rows


def setup_baseline_pin(arm: str) -> dict:
    setup = json.loads(SETUP_PATH.read_text(encoding="utf-8"))
    pin = setup["baselines"][arm]
    if not isinstance(pin.get("treeSha256"), str) or not isinstance(pin.get("fileCount"), int):
        raise ValueError(f"setup.json does not contain a complete {arm} baseline pin")
    return pin


def setup_runtime_pin(runtime_name: str) -> dict:
    setup = json.loads(SETUP_PATH.read_text(encoding="utf-8"))
    pin = setup["privateRuntimes"][runtime_name]
    if not isinstance(pin.get("treeSha256"), str) or not isinstance(pin.get("files"), list):
        raise ValueError(f"setup.json does not contain a complete {runtime_name} runtime pin")
    return pin


def verify_runtime_pin(cli: Path) -> dict:
    pin = setup_runtime_pin("agentlang")
    runtime_root = (REPO / pin["path"]).resolve()
    expected_cli = (runtime_root / pin["cli"]).resolve()
    rows = []
    if runtime_root.is_dir():
        for path in sorted(runtime_root.rglob("*"), key=lambda item: item.relative_to(runtime_root).as_posix()):
            if path.is_symlink():
                return {"passed": False, "reason": "runtime contains a symlink", "path": str(path)}
            if path.is_file():
                data = path.read_bytes()
                rows.append({
                    "path": path.relative_to(runtime_root).as_posix(),
                    "bytes": len(data),
                    "sha256": hashlib.sha256(data).hexdigest(),
                })
    encoded = json.dumps(rows, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    actual_tree = hashlib.sha256(encoded).hexdigest()
    actual_cli_hash = sha256_file(cli) if cli.is_file() else None
    expected_files = sorted(pin["files"], key=lambda row: row["path"])
    actual_files = sorted(rows, key=lambda row: row["path"])
    return {
        "passed": (
            cli.resolve() == expected_cli
            and actual_tree == pin["treeSha256"]
            and len(rows) == pin["fileCount"]
            and actual_files == expected_files
        ),
        "runtimeRoot": str(runtime_root),
        "expectedCli": str(expected_cli),
        "actualCli": str(cli.resolve()),
        "cliSha256": actual_cli_hash,
        "treeSha256": actual_tree,
        "expectedTreeSha256": pin["treeSha256"],
        "fileCount": len(rows),
        "expectedFileCount": pin["fileCount"],
    }


def _revision_rows(manifest: dict, word_id: str) -> list[dict]:
    return sorted((row for row in manifest["revisions"] if row["wordId"] == word_id),
                  key=lambda row: row["revision"])


def _word_current(manifest: dict, name: str) -> tuple[dict, dict] | None:
    word = next((row for row in manifest["words"] if row["currentName"] == name), None)
    if word is None:
        return None
    revision = next((row for row in manifest["revisions"]
                     if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"]), None)
    return (word, revision) if revision is not None else None


def _object_text(project: Path, ref: dict) -> str:
    path = project / ".agentlang" / "store" / "objects" / f"{ref['hash']}.agent"
    return path.read_text(encoding="utf-8-sig")


def _split_top_level_arguments(text: str) -> list[str]:
    arguments = []
    start = 0
    depth = 0
    in_string = False
    escaped = False
    for index, char in enumerate(text):
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth < 0:
                raise ValueError("unbalanced call source")
        elif char == "," and depth == 0:
            arguments.append(text[start:index].strip())
            start = index + 1
    if depth != 0 or in_string:
        raise ValueError("unbalanced call source")
    if text[start:].strip():
        arguments.append(text[start:].strip())
    return arguments


def _call_arguments(source: str, opening: int) -> tuple[list[str], int]:
    depth = 1
    in_string = False
    escaped = False
    start = opening + 1
    separators = []
    for index in range(start, len(source)):
        char = source[index]
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                body = source[start:index]
                return _split_top_level_arguments(body), index + 1
        elif char == "," and depth == 1:
            separators.append(index)
    raise ValueError("unterminated subscription.renew-monthly call")


def _timing_values(expression: str) -> tuple[str, str] | None:
    match = re.match(r"(?is)^\s*(?:renewalTiming|RenewalTiming)\.new\s*\(", expression)
    if not match:
        return None
    arguments, end = _call_arguments(expression, match.end() - 1)
    if expression[end:].strip():
        return None
    return _timing_values_from_arguments(arguments)


def _timing_values_from_arguments(arguments: list[str]) -> tuple[str, str] | None:
    fields = {}
    for argument in arguments:
        field_match = re.match(r"(?is)^(at|expires-at)\s*=\s*(.+)$", argument)
        if not field_match:
            return None
        fields[field_match.group(1).casefold()] = field_match.group(2).strip()
    if set(fields) != {"at", "expires-at"}:
        return None
    return fields["at"], fields["expires-at"]


def _monthly_timing_bindings(text: str) -> dict[str, dict]:
    pattern = re.compile(
        r"(?im)^[ \t]*let[ \t]+([A-Za-z_][A-Za-z0-9_-]*)[ \t]*=[ \t]*"
        r"(?:renewalTiming|RenewalTiming)\.new[ \t]*\("
    )
    bindings = {}
    for match in pattern.finditer(text):
        name = match.group(1)
        arguments, end = _call_arguments(text, match.end() - 1)
        line_end = text.find("\n", end)
        if line_end < 0:
            line_end = len(text)
        if text[end:line_end].strip():
            continue
        values = _timing_values_from_arguments(arguments)
        if values is None:
            continue
        if name in bindings:
            raise ValueError(f"duplicate grouped timing binding {name}")
        bindings[name] = {
            "values": values,
            "start": match.start(),
            "end": min(line_end + 1, len(text)),
        }
    return bindings


def _find_unquoted(source: str, needle: str, start: int) -> int:
    in_string = False
    escaped = False
    index = start
    while index < len(source):
        char = source[index]
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
        elif char == '"':
            in_string = True
        elif source.startswith(needle, index):
            return index
        index += 1
    return -1


def _collapse_whitespace_outside_strings(source: str) -> str:
    output = []
    in_string = False
    escaped = False
    pending_space = False
    for char in source:
        if in_string:
            output.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
        elif char == '"':
            if pending_space and output:
                output.append(" ")
            pending_space = False
            output.append(char)
            in_string = True
        elif char.isspace():
            pending_space = True
        else:
            if pending_space and output:
                output.append(" ")
            pending_space = False
            output.append(char)
    return "".join(output).strip()


def normalize_monthly_test_source(source: str) -> tuple[str, int]:
    """Normalize only the five-to-four input-shape migration in a test body."""
    text = source.lstrip("\ufeff")
    let_bindings = _monthly_timing_bindings(text)

    needle = "subscription.renew-monthly("
    replacements = []
    cursor = 0
    removed_bindings: set[str] = set()
    while True:
        start = _find_unquoted(text, needle, cursor)
        if start < 0:
            break
        opening = start + len(needle) - 1
        arguments, end = _call_arguments(text, opening)
        if len(arguments) == 5:
            store, old_id, new_id, at, expires = arguments
        elif len(arguments) == 4:
            store, old_id, new_id, timing_expression = arguments
            values = _timing_values(timing_expression)
            if values is None:
                binding = re.fullmatch(r"[A-Za-z_][A-Za-z0-9_-]*", timing_expression.strip())
                if binding is None or binding.group(0) not in let_bindings:
                    raise ValueError("new monthly test call does not construct RenewalTiming")
                name = binding.group(0)
                values = let_bindings[name]["values"]
                removed_bindings.add(name)
            at, expires = values
        else:
            raise ValueError(f"monthly test call has {len(arguments)} arguments")
        replacement = (
            f"subscription.renew-monthly({store}, {old_id}, {new_id}, "
            f"RenewalTiming(at={at},expires-at={expires}))"
        )
        replacements.append((start, end, replacement))
        cursor = end

    patches = list(replacements)
    for name in removed_bindings:
        binding = let_bindings[name]
        patches.append((binding["start"], binding["end"], ""))
    normalized = text
    for start, end, replacement in sorted(patches, key=lambda patch: patch[0], reverse=True):
        normalized = normalized[:start] + replacement + normalized[end:]
    return _collapse_whitespace_outside_strings(normalized), len(replacements)


def _test_name(source: str) -> str | None:
    match = re.search(r"(?m)^\s*test\s+([^\s{]+)\s*\{", source)
    return match.group(1) if match else None


def _test_sources(project: Path, revision: dict) -> dict[str, str]:
    result = {}
    for row in revision["tests"]:
        source = _object_text(project, row)
        name = _test_name(source)
        if name is None or name in result:
            raise ValueError("test attachments need unique, named Flow tests")
        result[name] = source
    return result


def grouped_regression_check(source: str) -> dict:
    """Require the new test to call the grouped API and inspect its result."""
    _, call_count = normalize_monthly_test_source(source)
    folded = source.casefold()
    has_grouped_constructor = _timing_values_from_source(source)
    result_reference_count = len(re.findall(
        r"\b(?:result|actual|renewed|replacement|updated)\b", folded
    ))
    checks = {
        "directMonthlyCall": call_count > 0,
        "groupedTimingConstruction": has_grouped_constructor,
        "checksSuccessfulResult": bool(re.search(r"\b(?:match\s+|ok\s+\w+\s*=>)", folded)),
        "referencesReturnedValue": result_reference_count >= 2,
    }
    return {"passed": all(checks.values()), "checks": checks,
            "monthlyCallCount": call_count, "resultReferenceCount": result_reference_count}


def targeted_added_tests_gate(expected_added: bool, checks: list[dict]) -> bool:
    """Require targeted regressions without constraining their count or names."""
    if not expected_added:
        return not checks
    return bool(checks) and all(item.get("passed") is True for item in checks)


def _timing_values_from_source(source: str) -> bool:
    if _timing_values_in_source(source):
        return True
    return bool(re.search(r"(?is)\b[A-Za-z_][A-Za-z0-9_-]*\s*=\s*"
                          r"(?:renewalTiming|RenewalTiming)\.new\s*\(", source))


def _timing_values_in_source(source: str) -> bool:
    needle = re.compile(r"(?is)(?:renewalTiming|RenewalTiming)\.new\s*\(")
    return bool(needle.search(source))


def replacement_preservation_diff(seed_project: Path, candidate_project: Path) -> dict:
    _, seed_manifest, _ = current_manifest(seed_project)
    _, candidate_manifest, _ = current_manifest(candidate_project)
    seed = projection_for_manifest(seed_manifest)
    candidate = projection_for_manifest(candidate_manifest)
    names_to_ids = {word["currentName"]: word["wordId"] for word in seed_manifest["words"]}
    allowed_ids = {names_to_ids[name] for name in ALLOWED_REPLACEMENTS}
    seed_words_by_id = {word["wordId"]: word for word in seed_manifest["words"]}
    candidate_words_by_id = {word["wordId"]: word for word in candidate_manifest["words"]}
    seed_words_by_name = {word["currentName"]: word for word in seed_manifest["words"]}
    candidate_words_by_name = {word["currentName"]: word for word in candidate_manifest["words"]}

    added_word_ids = set(candidate_words_by_id) - set(seed_words_by_id)
    added_word_names = {candidate_words_by_id[word_id]["currentName"] for word_id in added_word_ids}
    changed_words = {}
    for word_id, seed_word in seed_words_by_id.items():
        candidate_word = candidate_words_by_id.get(word_id)
        name = seed_word["currentName"]
        if candidate_word is None:
            changed_words[name] = {"expected": seed_word, "actual": None}
        elif word_id in allowed_ids:
            expected_revision = seed_word["currentRevision"] + 1
            expected_word = dict(seed_word, currentRevision=expected_revision)
            if candidate_word != expected_word:
                changed_words[name] = {"expected": expected_word, "actual": candidate_word}
        elif candidate_word != seed_word:
            changed_words[name] = {"expected": seed_word, "actual": candidate_word}

    inherited_types_same = all(candidate["types"].get(name) == row for name, row in seed["types"].items())
    added_type_names = set(candidate["types"]) - set(seed["types"])
    changed_revisions = {}
    test_preservation = {}
    missing_old_revisions = []
    for word_id, seed_rows in seed["revisions"].items():
        candidate_rows = candidate["revisions"].get(word_id)
        word_name = seed_words_by_id[word_id]["currentName"]
        if candidate_rows is None:
            missing_old_revisions.append(word_id)
            continue
        if word_id not in allowed_ids:
            if candidate_rows != seed_rows:
                changed_revisions[word_id] = {"expected": seed_rows, "actual": candidate_rows}
            continue
        if candidate_rows[:len(seed_rows)] != seed_rows or len(candidate_rows) != len(seed_rows) + 1:
            changed_revisions[word_id] = {
                "expectedPrefix": seed_rows,
                "actual": candidate_rows,
                "requiredNewRevision": seed_rows[-1]["revision"] + 1,
            }
            continue
        new_revision = candidate_rows[-1]
        if new_revision["revision"] != seed_rows[-1]["revision"] + 1:
            changed_revisions[word_id] = {"expectedNextRevision": seed_rows[-1]["revision"] + 1,
                                          "actual": new_revision["revision"]}
            continue
        seed_current = _word_current(seed_manifest, word_name)
        candidate_current = _word_current(candidate_manifest, word_name)
        if seed_current is None or candidate_current is None:
            test_preservation[word_name] = {"passed": False, "reason": "current word revision missing"}
            continue
        old_tests = _test_sources(seed_project, seed_current[1])
        new_tests = _test_sources(candidate_project, new_revision)
        normalized_differences = {}
        for test_name, old_source in old_tests.items():
            new_source = new_tests.get(test_name)
            if new_source is None:
                normalized_differences[test_name] = "missing inherited test"
                continue
            try:
                old_normalized, old_calls = normalize_monthly_test_source(old_source)
                new_normalized, new_calls = normalize_monthly_test_source(new_source)
            except ValueError as error:
                normalized_differences[test_name] = str(error)
                continue
            if old_normalized != new_normalized or old_calls != new_calls:
                normalized_differences[test_name] = {
                    "oldDirectCallCount": old_calls,
                    "newDirectCallCount": new_calls,
                    "oldNormalizedSha256": hashlib.sha256(old_normalized.encode()).hexdigest(),
                    "newNormalizedSha256": hashlib.sha256(new_normalized.encode()).hexdigest(),
                }
        added_tests = sorted(set(new_tests) - set(old_tests))
        expected_added = word_name == "subscription.renew-monthly"
        added_test_checks = [
            {"name": test_name, **grouped_regression_check(new_tests[test_name])}
            for test_name in added_tests
        ]
        test_gate = (
            not normalized_differences
            and targeted_added_tests_gate(expected_added, added_test_checks)
            and new_revision["examples"] == seed_current[1]["examples"]
        )
        test_preservation[word_name] = {
            "passed": test_gate,
            "oldTestCount": len(old_tests),
            "newTestCount": len(new_tests),
            "addedTestNames": added_tests,
            "addedTargetedRegressions": added_test_checks,
            "normalizedInheritedTestDifferences": normalized_differences,
            "examplesPreserved": new_revision["examples"] == seed_current[1]["examples"],
        }

    inherited_assets = sorted(referenced_assets(seed))
    expected_assets = asset_hashes(seed_project, seed)
    actual_assets = asset_hashes(candidate_project, seed)
    changed_assets = {
        key: {"expectedSha256": expected_assets.get(key), "actualSha256": actual_assets.get(key)}
        for key in inherited_assets
        if expected_assets.get(key) != key or actual_assets.get(key) != key
    }
    allowed_new_type_present = added_type_names == {TYPE_NAME}
    # Flow's generated constructor/accessors for a new record are runtime-only
    # in this project format. Persisting any new dictionary word would be an
    # unrelated helper/namespace change; the record itself is checked through
    # the exact added type entry above and the independent semantic type gate.
    no_new_persisted_word_rows = not added_word_ids
    all_revision_deltas_scoped = not changed_revisions and not missing_old_revisions
    test_changes_scoped = len(test_preservation) == len(allowed_ids) and all(
        row["passed"] for row in test_preservation.values()
    )
    passed = (
        inherited_types_same
        and allowed_new_type_present
        and not changed_words
        and no_new_persisted_word_rows
        and all_revision_deltas_scoped
        and test_changes_scoped
        and not changed_assets
    )
    return {
        "passed": passed,
        "allowedExistingWordNames": list(ALLOWED_REPLACEMENTS),
        "stableExistingWordIds": {name: names_to_ids[name] for name in ALLOWED_REPLACEMENTS},
        "changedOrMissingExistingWords": changed_words,
        "addedTypeNames": sorted(added_type_names),
        "inheritedTypesUnchanged": inherited_types_same,
        "addedGeneratedWordNames": sorted(added_word_names),
        "noNewPersistedWordRows": no_new_persisted_word_rows,
        "changedOrMissingRevisionRows": changed_revisions,
        "missingInheritedRevisionGroups": missing_old_revisions,
        "inheritedTestsAndExamples": test_preservation,
        "referencedInheritedObjects": inherited_assets,
        "changedOrMissingInheritedObjects": changed_assets,
        "immutableOldRevisionsPreserved": all_revision_deltas_scoped,
        "onlyAllowedTypeAdded": allowed_new_type_present,
    }


def validate_evidence_root(path: Path) -> Path:
    resolved = path if path.is_absolute() else REPO / path
    resolved = resolved.resolve()
    root = DEFAULT_EVIDENCE.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"Evidence must be inside {root}")
    return resolved


def create_run_directory(root: Path, label: str) -> Path:
    parent = root / label
    parent.mkdir(parents=True, exist_ok=True)
    stamp = strftime("%Y%m%dT%H%M%SZ", gmtime())
    index = 1
    while True:
        candidate = parent / f"{stamp}-{index:02d}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            index += 1


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _decode_output(value) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def run_cli(cli: Path, project: Path, requests: list[dict]) -> tuple[int | None, str, str, list[dict], list[str], str | None]:
    command = ["dotnet", str(cli), "--project", str(project), "--filesystem", "virtual", "--jsonl"]
    payload = "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n"
    try:
        process = subprocess.run(
            command,
            cwd=REPO,
            input=payload,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=900,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        return None, _decode_output(error.stdout), _decode_output(error.stderr), [], [], \
            f"CLI timed out after {error.timeout} seconds"
    except OSError as error:
        return None, "", f"{type(error).__name__}: {error}\n", [], [], f"CLI launch failed: {error}"
    responses = []
    parse_errors = []
    for line_number, line in enumerate(process.stdout.splitlines(), 1):
        try:
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError("response is not a JSON object")
            responses.append(row)
        except (json.JSONDecodeError, ValueError) as error:
            parse_errors.append(f"stdout line {line_number}: {error}: {line[:300]!r}")
    return process.returncode, process.stdout, process.stderr, responses, parse_errors, None


def type_control_source(separator: str, renewal_type: str = "SubscriptionRenewal") -> str:
    expires_type = "String" if renewal_type == "String" else "Instant"
    if separator == "semicolon":
        return (
            "// frontend: flow/2\n"
            "record RenewalTiming { "
            "field at: Instant; "
            f"field expires-at: {expires_type}; "
            "}\n"
        )
    return (
        "// frontend: flow/2\n"
        "record RenewalTiming {\n"
        "    field at: Instant\n"
        f"    field expires-at: {expires_type}\n"
        "}\n"
    )


def run_type_gate_controls(cli: Path, seed: Path, evidence_root: Path) -> dict:
    cases = [
        ("newline", "newline", "Instant", "pass"),
        ("semicolon", "semicolon", "Instant", "pass"),
        ("wrong-field-type", "newline", "String", "reject"),
    ]
    results = []
    seed_before = project_fingerprint(seed)
    for name, separator, field_type, expected in cases:
        run_dir = create_run_directory(evidence_root, f"type-gate-{name}")
        project = run_dir / "project-copy"
        shutil.copytree(seed, project)
        source_before = project_fingerprint(project)
        requests = [
            {"op": "define", "frontend": "flow", "syntaxVersion": 2,
             "source": type_control_source(separator, field_type)},
            {"op": "describe", "type": TYPE_NAME},
        ]
        exit_code, stdout, stderr, responses, parse_errors, launch_failure = run_cli(cli, project, requests)
        (run_dir / "requests.jsonl").write_text(
            "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
            encoding="utf-8",
        )
        (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
        (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")
        define_response = responses[0] if len(responses) > 0 else {}
        describe_response = responses[1] if len(responses) > 1 else {}
        gate = type_gate.type_gate(describe_response)
        described = type_gate.described_type(describe_response)
        semantic_record = (
            described is not None
            and described.get("name") == TYPE_NAME
            and described.get("typeKind") == "record"
            and isinstance(described.get("fields"), list)
        )
        expected_satisfied = semantic_record and gate["status"] == ("failure" if expected == "reject" else "pass")
        after_hash = project_fingerprint(project)
        well_formed = len(responses) == len(requests) and not parse_errors and launch_failure is None
        passed = (
            exit_code == 0 and well_formed
            and define_response.get("ok") is True and define_response.get("kind") == "defined"
            and expected_satisfied and source_before == after_hash
        )
        error_details = list(parse_errors)
        if launch_failure:
            error_details.append(launch_failure)
        if len(responses) != len(requests):
            error_details.append(f"expected {len(requests)} responses, got {len(responses)}")
        if exit_code != 0:
            error_details.append(f"CLI exit code {exit_code}")
        if source_before != after_hash:
            error_details.append("scoring copy changed during type-gate probe")
        result = {
            "name": name,
            "status": "passed" if passed else "failure",
            "expectedGate": expected,
            "definePassed": define_response.get("ok") is True and define_response.get("kind") == "defined",
            "semanticDescribeRecordObserved": semantic_record,
            "typeGate": gate,
            "typeGateMatchedExpected": expected_satisfied,
            "projectFingerprintBefore": source_before,
            "projectFingerprintAfter": after_hash,
            "cliExitCode": exit_code,
            "rawResponseCount": len(responses),
            "errors": error_details,
            "evidenceDirectory": str(run_dir.resolve()),
        }
        write_json(run_dir / "score.json", result)
        results.append(result)
    seed_after = project_fingerprint(seed)
    passed = seed_before == seed_after and all(row["status"] == "passed" for row in results)
    return {
        "status": "passed" if passed else "failure",
        "kind": "actual-cli-describe-probes",
        "seedFingerprintBefore": seed_before,
        "seedFingerprintAfter": seed_after,
        "seedUnchanged": seed_before == seed_after,
        "cases": results,
    }


def iso_minute(minutes: int) -> str:
    value = OLD.BASE_TIME + timedelta(minutes=minutes)
    return value.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"


def scheduled_item_expression(item: dict) -> str:
    renewal = item["renewal"]
    constructor = (
        "subscriptionRenewal.new("
        f"old-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, renewal['oldId']))}), "
        f"new-id = SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, renewal['newId']))}), "
        f"plan = SubscriptionPlan.{renewal['plan']}(), "
        f"expires-at = Instant.new({OLD.flow_string(iso_minute(renewal['expiry']))}))"
    )
    return (
        "scheduledSubscriptionRenewal.new("
        f"renewal = {constructor}, at = Instant.new({OLD.flow_string(iso_minute(item['at']))}))"
    )


def scheduled_observation_expression(item: dict) -> str:
    original_store = OLD.store_expr(item["store"])
    changes = OLD.list_expr("ScheduledSubscriptionRenewal", [
        scheduled_item_expression(row) for row in item["request"]["items"]
    ])
    call = f"{item['operation']}({original_store}, {changes})"
    return f"replacementTrial230Observation.new(input = {original_store}, outcome = {call})"


def direct_monthly_observation_expression(item: dict) -> str:
    request = item["request"]
    timing = request["timing"]
    original_store = OLD.store_expr(item["store"])
    grouped = (
        "renewalTiming.new("
        f"at = Instant.new({OLD.flow_string(iso_minute(timing['at']))}), "
        f"expires-at = Instant.new({OLD.flow_string(iso_minute(timing['expiresAt']))}))"
    )
    call = (
        "subscription.renew-monthly("
        f"{original_store}, "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['oldId']))}), "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['newId']))}), "
        f"{grouped})"
    )
    return f"replacementTrial230Observation.new(input = {original_store}, outcome = {call})"


def case_observation_expression(item: dict) -> str:
    if item["operation"] == "subscription.renew-monthly":
        return direct_monthly_observation_expression(item)
    return scheduled_observation_expression(item)


def canonical(value, key: str | None = None):
    if isinstance(value, dict):
        return {name: canonical(item, name) for name, item in value.items()}
    if isinstance(value, list):
        rows = [canonical(item) for item in value]
        if key in STORE_ARRAYS and rows and isinstance(rows[0], dict):
            rows.sort(key=lambda item: item.get("id", ""))
        return rows
    return value


def trial_actual_projection(observation: dict) -> dict:
    input_store = observation["input"]
    outcome = observation["outcome"]
    if outcome["tag"] == "ok":
        return {"tag": "ok", "store": outcome["value"], "input": input_store}
    error = outcome.get("value") or {}
    return {"tag": "error", "code": error.get("code"), "message": error.get("message"), "input": input_store}


def case_expected_projection(item: dict, expected: dict | None = None) -> dict:
    expected = expected if expected is not None else item["expected"]
    original_iso_day = OLD.iso_day
    OLD.iso_day = iso_minute
    try:
        input_store = OLD.normalized_projection({"store": item["store"]})
        if expected["tag"] == "ok":
            return {"tag": "ok", "store": OLD.normalized_projection({"store": expected["store"]}), "input": input_store}
        return {"tag": "error", "code": expected["code"],
                "message": expected.get("message", ERROR_MESSAGES[expected["code"]]), "input": input_store}
    finally:
        OLD.iso_day = original_iso_day


def score_trial_case(item: dict, response: dict, expected: dict | None = None,
                     compare_correct_model: bool = False) -> dict:
    result = {"caseId": item["id"], "executed": False, "passed": False}
    if response.get("ok") is True and response.get("kind") == "eval":
        structured = response.get("data", {}).get("structuredStack")
        values = structured.get("values", []) if isinstance(structured, dict) else []
        if len(values) == 1:
            actual = trial_actual_projection(OLD.unwrap_structured(values[0]))
            wanted = case_expected_projection(item, expected)
            actual = canonical(actual)
            wanted = canonical(wanted)
            result.update(executed=True, passed=(actual == wanted), actualTag=actual.get("tag"), expectedTag=wanted.get("tag"))
            if compare_correct_model:
                correct_wanted = canonical(case_expected_projection(item))
                result["matchesCorrectOracle"] = actual == correct_wanted
            if actual != wanted:
                result.update(expected=wanted, actual=actual)
        else:
            result["setupError"] = "eval response omitted one structured observation value"
    else:
        result["setupError"] = response.get("error", response.get("text", "missing eval response"))
    return result


def score_creator_case(item: dict, response: dict) -> dict:
    # This deliberately reuses only the previous study's behavior projection;
    # no old migration, manifest, effects, or eligibility gate is carried over.
    return SCORER210.score_eval(item, response)


def inherited_observation_expression(item: dict) -> str:
    """Adapt only the inherited monthly call constructor to RenewalTiming."""
    if item["operation"] != "subscription.renew-monthly":
        return SCORER210.observation_expression(item, "creator").replace(
            "organicReuse210Observation", "replacementTrial230Observation"
        )
    request = item["request"]
    store = OLD.store_expr(item["store"])
    timing = (
        "renewalTiming.new("
        f"at = Instant.new({OLD.flow_string(OLD.iso_day(request['at']))}), "
        f"expires-at = Instant.new({OLD.flow_string(OLD.iso_day(request['expiry']))}))"
    )
    call = (
        "subscription.renew-monthly("
        f"{store}, "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['oldId']))}), "
        f"SubscriptionId.new({OLD.flow_string(OLD.id_for(OLD.SUBSCRIPTION_IDS, request['newId']))}), "
        f"{timing})"
    )
    return f"replacementTrial230Observation.new(input = {store}, outcome = {call})"


def run_case_suite(cli: Path, project: Path, *, include_inherited: bool, full_suite: bool,
                   label: str, evidence_root: Path, mutation: str | None = None,
                   control_variant: str | None = None,
                   candidate_request: dict | None = None) -> dict:
    model, trial_cases = load_scheduled_model()
    if include_inherited:
        creator_cases = SCORER210.load_frozen_cases("creator")
    else:
        creator_cases = []

    before_source = project_fingerprint(project)
    _, manifest_before, _ = current_manifest(project)
    example_owners = sorted({
        word["currentName"] for word in manifest_before["words"]
        if next(row for row in manifest_before["revisions"]
                if row["wordId"] == word["wordId"] and row["revision"] == word["currentRevision"])["examples"]
    }) if full_suite else []
    run_dir = create_run_directory(evidence_root, label)
    scoring_project = run_dir / "project-copy"
    shutil.copytree(project, scoring_project)
    copy_before = project_fingerprint(scoring_project)
    if copy_before != before_source:
        raise RuntimeError("Scoring copy fingerprint differs from source project")

    requests = []
    if candidate_request is not None:
        requests.append(candidate_request)
    if full_suite:
        requests.append({"op": "test-all"})
        for owner in example_owners:
            requests.append({"op": "example", "word": owner})
    requests.append({"op": "define", "frontend": "flow", "syntaxVersion": 2, "source": OBSERVATION_SOURCE})
    requests.append({"op": "describe", "type": TYPE_NAME})

    # Build the nine trial fixtures with minute precision. Inherited creator
    # cases retain their original day precision and expected projections.
    original_iso_day = OLD.iso_day
    OLD.iso_day = iso_minute
    try:
        case_expressions = [case_observation_expression(item) for item in trial_cases]
    finally:
        OLD.iso_day = original_iso_day
    case_expected = [item["expected"] for item in trial_cases]
    if mutation is not None:
        case_expected = [model.expected(item, mutation=mutation) for item in model.CASES]
    for expression, item in zip(case_expressions, trial_cases):
        requests.append({"op": "eval", "frontend": "flow", "syntaxVersion": 2,
                         "structured": True, "code": expression})

    for item in creator_cases:
        expression = inherited_observation_expression(item)
        requests.append({"op": "eval", "frontend": "flow", "syntaxVersion": 2,
                         "structured": True, "code": expression})

    exit_code, stdout, stderr, responses, json_parse_errors, launch_failure = run_cli(cli, scoring_project, requests)
    expected_request_count = len(requests)
    raw_response_count = len(responses)
    protocol_errors = list(json_parse_errors)
    if launch_failure:
        protocol_errors.append(launch_failure)
    if raw_response_count != expected_request_count:
        protocol_errors.append(f"CLI returned {raw_response_count} valid responses for {expected_request_count} requests")
    parse_failure = "\n".join(protocol_errors) or None

    (run_dir / "requests.jsonl").write_text(
        "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in requests) + "\n",
        encoding="utf-8",
    )
    (run_dir / "responses.jsonl").write_text(stdout, encoding="utf-8")
    (run_dir / "stderr.txt").write_text(stderr, encoding="utf-8")

    if len(responses) < expected_request_count:
        responses.extend({} for _ in range(expected_request_count - len(responses)))

    cursor = 0
    candidate_response = {}
    if candidate_request is not None:
        candidate_response = responses[cursor]
        cursor += 1
    test_response = {}
    if full_suite:
        test_response = responses[cursor]
        cursor += 1
    example_responses = responses[cursor:cursor + len(example_owners)]
    cursor += len(example_owners)
    define_response = responses[cursor]
    cursor += 1
    describe_response = responses[cursor]
    cursor += 1
    case_responses = responses[cursor:cursor + len(trial_cases)]
    cursor += len(trial_cases)
    creator_responses = responses[cursor:cursor + len(creator_cases)]

    case_results = [
        score_trial_case(item, response, wanted, compare_correct_model=(mutation is not None))
        for item, response, wanted in zip(trial_cases, case_responses, case_expected)
    ]
    creator_results = [score_creator_case(item, response) for item, response in zip(creator_cases, creator_responses)]
    define_pass = define_response.get("ok") is True and define_response.get("kind") == "defined"
    candidate_pass = (
        candidate_request is None
        or (candidate_response.get("ok") is True and candidate_response.get("kind") == "defined")
    )
    type_result = type_gate.type_gate(describe_response)
    type_pass = type_result["status"] == "pass"
    test_pass = SCORER210.response_passed(test_response, "test") if full_suite else True
    example_results = [
        {"owner": owner, "passed": SCORER210.response_passed(response, "example"), "response": response}
        for owner, response in zip(example_owners, example_responses)
    ]
    examples_pass = len(example_results) == len(example_owners) and all(row["passed"] for row in example_results)

    actor_after = project_fingerprint(project)
    copy_after = project_fingerprint(scoring_project)
    mutation_pass = True
    correct_semantics_mismatch_ids = []
    if mutation is not None:
        model_checks = model.model_checks()
        predicted_mismatches = model_checks["discrimination"].get(mutation, [])
        correct_semantics_mismatch_ids = [
            row["caseId"] for row in case_results
            if row.get("executed") and row.get("matchesCorrectOracle") is False
        ]
        mutation_pass = (
            bool(predicted_mismatches)
            and all(item["passed"] for item in case_results)
            and correct_semantics_mismatch_ids == predicted_mismatches
        )

    failures = []
    if exit_code != 0:
        failures.append({"kind": "cli-exit", "code": exit_code})
    if parse_failure:
        failures.append({"kind": "cli-output", "detail": parse_failure})
    if not define_pass:
        failures.append({"kind": "observer-define", "response": define_response})
    if not candidate_pass:
        failures.append({"kind": "control-candidate-stage", "response": candidate_response})
    if not type_pass:
        failures.append({"kind": "replacement-type", "gate": type_result, "response": describe_response})
    if not test_pass:
        failures.append({"kind": "test-all", "response": test_response})
    if not examples_pass:
        failures.append({"kind": "examples", "results": example_results})
    if len(case_results) != len(trial_cases) or any(not row["executed"] for row in case_results):
        failures.append({"kind": "matched-case-setup", "cases": [row for row in case_results if not row["executed"]]})
    if mutation is None and any(not row["passed"] for row in case_results):
        failures.append({"kind": "matched-case-mismatch", "cases": [row for row in case_results if not row["passed"]]})
    if mutation is not None and any(not row["passed"] for row in case_results):
        failures.append({"kind": "mutant-case-mismatch", "mutation": mutation,
                         "cases": [row for row in case_results if not row["passed"]]})
    if mutation is not None and not mutation_pass:
        failures.append({"kind": "mutation-discrimination", "mutation": mutation})
    if include_inherited and (len(creator_results) != 66 or any(not row["executed"] or not row["passed"] for row in creator_results)):
        failures.append({"kind": "inherited-creator-behavior", "cases": [row for row in creator_results if not row["executed"] or not row["passed"]]})
    if actor_after != before_source:
        failures.append({"kind": "source-project-mutated", "before": before_source, "after": actor_after})
    if copy_after != copy_before:
        failures.append({"kind": "scoring-copy-mutated", "before": copy_before, "after": copy_after})

    report = {
        "status": (("control-calibrated" if not failures else "control-failure") if control_variant
                   else ("passed" if not failures else "score-failure")),
        "scoreKind": "calibration-control" if control_variant else "participant-acceptance",
        "label": label,
        "controlVariant": control_variant,
        "mutationModel": mutation,
        "project": str(project.resolve()),
        "sourceProjectFingerprintBefore": before_source,
        "sourceProjectFingerprintAfter": actor_after,
        "scoringProject": str(scoring_project.resolve()),
        "scoringProjectFingerprintBefore": copy_before,
        "scoringProjectFingerprintAfter": copy_after,
        "cli": str(cli.resolve()),
        "cliSha256": sha256_file(cli),
        "cliExitCode": exit_code,
        "rawValidResponseCount": raw_response_count,
        "responseCount": len(responses),
        "expectedResponseCount": expected_request_count,
        "requestParseFailure": parse_failure,
        "jsonParseErrors": json_parse_errors,
        "launchFailure": launch_failure,
        "testAllPassed": test_pass,
        "testAllResponse": test_response,
        "exampleOwnerCount": len(example_owners),
        "examplesPassed": examples_pass,
        "exampleResults": example_results,
        "observerDefined": define_pass,
        "controlCandidateStaged": candidate_pass,
        "controlCandidateResponse": candidate_response,
        "typeGate": {**type_result, "response": describe_response},
        "matchedCases": {
            "count": len(case_results),
            "executed": sum(row["executed"] for row in case_results),
            "matched": sum(row["passed"] for row in case_results),
            "results": case_results,
        },
        "inheritedCreatorCases": {
            "count": len(creator_results),
            "executed": sum(row["executed"] for row in creator_results),
            "matched": sum(row["passed"] for row in creator_results),
            "source": "66 organic-reuse-210 creator cases through the monthly call-shape adapter only",
        },
        "mutationDiscriminationPassed": mutation_pass,
        "correctSemanticsMismatchIds": correct_semantics_mismatch_ids,
        "failures": failures,
        "evidenceDirectory": str(run_dir.resolve()),
        "requests": "requests.jsonl",
        "responses": "responses.jsonl",
        "stderr": "stderr.txt",
    }
    write_json(run_dir / "score.json", report)
    return report


def parse_named_project(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected NAME=PROJECT_PATH")
    name, raw_path = value.split("=", 1)
    if not name or not raw_path:
        raise argparse.ArgumentTypeError("expected nonempty NAME=PROJECT_PATH")
    path = Path(raw_path)
    return name, path if path.is_absolute() else REPO / path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path, help="Fresh AgentLang.Cli.dll for this study")
    parser.add_argument("--actor-project", required=True, type=Path, help="Project root to score")
    parser.add_argument("--seed-project", type=Path, default=DEFAULT_SEED)
    parser.add_argument("--evidence-root", type=Path, default=DEFAULT_EVIDENCE)
    parser.add_argument("--mode", choices=("preflight", "frozen"), default="preflight")
    parser.add_argument("--source-purpose", choices=("participant-acceptance", "calibration"),
                        default="participant-acceptance",
                        help="Label whether actor-project is a participant submission or a calibration control")
    parser.add_argument("--control", action="append", type=parse_named_project, default=[],
                        help="Calibrated control as NAME=PROJECT_PATH; names: correct, swapped-timing")
    parser.add_argument("--control-source", action="append", type=parse_named_project, default=[],
                        help="Optional staged define request as NAME=JSON_PATH; used for behavior-only controls")
    args = parser.parse_args()

    cli = args.cli if args.cli.is_absolute() else REPO / args.cli
    actor = args.actor_project if args.actor_project.is_absolute() else REPO / args.actor_project
    seed = args.seed_project if args.seed_project.is_absolute() else REPO / args.seed_project
    evidence = validate_evidence_root(args.evidence_root)
    if not cli.is_file() or not actor.is_dir() or not seed.is_dir():
        raise FileNotFoundError("CLI, actor project, and retained seed must exist")
    if not SETUP_PATH.is_file():
        raise FileNotFoundError(f"Prepared 230 setup pin is missing: {SETUP_PATH}")
    setup_pin = setup_baseline_pin("retained")
    runtime_pin = verify_runtime_pin(cli.resolve())
    baseline_tree, baseline_inventory = prepared_tree_fingerprint(seed)
    if len(baseline_inventory) != setup_pin["fileCount"] or baseline_tree != setup_pin["treeSha256"]:
        raise ValueError(f"Retained 230 baseline does not match setup.json: {baseline_tree}")
    seed_before = project_fingerprint(seed)
    type_gate_controls = run_type_gate_controls(cli.resolve(), seed, evidence)
    actor_before = project_fingerprint(actor)
    _, actor_manifest, _ = current_manifest(actor)
    current = current_revisions(actor_manifest)
    qualifications = {
        name: {
            "available": name in current,
            "library": name in current and current[name]["revision"]["maturity"] == "library",
            "hasOwnTests": bool(current.get(name, {}).get("revision", {}).get("tests")),
            "wordId": current.get(name, {}).get("word", {}).get("wordId"),
            "revision": current.get(name, {}).get("revision", {}).get("revision"),
        }
        for name in ALLOWED_REPLACEMENTS
    }
    callers_qualified = all(row["available"] and row["library"] and row["hasOwnTests"]
                            for row in qualifications.values())
    type_available = TYPE_NAME in {row["name"] for row in actor_manifest["types"]}

    result_root = create_run_directory(evidence, "cal" if args.source_purpose == "calibration" else "participant")
    # Use the participant run folder as the common evidence root. The evaluator
    # creates run-specific copies below its child folder.
    suite_label = "correct" if args.source_purpose == "calibration" else "evaluation"
    suite = run_case_suite(cli.resolve(), actor.resolve(), include_inherited=True,
                           full_suite=True, label=suite_label, evidence_root=result_root)
    control_reports = []
    model, _ = load_scheduled_model()
    expected_controls = {"correct": None, "swapped-timing": "swap-monthly-timing"}
    control_names = [name for name, _ in args.control]
    control_set_complete = set(control_names) == set(expected_controls) and len(control_names) == len(set(control_names))
    candidate_sources = dict(args.control_source)
    if len(candidate_sources) != len(args.control_source):
        raise ValueError("A control may have at most one staged source request")
    if set(candidate_sources) - set(control_names):
        raise ValueError("A staged control source must accompany its named control project")
    for name, project in args.control:
        if name not in expected_controls:
            raise ValueError(f"Unknown control name {name!r}")
        if not project.is_dir():
            raise FileNotFoundError(f"Control project does not exist: {project}")
        if name == "swapped-timing" and name not in candidate_sources:
            raise ValueError("The swapped-timing behavior control requires its broker-staged define request")
        candidate_request = None
        if name in candidate_sources:
            source_path = candidate_sources[name]
            if not source_path.is_file():
                raise FileNotFoundError(f"Staged control request does not exist: {source_path}")
            candidate_request = json.loads(source_path.read_text(encoding="utf-8"))
            if not isinstance(candidate_request, dict) or candidate_request.get("op") != "define":
                raise ValueError(f"Staged control request must be a JSON define object: {source_path}")
        control = run_case_suite(cli.resolve(), project.resolve(), include_inherited=False,
                                 full_suite=False, label=f"control-{name}", evidence_root=result_root,
                                 mutation=expected_controls[name], control_variant=name,
                                 candidate_request=candidate_request)
        # Calibration compares exact neutral Store projections for every case.
        matches_predicted = (
            control["matchedCases"]["matched"] == len(model.CASES)
            and control.get("mutationDiscriminationPassed", False)
            and (
                name != "swapped-timing"
                or bool(model.model_checks()["discrimination"]["swap-monthly-timing"])
            )
        )
        control["matchesPredictedModel"] = matches_predicted
        if not matches_predicted:
            control["status"] = "control-failure"
            control["failures"].append({"kind": "control-model-mismatch", "expectedMutation": expected_controls[name]})
        control_reports.append(control)

    seed_after = project_fingerprint(seed)
    actor_after = project_fingerprint(actor)
    inherited_after = replacement_preservation_diff(seed, actor)
    preflight_mode = args.mode == "preflight"

    failures = []
    if not runtime_pin["passed"]:
        failures.append({"kind": "runtime-pin", "details": runtime_pin})
    if suite["status"] != "passed":
        failures.append({"kind": "behavior-suite", "directory": suite["evidenceDirectory"]})
    if not inherited_after["passed"]:
        failures.append({"kind": "inherited-preservation", "diff": inherited_after})
    if not type_available:
        failures.append({"kind": "new-type-missing", "type": TYPE_NAME})
    if not callers_qualified:
        failures.append({"kind": "replacement-caller-qualification", "words": qualifications})
    if seed_after != seed_before or actor_after != actor_before:
        failures.append({"kind": "source-project-mutated", "actorBefore": actor_before,
                         "actorAfter": actor_after, "seedBefore": seed_before, "seedAfter": seed_after})
    if type_gate_controls["status"] != "passed":
        failures.append({"kind": "type-gate-controls", "details": type_gate_controls})
    if not control_set_complete:
        failures.append({"kind": "control-set-incomplete", "expected": sorted(expected_controls),
                         "actual": control_names})
    if any(row["status"] != "control-calibrated" for row in control_reports):
        failures.append({"kind": "control-calibration", "controls": [row["label"] for row in control_reports
                                                                           if row["status"] != "control-calibrated"]})

    report = {
        "status": "passed" if not failures else "score-failure",
        "mode": args.mode,
        "sourcePurpose": args.source_purpose,
        "runtime": str(cli.resolve()),
        "runtimeSha256": sha256_file(cli),
        "runtimePin": runtime_pin,
        "seedProject": str(seed.resolve()),
        "seedProjectFingerprint": seed_before,
        "seedManifestHash": json.loads((seed / ".agentlang/store/CURRENT").read_text(encoding="utf-8-sig"))["manifestHash"],
        "expectedBaselineTreeSha256": setup_pin["treeSha256"],
        "baselineTreeSha256": baseline_tree,
        "baselineFileCount": len(baseline_inventory),
        "actorProject": str(actor.resolve()),
        "actorProjectFingerprintBefore": actor_before,
        "actorProjectFingerprintAfter": actor_after,
        "actorProjectUnchanged": actor_before == actor_after,
        "inheritedProjectionPreserved": inherited_after,
        "replacementQualifications": qualifications,
        "renewalTimingTypePersisted": type_available,
        "suite": suite,
        "controls": control_reports,
        "controlSetComplete": control_set_complete,
        "typeGateControls": type_gate_controls,
        "frozenPinStatus": "preflight-only; no frozen CLI pin is claimed" if preflight_mode else "freeze pin must be recorded by the coordinator",
        "failures": failures,
    }
    top_dir = Path(suite["evidenceDirectory"]).parents[1]
    write_json(top_dir / "language-score.json", report)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
