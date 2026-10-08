Updated the authorized `conventional-2` trial project through its broker. `queueRemindersForPair` now calls `queueOnce` for each invoice in order. The pair tests cover an ineligible first invoice followed by an eligible second, exact empty-marker preservation, duplicate IDs, repeated runs, call counts, and unrelated files. The pair marker now reports five executed tests.

Broker validation passed: `dotnet run --project StatefulPilot.fsproj --no-launch-profile` exited 0. I reread both changed files, then closed the broker and observed exit 0. The execution capability boundary and public types were left unchanged; I did not add a denied-capability test.

The initial sandbox launch failed before running; the Node runtime also exited unexpectedly. The explicitly authorized host launch of the exact broker command succeeded. I reused the existing `queueOnce` operation.
