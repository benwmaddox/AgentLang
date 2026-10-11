# 226 — Per-item scheduled renewal batch

Preparation for one fresh AgentLang/F# pair. The only new API is a batch that
receives each renewal together with its own instant. The question is whether
both arms preserve all inherited behavior while applying list order and each
item's exact instant.

The starting projects are copied from the retained and F# baselines prepared
for trial 220. The retained baseline must keep fingerprint
`b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611`; no 217
migration or historical outcome is a seed. Existing definitions, tests,
examples, fixtures, and project metadata remain unchanged. The only F# source
files participants may edit are `Operations.fs` and `SelfTests.fs`; changes are
additions for the scheduled API.

The operation processes input order, applies each item's own instant, returns
the first exact business error, and never reports an intermediate Store as a
successful result. An empty list returns the complete original Store. The
complete Store contents, unrelated state, and immutable input must be
preserved. Input order is evaluated through sequential renewal and first-error
behavior; the F# Store's keyed collections do not expose insertion order.

Preparation pins fresh CLI and conventional runtimes built by the main task,
validates pristine baselines through the actual V2 brokers, and calibrates the
independent seven-case oracle against a correct control and four bounded
mutation controls. Freezing is allowed only after those receipts pass. One
Luna/max context per arm uses the unchanged V2 broker, 100 exchanges, equal byte
and time limits, no external capabilities, virtual I/O, no network, and no
packages. No participant has been dispatched by preparation.
