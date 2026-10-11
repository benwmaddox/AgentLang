"""Small deterministic checks for the organic-reuse-200 model and scorer."""

from __future__ import annotations

from copy import deepcopy
import hashlib
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import model
import control_preflight as controls
import score_organic_reuse as scorer


def binding(source_kind: str, source_hash: str, target: str) -> dict:
    return {
        "source": {"kind": source_kind, "hash": source_hash},
        "target": {"kind": "userWord", "identity": target},
    }


def assert_export_matches(exported, generated, label: str) -> None:
    if exported != generated:
        raise ValueError(f"{label} differs from the model-generated audit export")


def revision(word_id: str, body_hash: str, calls: list[dict], revision_number: int = 1) -> dict:
    return {
        "wordId": word_id,
        "revision": revision_number,
        "definition": {"hash": body_hash},
        "maturity": "library",
        "callBindings": calls,
        "tests": [{"hash": f"test-{word_id}"}],
        "examples": [{"hash": f"example-{word_id}"}],
    }


def synthetic_project(root_calls: tuple[list[dict], list[dict]], store: Path) -> dict:
    monthly_calls, annual_calls = root_calls
    definitions = {
        "monthly": "fn subscription.renew-monthly(store: Store) -> Store {\n    store\n}\n",
        "annual": "fn subscription.renew-annual(store: Store) -> Store {\n    store\n}\n",
        "production": "fn subscription.shared-production-step(store: Store) -> Store {\n    store\n}\n",
        "metadata": "fn subscription.metadata-only-step(store: Store) -> Store {\n    store\n}\n",
        "seed-id-collision": "fn subscription.seed-id-collision(store: Store) -> Store {\n    store\n}\n",
        "creator-signature": "fn subscription.creator-signature-shadow(store: Store, old-id: SubscriptionId, new-id: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError> {\n    result.error<Store, BusinessError>(businessError.new(code = \"x\", message = \"x\"))\n}\n",
        "successor-signature": "fn subscription.successor-signature-shadow(store: Store, changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store, BusinessError> {\n    result.error<Store, BusinessError>(businessError.new(code = \"x\", message = \"x\"))\n}\n",
        "creator-signature-format": "fn subscription.creator-signature-format-shadow(\n    store: Store,\n    old-id: SubscriptionId,\n    new-id: SubscriptionId,\n    at: Instant,\n    expiry: Instant\n) -> Result<Store,  BusinessError>\n{\n    store\n}\n",
        "successor-signature-format": "fn subscription.successor-signature-format-shadow(store: Store,\n    changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store ,BusinessError>\n{\n    store\n}\n",
        "noncurrent-current": "fn subscription.noncurrent-revision-step(store: Store) -> Store {\n    store\n}\n",
        "noncurrent-stale": "fn subscription.noncurrent-revision-step(store: Store) -> Store {\n    store\n}\n",
    }
    hashes = {name: f"body-{name}" for name in definitions}
    objects = store / "objects"
    objects.mkdir(parents=True, exist_ok=True)
    for name, source in definitions.items():
        (objects / f"{hashes[name]}.agent").write_text(source, encoding="utf-8")

    words = [
        {"wordId": "root-monthly", "currentName": "subscription.renew-monthly", "currentRevision": 1},
        {"wordId": "root-annual", "currentName": "subscription.renew-annual", "currentRevision": 1},
        {"wordId": "new-production", "currentName": "subscription.shared-production-step", "currentRevision": 1},
        {"wordId": "new-metadata", "currentName": "subscription.metadata-only-step", "currentRevision": 1},
        {"wordId": "seed-word-id", "currentName": "subscription.seed-id-collision", "currentRevision": 1},
        {"wordId": "creator-signature-shadow", "currentName": "subscription.creator-signature-shadow", "currentRevision": 1},
        {"wordId": "successor-signature-shadow", "currentName": "subscription.successor-signature-shadow", "currentRevision": 1},
        {"wordId": "creator-signature-format-shadow", "currentName": "subscription.creator-signature-format-shadow", "currentRevision": 1},
        {"wordId": "successor-signature-format-shadow", "currentName": "subscription.successor-signature-format-shadow", "currentRevision": 1},
        {"wordId": "noncurrent-helper", "currentName": "subscription.noncurrent-revision-step", "currentRevision": 2},
    ]
    revisions = [
        revision("root-monthly", hashes["monthly"], monthly_calls),
        revision("root-annual", hashes["annual"], annual_calls),
        revision("new-production", hashes["production"], []),
        revision("new-metadata", hashes["metadata"], []),
        revision("seed-word-id", hashes["seed-id-collision"], []),
        revision("creator-signature-shadow", hashes["creator-signature"], []),
        revision("successor-signature-shadow", hashes["successor-signature"], []),
        revision("creator-signature-format-shadow", hashes["creator-signature-format"], []),
        revision("successor-signature-format-shadow", hashes["successor-signature-format"], []),
        revision("noncurrent-helper", hashes["noncurrent-stale"], [
            binding("word-definition", "body-monthly", "noncurrent-helper"),
            binding("word-definition", "body-annual", "noncurrent-helper"),
        ], revision_number=1),
        revision("noncurrent-helper", hashes["noncurrent-current"], [], revision_number=2),
    ]
    return {"words": words, "revisions": revisions}


def test_production_graph_and_helper_outcome() -> dict:
    metadata_calls = [
        binding("word-test", "test-root", "new-production"),
        binding("word-example", "example-root", "new-production"),
        binding("observer-definition", "observer-root", "new-production"),
        # Even a definition binding must match the current body hash.
        binding("word-definition", "stale-body-hash", "new-production"),
        binding("word-test", "test-root", "new-metadata"),
    ]
    production_calls = [
        binding("word-definition", "body-monthly", target)
        for target in ("new-production", "creator-signature-shadow", "successor-signature-shadow",
                       "creator-signature-format-shadow", "successor-signature-format-shadow")
    ]
    production_annual = [
        binding("word-definition", "body-annual", target)
        for target in ("new-production", "creator-signature-shadow", "successor-signature-shadow",
                       "creator-signature-format-shadow", "successor-signature-format-shadow")
    ]
    with TemporaryDirectory(dir=HERE) as temp:
        store = Path(temp)
        manifest = synthetic_project(
            ([*production_calls, *metadata_calls], [*production_annual, *metadata_calls]),
            store,
        )
        graph = scorer.production_graph(manifest)
        expected_edges = {
            "subscription.shared-production-step",
            "subscription.creator-signature-shadow",
            "subscription.successor-signature-shadow",
            "subscription.creator-signature-format-shadow",
            "subscription.successor-signature-format-shadow",
        }
        assert graph["subscription.renew-monthly"] == expected_edges
        assert graph["subscription.renew-annual"] == expected_edges
        assert scorer.path_to(graph, "subscription.renew-monthly", "subscription.metadata-only-step") is None
        assert scorer.path_to(graph, "subscription.renew-monthly", "subscription.noncurrent-revision-step") is None

        with patch.object(scorer, "current_manifest", return_value=({}, manifest, store)):
            result = scorer.find_eligible_helpers(
                Path(temp), manifest, {"words": [{"name": "old-name", "wordId": "seed-word-id"}]}
            )
            no_helper_manifest = synthetic_project(
                (metadata_calls, metadata_calls), store
            )
            no_helper_graph = scorer.production_graph(no_helper_manifest)
            assert scorer.path_to(no_helper_graph, "subscription.renew-monthly", "subscription.shared-production-step") is None
            with patch.object(scorer, "current_manifest", return_value=({}, no_helper_manifest, store)):
                absent = scorer.find_eligible_helpers(
                    Path(temp), no_helper_manifest, {"words": []}
                )

    format_creator = scorer.flow_definition_signature(
        "fn sample.creator(store: Store, old: SubscriptionId, new: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store,  BusinessError> { store }"
    )
    format_successor = scorer.flow_definition_signature(
        "fn sample.successor(store: Store, changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store ,BusinessError>\n{ store }"
    )
    assert format_creator == scorer.CREATOR_SIGNATURE
    assert format_successor == scorer.BATCH_SIGNATURE
    assert [row["name"] for row in result["eligibleHelpers"]] == ["subscription.shared-production-step"]
    assert result["successorDispatchAllowedByProvenance"] is True
    helper_paths = result["eligibleHelpers"][0]["productionPaths"]
    assert set(helper_paths) == {"subscription.renew-monthly", "subscription.renew-annual"}
    assert all(path[0] == root and path[-1] == "subscription.shared-production-step"
               for root, path in helper_paths.items())
    assert absent["creationOutcome"] == "no-eligible-helper"
    assert absent["eligibleHelpers"] == []
    assert absent["successorDispatchAllowedByProvenance"] is False
    return {
        "productionDependencyFilter": "passed: only current word-definition bindings count; tests, examples, observers, stale hashes, and noncurrent revisions are excluded",
        "taskSignatureExclusion": "passed: creator and held-out batch signatures remain ineligible across whitespace and multiline formatting",
        "eligibleHelper": result["eligibleHelpers"][0]["name"],
        "noHelperOutcome": absent["creationOutcome"],
    }


def test_body_preservation_is_separate() -> dict:
    baseline = {
        "types": {},
        "words": {
            "inherited": {
                "wordId": "seed-word",
                "revision": 1,
                "definitionHash": "body-before",
                "maturity": "library",
                "tests": ["test-before"],
                "examples": ["example-before"],
            }
        },
    }
    changed = deepcopy(baseline)
    changed["words"]["inherited"]["definitionHash"] = "body-after"
    preservation = scorer.preservation_diff(baseline, changed)
    assert not preservation["passed"]
    assert preservation["changedInheritedWords"]["inherited"]["actual"]["definitionHash"] == "body-after"
    # The model behavior oracle is an independent result, not a preservation proxy.
    assert len(model.creator_cases()) == 66
    return {"inheritedDefinitionBodyCheck": "passed", "behaviorCasesRemainSeparate": 66}


def test_typed_cases() -> dict:
    frozen = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    assert frozen == model.make_cases()
    creator = scorer.load_frozen_cases("creator")
    batch = scorer.load_frozen_cases("successor")
    all_items = [(item, "creator") for item in creator] + [(item, "successor") for item in batch]
    for item, stage in all_items:
        expression = scorer.observation_expression(item, stage)
        projection = scorer.expected_projection(item)
        assert "organicReuse200Observation.new" in expression
        assert projection["input"] == scorer.OLD.normalized_projection(item)
        if item["expected"]["tag"] == "error":
            assert projection["tag"] == "error" and "store" not in projection
        else:
            assert projection["tag"] == "ok" and "store" in projection
        if stage == "successor":
            for change in item["request"]["changes"]:
                assert f"SubscriptionPlan.{change['plan']}()" in expression
    assert sum(item["fixedTerm"] == "monthly" for item in creator) == 33
    assert sum(item["fixedTerm"] == "annual" for item in creator) == 33
    return {"typedObservationExpressions": len(all_items), "errorPayloadHasNoStore": "passed"}


def test_observer_discriminator_and_trace_discovery() -> dict:
    assert scorer.is_successful_define_response({"ok": True, "kind": "defined"})
    assert not scorer.is_successful_define_response({"ok": True, "kind": "define"})
    assert not scorer.is_successful_define_response({"ok": False, "kind": "defined"})
    events = [
        {"event": "exchange", "index": 1, "operation": "words",
         "request": {"canonical": "{}"},
         "response": {"canonical": json.dumps({"ok": True, "kind": "words", "data": ["subscription.renew-batch"]})}},
        {"event": "exchange", "index": 2, "operation": "dependencies",
         "request": {"canonical": "{}"},
         "response": {"canonical": json.dumps({"ok": True, "kind": "dependencies", "data": {"dependencies": [{"name": "subscription.helper"}]}})}},
        {"event": "exchange", "index": 3, "operation": "search",
         "request": {"canonical": "{}"},
         "response": {"canonical": json.dumps({"ok": False, "kind": "error", "data": ["subscription.failed-helper"]})}},
        {"event": "exchange", "index": 4, "operation": "eval",
         "request": {"canonical": "{}"},
         "response": {"canonical": json.dumps({"ok": True, "kind": "eval", "data": ["subscription.only-eval"]})}},
        {"event": "exchange", "index": 5, "operation": "examples",
         "request": {"canonical": json.dumps({"op": "examples", "word": "renew"})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "examples", "data": ["basic"]})}},
        {"event": "exchange", "index": 6, "operation": "tests",
         "request": {"canonical": json.dumps({"op": "tests", "word": "subscription.test-helper"})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "tests", "data": ["basic"]})}},
        {"event": "exchange", "index": 7, "operation": "examples",
         "request": {"canonical": json.dumps({"op": "examples", "word": "subscription.failed-helper"})},
         "response": {"canonical": json.dumps({"ok": False, "kind": "error", "data": ["basic"]})}},
        {"event": "exchange", "index": 8, "operation": "words",
         "request": {"canonical": json.dumps({"op": "words"})},
         "response": {"canonical": json.dumps({"ok": True, "kind": "words", "data": ["renew"]})}},
    ]
    eligible = {
        "subscription.renew", "subscription.helper",
        "subscription.failed-helper", "subscription.only-eval",
        "renew", "subscription.test-helper",
    }
    with TemporaryDirectory(dir=HERE) as temp:
        trace = Path(temp) / "synthetic-trace.jsonl"
        trace.write_text("\n".join(json.dumps(row) for row in events) + "\n", encoding="utf-8")
        result = scorer.trace_discovery(trace, eligible)
    assert result["discoveredHelpers"] == ["renew", "subscription.helper"]
    assert result["metadataInspectedHelpers"] == ["renew", "subscription.test-helper"]
    assert [hit["line"] for hit in result["inspectionHits"]] == [2, 5, 6, 8]
    return {
        "successfulDefineDiscriminator": "passed: only ok=true and kind=defined qualifies",
        "traceDiscovery": "passed: returned names are exact (including undotted helpers); tests/examples target metadata is recorded separately from discovery; prefix collisions and failed inspections are rejected",
    }


def test_control_sources_and_request_builders() -> dict:
    creator_correct = controls.creator_source()
    creator_order = controls.creator_source("start-before-cancel")
    creator_partial = controls.creator_source("wrong-ok")
    successor_correct = controls.successor_source()
    successor_partial = controls.successor_source("partial-ok")
    successor_reverse = controls.successor_source("reverse-order")
    assert '"monthly"' in creator_correct and '"annual"' in creator_correct
    assert creator_correct.index(".subscription.cancel(store") < creator_correct.index(".subscription.start(cancelled-store")
    assert creator_order.index(".subscription.start(store") < creator_order.index(".subscription.cancel(started-store")
    assert "error start-error => result.ok<Store, BusinessError>(cancelled-store)" in creator_partial
    assert "field plan: SubscriptionPlan" in successor_correct
    assert "field outcome: Result<Store, BusinessError>" in successor_correct
    assert "field at: Instant" in successor_correct
    assert "changes.fold(initial, subscription.renew-batch-step)" in successor_correct
    assert "changes.fold(list.empty<SubscriptionRenewal>(), subscription.reverse-renewal-step)" in successor_reverse
    assert "result.ok<Store, BusinessError>(current)" in successor_partial

    creator_requests = controls.requests_for_variant("creator-correct", ["subscription.start"])
    successor_requests = controls.requests_for_variant("successor-correct", [])
    assert creator_requests[0] == {"op": "test-all"}
    assert creator_requests[1] == {"op": "example", "word": "subscription.start"}
    assert [row["op"] for row in creator_requests].count("define") == 2
    assert sum(row["op"] == "eval" for row in creator_requests) == 66
    assert sum(row["op"] == "eval" for row in successor_requests) == 9
    assert all(row.get("syntaxVersion") == 2 for row in creator_requests if row["op"] in {"define", "eval"})
    with patch.object(scorer.subprocess, "run", return_value=__import__("subprocess").CompletedProcess([], 0, "", "")) as run:
        scorer.run_cli(Path("AgentLang.Cli.dll"), Path("project"), [])
    command = run.call_args.args[0]
    assert command[4:6] == ["--filesystem", "virtual"]
    assert "--allow" not in command and "--test-allow" not in command
    return {
        "controlSources": "passed: fixed-term monthly/annual, start-before-cancel, wrong-OK, typed batch, partial-OK, and reverse-order variants",
        "requestBuilders": {"creatorEvaluations": 66, "successorEvaluations": 9, "observationAndControlDefinitions": 2},
        "executionMode": "passed: filesystem virtual with empty capabilities; no CLI process launched",
    }


def main() -> int:
    frozen_case_path = HERE / "cases.json"
    frozen_check_path = HERE / "model-checks.json"
    before_hashes = {
        str(path.name): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in (frozen_case_path, frozen_check_path)
    }
    frozen_cases = json.loads(frozen_case_path.read_text(encoding="utf-8"))
    frozen_checks = json.loads(frozen_check_path.read_text(encoding="utf-8"))
    generated_cases = model.make_cases()
    generated_checks = model.model_checks()
    assert_export_matches(frozen_cases, generated_cases, "cases.json")
    assert_export_matches(frozen_checks, generated_checks, "model-checks.json")
    stale_cases = deepcopy(frozen_cases)
    stale_cases["creator"][0]["expected"]["tag"] = "stale-mismatch"
    try:
        assert_export_matches(stale_cases, generated_cases, "cases.json")
    except ValueError:
        pass
    else:
        raise AssertionError("A changed frozen case export must be rejected before scoring")
    inventory, projection = scorer.validate_seed_inventory(scorer.DEFAULT_SEED_PROJECT)
    assert len(inventory["words"]) == len(projection["words"]) == 47
    assert len(inventory["types"]) == len(projection["types"]) == 29
    report = {
        "status": "unit-checks-passed; root pinned-CLI controls reported complete; final source/runtime/control review remains",
        "model": {"caseCount": {stage: len(rows) for stage, rows in frozen_cases.items()},
                  "checks": frozen_checks},
        "frozenExports": {"matchedGeneratedModel": True, "unchangedSha256": before_hashes,
                          "mismatchFixtureRejected": True},
        "seedInventory": {"words": len(inventory["words"]), "types": len(inventory["types"])},
        "typedCases": test_typed_cases(),
        "productionGraph": test_production_graph_and_helper_outcome(),
        "preservation": test_body_preservation_is_separate(),
        "observerAndDiscovery": test_observer_discriminator_and_trace_discovery(),
        "controlArtifacts": test_control_sources_and_request_builders(),
    }
    after_hashes = {
        str(path.name): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in (frozen_case_path, frozen_check_path)
    }
    assert before_hashes == after_hashes, "unit checks must not rewrite frozen model exports"
    report["frozenExports"] = {"matchedGeneratedModel": True, "unchangedSha256": before_hashes,
                               "mismatchFixtureRejected": True}
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
