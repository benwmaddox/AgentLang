# Campfire authenticated HTTP evidence

This archive preserves the local milestone 242 source snapshots, build/test logs,
receipts, and synthetic HTTP/provider traces. The final combined receipt is
`validation/root-combined-03/receipt.json`; the fresh native regression is
`validation/root-llvm-04/receipt.json`. Earlier failing attempts are retained.

The tests use public synthetic Rails cookie fixtures and temporary databases.
No production secrets, database files, or executable artifacts are included.
The manifest records every ZIP member hash and the verified archive hash.
This evidence covers a two-route interpreter HTTP slice, not full migration.
