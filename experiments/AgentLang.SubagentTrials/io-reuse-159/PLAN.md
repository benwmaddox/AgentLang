# Retained I/O vocabulary follow-on

Status: completed. See [report 159](../../../reports/159-retained-io-vocabulary-follow-on.md)
for results and the partial control-output provenance loss.

Research question: does a fresh external coding agent discover and reuse the
state-sensitive publisher created in report 156 while adding caller behavior?
One AgentLang participant and one conventional F# participant receive copies
of the saved accepted projects and the corrected report-158 transport tooling.
This is a feasibility/reuse observation, not a statistical efficacy estimate.

Add publication with mirroring for an existing source and three distinct paths.
The original safe-publication outcome remains Created, Unchanged or Conflict.
On Created/Unchanged, copy the destination's exact contents to a mirror
unconditionally. On Conflict, leave the mirror unchanged. Source and unrelated
files remain unchanged. Preserve empty text, Unicode and CRLF exactly.

Per-call I/O counts are Created: three reads/two writes; Unchanged: four
reads/one write; Conflict: three reads/no writes. Existence checking counts as
a read. Repeated calls consult current state. A Created first call must become
Unchanged on the second call. Missing sources and aliased paths are outside the
contract. Reuse is encouraged but is an observation, not a hidden acceptance
condition. Existing functions, types and baseline tests must remain intact.

The language target is `configuration.publish-and-mirror`, committed as a library
function with its own tests. The conventional target is
`ConfigurationOperations.publishAndMirror` appended to Operations.fs, with
tests added to SelfTests.fs. Participants use only their assigned broker.

Before dispatch, validate both baseline projects and independent oracles with
a positive control and three faulty controls: conflict mirror overwrite,
incorrect mirrored text, and a redundant destination write. Freeze prompts,
runtime binaries, seed inventories and oracle sources. Each fresh participant
uses Luna/max with no inherited conversation and at most 100 broker exchanges.
Record actual results, library publication, preservation, dependency reuse,
broker errors, bytes and duration. Protocol bytes are not LLM tokens.

Score twelve input cases spanning the three outcomes and four text payloads,
with repeated invocations, isolated virtual files and exact effect assertions.
Use disposable copies for hidden checks. Do not put acceptance tests into the
participant project or use participant-supplied tests as the acceptance oracle.

Validation uses current Release CLI artifacts and local F# builds with
`-m:1 -p:NuGetAudit=false` and workspace-local TEMP/TMP. Keep broader retention
preparation deferred. This trial does not change language/runtime semantics or
require repeating native conformance checks.
