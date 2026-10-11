# Campfire message administration policy slice

Implemented the pure message-administration rule as the first application component in Flow/2. This delivers policy only; it does not authenticate sessions, resolve room access, read or write messages, implement HTTP/WebSocket behavior, or establish Campfire parity.

The tracked [evidence ZIP](evidence/234-campfire-policy/campfire-policy-evidence.zip) contains the Flow source, verifier, raw CLI requests and responses, pinned-source receipts, runtime hashes, both successful policy runs, and the initial verifier inventory error. Check the [member SHA-256 manifest](evidence/234-campfire-policy/members.sha256.json) and [archive receipt](evidence/234-campfire-policy/archive-receipt.json) alongside it.

The Flow source is `applications/campfire/message-policy.flow` (SHA-256 `64057aae104f085cf73828a5385bc7dd7ecef88c002f3a125da805601bcdb77e`). It defines nominal `UserId`, the `member`/`administrator`/`bot` roles, a `User`, and an `ExistingMessage` creator-ID projection. `user.can-administer?` implements `administrator || creator-id == user.id || is-new`. `message.can-administer?` applies that rule to an existing message with `is-new = false`.

Behavior is attributed to 37signals, LLC under the pinned upstream repository's [MIT license](https://github.com/basecamp/once-campfire-rust/blob/6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad/MIT-LICENSE). The Rust reference is pinned at `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad` (`crates/db/src/models/user.rs`, `crates/campfire/src/controllers/messages.rs`); the Rails submodule is pinned at `90b330024dec3e757c79b6a7e6568f93da8e3148` (`reference/app/models/user/role.rb`, `reference/app/controllers/messages_controller.rb`). Both local checkouts matched these pins during verification.

## Validation

`pwsh -NoProfile -File scripts/Verify-CampfirePolicy.ps1` passed on 2026-10-11 using the default Release CLI path. The verifier defined the Flow/2 source, passed all 24 attached cases, committed the helper and wrapper as libraries, then started a fresh CLI process. In that process all 24 tests passed again, `describe` reported the helper and wrapper at `library` maturity with 18 and 6 tests respectively and no effects, and the independent oracle evaluated all 18 role/creator/lifecycle inputs using structured Bool values against the separately computed PowerShell rule.

The same policy verifier also passed against the preserved previous Release runtime at `.agentlang/test-preservation-233/controls/runtime/previous/AgentLang.Cli.dll`, with the same Flow SHA-256 and 24/24 attached plus 18/18 independent cases. This provides an independent runtime snapshot check while the broader 233 validation remains separate.

The final normalized-source default-run evidence is retained at `.agentlang/campfire-234/runs/20261011T205847Z-23b54e95/verification.json`; the matching preserved-runtime receipt is `.agentlang/campfire-234/runs/20261011T205856Z-ece92e53/verification.json`. Both runs used Flow SHA-256 `64057aae104f085cf73828a5385bc7dd7ecef88c002f3a125da805601bcdb77e`, both pinned local reference checkouts matched, and each passed 24 attached plus 18 independent cases. The five runtime files for each run were snapshotted before execution and hashed. The default Release snapshot was:

| Runtime file | SHA-256 |
| --- | --- |
| `AgentLang.Cli.deps.json` | `6382f6dc29bb8f827824839010e313b266e51a3f87a4fdb8c382931d2549e97e` |
| `AgentLang.Cli.dll` | `d3266837ee364928c00d4a32ba7a7fb434b24f7d59c50f12edf5f50f96367c4e` |
| `AgentLang.Cli.runtimeconfig.json` | `3c0bf772b95c93363a0a14e5df78e4ec408c90f04cb7103205af97b2d276fab0` |
| `AgentLang.Core.dll` | `a390b1fcdd85b39de3143348e439a75ddbd783225d3f834dfc946254f03ff2e0` |
| `FSharp.Core.dll` | `bf72714c36dc21357a6530d59479e1d7fc5e6bc815a9af9a872e5e41611c1e6f` |

For a normal clean checkout, build `AgentLang.Cli` in Release and run `pwsh -NoProfile -File scripts/Verify-CampfirePolicy.ps1`; the script defaults to `src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll`. `-CliDll` selects another local CLI assembly. The pinned commits remain in verifier metadata, and local reference checkout verification is optional.

The first verifier attempt rejected a correct inventory because its checker expected the key `none` while Flow's attached test name is `no-creator`. The captured CLI output already reported 24/24 passing tests; this was a verifier mapping error, not a language or policy failure. The mapping was corrected, and the final normalized-source runs above include a complete inventory check. Older successful runs against the pre-normalized source are omitted from the tracked archive.
