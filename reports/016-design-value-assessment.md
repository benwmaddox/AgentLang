# Design value assessment

Date: 2026-10-05
Status: engineering assessment, not a measured agent-performance result.

## Evidence so far

The published implementation at `9c6ba7569b0b28c485a2dec753e001043762e2e4` includes nominal/refined types, checked stack compositions, effects and capability denial, word metadata and discovery, persistent definitions/history, task rollback, and library test/own-body coverage gates. Verified semantic IR is now the runtime execution representation; no AST execution fallback remains. [Report 010](010-runtime-ir-cutover.md) records 18 validation checks, including 494 language assertions, and 314 selected parity checks. These checks establish their scoped behaviors, not general correctness or production readiness.

[Report 004](004-subagent-vocabulary-pilot.md) demonstrates an external subagent creating two tested library words and a different external agent discovering and composing the retained words. The second agent retained earlier repository context. No matched Flat/Growing/Conventional performance comparison was completed, and exact model tokens, framework turns, and total context were unavailable. The observed workflow is useful evidence; cost savings and smaller-context success remain unproven.

A new isolated probe constructed a recursive Chain record with an Option<Chain> tail using approximately 2,403 expression operations. Despite remaining below the execution fuel limit, conversion of private runtime values into public values overflowed the host stack. Report 015 is reserved for the repair and validation. This shows why operation fuel alone is insufficient: value depth, expanded size, and output processing also need bounds. The newly discovered defect is not covered by the previously passing validation result.

## Where the value likely lies

**Inspectability is the strongest hypothesis.** Stable structured answers for signatures, effects, dependencies, callers, tests, source, and history can reduce repeated reconstruction of project behavior. The goal is inexpensive retrieval of the relevant contract and implementation. Merely allowing inspection of everything does not establish a small context requirement. Retrieval completeness, response size, and discovery cost must be measured.

**Reusable vocabulary with strong contracts is closely related.** A tested domain operation can preserve project knowledge as executable behavior. Nominal/refined types make its inputs more informative and reject accidental substitution of base values. The experiment must distinguish this benefit from ordinary well-designed F# APIs with equivalent tests and documentation; reusable abstractions are not exclusive to this language.

**Interactive definition replacement is a plausible iteration benefit.** The intended loop is inspect, replace, validate, run. Coherent runtime revisions, dependent-caller validation, and rollback make that loop useful. Whole-task wall time, validation time, and tool interactions must be observed before claiming it is faster than a conventional workflow.

**Library coverage improves the admission gate, but cannot establish intended behavior by itself.** Own-body instruction and supported branch-outcome coverage requires tests to exercise implementation paths. Wrong expectations can still agree with wrong code. Independent acceptance, boundary cases, type/effect checks, and applicable properties remain necessary. Coverage does not prove all inputs or all transitive dependency behavior. The stricter library quality gate and lighter project-word gate should remain separate from temporary/persistent lifetime.

**A stack execution model does not prove low memory usage.** A small instruction set can simplify the interpreter, but stack entries can reference large heap values. The current F#/.NET implementation has managed allocation and garbage collection. [.NET's memory documentation](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals) describes managed-heap allocation and reclamation. Runtime footprint, allocation rate, and peak memory need their own measurements. The conditional LLVM release design is not implemented and provides no current native-performance evidence.

**RPN syntax should remain an experimental choice.** Short compositions may be easy to inspect; longer ones may impose stack-position reasoning that named values avoid. The typed semantic IR is the durable architecture boundary. A future named-argument or expression frontend could lower to that same IR. This assessment does not authorize building multiple frontends now or changing established syntax without evidence.

## Next evidence needed

The immediate priorities are repairing bounded value processing, completing structured expected-value tests, and validating the matched trial fixtures and external host described in [report 014](014-matched-trial-readiness.md). Fresh agent contexts remain unavailable after the framework's confirmed thread-limit refusal; reused contexts must not be described as fresh trials.

The full PRD still requires the complete business vocabulary, executable acceptance task bank, controlled three-way evaluation, context-budget trials, and reuse/error-recovery/pollution metrics. A small fair comparison should happen before adding optional language sophistication. LLVM remains a conditional later backend. Neither the pilot nor current validation meets the PRD's 20% improvement or smaller-context success criteria.

This assessment recommends prioritizing inspectability, strong contracts, and retained vocabulary while treating syntax, update speed, and memory efficiency as separately testable claims. It does not narrow the active PRD goal or declare evaluation complete.

## Requested late research item

The user subsequently requested a later syntax study: preserve solid semantic IR and the stack's useful tendency to interact with recent data, while investigating whether RPN is desirable throughout the source language. The PRD now records this study explicitly, including alternative named/expression/pipeline syntax, shared semantic conformance, controlled agent task measurements, and separate memory measurements for the conditional LLVM backend. This is a research commitment after the initial experiments, not a current syntax migration. F# host/compiler implementation and LLVM execution backends are distinct architectural choices.
