# Independent tooling review

Initial read-only review: not actor-ready before focused controls, source
snapshots and clean-source freeze validation complete.

The reviewer independently confirmed the predecessor hash, all 10 S06 and 54
S07 cases/signatures/defaults, and the exact 506-byte six-declaration seed.
Raw Git blob bytes are used for snapshot verification. Metadata failures and
behavior outcomes remain separate.

Findings to resolve before launch:

- Assert zero authored words and the exact six-type baseline, rather than merely
  preserving whichever definitions a cached seed contains.
- Exercise invalid required pins and confirm zero runtime sessions/oracle cases.
- Validate declared Luna/max/no-inherited-turn fields; host traces do not prove
  the external actor's actual model configuration.
- Require the documented idle Ctrl+C teardown for ordinary completion and
  distinguish any explicit host-bound termination from EOF.
- Refuse overwriting verification and preflight evidence, preserving failures.
- Include the preflight script in the committed source snapshot inventory.

Resolution and focused execution results are recorded after implementation;
this initial review does not claim readiness or new agent outcomes.

Implementation follow-up: exact schema-only seed, declared model fields, ordinary
idle Ctrl+C teardown, collision guards and the 13-row source map are implemented.
The focused matrix passed 59 checks/14 outcomes, including missing required pins
with zero process/session/case execution. Valid-pin and one-field-tamper controls
remain pending after the clean source commit; this does not certify launch readiness.

Final bounded review found no additional blocker in the six guarded areas.
Positive freeze/tamper execution remains required. A subsequent coordinator
preparation failure exposed a new-field PowerShell assignment defect; it occurred
before freeze or actor launch and requires a matching source/snapshot repair.
