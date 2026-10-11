"""Deterministic checks for the frozen model and semantic type gate."""

from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import subprocess

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import model
import type_gate
import score_language

fsharp_spec = importlib.util.spec_from_file_location("scheduled_batch_226_fsharp_scorer", HERE / "fsharp" / "score_fsharp.py")
assert fsharp_spec is not None and fsharp_spec.loader is not None
fsharp_scorer = importlib.util.module_from_spec(fsharp_spec)
fsharp_spec.loader.exec_module(fsharp_scorer)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def type_response(separator: str, renewal_type: str = "SubscriptionRenewal") -> dict:
    # The gate receives semantic `describe` output; source punctuation is intentionally absent.
    fields = [
        {"name": "renewal", "type": renewal_type},
        {"name": "at", "type": "Instant"},
    ]
    return {
        "ok": True,
        "kind": "describe",
        "data": {
            "name": type_gate.TYPE_NAME,
            "kind": "type",
            "typeKind": "record",
            "fields": fields,
            "sourceSeparatorControl": separator,
        },
    }


def main() -> int:
    cases_path = HERE / "cases.json"
    checks_path = HERE / "model-checks.json"
    before = {path.name: sha256(path) for path in (cases_path, checks_path)}
    exported = json.loads(cases_path.read_text(encoding="utf-8"))
    check_exported = json.loads(checks_path.read_text(encoding="utf-8"))
    assert exported == model.make_cases()
    assert check_exported == model.model_checks()
    assert len(exported) == 7
    assert [item["id"] for item in exported] == [
        "empty-list",
        "monthly-annual-distinct-instants",
        "chained-renewal-replacement-later",
        "reverse-chronological-independent-items",
        "missing-old-first-beats-later-failure",
        "valid-prefix-then-missing-old-is-atomic",
        "chained-second-before-first-start",
    ]
    assert [item["at"] for item in exported[1]["request"]["items"]] == [75, 140]
    assert [item["at"] for item in exported[3]["request"]["items"]] == [180, 60]
    assert [row["id"] for row in exported[3]["expected"]["store"]["subscriptions"][-2:]] == ["new2", "new"]
    assert "reverse-chronological-independent-items" in check_exported["listOrderOnlyDifferences"]["sort-by-at"]
    assert "reverse-chronological-independent-items" not in check_exported["discrimination"]["sort-by-at"]
    assert exported[4]["expected"]["code"] == "SUBSCRIPTION_NOT_FOUND"
    assert "store" not in exported[4]["expected"]
    assert exported[5]["expected"]["input"] == exported[5]["store"]
    assert exported[6]["expected"]["code"] == "CANCEL_BEFORE_START"
    assert check_exported["perCaseControlResults"][6]["sort-by-at"]["outcome"]["code"] == "SUBSCRIPTION_NOT_FOUND"

    gate = type_gate.equivalence_control(
        type_response("newline"), type_response("semicolon")
    )
    wrong_field_type = type_gate.wrong_field_type_control(
        type_response("newline", renewal_type="String")
    )
    assert gate["status"] == "pass"
    assert wrong_field_type["status"] == "pass"
    scheduled = score_language.load_scheduled_model()[1]
    creator_cases = score_language.SCORER210.load_frozen_cases("creator")
    assert len(creator_cases) == 66
    assert "2026-01-01T13:15:00.0000000+00:00" in score_language.scheduled_observation_expression(scheduled[1])
    expected_error = score_language.schedule_expected_projection(scheduled[4])
    assert expected_error["tag"] == "error"
    assert expected_error["message"] == model.ERROR_MESSAGES["SUBSCRIPTION_NOT_FOUND"]
    wrong_message = dict(expected_error, message="different error message")
    assert score_language.canonical(expected_error) != score_language.canonical(wrong_message)
    seed = score_language.DEFAULT_SEED
    _, seed_manifest, _ = score_language.current_manifest(seed)
    inherited = score_language.projection_for_manifest(seed_manifest)
    preserved = score_language.preservation_diff(
        inherited, inherited, seed_project=seed, candidate_project=seed
    )
    assert preserved["passed"]
    assert preserved["inheritedObjectIntegrity"]["verifiedCount"] == 273
    changed_revision = json.loads(json.dumps(inherited))
    inherited_word_id = next(iter(changed_revision["revisions"]))
    changed_revision["revisions"][inherited_word_id][0]["maturity"] = "changed"
    assert not score_language.preservation_diff(inherited, changed_revision)["passed"]

    original_run = score_language.subprocess.run
    try:
        score_language.subprocess.run = lambda *args, **kwargs: SimpleNamespace(
            returncode=0, stdout='{"ok":true}\nnot-json\n', stderr="raw stderr\n"
        )
        exit_code, raw_stdout, raw_stderr, rows, parse_errors, launch_failure = score_language.run_cli(
            Path("fake-cli"), Path("fake-project"), []
        )
        assert exit_code == 0 and raw_stdout.endswith("not-json\n") and raw_stderr == "raw stderr\n"
        assert len(rows) == 1 and len(parse_errors) == 1 and launch_failure is None
        malformed_raw_retained = raw_stdout.endswith("not-json\n") and parse_errors

        def timeout_run(*args, **kwargs):
            raise score_language.subprocess.TimeoutExpired(args[0], 3, output=b"partial response\n", stderr=b"timeout stderr")

        score_language.subprocess.run = timeout_run
        exit_code, raw_stdout, raw_stderr, rows, parse_errors, launch_failure = score_language.run_cli(
            Path("fake-cli"), Path("fake-project"), []
        )
        assert exit_code is None and raw_stdout == "partial response\n" and raw_stderr == "timeout stderr"
        assert launch_failure and "timed out" in launch_failure
        timeout_raw_retained = raw_stdout == "partial response\n" and raw_stderr == "timeout stderr"
    finally:
        score_language.subprocess.run = original_run
    original_fsharp_run = fsharp_scorer.subprocess.run
    try:
        def fsharp_timeout_run(*args, **kwargs):
            raise subprocess.TimeoutExpired(args[0], 7, output=b"partial build/runtime stdout", stderr=b"partial stderr")

        fsharp_scorer.subprocess.run = fsharp_timeout_run
        partial, timed_out = fsharp_scorer.run_process(["dotnet", "test"], timeout=7)
        assert timed_out and partial.returncode == -1
        assert partial.stdout == "partial build/runtime stdout" and partial.stderr == "partial stderr"
    finally:
        fsharp_scorer.subprocess.run = original_fsharp_run
    after = {path.name: sha256(path) for path in (cases_path, checks_path)}
    assert before == after, "unit checks must not rewrite frozen case or model-check exports"
    print(json.dumps({
        "status": "unit-checks-passed",
        "frozenExportsMatchModel": True,
        "caseCount": len(exported),
        "modelControls": check_exported["casesEvaluatedPerVariant"],
        "typeGate": gate,
        "wrongFieldTypeControl": wrong_field_type,
        "languageScorerProjection": {
            "inheritedCreatorCaseCount": len(creator_cases),
            "minutePrecision": True,
            "exactErrorMessage": expected_error["message"],
            "messageChangesOutcomeComparison": True,
        },
        "inheritedManifestControl": {
            "passed": preserved["passed"],
            "wordCount": len(inherited["words"]),
            "revisionCount": sum(len(rows) for rows in inherited["revisions"].values()),
            "typeCount": len(inherited["types"]),
            "referencedObjectsVerified": preserved["inheritedObjectIntegrity"]["verifiedCount"],
            "revisionMutationRejected": True,
        },
        "rawFailureEvidenceControl": {
            "malformedJsonRetained": bool(malformed_raw_retained),
            "timeoutPartialOutputRetained": timeout_raw_retained,
            "fsharpTimeoutPartialOutputRetained": partial.stdout == "partial build/runtime stdout" and partial.stderr == "partial stderr",
        },
        "frozenExportSha256": before,
    }, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
