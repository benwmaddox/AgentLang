# 251 — Native owning lists and integer formatting

Updated 2026-10-12.

## Result

The owning LLVM AOT layout now includes typed lists. List descriptors use kind
10 and an eight-byte root header; `ListEmpty` emits an empty typed list. The
native `int.to-string` lowering formats signed Int64 values as invariant decimal
text. This unblocks two compiler primitives required by the room page, but it
does not compile or serve the Campfire page.

Adding the list descriptor changes the owning layout ABI from 4 to 5. ABI5
modules reject stale ABI4 layouts. The context ABI remains 2, the module ABI
remains 2, and the owning stack ABI remains 2. Descriptor structure sizes are
unchanged.

## Conformance

`AgentLang.OwningLists.Tests` passes **1,725 assertions** at O0 and O2. Its six
list cases cover empty construction and input, nominal records with options and
string lists, nested lists with empty and Unicode strings, records containing
list/option/result values, and zero-width Unit elements. The tests check native
results against the interpreter and independent byte-layout oracles, exact
capacity, and one-byte-short capacity rejection. The list instruction surface
in this slice is `ListEmpty` plus identity/pass-through entries for host-supplied
values; it does not implement general list construction or traversal.

The same suite executes 530 Int64 formatting values across both optimization
profiles (265 values at each profile), including signed boundaries and 256
deterministically generated values per profile. Each result is compared with
the interpreter and invariant signed-decimal and UTF-16 byte oracles.

The native C owning-stack scanner suite passes **58 cases / 872 checks**. The
fixture pins the list header, layout ABI5, descriptor kind 10, context/module
ABI stability, and ABI4 rejection. The existing native SQLite room-lookup
receipt also passes; it proves the typed AOT entry and membership-filtered query
only, not a Campfire endpoint. See [report 249](249-native-sqlite-room-adapter.md).

The full LLVM test runner passes **1,010 assertions**. The local Campfire
regression gate passes all **22 checks**, including **466 HTTP integration
assertions**. The owning mailbox gate also passes with fresh native artifacts.
An arena validation retry caught a stale `mailboxLayoutAbiVersion=4` expectation in the
native-value-stack experiment after the ABI change; the expectation was corrected
to ABI5 and the failing log was retained. The corrected owning-value-stack gate
passes **58 checks**, including fresh list conformance, C O0/O2 and UBSan runs,
compiler-proven arena lifetime cases, and cross-backend comparisons. Its source
hashes remain stable throughout validation.

The [sealed evidence](evidence/251-native-owning-lists/manifest.json) preserves
source snapshots, final gate results, generated textual artifacts and failed
attempts. Its manifest records member hashes and archive readback verification.

## Remaining boundary

The list fixture is generic compiler/runtime conformance, not a Campfire message
list adapter. It does not build the room page's message list, and no list payload
has been measured through mailbox begin/resume. Native HTTP parity and native
throughput remain unmeasured.

The next compiler/application step is to implement the exact nine host
operations in the shared `campfire.http.room-read` closure, then connect that
path to native HTTP transport and rendering. Prove room-response parity before
measuring the endpoint. See the [AOT delivery plan](../docs/CAMPFIRE-AOT-PLAN.md).
