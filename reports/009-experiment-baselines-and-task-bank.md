# Milestone 009: experiment baseline audit and task-bank artifacts

Status: baseline labeling and artifact contracts validated; controlled agent comparison and executable benchmark suite remain pending.

The external harness separates Flat/Growing retention from starting profile. Domain-seeded runs are explicit controls. Fresh primitive-only origins audit authoritative source/metadata and reject authored words, tests and examples before task/provider calls. Verified Growing continuations retain accepted words and report both origin and current inventories. The inventory does not prove cross-language schema equivalence.

Growing lineage schema 2 records complete typed origin metadata and canonical durable-state hashes. The reader rejects malformed inventories and non-hexadecimal hashes. Seed hashing and BOM-aware parser input use the same captured bytes; supplied, applied-this-run and established-at-origin fields are distinct. The hash covers authoritative vocabulary/identity and harness task history, excluding virtual providers, clock and host capabilities. This is a trial-labeling check, not a security boundary.

## Findings and repairs

Review found that unconditional rejection of authored words blocked valid Growing continuations, and bootstrap trusted the advisory dictionary export instead of Storage authority. Regression tests now allow retained tested algorithms and prove that missing/corrupt exports neither replace the manifest inventory nor trigger reseeding.

Independent review found a stale marker after post-commit failure and malformed origin inventories that could pass as empty. Rollback now reconciles the marker with restored storage; failed fresh initialization restores the pre-seed checkpoint. Malformed markers fail before provider invocation. Focused Windows execution also caught an open `FileShare.None` stream during marker rename; the stream is now disposed before replacement. Deterministic host-only failure injection verifies initial-marker and post-final-marker failures, retries and seed provenance. No model tool or language capability exposes this seam.

Fresh full Release validation passed, including 185 offline harness assertions and 99 fresh-process persistence checks. [Saved validation](evidence/008-working-tree-validation.json) identifies the dirty working tree based on `3acd8e2decf95f15755ae182827cea991a04932b`. Independent read-only review found the reported lineage gaps resolved.

The task bank contains all 60 proposed public tasks (20 simple, 20 medium, 10 debugging, 10 refactoring), with 180 separate proposed hidden cases. Its validator passed 2,114 assertions for inventory, prerequisite DAGs, public/hidden separation, schema and promotion-evidence shape. It does not authenticate referenced evidence or execute answers. [The suite contract](../docs/BENCHMARK-SUITE.md) documents the pending adapters, matched typed fixtures, reviewed oracles and snapshot pins. Some policy fields do not exist in the immutable F# reference; alignment remains required.

## Limits and next gate

Final report-artifact capture can fail after the task and matching lineage have committed. That reporting phase is not a transaction with dictionary publication; a failed/incomplete run must be reviewed or reset before inclusion in a controlled sequence. The harness is not an OS sandbox. The domain-seeded profile remains the CLI default for existing scripts; original mode A requires explicit `--baseline primitive-only`.

These are infrastructure and specification results, not live model outcomes or performance gains. Exact framework usage remains unavailable. The full executable suite and Flat/Growing/Conventional comparison remain open in the requirements ledger.

## Published revision and clean CI

Code and reports shipped together at `246dba27dfe0da619423bdf7781c8493596e42fa`. [Exact-revision CI run 37299454080](https://github.com/benwmaddox/AgentLang/actions/runs/37299454080) passed. Downloaded [validation](evidence/008-committed-ci-validation.json) confirms `dirty: false`, all 16 checks passed and the expected revision; [persistence evidence](evidence/008-committed-ci-projection.json) passed 99 checks. This checkpoint is merged and pushed to private `main`; runtime migration and actual benchmark execution remain pending.
