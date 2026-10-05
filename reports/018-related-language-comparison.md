# Related language comparison

Date: 2026-10-05. Scope: official-documentation comparison prompted by the expression/dot frontend replan. This is not a comparative implementation benchmark or a claim of language novelty.

## Closest architectural relative: Unison

Unison treats definitions as content-addressed syntax trees; names are separate metadata rather than definition identity. Its codebase manager provides search, dependency/dependent inspection, evaluation, and definition updates. Its abilities appear in function types and are checked transitively. These are substantial parallels with an inspectable typed vocabulary and definition-level development. Sources: [definition identity](https://www.unison-lang.org/docs/the-big-idea/), [UCM commands](https://www.unison-lang.org/docs/ucm-commands/), [type-driven search](https://www.unison-lang.org/docs/ucm-commands/find/), [abilities](https://www.unison-lang.org/docs/language-reference/abilities-and-ability-handlers/).

Unison is therefore the most important architectural reference to investigate. Our current design uses stable word IDs with revisions and plain durable source; that differs from content-addressed definition identity. Unison abilities are not evidence that our trusted-host capability policy is automatically supplied by an effect type system. The current PRD's library admission/coverage, task metrics and controlled agent experiments remain separate requirements.

## Closest source/data-model references

**F#:** typed functional definitions, pipes, records/unions and domain wrappers, with interactive execution. This overlap is especially relevant because F# is already our host and conventional baseline. Sources: [functions/pipelines](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/functions/), [domain wrappers](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions), [interactive execution](https://learn.microsoft.com/en-us/dotnet/fsharp/tools/fsharp-interactive/). A new language must demonstrate value beyond making syntax more comfortable: runtime discovery, constrained effects, persistent semantic editing and admission gates need fair comparison with a well-designed F# environment.

**Gleam:** pipelines explicitly insert the carried value as the first call argument, closely matching the proposed dot-call semantics. Opaque types permit smart constructors that validate values before exposing a domain type. Sources: [pipelines](https://tour.gleam.run/functions/pipelines/), [opaque types](https://tour.gleam.run/advanced-features/opaque-types/). This is a useful small-language reference for source design; our preferred dot spelling need not copy the pipe notation or add closures.

**Rust:** method-call syntax provides familiar receiver-first dot expressions, and newtype wrappers distinguish semantic types. Sources: [methods](https://doc.rust-lang.org/book/ch05-03-method-syntax.html), [advanced/newtype types](https://doc.rust-lang.org/book/ch20-03-advanced-types.html). Borrowing readable call syntax does not require adopting ownership/borrowing complexity, traits, or OOP-style runtime dispatch; the new frontend resolves ordinary dictionary words statically. Rust is also a relevant later native-memory comparison, not evidence that dot notation reduces memory.

**Factor:** its vocabulary contains named words, and its listener supports interactive definition, loading, testing and data-stack inspection. Sources: [word vocabulary](https://docs.factorcode.org/content/article-interned-words.html), [listener](https://docs.factorcode.org/content/article-listener.html). It remains a valuable live-development reference from the original stack-language direction, while the new authoring plan intentionally removes compulsory anonymous-stack notation.

## Implication for this prototype

The pieces have strong precedents. The research value is whether a deliberately small combination of typed contracts, effects, runtime discovery, tested admission and retained vocabulary improves the cost of correct changes by external agents. That combination should be evaluated rather than advertised as unprecedented. Keep the F# conventional comparison strong, study Unison's semantic tooling, and use Gleam/Rust as source references while preserving the existing verified semantic IR.
