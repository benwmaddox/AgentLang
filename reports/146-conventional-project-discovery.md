# Conventional broker project discovery

Status: implemented; focused local tests and trial-host integration pass.

Report 145's F# participants completed correct edits, but spent six and nine
responses on broker argument/path/hash or patch-anchor errors. That friction
confounds comparisons of tool interactions with AgentLang. This change gives the
conventional baseline a normal initial discovery request before further trials.
It does not change the language or revise historical experiment results.

`{"op":"inspect"}` returns a bounded project overview with relative source paths,
the configured validation target, and the supported operation argument forms.
`inspect(path)` retains file metadata and hashing. The same six operation names
remain; edits still require exact arguments, a current content hash and an
unambiguous patch anchor. The CLI's existing nested-args support is preserved,
while documentation uses canonical top-level request fields.

Overview and search share deterministic, bounded directory traversal. Overview
lists supported source paths without reading file bodies. Generated directories
remain excluded and reparse points rejected. Limits report truncation or the
existing per-directory limit error. Since overview is still `inspect`, the
existing trial host classifies and charges it as inspection without a classifier
or budget-version change.

## Local validation

Clean serial Release builds passed for both executable test projects, with
NuGetAudit disabled for local dependency validation. The dispatcher suite passes
216 assertions; the CLI suite passes eight groups and 339 assertions. Tests cover
no-argument discovery, both inspect forms, exact argument rejection, schemas,
invalid/oversize contents that overview never reads, supported paths, truncation
at file/result/global-entry/depth bounds, per-directory overflow and reparse
rejection. Existing search, compare-and-swap edits, validation and request-limit
checks pass. The CLI retains both canonical top-level and nested args forms.

A fresh CLI build in separate artifacts passes two checks through the unchanged
V2 trial host. Its 993-byte overview response is classified and charged as
inspection; a one-byte inspection budget withholds the same response. Both
sessions pass the existing terminal auditor with normal host.close and exits 0/0.
No inspection classifier/version or operation allowlist change was needed.

The first new test build needed F# expression syntax corrections. The first
runtime test attempt encountered a sandbox File.Move denial under the OS temp
directory; a separate minimal probe reproduced it. Repeating the unchanged tests
with process-local TMP/TEMP inside the writable workspace passed the atomic
replacement checks. This was an execution-environment adjustment, not a weakened
assertion or replacement implementation. Independent source review found no
material traversal/schema regression. Diff whitespace checks pass.

The 64 immutable report-145 protocol/runtime inputs remain byte-identical. Its
submitted code, archived binaries and findings are not rewritten. This milestone
changes the conventional benchmark tool and its documentation; it does not claim
a fresh full language-runtime validation gate.

## Research implication

New conventional trials can start with `{"op":"inspect"}` to learn the file
layout and request fields instead of guessing. Keep that bootstrap instruction
compact and freeze the new CLI with future trial inputs. The schemas describe
field forms/types; operation constraints such as hash freshness and unique patch
anchors still apply and are enforced by existing diagnostics.

This removes a concrete discovery omission in the baseline. It does not establish
that future agents make fewer errors, or that either language is more reliable.
A later independent comparison must measure that; historical interaction counts
remain observations of the older tool.


## Evidence

[Proof archive](evidence/146-conventional-project-discovery/proof.zip) and
[entry index](evidence/146-conventional-project-discovery/proof-index.json):
50 entries, 1,269,048 bytes; SHA-256
`29e6b84d764fd5e0cbb1c3eb7a406cc09cc5a580844199617d9af7a78ceaf538`.
Every entry was verified against source bytes and its indexed size/hash. The
archive contains implementation/test snapshots, final suite logs, initial failure
summaries, the fresh CLI, host traces/audits and historical-input verification.
Temporary test projects/build intermediates are excluded.
