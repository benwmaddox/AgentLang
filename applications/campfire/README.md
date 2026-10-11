# Campfire implementation

The first language component is the pure message-administration policy below. `Persistence/` now contains a standalone trusted SQLite adapter for bounded room/message reads and transactional message creation. It is not yet callable from language code; the typed host bridge is the next integration step. Authorization, HTTP handling and browser behavior remain application work.

Run the storage component's local checks with:

```powershell
dotnet run --project tests/AgentLang.Campfire.Persistence.Tests/AgentLang.Campfire.Persistence.Tests.fsproj --configuration Release
```

The tests create fresh local databases from a narrow schema fixture. Production opening requires an existing host-selected database. See [storage report](../../reports/235-campfire-sqlite-persistence.md) for the tested behavior and remaining integration, and [implementation plan](../../docs/CAMPFIRE-IMPLEMENTATION.md) for the full migration sequence.

## Request forgery policy

`request-policy.flow` separately implements the pinned Rust port's pure Origin/fetch-metadata checks. Run `scripts/Verify-CampfireRequestPolicy.ps1` after a Release CLI build. It qualifies the three functions as libraries and checks an independent 840-case matrix. See [request-policy report](../../reports/237-campfire-request-policy.md); HTTP/session handling is still pending.

## Message administration policy

`message-policy.flow` contains the pure Flow/2 policy for whether a given user may administer a message. It does not implement cookie/session verification, room membership, account or user status checks, message reads or writes, HTTP, WebSockets, or rendering. A `true` result establishes only this policy decision.

`UserId` is a nominal `Int` wrapper matching the Rust/SQLite integer identifier boundary. `ExistingMessage` is intentionally a narrow policy projection that contains its persisted creator ID; it is not the full Campfire `Message` model. `user.can-administer?` preserves the generic record rule, including creator absence and new-record cases. `message.can-administer?` applies it to an existing message, with `is-new = false`, as the edit/update/destroy controllers do after resolving that message inside its room.

The behavior attribution is to 37signals, LLC. The pinned upstream repository is distributed under its [MIT license](https://github.com/basecamp/once-campfire-rust/blob/6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad/MIT-LICENSE). The rule is independently expressed in Flow and follows these pinned source locations:

- Rust repository commit `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`: `crates/db/src/models/user.rs` (`User::can_administer`), `crates/db/src/models/message.rs` (`Message.creator_id`), and `crates/campfire/src/controllers/messages.rs` (`ensure_can_administer`).
- Rails reference submodule commit `90b330024dec3e757c79b6a7e6568f93da8e3148`: `reference/app/models/user/role.rb` (`can_administer?`) and `reference/app/controllers/messages_controller.rb` (`ensure_can_administer`).

The rule is `administrator || creator_id == current_user.id || is_new`. Rails compares the current user with `record.creator`; the Rust port expresses the creator comparison with user IDs. The Flow tests cover all role, creator, and new/existing combinations plus the existing-message wrapper. Run `scripts/Verify-CampfirePolicy.ps1` after a Release CLI build to execute attached tests and an independent CLI truth-table oracle; pass `-CliDll` to use a different local CLI build.
## Cookie compatibility

`Cookies/` contains the standalone trusted host component for signed `session_token` and encrypted `_campfire_session` compatibility. Its local executable suite checks Rails golden vectors and failure cases; it is not yet connected to HTTP/session restoration. See [cookie report](../../reports/238-campfire-cookie-compatibility.md).

```powershell
dotnet run --project tests/AgentLang.Campfire.Cookies.Tests/AgentLang.Campfire.Cookies.Tests.fsproj --configuration Release
```
## Authentication storage

`Authentication.Persistence/` provides six fixed SQLite operations for session/user lookup, conditional session refresh, active bot keys, banned IPs and membership-scoped room lookup. They are locally tested but not yet registered with the runtime or connected to HTTP. See [authentication storage report](../../reports/239-campfire-authentication-storage.md).

```powershell
dotnet run --project tests/AgentLang.Campfire.Authentication.Persistence.Tests/AgentLang.Campfire.Authentication.Persistence.Tests.fsproj --configuration Release
```
