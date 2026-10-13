# Native HTTP response-cookie conformance

Build this project in Release, then run the test DLL with three paths:
private synthetic request configuration, private synthetic secret, and a new
redacted receipt file. The paired fixtures must already be running at
`127.0.0.1:13773` (native) and `127.0.0.1:13774` (interpreted control).
This test never starts or stops listeners. Proxies and redirects are disabled.

The checker sends the generated fixture cookie to those two loopback endpoints.
The secret remains local and is used by the existing CookieService to decrypt
response state. Neither credential is included in the receipt or console output.
Keep all input configurations and fixture secrets in ignored private directories.

Six paired cases cover unauthenticated GET/HEAD remembered return URLs,
missing/nonmember room alert cookies, and authenticated room GET/HEAD. Checks
include decoded session/flash state, last_room, cookie attributes, calendar-valid
Expires weekdays and native HEAD Content-Length matching GET. The control's
different HEAD framing is reported explicitly. Run against both O0 and O2 hosts;
use distinct receipt filenames to retain each result.
