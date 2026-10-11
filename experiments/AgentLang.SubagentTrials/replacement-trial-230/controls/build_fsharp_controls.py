"""Build and score the correct and swapped F# RenewalTiming controls."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

REPO = Path(__file__).resolve().parents[4]
TRIAL = REPO / ".agentlang" / "replacement-trial-230"
BASELINE = TRIAL / "baselines" / "fsharp"
ORACLE = REPO / "experiments" / "AgentLang.SubagentTrials" / "replacement-trial-230" / "oracle"
OUT = TRIAL / "calibration"
VARIANTS = ("correct", "swapped-timing")
INPUTS = (
    "Domain.fs", "Fixtures.fs", "Operations.fs", "Program.fs",
    "SelfTests.fs", "SubscriptionBatch.Conventional.fsproj",
)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def replace_once(source: str, old: str, new: str, label: str) -> str:
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one {label} occurrence, found {count}")
    return source.replace(old, new, 1)


def make_operations(source: str, swapped: bool) -> str:
    old_monthly = """    let renewMonthly (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId)
                     (at: Instant) (expiry: Instant) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        renewWithTerm store oldId newId \"monthly\" at expiry
"""
    chosen_at, chosen_expiry = (
        ("timing.ExpiresAt", "timing.At") if swapped else ("timing.At", "timing.ExpiresAt")
    )
    new_monthly = f"""    let renewMonthly (store: Store) (oldId: SubscriptionId) (newId: SubscriptionId)
                     (timing: RenewalTiming) : Result<Store, AgentLang.SubscriptionHandoff.DomainError> =
        renewWithTerm store oldId newId \"monthly\" {chosen_at} {chosen_expiry}
"""
    updated = replace_once(source, old_monthly, new_monthly, "monthly function")
    old_step = "| Monthly -> renewMonthly current change.OldId change.NewId state.At change.ExpiresAt"
    new_step = (
        "| Monthly -> renewMonthly current change.OldId change.NewId "
        "{ At = state.At; ExpiresAt = change.ExpiresAt }"
    )
    updated = replace_once(updated, old_step, new_step, "batch-step monthly call")
    marker = "type ScheduledSubscriptionRenewal =\n    { Renewal: SubscriptionRenewal\n      At: Instant }\n"
    new_marker = marker + "\ntype RenewalTiming =\n    { At: Instant\n      ExpiresAt: Instant }\n"
    return replace_once(updated, marker, new_marker, "scheduled renewal type insertion")


def make_self_tests(source: str) -> str:
    old_callback = '        runRenewalScenarios \"monthly\" Subscription.renewMonthly \"monthly\"'
    new_callback = '''        runRenewalScenarios \"monthly\"
            (fun store oldId newId at expiry ->
                Subscription.renewMonthly store oldId newId { At = at; ExpiresAt = expiry })
            \"monthly\"'''
    updated = replace_once(source, old_callback, new_callback, "monthly scenario callback")
    insert_before = '        runRenewalScenarios \"annual\" Subscription.renewAnnual \"annual\"'
    grouped_test = '''        let groupedTimingSeed =
            legacySeed (subscriptionId \"50000000-0000-0000-0000-000000000060\")
        let groupedAt = Fixtures.instant 2026 2 2
        let groupedExpiry = Fixtures.instant 2026 4 7
        let groupedNewId = subscriptionId \"50000000-0000-0000-0000-000000000061\"
        let groupedOriginal =
            Store.subscription groupedTimingSeed.OldSubscriptionId groupedTimingSeed.Store
            |> Option.defaultWith (fun () -> failwith \"grouped-timing input subscription is missing\")
        let groupedInputSummary = Store.summary groupedTimingSeed.Store
        let groupedExpected =
            expectedRenewal groupedTimingSeed.Store groupedTimingSeed.OldSubscriptionId groupedNewId
                \"monthly\" groupedAt groupedExpiry
            |> Result.defaultWith (fun error ->
                failwithf \"expected grouped-timing renewal failed: %s\"
                    (AgentLang.Business.Domain.DomainError.message error))
        let groupedActual =
            Subscription.renewMonthly groupedTimingSeed.Store groupedTimingSeed.OldSubscriptionId
                groupedNewId { At = groupedAt; ExpiresAt = groupedExpiry }
        assertEqual \"grouped timing preserves complete renewal behavior\" (Ok groupedExpected) groupedActual
        assertEqual \"grouped timing leaves the input store unchanged\"
            groupedInputSummary (Store.summary groupedTimingSeed.Store)
        assertEqual \"grouped timing leaves the input subscription unchanged\"
            (Some groupedOriginal)
            (Store.subscription groupedTimingSeed.OldSubscriptionId groupedTimingSeed.Store)

'''
    return replace_once(updated, insert_before, grouped_test + insert_before, "annual scenario call")


def build_and_check(project: Path, variant: str, root: Path) -> dict:
    artifacts = root / "artifacts"
    command = [
        "dotnet", "run", "--project", str(project), "--configuration", "Release",
        f"--artifacts-path", str(artifacts), "-p:MSBuildEnableWorkloadResolver=false",
        "-p:NuGetAudit=true", "-p:WarningsNotAsErrors=NU1900",
    ]
    completed = subprocess.run(
        command, cwd=REPO, capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=300, check=False,
    )
    (root / "selftests.stdout.log").write_text(completed.stdout, encoding="utf-8")
    (root / "selftests.stderr.log").write_text(completed.stderr, encoding="utf-8")
    result = {
        "variant": variant,
        "command": command,
        "exitCode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "expectedOutcome": "pass" if variant == "correct" else "not-run",
        "dependencyAudit": {
            "enabled": True,
            "status": "unavailable-network-warning" if "NU1900" in completed.stdout + completed.stderr else "no-NU1900-warning-observed",
            "nugetAuditWarningCount": (completed.stdout + completed.stderr).count("NU1900"),
            "note": "A NU1900 service-index warning means vulnerability data could not be fetched; build success does not certify an audit pass.",
        },
    }
    if variant == "correct" and completed.returncode != 0:
        raise RuntimeError(f"Correct F# self-tests failed; raw logs are under {root}")
    if variant == "swapped-timing" and completed.returncode == 0:
        # A swapped implementation may still pass the existing broad suite;
        # behavior discrimination is established separately by all nine model cases.
        result["selfTestsExpectedToDetectSwap"] = False
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUT)
    parser.add_argument("--skip-selftests", action="store_true",
                        help="Build source copies only; use the independent scorer for behavior calibration")
    args = parser.parse_args()
    output = args.output if args.output.is_absolute() else REPO / args.output
    if not BASELINE.is_dir():
        raise FileNotFoundError(f"Prepared F# baseline is missing: {BASELINE}")

    output.mkdir(parents=True, exist_ok=True)
    rows = {}
    for variant in VARIANTS:
        destination = output / variant
        if destination.exists():
            raise RuntimeError(f"Refusing to overwrite existing calibration variant: {destination}")
        shutil.copytree(BASELINE, destination)
        before = {name: sha(destination / name) for name in INPUTS}
        operations = (destination / "Operations.fs").read_text(encoding="utf-8")
        self_tests = (destination / "SelfTests.fs").read_text(encoding="utf-8")
        swapped = variant == "swapped-timing"
        (destination / "Operations.fs").write_text(make_operations(operations, swapped),
                                                    encoding="utf-8", newline="")
        (destination / "SelfTests.fs").write_text(make_self_tests(self_tests),
                                                   encoding="utf-8", newline="")
        after = {name: sha(destination / name) for name in INPUTS}
        changed = sorted(name for name in INPUTS if before[name] != after[name])
        if changed != ["Operations.fs", "SelfTests.fs"]:
            raise RuntimeError(f"{variant} control touched unexpected source files: {changed}")
        rows[variant] = {
            "project": str(destination),
            "changedSourceFiles": changed,
            "sourceBefore": before,
            "sourceAfter": after,
        }
        if not args.skip_selftests and variant == "correct":
            rows[variant]["selfTests"] = build_and_check(
                destination / "SubscriptionBatch.Conventional.fsproj", variant, destination
            )
    (output / "source-index.json").write_text(json.dumps(rows, indent=2) + "\n",
                                               encoding="utf-8", newline="")
    print(json.dumps({"status": "built", "variants": rows}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
