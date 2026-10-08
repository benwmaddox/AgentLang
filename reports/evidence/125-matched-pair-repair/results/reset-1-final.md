The broker task completed and the host exited. I replaced `invoice.queue-reminders-for-pair` at revision 2, preserving its signature and `fs.read`/`fs.write` effects. Each invoice now qualifies independently, in order.

The corrected regression and new tests cover an open second invoice after a closed first, exact case-sensitive statuses, duplicate IDs, repeat runs, existing marker contents including an empty string, and provider call counts. Target tests passed **7/7**, and `test-all` passed **9/9**. The committed function has full coverage: **49/49 instructions, 8/8 branches**, with finite coverage complete; maturity remains `library`.

The sandbox launch failed, so I used the authorized scoped host launch. One initial definition attempt had a test-syntax error; I corrected it before staging. The broker ran with both capabilities, so missing-capability rejection was not directly exercised; the committed function retains both declared effects.
