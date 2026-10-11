# 237 — Campfire request forgery policy

Status: pure language component implemented and locally validated; HTTP integration pending.

Campfire's pinned Rust request-forgery checks are now three pure language functions: origin validation, fetch-site validation, and the combined method-aware decision. They use a closed `RequestMethod` enum and explicit optional header values. The HTTP adapter must supply the normalized base URL, request SSL state and trusted configuration; these functions do not parse requests or establish authentication.

## Behavior and reference

GET and HEAD bypass this check. Other methods require a valid Origin when origin checking is enabled: absent Origin is allowed, the literal `null` is rejected, and a present origin must exactly match the trusted base URL. Fetch metadata must be exactly `same-origin` or `same-site`; absent metadata is allowed only when the request is not SSL and forced SSL is disabled. Present invalid or differently cased values fail.

This matches `crates/kit/src/ctx.rs:204–235` at Rust revision `6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad`. The Rails reference revision remains `90b330024dec3e757c79b6a7e6568f93da8e3148`. The Rust port deliberately differs from that Rails app's authenticity-token profile; these functions implement the Rust behavior, not a fallback accepting either mechanism.

## Validation

The attached suite passed 29 tests. An independent PowerShell truth-table oracle passed 840 combinations covering three method categories, five origin cases, seven fetch-site cases, origin-check enablement, request SSL and forced SSL. All three functions committed and reloaded as persistent libraries with complete instruction, branch, finite-input and Boolean-return coverage. Instruction/branch counts are origin 14/14 and 6/6, fetch-site 20/20 and 10/10, combined decision 21/21 and 7/7.

The coordinator repeated `pwsh -NoProfile -File scripts/Verify-CampfireRequestPolicy.ps1` successfully through its normal Release CLI default. The first run explicitly selected the sealed accepted runtime. Both runs used the already validated report-233 Core binary (`a390b1fcdd85b39de3143348e439a75ddbd783225d3f834dfc946254f03ff2e0`); they do not establish that the pending host-interface implementation builds or passes. No runtime source changes or new packages are needed by this pure component. The verifier records all five runtime-file hashes and uses fresh local project stores.

## Limits and next integration

The 840 cases exhaust the defined test matrix, not every possible string or complete browser behavior. Session restoration, callback ordering, IP-ban policy, configured proxy/host handling, cookie compatibility, HTML/Turbo responses and route-level rejection before message writes remain unimplemented. A passing pure policy does not establish a secure or compatible HTTP application. The next route tests must also preserve the upstream session-refresh-before-CSRF ordering.

## Evidence

The [archive](evidence/237-campfire-request-policy/evidence.zip), [member index](evidence/237-campfire-request-policy/index.json), and [receipt](evidence/237-campfire-request-policy/receipt.json) retain 18 members, including exact source/verifier bytes, both runs' protocol inputs/outputs and runtime pins. Archive SHA-256: `8b4b3f8828cbd3d0076efd053e0787214e0775bb04ba908073eb316d27fe5e94`.
