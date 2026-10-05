# Origin-aware attached test and example compilation

Status: the coordinator’s Core Release build passed with zero warnings or errors, and the focused IR suite passed 102 assertions on 2026-10-05.

`Compiler` now exposes additive `WithSourceOrigins` variants for attached tests, test actual/expected pairs, and examples. The existing entry points delegate with `Map.empty`, preserving behavior for contexts without private markers. The new paths collect markers from the compiler context and each body, reject collisions between context, actual, and expected marker sets, and require an exact origin-map keyset before type checking or lowering. Snapshot validation still fingerprints the context origins filtered through `contextOriginsFromMap`, preserving the original verified-program binding.

The test checker still enforces one output of the expected type and a pure expected expression. Both actual and expected bodies are returned as separately verified bodies bound to the same program. Attached test/example diagnostics that point to a mapped zero-width marker now report its authored span while retaining the original code, word, message, expected values, and actual values. Marker-set and stale-snapshot failures report counts or authored spans without exposing private marker coordinates.

IR regressions build a Flow-like context containing a `Scope` marker, then compile and execute separate scoped actual, expected, and example bodies. They assert authored-only source maps; reject missing, extra, colliding, and stale origins; verify authored-span remapping for invalid actual, expected, and example bodies; retain the pure-expectation gate; and compare explicit empty-origin APIs with the legacy APIs.

Validation command:

```powershell
dotnet run --project tests/AgentLang.IR.Tests -c Release
```

Result: exit code 0; 102 assertions passed, including `PASS attached Flow source origins`. Evidence: `reports/evidence/026-ir-focused.json`. The integrated validation gate is coordinated separately. This change adds compiler APIs and IR regressions; it does not wire Flow lowering or runtime consumers to the new APIs, or complete the broader Flow authoring rollout.
