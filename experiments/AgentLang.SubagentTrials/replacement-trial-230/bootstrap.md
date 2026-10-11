# Common participant bootstrap

Launch the packet's exact PowerShell command through `exec_command` with
`tty:true`. Do not pipe requests or redirect stdin. Keep the returned
`session_id`; send every request with `write_stdin` to that same session. If an
observation times out, continue with that session and do not restart the
process. Send `{"op":"host.close"}` through `write_stdin` to that same handle
and observe terminal exit; the coordinator audits closure.

Use only the assigned project through its supplied broker. Read the assigned
packet and project only. Do not inspect sibling projects, reports, controls,
graders, or other agents. Do not use web tools. Use one session and no more
than 100 exchanges. No external effects are granted.

Poll the same live handle after an observation timeout. Do not restart for
silence, repeat an uncertain mutation, or kill the process to finish. If
closure fails, report it and preserve the session and trace. In your final
response, report changes, validation, and unresolved failures.
