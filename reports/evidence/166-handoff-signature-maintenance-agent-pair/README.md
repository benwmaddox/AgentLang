# Subscription handoff maintenance trial 166 evidence

The final Flow scorer passed 132/132 cases (66 handoff plus 66 retained-caller
cases). The final conventional scorer passed 132/132 (66 handoff plus 33 for
each retained wrapper). Attached Flow tests passed 149/149. The saved Flow
metadata coordinator error is preserved in the final-validation logs; its
summary was regenerated from saved responses without rerunning tests.

The ZIP contains the freeze inventory/source, seeds, final actors, pinned
runtimes, prompts/configs, broker traces, scorer/control outcomes, and review
audits. `ARCHIVE-MANIFEST.json` gives every payload entry's source, byte count,
and SHA-256. Temporary directories, generated bin/obj output, and disposable
full control/scorer/broker project copies are excluded. For the historical 163
freeze, only the frozen PLAN/oracle files and manifest are included because
the original ZIP contains full control copy trees.
