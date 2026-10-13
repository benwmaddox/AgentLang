# Local Campfire HTTP measurement client

Build with `dotnet build experiments/AgentLang.Campfire.HttpMeasure/HttpMeasure.csproj -c Release`.
Run the resulting DLL with a private JSON configuration path and a new result
path. `--self-test` verifies histogram indexing and quantiles.

The configuration names a loopback URI, Cookie, UserAgent, ExpectedStatus and
ExpectedBodySha256; optional fields are Concurrency, Seconds, WarmupRequests,
ServerPid, Method and MaxBodyBytes. Configuration files may contain synthetic
authentication cookies and must remain in ignored private run directories.

Each HTTP/1.1 keepalive request checks the complete response body and status.
The client disables redirects, proxies, decompression and automatic cookie
updates. Measurements are closed loop and include draining in-flight requests.
Failures are counted separately; latency quantiles describe correct responses
only, using histogram upper bounds. Server process memory is sampled every
100 ms and excludes HTTP.sys kernel buffers. The .NET client is a measurement
tool; it is not part of the native service runtime.

Use matched fixtures and report worker count, SQLite settings, runtime profile,
response size, hardware and failures. Repeat samples and retain raw results.
These measurements cannot establish a speed ratio against published Campfire
results unless hardware and workload are comparable.
