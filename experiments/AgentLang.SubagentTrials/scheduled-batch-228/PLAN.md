# 228 — Interactive retry for scheduled renewal batch

This is a fresh matched pair for the scheduled renewal batch task after the
F# arm in 226 ended at startup with input EOF. Both projects start from the
unchanged baseline copies retained by 226. The 226 participant endpoints are
not inputs.

Reuse the frozen 226 task, seven-case model, scorers, type gate, unit checks,
calibration receipts, private runtimes, and baseline copies by their recorded
hashes. Do not rebuild controls, oracles, scorers, or runtimes. The preparation
script checks the candidate and supplemental input pins before copying the
baseline trees. The retained actor includes the full durable AgentLang store;
the F# actor contains its six original source and project files, with no `bin`
or `obj` output.

The packets retain 226 task wording and operation limits. Their assigned
project, trace, and scratch paths point to 228. Both include the same explicit
interactive startup protocol: use `exec_command` with `tty:true`, keep the
returned session ID, send input through `write_stdin` to that handle, continue
after timeouts without restarting, and close with `host.close` before auditing
terminal status. The launchers keep using the CLI DLLs pinned in 226.

Preparation samples and records the arm order once. It creates undispatched
inputs only: zero participants dispatched and dispatch disabled. The actual
interactive preflight and protocol freeze belong to the coordinating task.

Participants have no external capabilities or effect grants. F# work is pure,
uses the assigned local validation project, and adds no packages. The no-web
and broker-only rules are instruction boundaries; this setup does not claim
OS-level network confinement.
