# Revised syntax teaching examples

Status: documentation update; new parser syntax remains pending implementation.

The user requested examples reflecting the revised language direction. Added
`docs/EXAMPLE-SYNTAX-MIGRATION.md` with a complete customer record, two functions,
four tests and an attached example using `fn`, property reads and `==`. Pure
effects can be omitted; an explicit-effects example demonstrates effects then
documentation, a blank line, and executable code.

README, authoring instructions and the earlier frontend migration document link
the preview and identify the implemented Flow/1 boundary. The migration table
covers runnable demos, business fixtures, runtime help and historical evidence.
No parser, runtime, executable `.agent` file, frozen study input or historical
source was changed. Float remains explicitly a toy example; nominal exact-money
and refined-type requirements are preserved. Library declaration intent is
distinguished from qualification and the pending stricter policies.

Validation: reviewed the preview against `examples/customer.agent`, the refined
type and business value examples, and report 085. Checked documentation links
and `git diff --check`. No runtime test result is claimed for preview syntax;
its parser, formatter and execution acceptance checks are listed in the document.
