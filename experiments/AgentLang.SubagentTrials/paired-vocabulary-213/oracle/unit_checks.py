"""Pure fixture checks for paired-vocabulary-213 acceptance gates."""

from __future__ import annotations

from copy import deepcopy
import json

import score_batch as adapter


def passing_case_results(count: int = adapter.CASE_COUNT) -> list[dict]:
    return [
        {"caseId": f"case-{index}", "executed": True, "passed": True}
        for index in range(count)
    ]


def passing_decision(**changes) -> dict:
    facts = {
        "condition": "reset",
        "exit_code": 0,
        "response_count": 14,
        "expected_response_count": 14,
        "case_results": passing_case_results(),
        "observation_setup_passed": True,
        "suite_passed": True,
        "examples_passed": True,
        "batch_root_passed": True,
        "baseline_binding_passed": True,
        "seed_preservation_passed": True,
        "baseline_preservation_passed": True,
        "actor_unchanged": True,
    }
    facts.update(changes)
    return adapter.evaluate_acceptance(**facts)


def test_nonzero_exit_is_rejected() -> None:
    result = passing_decision(exit_code=1)
    assert result["status"] == "score-failure"
    assert not result["gates"]["cliExitZero"]


def test_truncated_nine_case_batch_is_rejected() -> None:
    # Eight returned evals cannot satisfy the nine-case contract, even when all
    # eight values are valid and match their frozen expectations.
    result = passing_decision(
        response_count=13,
        case_results=passing_case_results(8),
    )
    assert result["status"] == "score-failure"
    assert not result["gates"]["responseCountExact"]
    assert not result["gates"]["allNineCasesExecuted"]
    assert result["caseCounts"] == {"expected": 9, "executed": 8, "passed": 8}


def test_missing_eval_response_is_setup_failure() -> None:
    item = {"id": "missing-case"}
    result = adapter.safe_score_eval(item, {})
    assert result["executed"] is False
    assert result["passed"] is False
    assert "setupError" in result
    assert passing_decision(case_results=passing_case_results(8) + [result])["status"] == "score-failure"


def test_malformed_structured_observation_is_setup_failure() -> None:
    item = {"id": "malformed-case"}
    response = {"ok": True, "kind": "eval", "data": {"structuredStack": "malformed"}}
    result = adapter.safe_score_eval(item, response)
    assert result["executed"] is False
    assert result["passed"] is False
    assert result["setupError"].startswith("Could not project structured eval response:")


def test_batch_root_requires_exact_signature_library_and_own_tests() -> None:
    good = adapter.batch_root_contract(adapter.scorer.BATCH_SIGNATURE, "library", 1)
    wrong_signature = adapter.batch_root_contract(
        (("Store", "List<SubscriptionRenewal>"), "Result<Store,BusinessError>"), "library", 1
    )
    assert good["passed"]
    assert not wrong_signature["signaturePassed"]
    assert not wrong_signature["passed"]
    assert passing_decision(batch_root_passed=wrong_signature["passed"])["status"] == "score-failure"
    assert not adapter.batch_root_contract(adapter.scorer.BATCH_SIGNATURE, "project", 1)["passed"]
    assert not adapter.batch_root_contract(adapter.scorer.BATCH_SIGNATURE, "library", 0)["passed"]


def test_seed_and_arm_baseline_preservation_are_independent() -> None:
    seed_word = {"wordId": "seed-id", "revision": 1, "definitionHash": "seed-body"}
    retained_word = {"wordId": "retained-id", "revision": 1, "definitionHash": "retained-body"}
    seed = {"types": {"SeedType": "seed-type"}, "words": {"seed.word": seed_word}}
    baseline = {
        "types": {"SeedType": "seed-type", "RetainedType": "retained-type"},
        "words": {"seed.word": seed_word, "retained.word": retained_word},
    }
    actor = {
        "types": {"SeedType": "seed-type", "RetainedType": "retained-type", "ExtraType": "extra"},
        "words": {
            "seed.word": seed_word,
            "retained.word": retained_word,
            "new.word": {"wordId": "new-id", "revision": 1, "definitionHash": "new-body"},
        },
    }
    assert adapter.scorer.preservation_diff(seed, actor)["passed"]
    assert adapter.scorer.preservation_diff(baseline, actor)["passed"]

    seed_changed = deepcopy(actor)
    seed_changed["words"]["seed.word"]["definitionHash"] = "changed-seed"
    assert not adapter.scorer.preservation_diff(seed, seed_changed)["passed"]

    baseline_changed = deepcopy(actor)
    baseline_changed["words"]["retained.word"]["definitionHash"] = "changed-retained"
    assert adapter.scorer.preservation_diff(seed, baseline_changed)["passed"]
    assert not adapter.scorer.preservation_diff(baseline, baseline_changed)["passed"]


def test_metadata_only_additions_do_not_need_library_maturity_or_tests() -> None:
    baseline = {
        "types": {"SeedType": "seed-type"},
        "words": {"seed.word": {"wordId": "seed-id", "revision": 1, "definitionHash": "seed-body"}},
    }
    actor = deepcopy(baseline)
    actor["types"]["AdditionalType"] = "additional-type"
    actor["words"]["metadata-only.word"] = {
        "wordId": "metadata-only-id",
        "revision": 1,
        "definitionHash": "metadata-only-body",
        "maturity": "project",
        "tests": [],
    }
    assert adapter.scorer.preservation_diff(baseline, actor)["passed"]
    assert passing_decision()["status"] == "passed"


def test_actor_mutation_is_rejected() -> None:
    result = passing_decision(actor_unchanged=False)
    assert result["status"] == "score-failure"
    assert not result["gates"]["actorProjectUnchanged"]


def test_reset_does_not_require_secondary_renewal_targets() -> None:
    # This reset-arm fixture has the required batch root but no monthly/annual
    # renewal functions; those names are secondary observations only.
    result = passing_decision(condition="reset")
    assert result["status"] == "passed"
    assert "secondaryTargetObservations" not in result


def test_suite_reports_common_seed_and_total_counts_separately() -> None:
    actor_manifest = {
        "words": [
            {"wordId": "seed", "currentName": "seed.word", "currentRevision": 1},
            {"wordId": "extra", "currentName": "metadata-only.word", "currentRevision": 1},
        ],
        "revisions": [
            {"wordId": "seed", "revision": 1, "tests": [{"hash": "seed-test"}], "examples": []},
            {"wordId": "extra", "revision": 1, "tests": [], "examples": []},
        ],
    }
    seed_manifest = {
        "words": [{"wordId": "seed", "currentName": "seed.word", "currentRevision": 1}],
        "revisions": [{"wordId": "seed", "revision": 1, "tests": [{"hash": "seed-test"}], "examples": []}],
    }
    response = {
        "ok": True,
        "kind": "test",
        "text": "1/1 test(s) passed.",
        "data": {"results": [{"word": "seed.word", "passed": True}]},
    }
    suite = adapter.score_suite(response, [], [], actor_manifest, seed_manifest,
                                {"words": {"seed.word": {}}})
    assert suite["testAllPassed"]
    assert suite["commonSeedTestCount"] == 1
    assert suite["totalTestCount"] == 1
    assert suite["commonSeedExpectedTestCount"] == 1
    assert suite["totalExpectedTestCount"] == 1


def test_secondary_trace_and_target_paths_do_not_change_acceptance() -> None:
    result = passing_decision()
    assert result["status"] == "passed"
    assert "secondaryTargetObservations" not in result
    assert json.loads(json.dumps(result))["passed"] is True


def main() -> int:
    checks = [
        test_nonzero_exit_is_rejected,
        test_truncated_nine_case_batch_is_rejected,
        test_missing_eval_response_is_setup_failure,
        test_malformed_structured_observation_is_setup_failure,
        test_batch_root_requires_exact_signature_library_and_own_tests,
        test_seed_and_arm_baseline_preservation_are_independent,
        test_metadata_only_additions_do_not_need_library_maturity_or_tests,
        test_actor_mutation_is_rejected,
        test_reset_does_not_require_secondary_renewal_targets,
        test_suite_reports_common_seed_and_total_counts_separately,
        test_secondary_trace_and_target_paths_do_not_change_acceptance,
    ]
    for check in checks:
        check()
    report = {
        "status": "unit-checks-passed; runtime scoring and arm controls remain separate",
        "checkCount": len(checks),
        "checks": [check.__name__ for check in checks],
    }
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
