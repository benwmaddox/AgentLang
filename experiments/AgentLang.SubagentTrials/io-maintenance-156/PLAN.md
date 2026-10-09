# Matched I/O maintenance probe

Status: completed. See [report 156](../../../reports/156-state-sensitive-io-maintenance.md)
for outcomes, environmental failures and limitations.

Research question: can a fresh external coding agent discover and reuse tested
vocabulary while adding state-sensitive behavior, without weakening existing
callers? Compare one AgentLang participant with one conventional F# participant.
This is a feasibility probe, not a statistical efficacy estimate.

Add safe configuration publication for existing source and distinct destination
paths. An absent destination is created with exact source contents (Created,
two reads and one write). Identical existing contents return Unchanged (three
reads, no writes). Different existing contents return Conflict without replacing
them (three reads, no writes). Preserve source and unrelated files. Include empty
text, Unicode and exact line endings. Missing source and aliased paths are outside
the contract. Existing unconditional publish and refresh behavior must survive.

Seed both environments with a tested publisher, its refresh caller, deterministic
virtual files, and the three-case outcome type. AgentLang's publisher originates
from the accepted report-154 agent output, reconstituted using current dot/newline
syntax; this is source provenance, not an identical stored project revision.
Observe reuse rather than imposing an undisclosed requirement to call the helper.

Freeze prompts, runtime hashes, starting projects and independent acceptance
cases before dispatch. Participants receive the contract and tools, not oracle
code or positive controls. They add their own tests. Score independent outcomes,
file state, effect counts, collateral preservation, and publication separately.
Use disposable oracle copies; never add hidden tests to participant originals.

Validate the acceptance oracle against a positive control and mutants for a
conflict overwrite, unnecessary unchanged write, and incorrect returned outcome.
Record actual broker exchanges, errors, bytes, and duration. Do not label protocol
bytes as LLM tokens. Prompt isolation is not an operating-system security boundary.

Validation: serial Release builds with `-m:1 -p:NuGetAudit=false`, conventional
SelfTests executable, language baseline test/library gates, positive-control and
mutation checks, then participant acceptance scoring and source/trace audits.
Product runtime is unchanged: do not repeat the full runtime gate solely for
this research fixture. Commit reports and evidence with the milestone.
