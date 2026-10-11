"""Pure adapter checks for typed-batch-failure-216; no CLI is started."""

from __future__ import annotations

from copy import deepcopy
from pathlib import Path
from tempfile import TemporaryDirectory

import model
import score_failure as adapter


def _structured(value):
    if isinstance(value, dict):
        return {"kind": "record", "fields": [
            {"name": key, "value": _structured(child)} for key, child in value.items()
        ]}
    if isinstance(value, list):
        return {"kind": "list", "items": [_structured(child) for child in value]}
    if value is None:
        return {"kind": "option", "case": "none"}
    if isinstance(value, bool):
        return {"kind": "bool", "value": value}
    if isinstance(value, int):
        return {"kind": "int", "value": str(value)}
    return {"kind": "string", "value": value}


def _typed_response(item: dict, *, omit_failure_field: str | None = None,
                    changed_message: bool = False, wrong_index_type: bool = False,
                    wrong_nominal_type: bool = False, wrong_old_id_type: bool = False) -> dict:
    expected = adapter.expected_projection(item)
    store_type = {"kind": "nominal", "name": "Store", "nominalKind": "record", "typeKey": 1}
    failure_name = "OtherFailure" if wrong_nominal_type else adapter.FAILURE_TYPE
    failure_type = {"kind": "nominal", "name": failure_name, "nominalKind": "record", "typeKey": 2}
    result_type = {"kind": "result", "okType": store_type, "errorType": failure_type}
    input_node = {
        "kind": "record", "name": "Store", "typeKey": 1,
        "fields": [{"name": key, "value": _structured(value)} for key, value in expected["input"].items()],
    }
    observation_fields = [{"name": "input", "type": store_type, "value": input_node}]
    if expected["tag"] == "ok":
        output_node = {
            "kind": "record", "name": "Store", "typeKey": 1,
            "fields": [{"name": key, "value": _structured(value)} for key, value in expected["store"].items()],
        }
        outcome = {"kind": "result", "okType": store_type, "errorType": failure_type,
                   "case": "ok", "value": output_node}
    else:
        failure = deepcopy(expected["failure"])
        if omit_failure_field:
            failure.pop(omit_failure_field)
        if changed_message:
            failure["cause"]["message"] += " (changed)"
        failure_fields = []
        if "index" in failure:
            failure_fields.append({
                "name": "index", "type": {"kind": "int"},
                "value": {"kind": "string", "value": str(failure["index"])}
                if wrong_index_type else {"kind": "int", "value": str(failure["index"])},
            })
        if "old-id" in failure:
            old_id_name = "AnotherId" if wrong_old_id_type else "SubscriptionId"
            failure_fields.append({
                "name": "old-id",
                "type": {"kind": "nominal", "name": old_id_name, "nominalKind": "scalar", "typeKey": 3},
                "value": {"kind": "scalar", "name": old_id_name, "typeKey": 3,
                          "baseType": {"kind": "string"},
                          "value": {"kind": "string", "value": failure["old-id"]}},
            })
        if "cause" in failure:
            cause_node = {
                "kind": "record", "name": "BusinessError", "typeKey": 4,
                "fields": [
                    {"name": key, "type": {"kind": "string"},
                     "value": {"kind": "string", "value": value}}
                    for key, value in failure["cause"].items()
                ],
            }
            failure_fields.append({
                "name": "cause",
                "type": {"kind": "nominal", "name": "BusinessError", "nominalKind": "record", "typeKey": 4},
                "value": cause_node,
            })
        failure_node = {"kind": "record", "name": failure_name, "typeKey": 2, "fields": failure_fields}
        outcome = {"kind": "result", "okType": store_type, "errorType": failure_type,
                   "case": "error", "error": failure_node}
    observation_fields.append({"name": "outcome", "type": result_type, "value": outcome})
    root = {"kind": "record", "name": "TypedBatchFailure216Observation", "typeKey": 5,
            "fields": observation_fields}
    return {"ok": True, "kind": "eval", "data": {"structuredStack": {"values": [root]}}}


def _passing_decision(**changes) -> dict:
    case_results = [{"caseId": str(index), "executed": True, "passed": True}
                    for index in range(adapter.CASE_COUNT)]
    suite = {"testAllPassed": True, "examplesPassed": True}
    batch_contract = {"passed": True}
    facts = {
        "condition": "reset",
        "cli_exit_code": 0,
        "response_count": 50,
        "expected_response_count": 50,
        "case_results": case_results,
        "creator_results": [],
        "suite": suite,
        "observer_defined": True,
        "batch_contract": batch_contract,
        "baseline_binding_passed": True,
        "baseline_seed_preservation_passed": True,
        "seed_preservation_passed": True,
        "baseline_preservation_passed": True,
        "actor_unchanged": True,
        "baseline_unchanged": True,
        "scoring_copy_unchanged": True,
    }
    facts.update(changes)
    return adapter.evaluate_acceptance(**facts)


def test_model_cases_preserve_inherited_inputs_and_controls() -> dict:
    cases = model.make_cases()
    assert len(cases) == model.CASE_COUNT == adapter.CASE_COUNT
    assert all(
        actual["store"] == inherited["store"]
        and actual["request"] == inherited["request"]
        for actual, inherited in zip(cases, model.PREDECESSOR.BATCH_CASES)
    )
    checks = model.model_checks()
    assert checks["correctControlPassed"] == 9
    assert checks["alwaysZeroIndex"]["predictedPassed"] == 6
    assert checks["lastErrorWins"]["predictedPassed"] == 8
    assert checks["changedMessageSameCode"]["predictedPassed"] == 4
    assert checks["causeSourceProvenance"]["expectedBusinessErrorMessagesVerified"]
    return checks


def test_failure_projection_keeps_index_id_and_exact_cause() -> dict:
    cases = model.make_cases()
    item = next(row for row in cases if row["id"] == "first-error-precedes-later-expiry")
    result = adapter.safe_score_typed_eval(item, _typed_response(item))
    assert result["executed"] and result["passed"]
    projected = adapter.validate_typed_observation_node(
        _typed_response(item)["data"]["structuredStack"]["values"][0]
    )
    assert projected["failure"] == adapter.expected_projection(item)["failure"]
    changed = adapter.safe_score_typed_eval(item, _typed_response(item, changed_message=True))
    assert changed["executed"] and not changed["passed"]
    assert changed["actual"]["failure"]["cause"]["code"] == changed["expected"]["failure"]["cause"]["code"]
    assert changed["actual"]["failure"]["cause"]["message"] != changed["expected"]["failure"]["cause"]["message"]
    return {"completeTypedFailureProjection": "passed", "sameCodeChangedMessageRejected": True}


def test_malformed_or_truncated_structured_results_fail_setup() -> dict:
    cases = model.make_cases()
    item = next(row for row in cases if row["expected"]["tag"] == "error")
    missing = adapter.safe_score_typed_eval(item, _typed_response(item, omit_failure_field="old-id"))
    wrong_index = adapter.safe_score_typed_eval(item, _typed_response(item, wrong_index_type=True))
    wrong_nominal = adapter.safe_score_typed_eval(item, _typed_response(item, wrong_nominal_type=True))
    wrong_old_id = adapter.safe_score_typed_eval(item, _typed_response(item, wrong_old_id_type=True))
    assert not missing["executed"] and "exactly index, old-id, and cause" in missing["setupError"]
    assert not wrong_index["executed"] and "is not Int" in wrong_index["setupError"]
    assert not wrong_nominal["executed"] and "SubscriptionRenewalFailure" in wrong_nominal["setupError"]
    assert not wrong_old_id["executed"] and "nominal SubscriptionId" in wrong_old_id["setupError"]
    truncated = adapter.safe_score_typed_eval(item, {})
    assert not truncated["executed"] and "setupError" in truncated
    decision = _passing_decision(
        response_count=49,
        case_results=[{"caseId": "only-eight", "executed": True, "passed": True}] * 8
                       + [truncated],
    )
    assert not decision["automatedPassed"]
    assert not decision["gates"]["responseCountExact"]
    assert not decision["gates"]["allNineCasesExecuted"]
    return {"missingField": "rejected", "wrongScalarType": "rejected",
            "wrongNominalType": "rejected", "wrongOldIdSubtype": "rejected",
            "truncatedResponse": "rejected"}


def test_nominal_failure_type_and_root_signature_are_exact() -> dict:
    good_source = """record SubscriptionRenewalFailure {
    field index: Int
    field old-id: SubscriptionId
    field cause: BusinessError
}
"""
    good = adapter.failure_type_contract(good_source)
    missing = adapter.failure_type_contract(good_source.replace("field old-id: SubscriptionId\n", ""))
    wrong_name = adapter.failure_type_contract(good_source.replace(
        "record SubscriptionRenewalFailure", "record OtherFailure"
    ))
    alias = adapter.failure_type_contract(good_source.replace("record SubscriptionRenewalFailure", "type SubscriptionRenewalFailure"))
    truncated = adapter.failure_type_contract(good_source.rsplit("}", 1)[0])
    assert good["passed"] and good["fields"] == [
        {"name": "index", "type": "Int"},
        {"name": "old-id", "type": "SubscriptionId"},
        {"name": "cause", "type": "BusinessError"},
    ]
    assert not missing["passed"] and not wrong_name["passed"]
    assert not alias["passed"] and not truncated["passed"]
    root_source = "fn subscription.renew-batch(store: Store, changes: List<SubscriptionRenewal>, at: Instant) -> Result<Store, SubscriptionRenewalFailure> { store }"
    wrong_root_source = root_source.replace("SubscriptionRenewalFailure", "BusinessError")
    assert adapter.scorer.flow_definition_signature(root_source) == adapter.BATCH_SIGNATURE
    assert adapter.scorer.flow_definition_signature(wrong_root_source) != adapter.BATCH_SIGNATURE
    return {"nominalRecord": "passed", "oldIdSubtype": "SubscriptionId", "wrongNominalAndMalformedTypes": "rejected"}


def test_baseline_fingerprint_and_test_identity_guards() -> dict:
    for condition, fingerprint in adapter.EXPECTED_BASELINE_FINGERPRINTS.items():
        assert adapter.validate_baseline_fingerprint(condition, fingerprint)["passed"]
        assert not adapter.validate_baseline_fingerprint(condition, "0" * 64)["passed"]
    extended = adapter.test_identity_diff(
        ["batch/first", "batch/empty"],
        ["batch/empty", "batch/first", "batch/first-error", "batch/later-item"],
    )
    assert extended["passed"]
    assert extended["added"] == ["batch/first-error", "batch/later-item"]
    lost = adapter.test_identity_diff(["batch/first", "batch/empty"], ["batch/first"])
    assert not lost["passed"] and lost["missing"] == ["batch/empty"]
    renamed = adapter.test_identity_diff(["batch/first"], ["batch/renamed"])
    assert not renamed["passed"] and renamed["missing"] == ["batch/first"]
    duplicate_reduced = adapter.test_identity_diff(
        ["batch/repeated", "batch/repeated"], ["batch/repeated"]
    )
    assert not duplicate_reduced["passed"] and duplicate_reduced["missing"] == ["batch/repeated"]
    assert not _passing_decision(actor_unchanged=False)["automatedPassed"]
    assert not _passing_decision(baseline_unchanged=False)["automatedPassed"]
    return {
        "baselineMutation": "rejected",
        "inheritedIdentityAndMultiplicityPreservedWithNewCases": "passed",
        "lostOrRenamedInheritedTestIdentity": "rejected",
        "reducedDuplicateIdentityCount": "rejected",
        "actorMutation": "rejected",
    }


def _synthetic_project(path: Path, *, candidate: bool, lose_test_id: bool = False,
                       mutate_unrelated: bool = False, mutate_other_type: bool = False,
                       mutate_example: str | None = None,
                       add_batch_example: str | None = None,
                       mutate_batch_example: str | None = None) -> tuple[dict, dict]:
    objects = path / ".agentlang" / "store" / "objects"
    objects.mkdir(parents=True, exist_ok=True)
    target = "subscription.renew-batch"
    step = "subscription.renew-batch-step"
    monthly = "subscription.renew-monthly"
    names = [target, step, monthly]
    words = []
    revisions = []
    source_rows = {}
    for index, name in enumerate(names):
        word_id = f"word-{name}"
        revision = 2 if candidate and name in {target, step} else 1
        definition = f"definition-{name}-{revision}"
        if mutate_unrelated and name == monthly:
            definition = "changed-monthly-definition"
        tests = []
        examples = []
        if name in {target, step}:
            # Synthetic inherited hashes exercise removals and multiplicity;
            # accepted 213 root and step currently have no examples.
            examples = (
                [{"hash": "root-example"}, {"hash": "root-example"}]
                if name == target else [{"hash": "step-example"}]
            )
            if candidate and add_batch_example == name:
                examples.append({"hash": f"added-{name}-example"})
            if candidate and name == target:
                if mutate_batch_example == "root-missing":
                    examples = []
                elif mutate_batch_example == "root-rewritten":
                    examples = [{"hash": "rewritten-root-example"}, {"hash": "root-example"}]
                elif mutate_batch_example == "root-duplicate-reduced":
                    examples = [{"hash": "root-example"}]
            identity = f"{name}/first-error"
            if candidate and lose_test_id and name == step:
                identity = f"{name}/replacement-test"
            test_hash = f"test-{name}-{revision}"
            tests = [{"hash": test_hash}]
            source_rows[test_hash] = f"test {identity} {{\n    => true\n}}\n"
        if name == monthly:
            examples = [{"hash": "monthly-example"}]
            if candidate and mutate_example == "missing":
                examples = []
            elif candidate and mutate_example == "rewritten":
                examples = [{"hash": "rewritten-monthly-example"}]
        words.append({"wordId": word_id, "currentName": name, "currentRevision": revision})
        revisions.append({
            "wordId": word_id,
            "revision": revision,
            "definition": {"hash": definition},
            "maturity": "library",
            "tests": tests,
            "examples": examples,
        })
    type_names = ["BusinessError", "SubscriptionRenewalState", "SubscriptionPlan", "SubscriptionRenewal"]
    types = []
    for name in type_names:
        definition = f"type-{name}"
        if candidate and name == "SubscriptionRenewalState":
            definition = "updated-state-type"
        if candidate and mutate_other_type and name == "BusinessError":
            definition = "changed-business-error-type"
        types.append({"name": name, "definition": {"hash": definition}})
    if candidate:
        types.append({"name": "SubscriptionRenewalFailure", "definition": {"hash": "new-failure-type"}})
        word_id = "word-subscription.new-helper"
        words.append({"wordId": word_id, "currentName": "subscription.new-helper", "currentRevision": 1})
        revisions.append({
            "wordId": word_id, "revision": 1,
            "definition": {"hash": "helper-definition"}, "maturity": "library",
            "tests": [], "examples": [{"hash": "new-helper-example"}],
        })
    for digest, source in source_rows.items():
        (objects / f"{digest}.agent").write_text(source, encoding="utf-8")
    manifest = {"words": words, "revisions": revisions, "types": types}
    projection = adapter.scorer.projection_for_manifest(manifest)
    return manifest, projection


def test_preservation_allows_only_batch_edits_and_arm_state_type() -> dict:
    # Windows sandbox temp roots can be outside the writable workspace. Keep
    # this short-lived synthetic project beside the check script instead.
    with TemporaryDirectory(dir=Path(__file__).resolve().parent) as temporary:
        root = Path(temporary)
        baseline_project = root / "baseline"
        candidate_project = root / "candidate"
        baseline_project.mkdir()
        candidate_project.mkdir()
        baseline_manifest, baseline_projection = _synthetic_project(baseline_project, candidate=False)
        candidate_manifest, candidate_projection = _synthetic_project(candidate_project, candidate=True)
        allowed = adapter.preservation_report(
            "reset", baseline_project, baseline_manifest, baseline_projection,
            candidate_project, candidate_manifest, candidate_projection,
        )
        assert allowed["passed"]
        assert allowed["inheritedTestIdentitiesPassed"]
        assert allowed["newWords"] == ["subscription.new-helper"]

        for target_name in ("subscription.renew-batch", "subscription.renew-batch-step"):
            added_manifest, added_projection = _synthetic_project(
                candidate_project, candidate=True, add_batch_example=target_name
            )
            added_example = adapter.preservation_report(
                "reset", baseline_project, baseline_manifest, baseline_projection,
                candidate_project, added_manifest, added_projection,
            )
            target_check = added_example["batchTargetChecks"][target_name]
            assert added_example["passed"]
            assert target_check["exampleHashesPreserved"]
            assert target_check["exampleHashDiff"]["added"]

        lost_manifest, lost_projection = _synthetic_project(
            candidate_project, candidate=True, lose_test_id=True
        )
        lost = adapter.preservation_report(
            "reset", baseline_project, baseline_manifest, baseline_projection,
            candidate_project, lost_manifest, lost_projection,
        )
        assert not lost["passed"] and not lost["inheritedTestIdentitiesPassed"]

        changed_manifest, changed_projection = _synthetic_project(
            candidate_project, candidate=True, mutate_unrelated=True
        )
        changed_word = adapter.preservation_report(
            "reset", baseline_project, baseline_manifest, baseline_projection,
            candidate_project, changed_manifest, changed_projection,
        )
        assert not changed_word["passed"]
        changed_type_manifest, changed_type_projection = _synthetic_project(
            candidate_project, candidate=True, mutate_other_type=True
        )
        changed_type = adapter.preservation_report(
            "reset", baseline_project, baseline_manifest, baseline_projection,
            candidate_project, changed_type_manifest, changed_type_projection,
        )
        assert not changed_type["passed"]
        for mutation in ("missing", "rewritten"):
            example_manifest, example_projection = _synthetic_project(
                candidate_project, candidate=True, mutate_example=mutation
            )
            changed_example = adapter.preservation_report(
                "reset", baseline_project, baseline_manifest, baseline_projection,
                candidate_project, example_manifest, example_projection,
            )
            assert not changed_example["passed"]
        for mutation in ("root-missing", "root-rewritten", "root-duplicate-reduced"):
            batch_example_manifest, batch_example_projection = _synthetic_project(
                candidate_project, candidate=True, mutate_batch_example=mutation
            )
            changed_batch_example = adapter.preservation_report(
                "reset", baseline_project, baseline_manifest, baseline_projection,
                candidate_project, batch_example_manifest, batch_example_projection,
            )
            target_check = changed_batch_example["batchTargetChecks"]["subscription.renew-batch"]
            assert not changed_batch_example["passed"]
            assert not target_check["exampleHashesPreserved"]
    return {
        "batchRootStepAndResetStateChanges": "allowed structurally",
        "batchRootAndStepExampleAdditions": "allowed with inherited hashes preserved",
        "batchInheritedExampleRemovalRewriteOrDuplicateReduction": "rejected",
        "relatedLibraryWithNewExample": "flagged for independent source review",
        "lostTestIdentity": "rejected",
        "unrelatedWordMutation": "rejected",
        "unrelatedTypeMutation": "rejected",
        "missingOrRewrittenBaselineExample": "rejected",
    }


def test_candidate_examples_include_additions_and_all_owner_responses() -> dict:
    baseline_examples = {"subscription.renew-batch": adapter.EXPECTED_EXAMPLE_COUNT}
    candidate_examples = {
        "subscription.renew-batch": adapter.EXPECTED_EXAMPLE_COUNT,
        "subscription.new-helper": 1,
    }
    example_responses = [
        {"ok": True, "kind": "example", "text": "1/1"},
        {"ok": True, "kind": "example", "text": "39/39"},
    ]
    empty_manifest = {"words": [], "revisions": [], "types": []}
    suite = adapter.score_suite(
        {"ok": True, "kind": "test", "text": "0/0", "data": {"results": []}},
        candidate_examples,
        example_responses,
        baseline_examples,
        empty_manifest,
        empty_manifest,
        {"words": {}},
    )
    assert suite["examplesPassed"]
    assert suite["baselineExampleCount"] == adapter.EXPECTED_EXAMPLE_COUNT
    assert suite["expectedExampleCount"] == adapter.EXPECTED_EXAMPLE_COUNT + 1
    assert suite["baselineOwnerCountsPreserved"]

    missing_owner = adapter.score_suite(
        {"ok": True, "kind": "test", "text": "0/0", "data": {"results": []}},
        candidate_examples,
        example_responses[:1],
        baseline_examples,
        empty_manifest,
        empty_manifest,
        {"words": {}},
    )
    assert not missing_owner["examplesPassed"]
    missing_baseline_example = adapter.score_suite(
        {"ok": True, "kind": "test", "text": "0/0", "data": {"results": []}},
        {"subscription.new-helper": 1, "subscription.renew-batch": 38},
        example_responses,
        baseline_examples,
        empty_manifest,
        empty_manifest,
        {"words": {}},
    )
    assert not missing_baseline_example["examplesPassed"]
    return {
        "newCurrentExampleScored": "passed",
        "baseline39ExamplesRetained": "passed",
        "missingCurrentOwnerResponse": "rejected",
        "missingBaselineExampleCount": "rejected",
    }


def test_request_counts_and_review_gate() -> dict:
    cases = model.make_cases()
    examples = {f"subscription.example-{index:02d}": 1 for index in range(adapter.EXPECTED_EXAMPLE_COUNT)}
    reset = adapter.build_requests(cases, examples, "reset", [])
    retained_creator = adapter.scorer.load_frozen_cases("creator")
    retained = adapter.build_requests(cases, examples, "retained", retained_creator)
    assert len(reset) == 50
    assert len(retained) == 116
    assert sum(row.get("op") == "eval" for row in reset) == 9
    assert sum(row.get("op") == "eval" for row in retained) == 75
    result = _passing_decision()
    assert result["automatedPassed"]
    assert result["status"] == "behavior-passed-review-required"
    assert result["passed"] is False and result["acceptedEditPassed"] is False
    assert result["independentSourceReview"]["status"] == "required"
    return {"resetResponses": len(reset), "retainedResponses": len(retained), "independentSourceReview": "required"}


def main() -> int:
    checks = [
        test_model_cases_preserve_inherited_inputs_and_controls,
        test_failure_projection_keeps_index_id_and_exact_cause,
        test_malformed_or_truncated_structured_results_fail_setup,
        test_nominal_failure_type_and_root_signature_are_exact,
        test_baseline_fingerprint_and_test_identity_guards,
        test_preservation_allows_only_batch_edits_and_arm_state_type,
        test_candidate_examples_include_additions_and_all_owner_responses,
        test_request_counts_and_review_gate,
    ]
    results = {check.__name__: check() for check in checks}
    import json

    print(json.dumps({
        "status": "unit-checks-passed; runtime scoring and independent source review remain separate",
        "checkCount": len(checks),
        "checks": results,
    }, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
