# Final report review: mailbox confirmation 208

## Result

No blocker or numeric error found in `reports/208-sixteen-slot-confirmation.md`. I compared its trial table, qualification statements, throughput and memory summaries to the saved run `8e5d6ef649044874ba7c4635cd24df5b` and `root-raw-audit.json`.

## Checks

- All nine table rows match the saved trials after the displayed rounding. F# qualifies in repeats 1–3; RETURN and KEEP fail only the rejection gate in repeats 1–3, while each p99 remains below 50 ms. Group results are F# `3/3`, RETURN `0/3`, KEEP `0/3` (`run-report.json`, `root-raw-audit.json`).
- Recomputed completed-within-window throughput medians are 497.17, 492.20, and 492.57 requests/s for F#, RETURN, and KEEP. Recomputed p99 medians are 32.373, 33.332, and 33.139 ms. The report uses the declared successful-completions-per-measurement-window metric.
- Recomputed MiB memory medians/ranges match the report: commit 28.32 [28.30–28.43], 8.25 [8.23–8.31], 8.24 [8.24–8.30]; working set 54.27 [54.20–54.36], 6.22 [6.22–6.22], 7.47 [7.47–7.47]. All three native trials per policy report 6,317,704 caller-storage bytes. All measured ceilings are nonbinding and working-set queries succeeded.
- All nine raw application results report `verified=true`, zero errors/timeouts/pending operations, 16 verified mailbox states, and conserved request/mailbox counts. Each provider has 16 accepted connections and accepted/completed frames equal warmup completions plus measured admissions. The root audit passed all 20 smoke and measured trials. Its stated limitation is accurate: it recomputes saved accounting and percentiles; runtime content validation remains the harness's responsibility.
- `confirmation-208.md` currently hashes to `d5ab9db071fcf7afe8e4fd9c40d354fd4ebfe900e67e9f86246fe2633fb2e8b1`, matching `freeze.json`. The run report has 94 source hashes before and after with `sourceStable=true`; its recorded interval is 08:32:36–08:38:15 UTC, matching the report's rounded time.

## Claim scope

The report correctly marks copy accounting as unmeasured: raw native application results contain `storageReservedBytes` and `scratchSlots`, but no copy-byte counter. It does not infer zero copying or a copy-cost benefit. It distinguishes caller storage, process commit, and working set; reports nonbinding ceilings; and avoids a winning-policy, capacity, or agent-efficacy claim. Its 0–6 missed-arrival and 15,000-offered-request statements match the raw trials. The archive index reports 379 members and says entry lengths and hashes were verified.

No new measurement was run for this review.
