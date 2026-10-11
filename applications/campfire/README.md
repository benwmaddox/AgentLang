# Campfire message administration policy

This directory contains one pure Flow/2 policy component from ONCE Campfire: whether a given user may administer a message. It does not implement cookie/session verification, room membership, account or user status checks, message reads or writes, HTTP, WebSockets, or rendering. A `true` result establishes only this policy decision.

`UserId` is a nominal `Int` wrapper matching the Rust/SQLite integer identifier boundary. `ExistingMessage` is intentionally a narrow policy projection that contains its persisted creator ID; it is not the full Campfire `Message` model. `user.can-administer?` preserves the generic record rule, including creator absence and new-record cases. `message.can-administer?` applies it to an existing message, with `is-new = false`, as the edit/update/destroy controllers do after resolving that message inside its room.

The behavior attribution is to 37signals, LLC. The pinned upstream repository is distributed under its [MIT license](https://github.com/basecamp/once-campfire-rust/blob/6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad/MIT-LICENSE). The rule is independently expressed in Flow and follows these pinned source locations:

- Rust repository commit `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`: `crates/db/src/models/user.rs` (`User::can_administer`), `crates/db/src/models/message.rs` (`Message.creator_id`), and `crates/campfire/src/controllers/messages.rs` (`ensure_can_administer`).
- Rails reference submodule commit `90b330024dec3e757c79b6a7e6568f93da8e3148`: `reference/app/models/user/role.rb` (`can_administer?`) and `reference/app/controllers/messages_controller.rb` (`ensure_can_administer`).

The rule is `administrator || creator_id == current_user.id || is_new`. Rails compares the current user with `record.creator`; the Rust port expresses the creator comparison with user IDs. The Flow tests cover all role, creator, and new/existing combinations plus the existing-message wrapper. Run `scripts/Verify-CampfirePolicy.ps1` after a Release CLI build to execute attached tests and an independent CLI truth-table oracle; pass `-CliDll` to use a different local CLI build.
