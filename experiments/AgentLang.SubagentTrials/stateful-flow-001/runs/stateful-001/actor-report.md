Actor: /root/stateful_reminder_01, GPT-6 Luna/max, fork none.

Completed response:

Completed and durably committed `invoice.queue-reminder-once` as a library word.
It uses `invoice::reminder-path`, compares the unwrapped status exactly to `open`,
and reads or writes only inside that branch.

All 4 attached tests passed with 22/22 instructions and 4/4 branch outcomes.
The first-class example passed. The word retains 4 tests and 1 example and
declares `fs.read` and `fs.write`.

One discovery request failed because `search-type` requires a `type` field;
the agent obtained the invoice type details through `context`. No capabilities
were missing. Host session 77850 remained open for coordinator teardown.

The coordinator then requested Ctrl+C only, on the same session. The host
terminated with exit code 0. No additional JSONL request or repair hint was sent.
Independent acceptance passed all 125 checks.
