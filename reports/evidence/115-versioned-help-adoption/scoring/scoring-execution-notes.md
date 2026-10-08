# Scoring execution notes

The pinned behavioral verifier and the mutation check completed using the frozen runtime. Both host traces record host/runtime exit code 0 and zero runtime stderr. A post-close `write_stdin` poll for the mutation session returned `Unknown process id`; the trace is the terminal evidence.

One redundant second host-launch attempt was made after the successful mutation session. The launcher refused to start because `mutant.trace.jsonl` already existed (exit 1). The guard prevented overwriting the first run's trace; no project or runtime was changed by this refused launch.