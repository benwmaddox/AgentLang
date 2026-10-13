# Interpreted Campfire HTTP control

This local control hosts the same language application through .NET/Kestrel
and the existing interpreted engine. It is not conventional compiled F#,
Rust Campfire, or Rails Campfire. Its existing persistence provider opens
connections per operation with pooling disabled; the native host instead uses
worker-owned prepared connections. Report this difference with measurements.

Build with `dotnet build experiments/AgentLang.Campfire.HttpReference/ReferenceHost.fsproj -c Release`.
Run `ReferenceHost.dll` with `--db`, `--secret-file`, `--project` (a disposable
dictionary), `--source` (`applications/campfire`), `--port` and a dedicated
`--stop-event`. Use an existing copied fixture and private synthetic secret.
Never run both controls against one mutable database file. Verify the loaded
SQLite binary and effective durability/isolation settings before comparing.

The runner prints a READY record after starting and STOPPED after its dedicated
event is signalled. Keep one owner responsible for the listener. Save redacted
corpus and measurement outputs; do not commit private cookie configurations,
database files or secret files.
