#!/usr/bin/env python3
"""Run pure positive/negative controls for the 230 F# preservation audit.

Usage from the repository root::

    python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/preservation_checks.py

No candidate F# code is compiled or executed. Each invocation copies only the
six seed source files into a new calibration run directory, mutates disposable
copies for negative controls, and saves the audit reports there.
"""

from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import sys


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[4]
CALIBRATION = REPOSITORY / ".agentlang" / "replacement-trial-230" / "calibration" / "f-preservation"
CORRECT = REPOSITORY / ".agentlang" / "replacement-trial-230" / "calibration" / "fsharp" / "correct"
SWAPPED = REPOSITORY / ".agentlang" / "replacement-trial-230" / "calibration" / "swapped-timing"
sys.path.insert(0, str(HERE))
import preservation  # noqa: E402  (local sibling, standard library only)


def sha256(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def source_hashes(directory: Path) -> dict[str, str]:
    return {name: sha256((directory / name).read_bytes()) for name in preservation.REQUIRED_FILES}


def copy_seed(source: Path, destination: Path) -> None:
    if destination.exists() or destination.is_symlink():
        raise RuntimeError(f"Refusing to overwrite control source copy: {destination}")
    destination.mkdir(parents=True)
    for name in preservation.REQUIRED_FILES:
        shutil.copyfile(source / name, destination / name)


def save_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="")


def expect(name: str, candidate: Path, expectation: str, run_dir: Path) -> dict[str, object]:
    before = source_hashes(candidate)
    report = preservation.audit(candidate)
    after = source_hashes(candidate)
    unchanged = before == after
    accepted = report["status"] == expectation and unchanged
    entry = {
        "name": name,
        "expectedStatus": expectation,
        "actualStatus": report["status"],
        "sourceReadOnly": unchanged,
        "sourceSha256Before": before,
        "sourceSha256After": after,
        "auditReceipt": f"{name}/audit.json",
        "accepted": accepted,
    }
    save_json(run_dir / name / "audit.json", report)
    if not accepted:
        entry["failure"] = {
            "status": report["status"],
            "failures": report["failures"],
            "sourceReadOnly": unchanged,
        }
    return entry


def add_control_sources(run_dir: Path) -> dict[str, Path]:
    controls: dict[str, Path] = {}
    for name, source in (("correct", CORRECT), ("swapped-timing", SWAPPED)):
        destination = run_dir / name
        copy_seed(source, destination)
        controls[name] = destination

    reversed_fields = run_dir / "reversed-record-field-order"
    copy_seed(CORRECT, reversed_fields)
    operations_path = reversed_fields / "Operations.fs"
    operations_text = operations_path.read_text(encoding="utf-8")
    old_branch_record = "{ At = state.At; ExpiresAt = change.ExpiresAt }"
    new_branch_record = "{ ExpiresAt = change.ExpiresAt; At = state.At }"
    if operations_text.count(old_branch_record) != 1:
        raise RuntimeError("Could not locate the Monthly caller record for the reversed-field-order control.")
    operations_path.write_text(operations_text.replace(old_branch_record, new_branch_record, 1), encoding="utf-8", newline="")
    tests_path = reversed_fields / "SelfTests.fs"
    tests_text = tests_path.read_text(encoding="utf-8")
    callback_record = "{ At = at; ExpiresAt = expiry }"
    grouped_record = "{ At = groupedAt; ExpiresAt = groupedExpiry }"
    if tests_text.count(callback_record) != 1 or tests_text.count(grouped_record) != 1:
        raise RuntimeError("Could not locate both SelfTests RenewalTiming literals for the reversed-field-order control.")
    tests_text = tests_text.replace(callback_record, "{ ExpiresAt = expiry; At = at }", 1)
    tests_text = tests_text.replace(grouped_record, "{ ExpiresAt = groupedExpiry; At = groupedAt }", 1)
    tests_path.write_text(tests_text, encoding="utf-8", newline="")
    controls["reversed-record-field-order"] = reversed_fields

    local_bindings = run_dir / "local-timing-bindings"
    copy_seed(CORRECT, local_bindings)
    operations_path = local_bindings / "Operations.fs"
    operations_text = operations_path.read_text(encoding="utf-8")
    monthly_body = '        renewWithTerm store oldId newId "monthly" timing.At timing.ExpiresAt'
    local_monthly_body = (
        '        let localTiming = { At = timing.At; ExpiresAt = timing.ExpiresAt }\n'
        '        renewWithTerm store oldId newId "monthly" localTiming.At localTiming.ExpiresAt'
    )
    monthly_branch = (
        '                | Monthly -> renewMonthly current change.OldId change.NewId '
        '{ At = state.At; ExpiresAt = change.ExpiresAt }'
    )
    local_monthly_branch = (
        '                | Monthly ->\n'
        '                    let localTiming = { At = state.At; ExpiresAt = change.ExpiresAt }\n'
        '                    renewMonthly current change.OldId change.NewId localTiming'
    )
    if operations_text.count(monthly_body) != 1 or operations_text.count(monthly_branch) != 1:
        raise RuntimeError("Could not locate the monthly implementation and caller for the local-binding control.")
    operations_text = operations_text.replace(monthly_body, local_monthly_body, 1)
    operations_text = operations_text.replace(monthly_branch, local_monthly_branch, 1)
    operations_path.write_text(operations_text, encoding="utf-8", newline="")
    callback_path = local_bindings / "SelfTests.fs"
    callback_text = callback_path.read_text(encoding="utf-8")
    callback_call = "Subscription.renewMonthly store oldId newId { At = at; ExpiresAt = expiry }"
    callback_with_binding = (
        "let localTiming = { At = at; ExpiresAt = expiry }\n"
        "                  Subscription.renewMonthly store oldId newId localTiming"
    )
    if callback_text.count(callback_call) != 1:
        raise RuntimeError("Could not locate the monthly scenario callback for the local-binding control.")
    callback_path.write_text(callback_text.replace(callback_call, callback_with_binding, 1), encoding="utf-8", newline="")
    controls["local-timing-bindings"] = local_bindings

    weakened = run_dir / "weakened-inherited-assertion"
    copy_seed(CORRECT, weakened)
    tests_path = weakened / "SelfTests.fs"
    tests = tests_path.read_text(encoding="utf-8")
    line = '        assertEqual "seed customer count" 1 summary.Customers\n'
    if tests.count(line) != 1:
        raise RuntimeError("Could not locate exactly one inherited assertion for the weakened control.")
    tests_path.write_text(tests.replace(line, "", 1), encoding="utf-8", newline="")
    controls["weakened-inherited-assertion"] = weakened

    weakened_expression = run_dir / "weakened-inherited-assertion-expression"
    copy_seed(CORRECT, weakened_expression)
    expression_path = weakened_expression / "SelfTests.fs"
    expression_text = expression_path.read_text(encoding="utf-8")
    old_expression = 'assertEqual "seed customer count" 1 summary.Customers'
    new_expression = 'assertEqual "seed customer count" 0 summary.Customers'
    if expression_text.count(old_expression) != 1:
        raise RuntimeError("Could not locate exactly one inherited assertion expression for the weakening control.")
    expression_path.write_text(expression_text.replace(old_expression, new_expression, 1), encoding="utf-8", newline="")
    controls["weakened-inherited-assertion-expression"] = weakened_expression

    fixture_changed = run_dir / "changed-fixture"
    copy_seed(CORRECT, fixture_changed)
    fixture_path = fixture_changed / "Fixtures.fs"
    fixture_path.write_bytes(fixture_path.read_bytes() + b"\n// unauthorized seed fixture change\n")
    controls["changed-fixture"] = fixture_changed

    project_changed = run_dir / "changed-project"
    copy_seed(CORRECT, project_changed)
    project_path = project_changed / "SubscriptionBatch.Conventional.fsproj"
    project_path.write_bytes(project_path.read_bytes() + b"\n")
    controls["changed-project"] = project_changed

    literal_changed = run_dir / "changed-inherited-assertion-literal"
    copy_seed(CORRECT, literal_changed)
    literal_path = literal_changed / "SelfTests.fs"
    literal_text = literal_path.read_text(encoding="utf-8")
    old_literal = "leaves its input summary unchanged"
    new_literal = "leaves its input  summary unchanged"
    if literal_text.count(old_literal) != 1:
        raise RuntimeError("Could not locate exactly one inherited assertion literal for the whitespace control.")
    literal_path.write_text(literal_text.replace(old_literal, new_literal, 1), encoding="utf-8", newline="")
    controls["changed-inherited-assertion-literal"] = literal_changed

    unasserted = run_dir / "unasserted-grouped-result"
    copy_seed(CORRECT, unasserted)
    unasserted_path = unasserted / "SelfTests.fs"
    unasserted_text = unasserted_path.read_text(encoding="utf-8")
    old_assertion = '(Ok groupedExpected) groupedActual'
    new_assertion = '(Ok groupedExpected) groupedExpected'
    if unasserted_text.count(old_assertion) != 1:
        raise RuntimeError("Could not locate exactly one grouped-result assertion for the negative control.")
    unasserted_path.write_text(unasserted_text.replace(old_assertion, new_assertion, 1), encoding="utf-8", newline="")
    controls["unasserted-grouped-result"] = unasserted

    inline_assertion = run_dir / "inline-grouped-result-assertion"
    copy_seed(CORRECT, inline_assertion)
    inline_path = inline_assertion / "SelfTests.fs"
    inline_text = inline_path.read_text(encoding="utf-8")
    old_call_and_assertion = (
        "        let groupedActual =\n"
        "            Subscription.renewMonthly groupedTimingSeed.Store groupedTimingSeed.OldSubscriptionId\n"
        "                groupedNewId { At = groupedAt; ExpiresAt = groupedExpiry }\n"
        '        assertEqual "grouped timing preserves complete renewal behavior" (Ok groupedExpected) groupedActual'
    )
    inline_call_and_assertion = (
        '        assertEqual "grouped timing preserves complete renewal behavior" (Ok groupedExpected)\n'
        "            (Subscription.renewMonthly groupedTimingSeed.Store groupedTimingSeed.OldSubscriptionId\n"
        "                groupedNewId { At = groupedAt; ExpiresAt = groupedExpiry })"
    )
    if inline_text.count(old_call_and_assertion) != 1:
        raise RuntimeError("Could not locate the direct grouped call for the inline-assertion control.")
    inline_path.write_text(inline_text.replace(old_call_and_assertion, inline_call_and_assertion, 1), encoding="utf-8", newline="")
    controls["inline-grouped-result-assertion"] = inline_assertion

    relocated = run_dir / "renamed-and-relocated-grouped-test"
    copy_seed(CORRECT, relocated)
    relocated_path = relocated / "SelfTests.fs"
    source = relocated_path.read_text(encoding="utf-8")
    block_start = source.find("        let groupedTimingSeed =")
    annual_marker = '        runRenewalScenarios "annual"'
    block_end = source.find(annual_marker, block_start)
    final_marker = '        printfn "Baseline checks passed:'
    final_at = source.find(final_marker)
    if min(block_start, block_end, final_at) < 0 or not (block_start < block_end < final_at):
        raise RuntimeError("Could not locate the grouped test and scenario calls for the relocation control.")
    block = source[block_start:block_end].rstrip()
    source = source[:block_start] + source[block_end:]
    renamed = {
        "groupedTimingSeed": "seedForDirectCase",
        "groupedAt": "startAtForDirectCase",
        "groupedExpiry": "expiresAtForDirectCase",
        "groupedNewId": "replacementIdForDirectCase",
        "groupedOriginal": "subscriptionBeforeDirectCase",
        "groupedInputSummary": "summaryBeforeDirectCase",
        "groupedExpected": "expectedDirectResult",
        "groupedActual": "observedDirectResult",
    }
    for old, new in renamed.items():
        block = block.replace(old, new)
    block = "        // Local placement and names are not part of the public contract.\n" + block
    match_assertion = (
        '        assertEqual "grouped timing preserves complete renewal behavior" '
        '(Ok expectedDirectResult) observedDirectResult'
    )
    match_result = (
        "        match observedDirectResult with\n"
        "        | Ok renewed ->\n"
        '            assertEqual "grouped timing preserves complete renewal behavior" expectedDirectResult renewed\n'
        "        | Error error ->\n"
        '            failwithf "grouped timing returned an error: %s" '
        "(AgentLang.Business.Domain.DomainError.message error)"
    )
    if block.count(match_assertion) != 1:
        raise RuntimeError("Could not locate the grouped result assertion for the match-style control.")
    block = block.replace(match_assertion, match_result, 1)
    final_at = source.find(final_marker)
    source = source[:final_at] + block + "\n\n" + source[final_at:]
    source = source.replace('runRenewalScenarios "monthly"', 'runRenewalScenarios\n            "monthly"', 1)
    relocated_path.write_text(source, encoding="utf-8", newline="")
    controls["renamed-and-relocated-grouped-test"] = relocated
    return controls


def main() -> int:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    run_dir = CALIBRATION / "runs" / f"run-{stamp}"
    run_dir.mkdir(parents=True, exist_ok=False)
    controls = add_control_sources(run_dir)
    expectations = {
        "correct": "pass",
        "swapped-timing": "pass",
        "reversed-record-field-order": "pass",
        "local-timing-bindings": "pass",
        "weakened-inherited-assertion": "failure",
        "weakened-inherited-assertion-expression": "failure",
        "changed-fixture": "failure",
        "changed-project": "failure",
        "changed-inherited-assertion-literal": "failure",
        "unasserted-grouped-result": "failure",
        "inline-grouped-result-assertion": "pass",
        "renamed-and-relocated-grouped-test": "pass",
    }
    results = [expect(name, controls[name], status, run_dir) for name, status in expectations.items()]
    receipt = {
        "study": "replacement-trial-230",
        "gate": "F# source preservation audit",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "pass" if all(row["accepted"] for row in results) else "failure",
        "sourceCodeExecuted": False,
        "controlCount": len(results),
        "results": results,
        "runDirectory": str(run_dir.relative_to(REPOSITORY).as_posix()),
    }
    save_json(run_dir / "checks-receipt.json", receipt)
    print(json.dumps(receipt, indent=2, ensure_ascii=False))
    return 0 if receipt["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
