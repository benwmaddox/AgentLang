# Business fixture contract

**Status: prototype contract, version 1.** The F# reference domain remains the authority for constructor and transition behavior. This document describes a JSON-neutral typed representation for fixtures, observations, and comparisons. It does not claim that the language runtime can yet parse or reproduce this schema.

The implementation is `AgentLang.Business.Contracts`, a small F# library that depends on `AgentLang.Business` and has no dependency on AgentLang Core. Its document schema is `agentlang.business.fixture`, version `1`. `Contract.parse` accepts a complete document and returns either a typed `FixtureDocument` or a structured diagnostic with a code and JSON path. Missing required fields, unknown fields, duplicate keys, wrong JSON kinds, unsupported versions, duplicate entity IDs, missing references, invalid state combinations, and arithmetic mismatches fail closed.

The root object requires `schema`, `version`, and every collection: `customers`, `products`, `subscriptions`, `invoices`, `payments`, `pendingEmails`, `sentEmails`, and `providerOutcomes`. Empty collections are represented by `[]`; the full parser never fills defaults. Each record requires all fields in its schema. Identifiers use strings, money uses signed integer minor units, quantities use positive Int32 integers, and instants use ISO 8601 strings with an explicit `Z` or numeric offset. A document and each collection are limited to 4 MiB and 10,000 entries respectively; invoice lines have the same item limit.

| Collection | Fields |
| --- | --- |
| `customers` | `id`, `email`, `kind`, `balanceMinorUnits`, `createdAt` |
| `products` | `id`, `name`, `unitPriceMinorUnits` |
| `subscriptions` | `id`, `customerId`, `productId`, `term`, `startedAt`, `expiresAt`, `status`, `cancelledAt` |
| `invoices` | `id`, `customerId`, `lines`, `totalMinorUnits`, `createdAt`, `status` |
| invoice `lines` | `productId`, `description`, `quantity`, `unitPriceMinorUnits`, `lineTotalMinorUnits` |
| `payments` | `id`, `invoiceId`, `amountMinorUnits`, `providerReference`, `paidAt` |
| `pendingEmails`, `sentEmails` | `to`, `subject`, `body` |
| `providerOutcomes` | tagged records: `payment-accepted`, `payment-declined`, `email-accepted`, or `email-failed` |

The exact tagged fields are determined by `kind`: `payment-accepted` has `reference` and `amountMinorUnits`; `payment-declined` and `email-failed` have `message`; `email-accepted` has no additional fields. Accepted payment references are trimmed like the reference receipt constructor. Decline/failure detail strings are preserved verbatim because the reference propagates provider diagnostics unchanged. Provider outcomes are fixture values only. Parsing or projecting this contract does not invoke a provider, perform I/O, or claim provider behavior parity.

## Canonical form and reference policies

Valid GUID forms accepted by `Guid.TryParse` are input forms, not errors. Canonical JSON emits every nominal ID as lower-case `D` format (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`). Explicit-offset instants that denote the same instant are accepted and emitted in UTC using the reference's round-trip timestamp format. Property order is fixed, entity collections are sorted by nominal ID, and byte hashing uses UTF-8 SHA-256 in lower-case hexadecimal. Email and human-readable names follow the reference constructors' policy: email is retained as provided; Customer `kind`, Subscription `term`, Product `name`, invoice line `description`, payment provider reference, and email subject/body are trimmed at the same boundary as their reference constructors.

Money is a signed Int64 count of minor units; no floating-point money representation is accepted. Signed values are permitted for customer balances and generic arithmetic, while product and invoice line prices must be nonnegative and recorded payment amounts positive. Line multiplication and invoice summation use the reference's checked arithmetic, and the contract verifies both each line total and the invoice total. Quantities must be positive and fit Int32 before conversion.

Email validation deliberately matches the modest policy in `Business.fs`: ASCII local/domain characters, a single `@`, at least two nonempty domain labels, no whitespace or empty dot segments, and a maximum total length of 254 characters. It is not a full Internet mail-standard implementation. `Email.create null` is rejected safely.

Raw `Customer.kind` and `Subscription.term` are persisted input data. Values such as `premium`, `annual`, or `monthly` do not activate hidden discount, renewal, or eligibility rules. Customer has no `status` or `active` field in this reference model; strict parsing rejects such fields.

## Projection and deterministic data

`Factory.baseline()` supplies fixed GUIDs and UTC instants for one customer, one product, and one subscription. It deliberately supplies ordinary raw `kind`/`term` values and does not implement benchmark answers. Repeated calls produce the same canonical projection and hash.

`Projection.store store knownIds` uses only public domain accessors and the supplied identity ledger to look up Customers, Products, Subscriptions, Invoices, and Payments. The ledger is required because `Store` exposes lookup by ID but no map enumeration API. Projection rejects duplicate ledger IDs, missing ledger entries, or a mismatch between ledger coverage and `Store.summary`; pending/sent email lists are obtained through their public ordered accessors and checked against summary counts. Thus this is a checked projection over caller-supplied IDs, not independent evidence that those IDs enumerate private maps.

The contract has typed wire shapes for all six entity kinds, including invoice snapshots, payment records, email messages, and provider outcomes. The reference domain keeps some constructors/entities private and has no APIs to restore arbitrary historical invoices or payments. Parsing validates the complete wire contract, but that is distinct from reconstructing a whole historical `Store`. Actual-domain projection only covers states reachable through the public reference API and a complete identity ledger.

## Strict documents and fragments

Use `Contract.parse` for complete documents. Use `Fragment.parseCustomerFragments` and `Fragment.expand` only when a fixture author intentionally supplies sparse customer fragments. The fragment parser has its own explicit field list and rejects unsupported fields such as `status` and `active`. Expansion fills omitted values deterministically and returns the original UTF-8 input SHA-256 plus a per-field list of `DefaultsApplied` provenance records. Its defaults are test fixture data, not business policy. It does not silently alter strict-document parsing.

## Current limits

This contract is standalone groundwork. It is not wired into AgentLang Runtime, the benchmark runner, or an agent protocol. The provider outcome union records data and does not test provider invocation. Full document validation is not proof that private reference-domain entities can be reconstructed. Cross-language equivalence, a complete business fixture in AgentLang, and an executable benchmark suite remain unverified.
