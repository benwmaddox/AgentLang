Completed the trial using only broker-mediated reads, edits, and validation in the `conventional-1` project. `queueRemindersForPair` now calls `queueOnce` for the first invoice and then the second, so an ineligible first invoice cannot suppress the second and duplicate IDs observe the first result.

I updated the pair expectation and added duplicate-ID and capability-preflight regressions. Validation passed: `OWN_TESTS_PASSED=4` and `PAIR_OWN_TESTS_PASSED=6`. An initial validation exposed a compile error in the new test; I fixed it and reran validation successfully. Two patch requests were rejected for malformed hashes before any edit; the corrected requests succeeded. The broker closed with exit code 0.

One scope issue: a broker search for `CAPABILITY_DENIED` returned two matching `Domain.fs` lines. I did not read that file or investigate further. No other limitations remain.
