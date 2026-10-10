# AgentLang project storage

Status: first storage module; Runtime integration owns source parsing, type/effect validation, and activation policy.

## Layout and authority

The project remains readable as `dictionary.agent`. The authoritative state is a versioned pointer at `.agentlang/store/CURRENT` once the project has been committed through Storage. Content-addressed source is stored under `.agentlang/store/objects/`, immutable manifests under `.agentlang/store/manifests/`, and named snapshots under `.agentlang/store/snapshots/`. Structured task logs are written under `history/`.

`CURRENT` has one of three version-1 states:

- `empty`: an explicit empty-project tombstone;
- `legacy`: a hash reference to a previously imported `dictionary.agent`;
- `manifest`: the hash of the active project manifest.

The pointer generation starts at 1 on the first commit. A project with no pointer is generation 0. A read-only legacy load reports `LegacyAuthority` at generation 0 and leaves the legacy file unchanged. Storage does not fabricate stable IDs or revisions for legacy source.

Manifest and source hashes are lowercase SHA-256 of the exact UTF-8 bytes without a BOM. Object filenames are derived only from validated hashes and object kind. Source objects use `.agent`; provider-state objects use `.json`. Manifest serialization sorts types, word heads, revisions, tests, and examples to keep its hash deterministic. A word head has a stable ID, current name, current revision, and deprecation flag. Immutable revisions retain the name used at that revision, definition/test/example references, maturity, actor, optional task ID, UTC timestamp, and deprecation state. Historical revisions may remain after a word head is archived or replaced; no missing historical source is invented.

Storage validates pointer/manifest schema versions, source hashes and kinds, unique active IDs/names and revision identities, object availability, UTF-8, content sizes, and safe derived paths. It rejects traversal-shaped hashes and snapshot names, reparse points in the project path, missing referenced objects, and tampered content. It deliberately does not parse `.agent` source or decide whether the language-level dependency, type, effect, or test graph is valid. Runtime must validate a complete candidate graph before passing it to `commit`, and validate a loaded graph before exposing it to an agent.

## Commit boundary

`Storage.commit store expectedGeneration manifest sources exportText` validates the full metadata and every supplied source object, checks the expected generation while holding the exclusive `WRITE.lock`, writes referenced immutable objects and the content-addressed manifest, and then atomically replaces `CURRENT`. The pointer replacement is the commit point. A failure before replacement leaves the previous authority unchanged; harmless unreferenced content-addressed files can remain. Unreferenced objects supplied to a commit are validated but are not written.

Only after `CURRENT` is replaced does Storage refresh `dictionary.agent`. An export error is returned as `ExportWarning` in a successful `StorageCommitResult`; the caller must not report that commit as failed. `load` validates the current authority and makes a best-effort export refresh, also returning any warning. If `CURRENT` exists but is corrupt or references invalid content, Storage fails closed and does not fall back to `dictionary.agent`.

`capture` records the exact current authority and export text for task rollback. `restore store expectedGeneration snapshot` restores that authority without adding word revisions, and advances the current generation monotonically. This supports abort after one or more interim commits. Empty and pre-migration legacy states are restorable through explicit `empty` or `legacy` pointers. A saved generation is descriptive and is not reused as the new live generation.

Named snapshots are separate from task rollback. `saveSnapshot` records the active committed manifest hash and optional deterministic provider state: a virtual-file map plus an opaque fixed-clock string. `readSnapshot` verifies all referenced objects and returns data without activating it. `restoreSnapshot` checks the expected generation and activates the referenced manifest; Runtime should apply the returned snapshot's virtual files and clock only after that operation succeeds. Host capabilities and effect permissions are never stored or restored by this module. Callers must choose whether task abort also restores provider state; the storage layer does not own in-memory effect providers.

The public boundary is intentionally small:

| Operation | Contract |
| --- | --- |
| `load` | Load and validate the current empty, legacy, or manifest authority. |
| `capture` / `restore` | Capture exact logical authority and restore it transactionally with a generation check. |
| `commit` | Publish a new validated manifest and its referenced immutable source. |
| `saveSnapshot` / `readSnapshot` / `restoreSnapshot` | Persist, inspect, and activate named manifest/provider snapshots. Reading never activates. |
| `readSource` / `readRevision` | Read exact verified content by hash or immutable revision identity. |
| `saveTaskLog` | Atomically store valid JSON under a filename-safe task ID. |

All operations return structured `StorageError` values with a stable code, message, and optional path. Mutating operations use an exclusive short-lived file lock and recheck the generation under that lock. A stale expected generation returns `STORAGE_STALE_GENERATION` before the pointer changes.

## Limits and durability

Version 1 caps each source/provider object at 8 MiB, each manifest/snapshot/task-log document at 8 MiB, each manifest or commit at 20,000 references/source objects, and each named virtual filesystem at 10,000 files. Virtual paths are relative slash-separated keys of at most 4,096 characters and cannot contain empty, `.` or `..` segments, backslashes, colons, or NUL. Snapshot names are 1–64 lowercase ASCII letters, digits, dots, underscores, or hyphens. Fixed-clock values are limited to 256 characters; task IDs are at most 128 ASCII letters, digits, hyphens, or underscores.

Files are flushed before atomic same-directory replacement, and the lock relies on the host filesystem's `FileShare.None` behavior. This is not a promise of directory-entry durability under sudden power loss, nor is this a multi-host/distributed lock protocol. The storage root and its ancestors must not be reparse points. A crash before pointer replacement can leave orphan objects/manifests; garbage collection is deferred. A crash after pointer replacement can leave a stale human-readable export, which the next successful load repairs and reports if it cannot.

## Focused validation

The storage-only executable is `dotnet run --project tests/AgentLang.Storage.Tests/AgentLang.Storage.Tests.fsproj`. It exercises commit/reload and stable revision IDs, rename/archive history, stale generations and writer exclusion, injected failures on both sides of the pointer boundary, legacy and empty rollback, named provider snapshots, hash/schema/path/missing-object/reparse rejection, and task-log validation. These tests establish the storage contract only; they do not establish that a language source graph is semantically valid or that Runtime has applied provider state correctly.

## Manifest frontend versions

Manifest v1 retains the original Stack source encoding. V2 adds explicit word
source formats and retained call bindings. V3 keeps that revision encoding and
adds `sourceFormat` plus a required nullable `validatorTarget` to each type
source. Flow types use frontend `flow`, version 1; legacy types use `stack`,
version 1. A validator target identifies the resolved primitive, generated word
or stable user word rather than permitting a later name-based redirect.

V1/v2 serialization omits the v3 type fields and rejects nondefault metadata;
loading supplies their legacy defaults without changing historical object
bytes. The pointer and named snapshot envelopes remain version 1. Runtime uses
v3 for Flow types and retains v3 once selected. Type declarations are immutable
source objects, not word revisions. Their exact authored bytes are hash-checked
and returned by type source inspection; aggregate project export is a separate,
deterministic frontend-marked rendering. Runtime reparses with the declared
frontend, validates the complete typed graph and frozen validator target, and
checks canonical export before activation. Parser fallback is forbidden.

V4 retains the v3 field encoding and adds shared Flow/2 test-file sources and
test-override call bindings. A revision references each complete wrapper once,
even when it contains several cases. Header bindings use `testOverride` role,
`testOverrideTarget` form and a `testOverrideDefinition` root; fixture-body
bindings use the same role below that root. Their case name is null. Ordinary
test-body and expectation bindings retain their case names. Earlier manifest
versions reject these new roles, forms and paths. Runtime selects cases by
owner/name after parsing the shared source, verifies all retained identities,
and rechecks replacement signatures and effects before constructing ephemeral
dispatch. It retains v4 after selection, including historical wrapper revisions.

V5 adds `attachmentSourceFormats` to each revision: a sorted array of
`{source, sourceFormat}` entries keyed by the exact test/example source reference.
The table covers every referenced source exactly once, including each shared
test-file wrapper. Attachments use the owner's frontend but may retain a different
supported syntax version. This lets a Flow/2 replacement keep its Flow/1 tests
and examples unchanged. Wrapper source remains Flow/2 and every nested case
uses the wrapper's version.

Loading v1–v4 assigns the historical owner format to its attachments; it does
not guess from source text. V5 requires explicit complete metadata, including
historical revisions. Missing, extra, duplicate, unsupported or cross-frontend
entries are invalid. Runtime selects v5 when independently versioned attachments
require it and retains v5 thereafter. Older manifest serialization must reject
format differences that it cannot represent. Pointer/snapshot and source object
envelopes are unchanged.

Call bindings remain attached to exact source hashes, structural paths and stable
targets. Definition paths use the owner's source format; test, expectation and
override paths use their attachment's source format. Reload reparses each source
with its recorded version and verifies the complete candidate graph. Function
replacement advances owner revision metadata without translating retained cases
or changing their expected values. Type/effect checks, scoped test replacements,
library coverage, CAS and source integrity remain required.

## Flow/2 closed enum extension

Payload-free enums use the existing v3 type-source envelope with `flow`, version
2 and a null validator target. The exact enum source is the authority for its
ordered case table. Runtime checks the nominal declaration and generated
constructors when loading the complete graph; no new native ABI layout is implied.

Retained Flow/2 call paths add `enumScrutinee` and `enumCaseStatement`. The latter
stores nonnegative `caseIndex` and `statementIndex`, both subject to the existing
call-path index limit. The index identifies the authored arm position, not a
string lookup after editing. Normal recompilation regenerates and reconciles
bindings; rename rewrites preserve resolved identities. Flow/1 revisions reject
these path tags. Existing manifest/source versions remain readable, but an older
runtime is not promised to load a project using the new Flow/2 enum extension.
