# Optional elastic mailbox pools

Status: idea to consider, not an adopted V1 feature or runtime implementation.

The user suggested per-mailbox-type minimum/maximum instance counts with fast
scale-up and slow scale-down. Added it to the roadmap and async evaluation
contract as an alternative to fixed capacity, preserving the simpler current
persistent-mailbox/idle-stack-release candidate.

The evaluation must distinguish interchangeable workers from stateful mailbox
identities, preserve retained state and pending work during retirement, and
bound aggregate bytes and I/O as well as counts. More mailboxes on a single
execution thread do not themselves create CPU parallelism. Test workload skew,
burst/idle cycles, startup/retirement costs and late I/O under matched memory,
latency and correctness requirements.

Related precedents: [Kubernetes autoscaling](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/)
supports different up/down policies and downscale stabilization;
[Orleans activation collection](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/activation-collection)
reclaims activations after configured inactivity. These operate at different
boundaries and do not define AgentLang's state-retention semantics.

Validation: local documentation link and diff checks. No runtime or performance
tests were run for this proposal.
