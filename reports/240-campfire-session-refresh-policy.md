# 240 — Campfire session refresh policy

Status: validated pure policy against both the sealed accepted runtime and the fresh integration runtime.

`session-policy.flow` puts the hourly refresh decision in language code. `UtcInstant` is a refined nominal integer representing UTC microseconds since Unix epoch, bounded to the host timestamp range from year 1 through year 9999. The validator rejects values outside that range. `campfire.sessions.refresh-due?` accepts explicit last-activity and current instants and returns true only when activity is strictly more than one hour old. Activity exactly one hour old does not trigger a refresh, matching the pinned Rust `Session::needs_resume` rule.

The database adapter will receive the chosen cutoff/current time and perform a conditional update. This pure function performs no database, clock or cookie effects. It does not restore a session or authenticate a request. Host conversion should floor UTC ticks to microseconds before subtracting Unix epoch microseconds, including for pre-epoch timestamps.

## Validation so far

The public `scripts/Verify-CampfireSessionPolicy.ps1` passed against the sealed accepted 233 runtime at `.agentlang/campfire-host-236/pure-policy-runtime/`. It defined the source, passed 10 attached tests, qualified both functions as libraries, reopened the project in a fresh process, reran the tests, and verified current instruction/branch and finite-return coverage. It independently checked 36 duration cases spanning minimum/maximum instants, negative epochs, future activity and the strict one-hour boundary. Two additional constructor checks rejected out-of-range instants with `REFINEMENT_FAILED`.

Raw requests, responses, oracle cases and the baseline runtime/source hash receipt are retained at `.agentlang/campfire-session-policy-240/runs/20261011T220127Z-cc9e71bd/`. The combined Campfire gate repeated all checks after a fresh Release CLI build; that passing receipt is `.agentlang/campfire-session-policy-240/runs/20261011T225204Z-1ab9f430/receipt.json`, with the combined build/run receipt at `.agentlang/campfire-host-236/root-combined-repeat/receipt.json`.

Earlier attempts are retained separately: the first source used comparison operators the current parser does not support, and a private PowerShell oracle attempt incorrectly grouped arithmetic expressions inside an array. The source now uses the existing numeric functions; the public verifier fixes the array grouping and checks every structured response rather than treating CLI exit zero as proof that all requests succeeded.

The behavior reference is `crates/db/src/models/session.rs` at Rust commit `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`. The underlying Rails reference is `90b330024dec3e757c79b6a7e6568f93da8e3148`. Full request ordering, cookie renewal and HTTP behavior remain separate acceptance work.

The latest combined gate repeated the policy again at `.agentlang/campfire-session-policy-240/runs/20261011T232328Z-ad44739d/`. Its raw requests/results and receipt are sealed with the host milestone in `reports/evidence/236-campfire-host-integration/evidence.zip`; see [report 236](236-campfire-host-integration.md) for the archive hash and complete regression resolutions. Session restoration/storage are now separately validated in [report 241](241-campfire-session-restoration.md), while HTTP cookie behavior remains pending.
