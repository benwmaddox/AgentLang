"""Python-only checks for the bounded task-required vocabulary adapter."""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
sys.path.insert(0, str(HERE))
import score_public_vocabulary_reuse as adapter  # noqa: E402


def target_payload() -> dict:
    return {
        "schemaVersion": 1,
        "targetKind": adapter.TARGET_KIND,
        "acceptedProjectFingerprint": adapter.EXPECTED_PROJECT_FINGERPRINT,
        "targets": [{"name": name} for name in adapter.TARGET_NAMES],
    }


def synthetic_manifest() -> tuple[dict, dict[str, str]]:
    manifest = {"words": [], "revisions": []}
    sources = {}
    for index, name in enumerate(adapter.TARGET_NAMES, 1):
        word_id = f"target-{index}"
        body_hash = f"body-{index}"
        manifest["words"].append({"wordId": word_id, "currentName": name, "currentRevision": 1})
        manifest["revisions"].append({
            "wordId": word_id,
            "revision": 1,
            "definition": {"hash": body_hash},
            "maturity": "library",
            "tests": [{"hash": f"test-{index}"}],
        })
        sources[body_hash] = (
            f"fn {name}(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, "
            "at: Instant, expiry: Instant) -> Result<Store, BusinessError> { store }"
        )
    return manifest, sources


def check_target_binding() -> dict:
    payload = target_payload()
    boolean_version = deepcopy(payload)
    boolean_version["schemaVersion"] = True
    try:
        adapter.validate_target_record(boolean_version)
    except ValueError:
        pass
    else:
        raise AssertionError("Boolean true must not be accepted as schemaVersion 1")
    wrong = deepcopy(payload)
    wrong["targets"][0]["name"] = "subscription.renew-monthly-alias"
    try:
        adapter.validate_target_record(wrong)
    except ValueError:
        pass
    else:
        raise AssertionError("A misnamed target must be rejected")

    manifest, sources = synthetic_manifest()
    with TemporaryDirectory(dir=HERE) as temp:
        store = Path(temp)
        with patch.object(adapter.scorer, "project_fingerprint",
                          return_value=adapter.EXPECTED_PROJECT_FINGERPRINT), \
             patch.object(adapter.scorer, "current_manifest",
                          return_value=({"manifestHash": "synthetic-manifest"}, manifest, store)), \
             patch.object(adapter.scorer, "definition_source",
                          side_effect=lambda _store, revision: sources[revision["definition"]["hash"]]):
            bound = adapter.bind_targets(Path("synthetic-creator"), payload, {"words": []})
            assert [row["name"] for row in bound["targets"]] == list(adapter.TARGET_NAMES)
            assert all(row["maturity"] == "library" and row["ownTestHashes"] for row in bound["targets"])
    return {"targetSchemaAndBinding": "passed: exact names, accepted fingerprint, current source signature, library maturity, and own tests"}


def check_missing_production_path_fails() -> dict:
    binding = {
        "targetKind": adapter.TARGET_KIND,
        "acceptedProjectFingerprint": adapter.EXPECTED_PROJECT_FINGERPRINT,
        "manifestHash": "accepted-manifest",
        "targets": [
            {"name": name, "wordId": f"id-{index}", "revision": 1,
             "definitionHash": f"hash-{index}",
             "signature": {"inputs": list(adapter.scorer.CREATOR_SIGNATURE[0]),
                           "output": adapter.scorer.CREATOR_SIGNATURE[1]},
             "maturity": "library", "ownTestHashes": [f"test-{index}"]}
            for index, name in enumerate(adapter.TARGET_NAMES, 1)
        ],
    }
    actor_snapshot = {"targets": {
        row["name"]: {**row, "present": True} for row in binding["targets"]
    }}
    report = {
        "status": "passed", "behavior": {"caseCount": 9, "executedCaseCount": 9, "passedCaseCount": 9},
        "suite": {"testAllPassed": True, "exampleOwnerCount": 1, "examplesPassed": True},
        "libraryQualification": {}, "preservation": {}, "actorProjectUnchangedDuringScore": True,
        "discovery": {"status": "scored", "discoveredHelpers": list(adapter.TARGET_NAMES),
                      "metadataInspectedHelpers": []},
        "helperReuse": {"productionPaths": {adapter.TARGET_NAMES[0]: ["subscription.renew-batch", adapter.TARGET_NAMES[0]]}},
        "evidenceDirectory": "internal-successor-score",
    }
    creator_report = deepcopy(report)
    creator_report["behavior"] = {"caseCount": 66, "executedCaseCount": 66, "passedCaseCount": 66}
    summary = adapter.assemble_summary(
        binding, 0, creator_report, 0, report, actor_snapshot, Path("internal-input.json")
    )
    assert not summary["vocabularyReuse"]["allTargetsHaveProductionPath"]
    assert summary["status"] == "score-failure"
    complete_paths_report = deepcopy(report)
    complete_paths_report["helperReuse"]["productionPaths"][adapter.TARGET_NAMES[1]] = [
        "subscription.renew-batch", adapter.TARGET_NAMES[1]
    ]
    nonzero_return_summary = adapter.assemble_summary(
        binding, 1, creator_report, 0, complete_paths_report, actor_snapshot,
        Path("internal-input.json")
    )
    assert nonzero_return_summary["vocabularyReuse"]["passed"]
    assert nonzero_return_summary["status"] == "score-failure"
    return {
        "missingProductionPath": "passed: discovery alone cannot pass the reuse endpoint",
        "nonzeroExitGate": "passed: a passed-looking report with a nonzero scorer return cannot pass the summary",
    }


def check_test_only_binding_is_not_production() -> dict:
    root_id, monthly_id = "batch-root", "monthly-target"
    manifest = {
        "words": [
            {"wordId": root_id, "currentName": adapter.scorer.BATCH_ROOT, "currentRevision": 1},
            {"wordId": monthly_id, "currentName": adapter.TARGET_NAMES[0], "currentRevision": 1},
        ],
        "revisions": [
            {"wordId": root_id, "revision": 1, "definition": {"hash": "root-body"},
             "callBindings": [{"source": {"kind": "word-test", "hash": "test-body"},
                               "target": {"kind": "userWord", "identity": monthly_id}}]},
            {"wordId": monthly_id, "currentName": adapter.TARGET_NAMES[0], "revision": 1,
             "definition": {"hash": "target-body"}, "callBindings": []},
        ],
    }
    graph = adapter.scorer.production_graph(manifest)
    assert adapter.scorer.path_to(graph, adapter.scorer.BATCH_ROOT, adapter.TARGET_NAMES[0]) is None
    return {"testOnlyCall": "passed: test bindings do not create production paths"}


def check_exact_discovery_and_packet() -> dict:
    events = [
        {"event": "exchange", "index": 1, "operation": "words",
         "request": {"canonical": json.dumps({"op": "words"})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "words",
                                                "data": [adapter.TARGET_NAMES[0]]})}},
        {"event": "exchange", "index": 2, "operation": "words",
         "request": {"canonical": json.dumps({"op": "words"})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "words",
                                                "data": [adapter.TARGET_NAMES[1] + "-draft"]})}},
        {"event": "exchange", "index": 3, "operation": "tests",
         "request": {"canonical": json.dumps({"op": "tests", "word": adapter.TARGET_NAMES[1]})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "tests", "data": ["basic"]})}},
    ]
    with TemporaryDirectory(dir=HERE) as temp:
        trace = Path(temp) / "trace.jsonl"
        trace.write_text("\n".join(json.dumps(row) for row in events) + "\n", encoding="utf-8")
        discovery = adapter.scorer.trace_discovery(trace, set(adapter.TARGET_NAMES))
    assert discovery["discoveredHelpers"] == [adapter.TARGET_NAMES[0]]
    assert discovery["metadataInspectedHelpers"] == [adapter.TARGET_NAMES[1]]
    source_packet = (REPO / "experiments/AgentLang.SubagentTrials/organic-reuse-210/preparation/successor-packet.md").read_bytes()
    target_packet = (HERE.parent / "preparation/successor-packet.md").read_bytes()
    assert source_packet == target_packet, "211 public packet must be byte-identical to the 210 successor packet"
    return {"exactNameDiscovery": "passed: only an exact successful inspection response counts",
            "publicPacket": "passed: byte-identical to organic-reuse-210 successor packet"}


def main() -> int:
    report = {
        "status": "unit-checks-passed; root runtime, controls, and launch review remain separate",
        "checks": {
            **check_target_binding(),
            **check_missing_production_path_fails(),
            **check_test_only_binding_is_not_production(),
            **check_exact_discovery_and_packet(),
        },
    }
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
