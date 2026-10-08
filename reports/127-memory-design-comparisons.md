# 127 — Memory design comparisons: Midori and Goose

This is a documentation-only research checkpoint. It adds external architectural
comparisons to the native roadmap without treating another project's claims as
AgentLang results. No new agent trial or native performance benchmark was run
for this report. The latest agent outcomes remain [report 125](125-matched-pair-repair.md):
reuse and correct repairs were observed, with no reliability advantage established.

## Findings and decisions

The [Midori note](../docs/MIDORI-RESEARCH.md) separates execution-stack scheduling
from our owning program-data-stack requirement. Its main implications for future
experiments are explicit suspension, bounded work, separate running/suspended
memory accounting, and tests of state changes across awaits.

The [Goose comparison](../docs/GOOSE-RESEARCH.md) is the closer memory-layout
reference. It records the reviewed revision, primary sources, design differences
and proposed experiments. Its reported benchmark results are not replicated
here. Any proposed adjustments are hypotheses, not silently adopted requirements.

The current AgentLang native backend still uses shared record handles and an
invocation arena. An actual owning-value native implementation is in progress;
that work is not validated or published as complete by this checkpoint. The
first implementation stage targets fixed-size nested records using the same
verified semantic IR as the interpreter and existing native control. Variable-size
values, broader native support and real-I/O mailbox comparisons remain required.

Preserve these project decisions while researching alternatives:

- Owning values retain the complete nested payload. Copies are independent,
  moves invalidate the source, and reclamation cannot leave surviving aliases.
- Physical payload locality is required, reflecting the user's Stasislang
  experience. Nearby handles to scattered objects do not qualify. Verify actual
  offsets/extents and separately measure workload performance.
- Treat copy cost, compaction, reserved capacity and live payload as distinct
  quantities. Avoid claiming small memory use from payload bytes alone.
- Keep capacity failure and error cleanup explicit and testable.
- Keep reliable agent edits and discoverable typed vocabulary as the primary
  research objective; runtime comparisons answer a separate question.

## Validation

Primary-source links were inspected; all 99 local links in the eight changed
documents resolved before adding the evidence link below. The documentation
diff passed the whitespace check. The final
[documentation validation](evidence/127-memory-design-comparisons/validation.json)
records file hashes and a repeated link check. No production source changes
are included in this checkpoint, so no product test rerun is claimed. Concurrent
owning-stack implementation files are excluded from this publication.
