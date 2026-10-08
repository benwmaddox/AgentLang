# Populated delivery contract implementation plan

Base: c825e3e. Previous goal turn made progress: report 117 recorded a fresh agent's correct coverage adoption and mutation resistance, with limits. This slice follows the observed impossible Option output and keeps historical research fixtures intact.

Implement one pure trusted list.tail operation using existing typed primitive-call IR. Empty lists return empty; nonempty lists return the positional suffix, preserving element types, order and duplicates without mutating the input. No new native List capability, ABI or syntax is implied.

Add examples/business-populated-delivery.agent as a separately loaded Flow/2 extension. PopulatedEmailDeliveryPlan requires first: EmailMessage and remaining: List<EmailMessage>. A plan function returns Option<PopulatedEmailDeliveryPlan> with empty/nonempty cases. A new delivery transition consumes that plan and preserves the existing business contract. list.tail removes the need for the old optional fold accumulator and its always-Some step. Keep old functions and fixture counts unchanged.

Acceptance: typed rejection of nonlists; empty/singleton/multiple/duplicate/nominal lists; discovery metadata; qualified new library functions with full own branches/finite returns; independent business oracle for empty precedence, provider failure, FIFO success, duplicates, and unrelated fields; exact source/type persistence and library qualification after reload. Validate interpreter separately from unsupported native List behavior.

Ownership: construction_boundary_plan owns primitive Core files, focused primitive tests and docs/CONTAINERS.md. populated_delivery_plan owns the new example and Business.Transitions test integration. Root owns roadmap, structural-status documentation, report 118/evidence, review, full local validation and publication. Existing unrelated retention drafts are untouched.

Validation: fresh affected Debug builds and focused tests, then scripts/Validate.ps1 -Configuration Debug with a saved report, diff checks and source/evidence hashes. Do not rebuild frozen Release/trial runtimes. Commit, fast-forward main and push reports with the milestone after local checks.

Separate pending work: cross-field record validators are not currently supported. Primitive nominal wrappers cannot wrap records; adding a record validator must cover parser, authoritative IR, interpreter, persistence and native construction boundaries. This plan does not claim that broader construction requirement is completed. Record the investigation and retain it as an explicit next structural slice.
