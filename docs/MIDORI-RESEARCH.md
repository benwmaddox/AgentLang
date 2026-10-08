# Midori: lessons for mailbox and owning-stack research

Status: architectural research, 2026-10-08. This is not benchmark evidence for
AgentLang, and does not change the preferred owning-value contract.

## What the primary accounts establish

Midori used small software-isolated processes, usually with a single message
loop, connected through typed asynchronous interfaces. Isolation exposed
parallel work without requiring application code to coordinate shared mutable
memory. Its type system also supported ownership transfer of isolated object
graphs, with immutable sharing permitted. This is a different contract from
AgentLang's fully owned working values. Joe Duffy describes these choices and
their complexity in [15 Years of Concurrency](https://joeduffyblog.com/2016/11/30/15-years-of-concurrency/).

Application code could not synchronously block. Suspended activities used linked
execution-stack segments starting at 128 bytes and growing to 8 KB chunks;
non-suspending computation used pooled conventional stacks whose count scaled
with processors. Messaging used batching and pipelining. Flow control and limits
on outstanding work addressed overload, but automatic resource management
remained a weakness. These are execution stacks, not an owning program-data
stack. Midori retained independently collected process heaps and reference-counted
immutable shared buffers. Avoiding copies was important to its performance.
See [Asynchronous Everything](https://joeduffyblog.com/2015/11/19/asynchronous-everything/).

Its native compiler exploited guarantees from the language and whole system.
Duffy reports competitive code quality and some advantages over C/C++, rather
than claiming safety inevitably costs performance. See
[Safe Native Code](https://joeduffyblog.com/2015/12/19/safe-native-code/).
These retrospective engineering accounts do not provide a reproducible
processor-scaling curve for our workload. Do not turn them into quantitative
claims about AgentLang or treat contemporary .NET behavior as identical to the
historical implementation discussed there.

## Implications to test here

These are project recommendations inferred from the accounts, not Midori results:

- Make suspension visible in the semantic contract. Do not silently classify
  potentially blocking host work as ordinary non-suspending computation.
- Bound both outstanding operations and queued bytes. Mailbox count alone does
  not bound retained working sets or downstream work.
- Measure running, suspended, retained, pooled and whole-process memory
  separately. The existing keep-associated versus return-at-suspension
  comparison remains required with the same mailbox identity in both arms.
- Count copy/move/compaction bytes as well as allocation and reclamation.
  Fully owned values may simplify cleanup yet cost too much if large payloads
  are repeatedly copied. Destination construction and checked moves must
  preserve ownership rather than introduce hidden sharing.
- Specify whether other messages may change retained mailbox state during an
  await, and test resumed updates against those changes. Private owned working
  values alone do not prevent stale-state logic errors.

Proceed first with the actual owning-stack implementation and semantic/storage
conformance. These lessons guide later async evaluation; they are not a reason
to add an OS, demand a new scheduler now, or substitute a garbage-collected or
shared-object representation for the chosen memory direction.
