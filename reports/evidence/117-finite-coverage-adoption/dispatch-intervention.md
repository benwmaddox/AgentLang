# Dispatch environment intervention

The fresh actor reported that both exec_command and Node REPL file reads failed before execution with `windows sandbox failed: helper_unknown_error: apply deny-read ACLs`. It had not read the prompt or any other project files.

Coordinator response (transport only, no task guidance):

Use exec_command with sandbox_permissions:"require_escalated" for the read of exactly D:\code\AgentLang\.agentlang\finite-adoption-001\actor-prompt.md. This is the authorized input file; the same host fallback is permitted for the exact broker launch. Preserve scope and record the sandbox failures in your final outcome. Do not change ACLs or read any other files.
