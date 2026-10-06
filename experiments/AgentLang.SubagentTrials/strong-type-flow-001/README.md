# Strong nominal types and refinement trial

This trial asks whether an AI coding agent can use distinct nominal types and a
validated scalar type while editing a small Flow project. The setup script
creates a fresh seed through the normal Flow define, test, and commit protocol.
It does not modify the runtime or launch an agent. The experiment coordinator
adds the frozen source, runtime, host, and prompt pins before a fresh subagent
starts.

The seed contains Email refined from String, distinct Float-backed
MetersPerSecond and KilometersPerHour types, and a Delivery record whose
contact and speed fields require those types. Email validation is an
illustrative shape policy: a value must contain both an at sign and a dot,
contain no spaces, and have neither a leading nor trailing at sign. It is not an
implementation of the Internet email standard. The unit wrappers are nominal
only; they do not provide unit algebra, range validation, or implicit
conversion.

The Email validator is published as a library word only after all six attached
cases pass with complete instruction and branch coverage. The follow-up task
asks the agent to add a pure, tested speed conversion and commit it as a
library word. Separate coordinator-owned acceptance checks cover wrong-unit
and wrong-base-type rejection, invalid Email construction, preservation of the
seed, and behavior after durable reload. This small trial does not establish
the performance of strong types across a full business domain or compare model
tokens, turns, or matched conventional implementations.
