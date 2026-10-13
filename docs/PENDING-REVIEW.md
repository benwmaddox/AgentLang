# Pending review

Design topics the user has asked to revisit later. Each entry says what was
decided for now and why it stays open. Add new items rather than deciding them
silently.

| Topic | Current decision | Revisit because | Since |
| --- | --- | --- | --- |
| Immutable strings | Strings are immutable and bump-allocated on the request data stack. Building a page copies pieces into joins. | Copy volume for large pages. Alternatives: last-use in-place append, or part lists written straight to the socket ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md)). | 2026-10-13 |
| Workflow ceremony | A word must have a passing attached test before commit, and Flow code goes through define, commit and test. | Feels heavy when iterating on UI templates. Kept for now; review against agent authoring experience. | 2026-10-13 |
| Shared response cache | Not implemented. Caching is disabled in all benchmark servers. | A cross-worker cache is a new cross-thread dependency. Discuss it before adopting ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md#cached-parts)). | 2026-10-13 |

## Deferred usability work (user, 2026-10-13)

Noted for after the apples-to-apples Campfire comparison.

- **Enforceable casing rule (proposal).**
  - Type names (records, refined types, enums) are PascalCase:
    `^[A-Z][A-Za-z0-9]*$`.
  - Every other name segment (namespaces, words, fields, locals, enum cases) is
    lowercase kebab-case: `^[a-z][a-z0-9]*(-[a-z0-9]+)*[?]?$`.
  - Operations generated from a type are always addressed through the type's
    exact name: `Pair.new`, `Pair.name`, `Html.new`, `Html.value`,
    `RenewalState.pending()`. Today records use `pair.new` while refined types
    and enums use type casing.
  - Enforcement: a segment starting with an uppercase letter is always a type,
    so the compiler can reject violations (`FLOW_CASING_*`) with a suggested
    fix. `agentlang fmt` rewrites old lowercase record forms during a
    migration window.
- **CLI file workflow.** `agentlang test|check|fmt FILE.flow...` exists (basic
  version, added 2026-10-13). It uses a fresh in-memory project, maps
  diagnostics to file lines, and gives the exit code CI needs. Possible next
  steps: `agentlang run FILE word args`, watch mode, project manifests.
- **Language card.** A one-page, current summary for people and AI agents:
  syntax, stdlib list, naming rules and one complete web example.
- **`effects none` by default.** Flow/2 already treats an omitted `effects` line
  as `effects none`. Make the canonical form omit it (formatter), and update
  docs and examples so authors only write `effects` when a word has effects.
- **Web standard library** (the general host-capability mechanism itself is now scheduled on the apples-to-apples path). Typed,
  app-declared host operations (database queries, HTTP) instead of closed
  Campfire profiles in the compiler, plus routing, request/response,
  forms, cookies/sessions, JSON and time formatting.

## Scheduled removals

| Item | Action | Deadline |
| --- | --- | --- |
| `string.length` | Deprecated alias of `string.byte-length`, introduced with UTF-8 strings. Migrate every source, test and fixture (including hash-pinned frozen fixtures, through recorded transitions) to `string.byte-length` or `string.char-length`, then remove the alias (user, 2026-10-13). | **2026-11-01** |
