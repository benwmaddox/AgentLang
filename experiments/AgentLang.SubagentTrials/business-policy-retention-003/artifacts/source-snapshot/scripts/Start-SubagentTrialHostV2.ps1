[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string[]]$AllowedOperations,
    [ValidateSet('agentlang', 'conventional')][string]$Profile = 'agentlang',
    [string[]]$AdditionalCliArguments = @(),
    [string]$AdditionalCliArgumentsJson,
    [string[]]$Capabilities = @(),
    [string]$ClockValue = '2000-01-01T00:00:00Z',
    [ValidateRange(1, 524288)][int]$MaxRequestBytes = 262144,
    [ValidateRange(1, 1048576)][int]$MaxResponseBytes = 524288,
    [Nullable[long]]$MaxInspectionResponseBytes = $null,
    [ValidateRange(1, 120000)][int]$ExchangeTimeoutMilliseconds = 15000,
    [ValidateRange(1, 100)][int]$MaxExchanges = 100
)

$ErrorActionPreference = 'Stop'

$hostSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLang.SubagentTrialHostV2
{
    public sealed class LineFrame
    {
        public byte[] Data;
        public bool Eof;
        public bool Terminated;
    }

    public sealed class LineLimitException : Exception
    {
        public int Limit;
        public byte[] Prefix;
        public LineLimitException(int limit, byte[] prefix) : base("JSONL line exceeded its configured byte limit.")
        {
            Limit = limit;
            Prefix = prefix;
        }
    }

    public sealed class BoundedLineReader
    {
        private readonly Stream _stream;
        private readonly byte[] _chunk = new byte[4096];
        private readonly object _partialLock = new object();
        private List<byte> _currentLine;
        private int _offset;
        private int _count;

        public BoundedLineReader(Stream stream) { _stream = stream; }

        public byte[] SnapshotPartial()
        {
            lock (_partialLock) return _currentLine == null ? new byte[0] : _currentLine.ToArray();
        }

        public async Task<LineFrame> ReadAsync(int maxBytes)
        {
            var line = new List<byte>(Math.Min(maxBytes, 4096));
            lock (_partialLock) _currentLine = line;
            while (true)
            {
                if (_offset >= _count)
                {
                    _count = await _stream.ReadAsync(_chunk, 0, _chunk.Length).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        lock (_partialLock) _currentLine = null;
                        return new LineFrame { Data = line.ToArray(), Eof = line.Count == 0, Terminated = false };
                    }
                }

                byte value = _chunk[_offset++];
                if (value == 10)
                {
                    lock (_partialLock) _currentLine = null;
                    return new LineFrame { Data = line.ToArray(), Eof = false, Terminated = true };
                }

                byte[] oversizedPrefix = null;
                lock (_partialLock)
                {
                    if (line.Count >= maxBytes)
                    {
                        line.Add(value);
                        oversizedPrefix = line.ToArray();
                        _currentLine = null;
                    }
                    else line.Add(value);
                }
                if (oversizedPrefix != null) throw new LineLimitException(maxBytes, oversizedPrefix);
            }
        }
    }

    internal sealed class CapturedText
    {
        public byte[] PrefixBytes;
        public long TotalBytes;
    }

    internal sealed class ResponseAccounting
    {
        public long RawValidRuntimeResponsePayloadUtf8Bytes { get; private set; }
        public long SelectedResponsePayloadUtf8Bytes { get; private set; }
        public long AdmittedInspectionPayloadUtf8Bytes { get; private set; }
        public long NonInspectionRuntimeResponsePayloadUtf8Bytes { get; private set; }
        public long HostDenialControlResponsePayloadUtf8Bytes { get; private set; }
        public long StdoutPipeDeliveredResponseCount { get; private set; }
        public long StdoutPipeDeliveredResponsePayloadUtf8Bytes { get; private set; }
        public long StdoutPipeDeliveredResponseWireUtf8Bytes { get; private set; }

        public void RecordExchange(long rawValidRuntimePayloadBytes, bool inspection, long admittedInspectionBytes,
            long selectedPayloadBytes, bool selectedHostControlResponse)
        {
            RawValidRuntimeResponsePayloadUtf8Bytes = Add(RawValidRuntimeResponsePayloadUtf8Bytes, rawValidRuntimePayloadBytes);
            if (inspection)
                AdmittedInspectionPayloadUtf8Bytes = Add(AdmittedInspectionPayloadUtf8Bytes, admittedInspectionBytes);
            else
                NonInspectionRuntimeResponsePayloadUtf8Bytes = Add(NonInspectionRuntimeResponsePayloadUtf8Bytes, rawValidRuntimePayloadBytes);
            SelectedResponsePayloadUtf8Bytes = Add(SelectedResponsePayloadUtf8Bytes, selectedPayloadBytes);
            if (selectedHostControlResponse)
                HostDenialControlResponsePayloadUtf8Bytes = Add(HostDenialControlResponsePayloadUtf8Bytes, selectedPayloadBytes);
        }

        public void RecordTerminalHostResponse(long selectedPayloadBytes)
        {
            SelectedResponsePayloadUtf8Bytes = Add(SelectedResponsePayloadUtf8Bytes, selectedPayloadBytes);
            HostDenialControlResponsePayloadUtf8Bytes = Add(HostDenialControlResponsePayloadUtf8Bytes, selectedPayloadBytes);
        }

        public void RecordPipeDelivery(long payloadBytes, long wireBytes)
        {
            StdoutPipeDeliveredResponseCount = Add(StdoutPipeDeliveredResponseCount, 1);
            StdoutPipeDeliveredResponsePayloadUtf8Bytes = Add(StdoutPipeDeliveredResponsePayloadUtf8Bytes, payloadBytes);
            StdoutPipeDeliveredResponseWireUtf8Bytes = Add(StdoutPipeDeliveredResponseWireUtf8Bytes, wireBytes);
        }

        public object Snapshot()
        {
            return new Dictionary<string, object> {
                ["rawValidRuntimeResponsePayloadUtf8Bytes"] = RawValidRuntimeResponsePayloadUtf8Bytes,
                ["selectedResponsePayloadUtf8Bytes"] = SelectedResponsePayloadUtf8Bytes,
                ["admittedInspectionPayloadUtf8Bytes"] = AdmittedInspectionPayloadUtf8Bytes,
                ["nonInspectionRuntimeResponsePayloadUtf8Bytes"] = NonInspectionRuntimeResponsePayloadUtf8Bytes,
                ["hostDenialControlResponsePayloadUtf8Bytes"] = HostDenialControlResponsePayloadUtf8Bytes,
                ["stdoutPipeDeliveredResponseCount"] = StdoutPipeDeliveredResponseCount,
                ["stdoutPipeDeliveredResponsePayloadUtf8Bytes"] = StdoutPipeDeliveredResponsePayloadUtf8Bytes,
                ["stdoutPipeDeliveredResponseWireUtf8Bytes"] = StdoutPipeDeliveredResponseWireUtf8Bytes,
                ["pipeDeliveryMeaning"] = "A response-delivered event means the host stdout write and flush completed; it does not establish model-context consumption."
            };
        }

        private static long Add(long current, long value)
        {
            if (value <= 0) return current;
            return current > Int64.MaxValue - value ? Int64.MaxValue : current + value;
        }
    }

    public static class TrialHost
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions { WriteIndented = false };
        private static readonly string[] KnownCapabilities = new string[] {
            "fs.read", "fs.write", "db.read", "db.write", "network.read", "network.write",
            "process.execute", "clock.read", "random.read", "console.write"
        };
        private static readonly string[] CommonInspectionOperations = new string[] { "inspect", "read", "search" };
        private static readonly string[] AuditedAgentLangInspectionOperations = new string[] {
            "words", "describe", "type-of", "search-type", "search-output", "search-effect", "search-dependency",
            "transitive-dependencies", "transitive-callers", "graph", "context", "source", "dependencies", "callers",
            "effects", "ir", "tests", "examples", "history", "diff", "task.status", "task.log", "storage.status", "stack"
        };
        private const string InspectionClassifierVersion = "trial-host-inspection-v1";
        private const string InspectionBudgetExhaustedMessage = "The cumulative inspection response budget is exhausted; this inspection request was not forwarded.";
        private const string InspectionBudgetExceededMessage = "The complete inspection response exceeded the remaining cumulative budget; the response was withheld after execution.";

        public static int Run(string cliDll, string projectPath, string tracePath, string[] allowedOperations,
            string profile, string[] additionalCliArguments, string[] capabilities, string clockValue, int maxRequestBytes, int maxResponseBytes,
            long? maxInspectionResponseBytes, int timeoutMilliseconds, int maxExchanges)
        {
            string terminationKind = "other-failure";
            Process process = null;
            JobObject job = null;
            TraceFile trace = null;
            Task<CapturedText> stderrTask = null;
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConsoleCancelEventHandler cancelHandler = (sender, args) =>
            {
                args.Cancel = true;
                cancelled.TrySetResult(true);
            };

            try
            {
                ValidateConfiguration(ref cliDll, ref projectPath, ref tracePath, allowedOperations,
                    profile, additionalCliArguments, capabilities, clockValue, maxRequestBytes, maxResponseBytes,
                    maxInspectionResponseBytes, timeoutMilliseconds, maxExchanges);
                trace = new TraceFile(tracePath);
                job = JobObject.Create();
                process = StartRuntime(cliDll, projectPath, capabilities, clockValue, profile, additionalCliArguments);
                job.Assign(process.Handle);
                stderrTask = DrainStderrAsync(process.StandardError.BaseStream, 8192);

                var accounting = maxInspectionResponseBytes.HasValue ? new ResponseAccounting() : null;
                var sessionStart = new Dictionary<string, object> {
                    ["event"] = "session-start",
                    ["schemaVersion"] = accounting == null ? 1 : 2,
                    ["hostProtocolVersion"] = "subagent-trial-host-v2",
                    ["transportControls"] = new string[] { "host.close" },
                    ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["hostProcessId"] = Process.GetCurrentProcess().Id,
                    ["runtimeProcessId"] = process.Id,
                    ["runtimeExecutable"] = "dotnet",
                    ["processArchitecture"] = IntPtr.Size == 8 ? "x64" : "unsupported",
                    ["jobObjectLayout"] = DescribeJobObjectLayout(),
                    ["cliDll"] = cliDll,
                    ["cliFiles"] = HashRuntimeFiles(cliDll),
                    ["projectPath"] = projectPath,
                    ["profile"] = profile,
                    ["allowedOperations"] = allowedOperations.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    ["additionalCliArguments"] = additionalCliArguments,
                    ["capabilities"] = capabilities.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    ["limits"] = new Dictionary<string, object> {
                        ["maxRequestBytes"] = maxRequestBytes,
                        ["maxResponseBytes"] = maxResponseBytes,
                        ["exchangeTimeoutMilliseconds"] = timeoutMilliseconds,
                        ["maxExchanges"] = maxExchanges
                    },
                    ["cleanup"] = "Windows job object kill-on-close plus explicit process cleanup"
                };
                if (accounting != null)
                {
                    sessionStart["inspectionBudget"] = InspectionBudgetMetadata(maxInspectionResponseBytes.Value, profile);
                    sessionStart["responseAccounting"] = accounting.Snapshot();
                }
                trace.Write(sessionStart);

                Console.CancelKeyPress += cancelHandler;
                var inputReader = new BoundedLineReader(Console.OpenStandardInput());
                var output = Console.OpenStandardOutput();
                var runtimeOutputReader = new BoundedLineReader(process.StandardOutput.BaseStream);
                int exchangeCount = 0;
                int exitCode = 0;
                bool done = false;

                while (!done)
                {
                    var readTask = inputReader.ReadAsync(maxRequestBytes);
                    var winner = Task.WhenAny(readTask, cancelled.Task).GetAwaiter().GetResult();
                    if (winner == cancelled.Task)
                    {
                        terminationKind = "cancelled";
                        WriteAccountedHostResponse(output, trace, "TRIAL_CANCELLED", "The trial host was cancelled; the runtime process is stopping.",
                            "cancelled-before-request", exchangeCount, accounting);
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "host-cancelled",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["exchangeCount"] = exchangeCount
                        });
                        exitCode = 130;
                        break;
                    }

                    LineFrame requestFrame;
                    try { requestFrame = readTask.GetAwaiter().GetResult(); }
                    catch (LineLimitException limitError)
                    {
                        WriteAccountedHostResponse(output, trace, "TRIAL_REQUEST_TOO_LARGE", "The request line exceeded the configured byte limit; the session is closing.",
                            "oversized-request", exchangeCount, accounting);
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "request-rejected",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["code"] = "TRIAL_REQUEST_TOO_LARGE",
                            ["maxRequestBytes"] = limitError.Limit,
                            ["observedPrefixBytes"] = limitError.Prefix.Length,
                            ["observedPrefixSha256"] = Sha256(limitError.Prefix),
                            ["prefixBase64"] = Convert.ToBase64String(limitError.Prefix)
                        });
                        exitCode = 2;
                        break;
                    }

                    if (requestFrame.Eof)
                    {
                        terminationKind = "input-eof";
                        try { process.StandardInput.Close(); } catch { }
                        if (!process.WaitForExit(timeoutMilliseconds))
                        {
                            TryKill(process);
                            trace.Write(new Dictionary<string, object> {
                                ["event"] = "shutdown-timeout",
                                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["timeoutMilliseconds"] = timeoutMilliseconds
                            });
                            exitCode = 3;
                        }
                        done = true;
                        break;
                    }

                    if (!requestFrame.Terminated)
                    {
                        WriteAccountedHostResponse(output, trace, "TRIAL_UNTERMINATED_REQUEST", "JSONL requests must end with LF; the unterminated final request was not forwarded.",
                            "unterminated-request", exchangeCount, accounting);
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "request-rejected",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["code"] = "TRIAL_UNTERMINATED_REQUEST",
                            ["requestPayloadBytes"] = requestFrame.Data.Length,
                            ["requestSha256"] = Sha256(requestFrame.Data),
                            ["requestBase64"] = Convert.ToBase64String(requestFrame.Data)
                        });
                        exitCode = 2;
                        break;
                    }

                    byte[] wireRequest = WithLf(requestFrame.Data);
                    string rawRequest = null;
                    string canonicalRequest = null;
                    string op = null;
                    string requestErrorCode = null;
                    string requestErrorMessage = null;
                    bool exactHostClose = false;
                    try
                    {
                        rawRequest = StrictUtf8.GetString(requestFrame.Data);
                        using (JsonDocument document = JsonDocument.Parse(rawRequest, new JsonDocumentOptions { MaxDepth = 64 }))
                        {
                            ValidateNoDuplicateProperties(document.RootElement);
                            if (document.RootElement.ValueKind != JsonValueKind.Object)
                                throw new RequestValidationException("TRIAL_INVALID_REQUEST", "Each request must be a JSON object.");
                            JsonElement operationValue;
                            if (!document.RootElement.TryGetProperty("op", out operationValue) || operationValue.ValueKind != JsonValueKind.String)
                                throw new RequestValidationException("TRIAL_INVALID_REQUEST", "Each request must contain a string 'op' field.");
                            op = operationValue.GetString();
                            if (String.IsNullOrWhiteSpace(op))
                                throw new RequestValidationException("TRIAL_INVALID_REQUEST", "Request operation cannot be empty.");
                            if (String.Equals(op, "host.close", StringComparison.Ordinal))
                            {
                                if (document.RootElement.EnumerateObject().Count() != 1)
                                    throw new RequestValidationException("TRIAL_INVALID_REQUEST", "The reserved host.close transport control must contain only its 'op' field.");
                                exactHostClose = true;
                            }
                            canonicalRequest = Canonicalize(document.RootElement);
                        }
                    }
                    catch (DecoderFallbackException ex)
                    {
                        requestErrorCode = "TRIAL_INVALID_UTF8";
                        requestErrorMessage = ex.Message;
                    }
                    catch (JsonException ex)
                    {
                        requestErrorCode = "TRIAL_INVALID_JSON";
                        requestErrorMessage = ex.Message;
                    }
                    catch (RequestValidationException ex)
                    {
                        requestErrorCode = ex.Code;
                        requestErrorMessage = ex.Message;
                    }

                    if (exactHostClose && requestErrorCode == null)
                    {
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "host-close",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["exchangeCount"] = exchangeCount,
                            ["requestWireUtf8Bytes"] = wireRequest.Length,
                            ["requestWireSha256"] = Sha256(wireRequest),
                            ["requestWireBase64"] = Convert.ToBase64String(wireRequest),
                            ["requestRaw"] = rawRequest,
                            ["requestCanonical"] = canonicalRequest
                        });

                        try { process.StandardInput.Close(); } catch { }
                        if (!process.WaitForExit(timeoutMilliseconds))
                        {
                            TryKill(process);
                            trace.Write(new Dictionary<string, object> {
                                ["event"] = "shutdown-timeout",
                                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["timeoutMilliseconds"] = timeoutMilliseconds
                            });
                            exitCode = 3;
                            terminationKind = "other-failure";
                        }
                        else if (process.ExitCode != 0)
                        {
                            trace.Write(new Dictionary<string, object> {
                                ["event"] = "shutdown-failure",
                                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["runtimeExitCode"] = process.ExitCode
                            });
                            exitCode = 3;
                            terminationKind = "other-failure";
                        }
                        else
                        {
                            terminationKind = "host-close";
                        }
                        done = true;
                        break;
                    }

                    exchangeCount++;
                    if (exchangeCount > maxExchanges)
                    {
                        terminationKind = "exchange-limit";
                        WriteAccountedHostResponse(output, trace, "TRIAL_EXCHANGE_LIMIT", "The session exceeded its configured exchange limit and is closing.",
                            "exchange-limit", exchangeCount, accounting);
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "exchange-rejected",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["index"] = exchangeCount,
                            ["code"] = "TRIAL_EXCHANGE_LIMIT",
                            ["requestUtf8Bytes"] = requestFrame.Data.Length + 1,
                            ["requestBase64"] = Convert.ToBase64String(wireRequest)
                        });
                        exitCode = 2;
                        break;
                    }

                    bool allowed = op != null && allowedOperations.Contains(op, StringComparer.Ordinal);
                    if (requestErrorCode == null && !allowed)
                    {
                        requestErrorCode = "TRIAL_OPERATION_DENIED";
                        requestErrorMessage = "Operation is not in the trial host's allowed protocol operation set.";
                    }

                    bool inspectionOperation = accounting != null && IsInspectionOperation(profile, op);
                    long admittedInspectionBeforeBytes = accounting == null ? 0 : accounting.AdmittedInspectionPayloadUtf8Bytes;
                    long inspectionRemainingBeforeBytes = accounting == null ? 0 : maxInspectionResponseBytes.Value - admittedInspectionBeforeBytes;
                    string inspectionBudgetDecision = "not-applicable";
                    if (requestErrorCode == null && inspectionOperation && inspectionRemainingBeforeBytes == 0)
                    {
                        requestErrorCode = "TRIAL_INSPECTION_BUDGET_EXHAUSTED";
                        requestErrorMessage = InspectionBudgetExhaustedMessage;
                        inspectionBudgetDecision = "rejected-before-forwarding";
                    }

                    var stopwatch = Stopwatch.StartNew();
                    byte[] responseBytes = null;
                    string responseSource = "runtime";
                    string canonicalResponse = null;
                    string responseParseError = null;
                    string outcome = "forwarded";
                    string deliveryState = requestErrorCode == null ? "not-started" : "not-sent";
                    string executionState = requestErrorCode == null ? "not-started" : "not-executed";
                    int attemptedRequestBytes = 0;
                    int? confirmedRequestBytes = requestErrorCode == null ? (int?)null : 0;
                    byte[] observedResponseBytes = null;
                    bool? observedResponseComplete = null;
                    bool? observedResponseValidJson = null;
                    byte[] rawRuntimeResponseBytes = null;
                    string rawRuntimeCanonicalResponse = null;
                    bool rawRuntimeResponseValid = false;
                    bool admittedInspectionResponse = false;
                    bool terminate = false;

                    if (requestErrorCode != null)
                    {
                        responseSource = "host";
                        outcome = requestErrorCode == "TRIAL_OPERATION_DENIED" ? "denied" :
                            requestErrorCode == "TRIAL_INSPECTION_BUDGET_EXHAUSTED" ? "inspection-budget-exhausted" : "malformed";
                        responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                        canonicalResponse = CanonicalizeUtf8(responseBytes);
                    }
                    else
                    {
                        attemptedRequestBytes = wireRequest.Length;
                        deliveryState = "uncertain";
                        executionState = "uncertain";
                        Task timeoutTask = Task.Delay(timeoutMilliseconds);
                        Task writeTask = Task.Run(() => WriteRequestAsync(process.StandardInput.BaseStream, wireRequest));
                        Task<LineFrame> responseReadTask = Task.Run(() => runtimeOutputReader.ReadAsync(maxResponseBytes));
                        bool writeCompleted = false;
                        bool responseCompleted = false;
                        LineFrame runtimeFrame = null;
                        LineLimitException responseLimitError = null;
                        Exception transportError = null;

                        while (!terminate && (!writeCompleted || !responseCompleted))
                        {
                            var pending = new List<Task> { timeoutTask, cancelled.Task };
                            if (!writeCompleted) pending.Add(writeTask);
                            if (!responseCompleted) pending.Add(responseReadTask);
                            Task exchangeWinner = Task.WhenAny(pending).GetAwaiter().GetResult();

                            if (exchangeWinner == cancelled.Task)
                            {
                                terminationKind = "cancelled";
                                responseSource = "host";
                                outcome = "cancelled";
                                executionState = "uncertain";
                                requestErrorCode = "TRIAL_CANCELLED";
                                requestErrorMessage = "The trial host was cancelled during request delivery or response wait. Execution outcome is uncertain; do not retry automatically, inspect persisted project state first.";
                                if (writeTask.Status == TaskStatus.RanToCompletion)
                                {
                                    deliveryState = "confirmed";
                                    confirmedRequestBytes = wireRequest.Length;
                                }
                                observedResponseBytes = CapturePendingRuntimeResponse(responseReadTask, runtimeOutputReader, out observedResponseComplete);
                                CaptureValidObservedRuntimeResponse(observedResponseBytes, observedResponseComplete, accounting != null,
                                    ref rawRuntimeResponseBytes, ref rawRuntimeCanonicalResponse, ref rawRuntimeResponseValid, ref observedResponseValidJson);
                                responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                canonicalResponse = CanonicalizeUtf8(responseBytes);
                                terminate = true;
                                exitCode = 130;
                            }
                            else if (exchangeWinner == timeoutTask)
                            {
                                responseSource = "host";
                                outcome = "timeout";
                                executionState = "uncertain";
                                requestErrorCode = "TRIAL_EXCHANGE_TIMEOUT";
                                requestErrorMessage = "Request delivery and response did not both complete before the single exchange deadline. Execution outcome is uncertain; do not retry automatically, inspect persisted project state first.";
                                if (writeTask.Status == TaskStatus.RanToCompletion)
                                {
                                    deliveryState = "confirmed";
                                    confirmedRequestBytes = wireRequest.Length;
                                }
                                observedResponseBytes = CapturePendingRuntimeResponse(responseReadTask, runtimeOutputReader, out observedResponseComplete);
                                CaptureValidObservedRuntimeResponse(observedResponseBytes, observedResponseComplete, accounting != null,
                                    ref rawRuntimeResponseBytes, ref rawRuntimeCanonicalResponse, ref rawRuntimeResponseValid, ref observedResponseValidJson);
                                responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                canonicalResponse = CanonicalizeUtf8(responseBytes);
                                terminate = true;
                                exitCode = 124;
                            }
                            else if (exchangeWinner == writeTask)
                            {
                                try
                                {
                                    writeTask.GetAwaiter().GetResult();
                                    writeCompleted = true;
                                    deliveryState = "confirmed";
                                    confirmedRequestBytes = wireRequest.Length;
                                }
                                catch (Exception ex)
                                {
                                    transportError = ex;
                                    responseSource = "host";
                                    outcome = "child-exited";
                                    executionState = "uncertain";
                                    requestErrorCode = "TRIAL_CHILD_EXITED";
                                    requestErrorMessage = "The runtime process exited or stopped accepting input before the complete request was delivered. Execution outcome is uncertain; do not retry automatically, inspect persisted project state first.";
                                    if (accounting != null)
                                    {
                                        observedResponseBytes = CapturePendingRuntimeResponse(responseReadTask, runtimeOutputReader, out observedResponseComplete);
                                        CaptureValidObservedRuntimeResponse(observedResponseBytes, observedResponseComplete, true,
                                            ref rawRuntimeResponseBytes, ref rawRuntimeCanonicalResponse, ref rawRuntimeResponseValid, ref observedResponseValidJson);
                                    }
                                    responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                    canonicalResponse = CanonicalizeUtf8(responseBytes);
                                    terminate = true;
                                    exitCode = 3;
                                }
                            }
                            else if (exchangeWinner == responseReadTask)
                            {
                                responseCompleted = true;
                                try { runtimeFrame = responseReadTask.GetAwaiter().GetResult(); }
                                catch (LineLimitException ex) { responseLimitError = ex; }
                                catch (Exception ex) { transportError = ex; }

                                if (responseLimitError != null)
                                {
                                    observedResponseBytes = responseLimitError.Prefix;
                                    observedResponseComplete = false;
                                    responseSource = "host";
                                    outcome = "response-too-large";
                                    requestErrorCode = "TRIAL_RESPONSE_TOO_LARGE";
                                    requestErrorMessage = "Runtime response exceeded the configured byte limit. The request may already have executed; do not retry automatically, inspect persisted project state first.";
                                    responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                    canonicalResponse = CanonicalizeUtf8(responseBytes);
                                    responseParseError = "observedPrefixBytes=" + responseLimitError.Prefix.Length + "; sha256=" + Sha256(responseLimitError.Prefix);
                                    terminate = true;
                                    exitCode = 2;
                                }
                                else if (transportError != null || runtimeFrame == null || runtimeFrame.Eof || !runtimeFrame.Terminated)
                                {
                                    observedResponseBytes = runtimeFrame == null ? runtimeOutputReader.SnapshotPartial() : runtimeFrame.Data;
                                    observedResponseComplete = false;
                                    responseSource = "host";
                                    outcome = "child-exited";
                                    requestErrorCode = "TRIAL_CHILD_EXITED";
                                    requestErrorMessage = "The runtime exited without a complete JSONL response. The request may already have executed; do not retry automatically, inspect persisted project state first.";
                                    responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                    canonicalResponse = CanonicalizeUtf8(responseBytes);
                                    terminate = true;
                                    exitCode = 3;
                                }
                                else
                                {
                                    responseBytes = runtimeFrame.Data;
                                    try
                                    {
                                        canonicalResponse = CanonicalizeUtf8(responseBytes);
                                        executionState = "response-observed";
                                        if (accounting != null)
                                        {
                                            rawRuntimeResponseBytes = runtimeFrame.Data;
                                            rawRuntimeCanonicalResponse = canonicalResponse;
                                            rawRuntimeResponseValid = true;
                                            observedResponseBytes = runtimeFrame.Data;
                                            observedResponseComplete = true;
                                            observedResponseValidJson = true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        responseParseError = ex.Message;
                                        outcome = "invalid-runtime-response";
                                        executionState = "uncertain";
                                        observedResponseBytes = runtimeFrame.Data;
                                        observedResponseComplete = true;
                                        observedResponseValidJson = false;
                                        responseSource = "host";
                                        requestErrorCode = "TRIAL_INVALID_RUNTIME_RESPONSE";
                                        requestErrorMessage = "The runtime returned a complete line that is not valid JSON. Execution outcome is uncertain; do not retry automatically, inspect persisted project state first.";
                                        responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                        canonicalResponse = CanonicalizeUtf8(responseBytes);
                                        terminate = true;
                                        exitCode = 4;
                                    }
                                }
                            }
                        }

                        if (!terminate && requestErrorCode == null && inspectionOperation && rawRuntimeResponseValid)
                        {
                            if ((long)rawRuntimeResponseBytes.Length > inspectionRemainingBeforeBytes)
                            {
                                responseSource = "host";
                                outcome = "inspection-budget-exceeded";
                                requestErrorCode = "TRIAL_INSPECTION_BUDGET_EXCEEDED";
                                requestErrorMessage = InspectionBudgetExceededMessage;
                                inspectionBudgetDecision = "withheld-after-response";
                                responseBytes = MakeErrorResponse(requestErrorCode, requestErrorMessage);
                                canonicalResponse = CanonicalizeUtf8(responseBytes);
                            }
                            else
                            {
                                admittedInspectionResponse = true;
                                inspectionBudgetDecision = "admitted";
                            }
                        }
                    }

                    stopwatch.Stop();
                    var requestWireBytes = wireRequest;
                    var responseWireBytes = WithLf(responseBytes);
                    long rawValidRuntimePayloadBytes = rawRuntimeResponseValid ? rawRuntimeResponseBytes.Length : 0;
                    long admittedInspectionPayloadBytes = admittedInspectionResponse ? rawRuntimeResponseBytes.Length : 0;
                    if (accounting != null)
                        accounting.RecordExchange(rawValidRuntimePayloadBytes, inspectionOperation, admittedInspectionPayloadBytes,
                            responseBytes.Length, responseSource == "host");
                    long admittedInspectionAfterBytes = accounting == null ? 0 : accounting.AdmittedInspectionPayloadUtf8Bytes;
                    var record = new Dictionary<string, object> {
                        ["event"] = "exchange",
                        ["index"] = exchangeCount,
                        ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["operation"] = op,
                        ["outcome"] = outcome,
                        ["requestDelivery"] = new Dictionary<string, object> {
                            ["state"] = deliveryState,
                            ["attemptedUtf8Bytes"] = attemptedRequestBytes,
                            ["confirmedUtf8Bytes"] = confirmedRequestBytes,
                            ["completeLineConfirmed"] = deliveryState == "confirmed"
                        },
                        ["executionState"] = executionState,
                        ["automaticRetry"] = "never; inspect authoritative stored state after any uncertain outcome",
                        ["observedRuntimeResponse"] = observedResponseBytes == null ? null : new Dictionary<string, object> {
                            ["complete"] = observedResponseComplete,
                            ["validJson"] = observedResponseValidJson,
                            ["utf8Bytes"] = observedResponseBytes.Length,
                            ["sha256"] = Sha256(observedResponseBytes),
                            ["base64"] = Convert.ToBase64String(observedResponseBytes)
                        },
                        ["request"] = new Dictionary<string, object> {
                            ["rawLine"] = rawRequest,
                            ["canonical"] = canonicalRequest,
                            ["wireBase64"] = Convert.ToBase64String(requestWireBytes),
                            ["payloadUtf8Bytes"] = requestFrame.Data.Length,
                            ["wireUtf8Bytes"] = requestWireBytes.Length,
                            ["sha256"] = Sha256(requestWireBytes)
                        },
                        ["response"] = new Dictionary<string, object> {
                            ["source"] = responseSource,
                            ["rawLine"] = TryDecode(responseBytes),
                            ["canonical"] = canonicalResponse,
                            ["wireBase64"] = Convert.ToBase64String(responseWireBytes),
                            ["payloadUtf8Bytes"] = responseBytes.Length,
                            ["wireUtf8Bytes"] = responseWireBytes.Length,
                            ["sha256"] = Sha256(responseWireBytes),
                            ["parseError"] = responseParseError
                        },
                        ["elapsedMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds,
                        ["errorCode"] = requestErrorCode,
                        ["errorMessage"] = requestErrorMessage
                    };
                    if (accounting != null)
                    {
                        if (inspectionOperation && rawRuntimeResponseValid && !admittedInspectionResponse &&
                            inspectionBudgetDecision == "not-applicable")
                            inspectionBudgetDecision = "observed-not-admitted-uncertain";
                        var observed = record["observedRuntimeResponse"] as Dictionary<string, object>;
                        if (observed != null && rawRuntimeResponseValid)
                        {
                            observed["canonical"] = rawRuntimeCanonicalResponse;
                            observed["wireUtf8Bytes"] = rawRuntimeResponseBytes.Length + 1;
                            observed["wireSha256"] = Sha256(WithLf(rawRuntimeResponseBytes));
                            observed["wireBase64"] = Convert.ToBase64String(WithLf(rawRuntimeResponseBytes));
                        }
                        ((Dictionary<string, object>)record["response"])["payloadSha256"] = Sha256(responseBytes);
                        record["inspectionBudget"] = new Dictionary<string, object> {
                            ["classifierVersion"] = InspectionClassifierVersion,
                            ["operationClass"] = inspectionOperation ? "inspection" : "non-inspection",
                            ["decision"] = inspectionBudgetDecision,
                            ["maximumPayloadUtf8Bytes"] = maxInspectionResponseBytes.Value,
                            ["admittedBeforePayloadUtf8Bytes"] = admittedInspectionBeforeBytes,
                            ["remainingBeforePayloadUtf8Bytes"] = inspectionRemainingBeforeBytes,
                            ["rawRuntimeResponsePayloadUtf8Bytes"] = rawRuntimeResponseValid ? (object)rawRuntimeResponseBytes.Length : null,
                            ["admittedPayloadUtf8Bytes"] = admittedInspectionPayloadBytes,
                            ["admittedAfterPayloadUtf8Bytes"] = admittedInspectionAfterBytes,
                            ["remainingAfterPayloadUtf8Bytes"] = maxInspectionResponseBytes.Value - admittedInspectionAfterBytes
                        };
                        record["responseAccounting"] = accounting.Snapshot();
                    }
                    trace.Write(record);
                    output.Write(responseBytes, 0, responseBytes.Length);
                    output.WriteByte(10);
                    output.Flush();
                    if (accounting != null)
                    {
                        accounting.RecordPipeDelivery(responseBytes.Length, responseWireBytes.Length);
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "response-delivered",
                            ["schemaVersion"] = 2,
                            ["index"] = exchangeCount,
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["source"] = responseSource,
                            ["pipeWriteFlushCompleted"] = true,
                            ["selectedResponse"] = new Dictionary<string, object> {
                                ["payloadUtf8Bytes"] = responseBytes.Length,
                                ["payloadSha256"] = Sha256(responseBytes),
                                ["wireUtf8Bytes"] = responseWireBytes.Length,
                                ["wireSha256"] = Sha256(responseWireBytes)
                            },
                            ["responseAccounting"] = accounting.Snapshot()
                        });
                    }

                    if (terminate) done = true;
                }

                int? childExitCode = null;
                if (process != null)
                {
                    if (!process.HasExited)
                    {
                        try { process.StandardInput.Close(); } catch { }
                        if (!process.WaitForExit(2000)) TryKill(process);
                    }
                    if (process.HasExited) childExitCode = process.ExitCode;
                }
                CapturedText stderr = GetCapturedStderr(stderrTask);
                var sessionEnd = new Dictionary<string, object> {
                    ["event"] = "session-end",
                    ["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["exchangeCount"] = exchangeCount,
                    ["terminationKind"] = terminationKind,
                    ["hostExitCode"] = exitCode,
                    ["runtimeExitCode"] = childExitCode,
                    ["runtimeStderrPrefixBytes"] = stderr.PrefixBytes.Length,
                    ["runtimeStderrTotalBytes"] = stderr.TotalBytes,
                    ["runtimeStderrPrefixSha256"] = Sha256(stderr.PrefixBytes),
                    ["runtimeStderrCaptureTruncated"] = stderr.TotalBytes > stderr.PrefixBytes.Length || (stderrTask != null && !stderrTask.IsCompleted)
                };
                if (accounting != null)
                {
                    sessionEnd["schemaVersion"] = 2;
                    sessionEnd["responseAccounting"] = accounting.Snapshot();
                }
                trace.Write(sessionEnd);
                return exitCode;
            }
            catch (Exception ex)
            {
                try
                {
                    if (trace != null)
                        trace.Write(new Dictionary<string, object> {
                            ["event"] = "host-failure",
                            ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["type"] = ex.GetType().FullName,
                            ["message"] = ex.Message
                        });
                }
                catch { }
                try { Console.Error.WriteLine("TRIAL_HOST_FAILURE: " + ex.Message); } catch { }
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                if (process != null)
                {
                    try { if (!process.HasExited) TryKill(process); } catch { }
                    try { process.Dispose(); } catch { }
                }
                if (job != null) job.Dispose();
                if (trace != null) trace.Dispose();
            }
        }

        private static void ValidateConfiguration(ref string cliDll, ref string projectPath, ref string tracePath,
            string[] allowedOperations, string profile, string[] additionalCliArguments, string[] capabilities, string clockValue, int maxRequestBytes,
            int maxResponseBytes, long? maxInspectionResponseBytes, int timeoutMilliseconds, int maxExchanges)
        {
            AssertJobObjectLayout();
            if (maxInspectionResponseBytes.HasValue && maxInspectionResponseBytes.Value < 0)
                throw new ArgumentOutOfRangeException("maxInspectionResponseBytes", "Inspection response budget must be nonnegative.");
            if (String.IsNullOrWhiteSpace(cliDll) || String.IsNullOrWhiteSpace(projectPath) || String.IsNullOrWhiteSpace(tracePath))
                throw new ArgumentException("CLI DLL, project path, and trace path are required.");
            cliDll = Path.GetFullPath(cliDll);
            projectPath = Path.GetFullPath(projectPath);
            tracePath = Path.GetFullPath(tracePath);
            AssertNoReparseComponents(cliDll, true);
            if (!File.Exists(cliDll) || !String.Equals(Path.GetExtension(cliDll), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Pinned CLI DLL was not found or is not a DLL.", cliDll);
            foreach (string suffix in new string[] { ".deps.json", ".runtimeconfig.json" })
            {
                string companion = Path.ChangeExtension(cliDll, suffix);
                AssertNoReparseComponents(companion, true);
                if (!File.Exists(companion)) throw new FileNotFoundException("Required CLI companion file is missing.", companion);
            }

            if (Directory.Exists(projectPath)) AssertNoReparseComponents(projectPath, true);
            else
            {
                AssertNoReparseComponents(projectPath, false);
                Directory.CreateDirectory(projectPath);
                AssertNoReparseComponents(projectPath, true);
            }
            if (File.Exists(projectPath)) throw new IOException("Project path must be a directory.");
            if (Path.GetPathRoot(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Project path cannot be a filesystem root.");

            string traceParent = Path.GetDirectoryName(tracePath);
            if (String.IsNullOrWhiteSpace(traceParent)) throw new IOException("Trace path must name a file inside a directory.");
            AssertNoReparseComponents(traceParent, false);
            Directory.CreateDirectory(traceParent);
            AssertNoReparseComponents(traceParent, true);
            if (File.Exists(tracePath) || Directory.Exists(tracePath)) throw new IOException("Trace path already exists; refusing to overwrite it.");
            if (IsWithin(projectPath, tracePath) || IsWithin(tracePath, projectPath))
                throw new IOException("Project and trace paths must not contain one another.");

            if (allowedOperations == null || allowedOperations.Length == 0) throw new ArgumentException("At least one allowed protocol operation is required.");
            if (allowedOperations.Any(String.IsNullOrWhiteSpace) || allowedOperations.Distinct(StringComparer.Ordinal).Count() != allowedOperations.Length)
                throw new ArgumentException("Allowed protocol operations must be nonempty and unique.");
            if (allowedOperations.Contains("host.close", StringComparer.Ordinal))
                throw new ArgumentException("The reserved host.close transport control cannot be exposed as a runtime operation.");
            if (profile != "agentlang" && profile != "conventional") throw new ArgumentException("Profile must be 'agentlang' or 'conventional'.");
            if (additionalCliArguments == null || additionalCliArguments.Length > 16)
                throw new ArgumentException("Additional CLI arguments must be a bounded host-provided list.");
            foreach (string argument in additionalCliArguments)
            {
                if (argument == null || argument.Length > 4096 || argument.Any(Char.IsControl))
                    throw new ArgumentException("Additional CLI arguments must be bounded strings without control characters.");
                string optionName = argument.Split(new char[] { '=' }, 2)[0];
                if (new string[] { "--project", "--jsonl", "--allow", "--clock" }.Contains(optionName, StringComparer.Ordinal))
                    throw new ArgumentException("Additional CLI arguments cannot override wrapper-owned project, protocol, capability, or clock settings.");
            }
            if (capabilities == null) throw new ArgumentException("Capabilities cannot be null.");
            foreach (string capability in capabilities)
                if (!KnownCapabilities.Contains(capability, StringComparer.Ordinal)) throw new ArgumentException("Unknown capability: " + capability);
            if (capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length) throw new ArgumentException("Capabilities must be unique.");
            if (profile == "conventional" && capabilities.Length > 0)
                throw new ArgumentException("The conventional profile cannot receive AgentLang capability flags.");
            if (String.IsNullOrWhiteSpace(clockValue)) throw new ArgumentException("Clock value cannot be empty.");
            if (maxRequestBytes < 1 || maxRequestBytes > 524288) throw new ArgumentOutOfRangeException("maxRequestBytes");
            if (maxResponseBytes < 1 || maxResponseBytes > 1048576) throw new ArgumentOutOfRangeException("maxResponseBytes");
            if (timeoutMilliseconds < 1 || timeoutMilliseconds > 120000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if (maxExchanges < 1 || maxExchanges > 100) throw new ArgumentOutOfRangeException("maxExchanges");
        }

        private static Process StartRuntime(string cliDll, string projectPath, string[] capabilities, string clockValue,
            string profile, string[] additionalCliArguments)
        {
            var arguments = new List<string> { cliDll, "--project", projectPath };
            arguments.AddRange(additionalCliArguments);
            arguments.Add("--jsonl");
            if (profile == "agentlang")
            {
                arguments.Add("--clock");
                arguments.Add(clockValue);
                if (capabilities.Length > 0)
                {
                    arguments.Add("--allow");
                    arguments.Add(String.Join(",", capabilities.OrderBy(x => x, StringComparer.Ordinal)));
                }
            }
            var startInfo = new ProcessStartInfo {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(cliDll),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var argumentListProperty = typeof(ProcessStartInfo).GetProperty("ArgumentList");
            if (argumentListProperty != null)
            {
                var list = (System.Collections.IList)argumentListProperty.GetValue(startInfo);
                foreach (string argument in arguments) list.Add(argument);
            }
            else
            {
                startInfo.Arguments = String.Join(" ", arguments.Select(QuoteWindowsArgument));
            }
            startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            startInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Could not start the pinned AgentLang CLI process.");
            return process;
        }

        private static Dictionary<string, object> DescribeJobObjectLayout()
        {
            return new Dictionary<string, object> {
                ["basicLimitInformationBytes"] = Marshal.SizeOf(typeof(JobObject.BasicLimitInformation)),
                ["basicLimitFlagsOffset"] = Marshal.OffsetOf(typeof(JobObject.BasicLimitInformation), "LimitFlags").ToInt32(),
                ["ioCountersBytes"] = Marshal.SizeOf(typeof(JobObject.IoCounters)),
                ["extendedLimitInformationBytes"] = Marshal.SizeOf(typeof(JobObject.ExtendedLimitInformation)),
                ["extendedIoInfoOffset"] = Marshal.OffsetOf(typeof(JobObject.ExtendedLimitInformation), "IoInfo").ToInt32(),
                ["processMemoryLimitOffset"] = Marshal.OffsetOf(typeof(JobObject.ExtendedLimitInformation), "ProcessMemoryLimit").ToInt32(),
                ["jobMemoryLimitOffset"] = Marshal.OffsetOf(typeof(JobObject.ExtendedLimitInformation), "JobMemoryLimit").ToInt32(),
                ["peakProcessMemoryUsedOffset"] = Marshal.OffsetOf(typeof(JobObject.ExtendedLimitInformation), "PeakProcessMemoryUsed").ToInt32(),
                ["peakJobMemoryUsedOffset"] = Marshal.OffsetOf(typeof(JobObject.ExtendedLimitInformation), "PeakJobMemoryUsed").ToInt32()
            };
        }

        private static object[] HashRuntimeFiles(string cliDll)
        {
            string directory = Path.GetDirectoryName(cliDll);
            var paths = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).ToList();
            paths.Add(Path.ChangeExtension(cliDll, ".deps.json"));
            paths.Add(Path.ChangeExtension(cliDll, ".runtimeconfig.json"));
            return paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => {
                    AssertNoReparseComponents(path, true);
                    using (var stream = File.OpenRead(path))
                    using (var hash = SHA256.Create())
                        return (object)new Dictionary<string, object> {
                            ["name"] = Path.GetFileName(path),
                            ["sha256"] = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
                        };
                }).ToArray();
        }

        private static void AssertJobObjectLayout()
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("The trial host currently supports only x64 Windows because its native job-object ABI is validated for 64-bit processes.");
            var layout = DescribeJobObjectLayout();
            var expected = new Dictionary<string, int> {
                ["basicLimitInformationBytes"] = 64,
                ["basicLimitFlagsOffset"] = 16,
                ["ioCountersBytes"] = 48,
                ["extendedLimitInformationBytes"] = 144,
                ["extendedIoInfoOffset"] = 64,
                ["processMemoryLimitOffset"] = 112,
                ["jobMemoryLimitOffset"] = 120,
                ["peakProcessMemoryUsedOffset"] = 128,
                ["peakJobMemoryUsedOffset"] = 136
            };
            foreach (var item in expected)
            {
                if (Convert.ToInt32(layout[item.Key]) != item.Value)
                    throw new PlatformNotSupportedException("Unexpected x64 Windows job-object ABI layout for " + item.Key + ".");
            }
        }

        private static string QuoteWindowsArgument(string value)
        {
            if (value.Length > 0 && value.All(c => !Char.IsWhiteSpace(c) && c != '"')) return value;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    result.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0) result.Append('\\', slashes);
                slashes = 0;
                result.Append(c);
            }
            if (slashes > 0) result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        private static async Task<CapturedText> DrainStderrAsync(Stream stream, int captureLimit)
        {
            var captured = new MemoryStream();
            var buffer = new byte[1024];
            long total = 0;
            while (true)
            {
                int count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                int retain = (int)Math.Min(count, Math.Max(0, captureLimit - captured.Length));
                if (retain > 0) captured.Write(buffer, 0, retain);
            }
            return new CapturedText { PrefixBytes = captured.ToArray(), TotalBytes = total };
        }

        private static async Task WriteRequestAsync(Stream stream, byte[] request)
        {
            await stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static byte[] CapturePendingRuntimeResponse(Task<LineFrame> responseTask, BoundedLineReader reader, out bool? complete)
        {
            complete = false;
            if (!responseTask.IsCompleted) return reader.SnapshotPartial();
            try
            {
                LineFrame frame = responseTask.GetAwaiter().GetResult();
                complete = !frame.Eof && frame.Terminated;
                return frame.Data ?? new byte[0];
            }
            catch (LineLimitException error) { return error.Prefix; }
            catch { return reader.SnapshotPartial(); }
        }

        private static CapturedText GetCapturedStderr(Task<CapturedText> stderrTask)
        {
            if (stderrTask == null || !stderrTask.IsCompleted) return new CapturedText { PrefixBytes = new byte[0], TotalBytes = 0 };
            try { return stderrTask.GetAwaiter().GetResult(); }
            catch { return new CapturedText { PrefixBytes = Encoding.UTF8.GetBytes("<stderr drain failed>"), TotalBytes = 0 }; }
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.WaitForExit(2000); } catch { }
        }

        private static byte[] WithLf(byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            var result = new byte[payload.Length + 1];
            Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
            result[result.Length - 1] = 10;
            return result;
        }

        private static string TryDecode(byte[] bytes)
        {
            try { return StrictUtf8.GetString(bytes ?? new byte[0]); }
            catch { return null; }
        }

        private static byte[] MakeErrorResponse(string code, string message)
        {
            var error = new Dictionary<string, object> { ["code"] = code, ["message"] = message };
            var response = new Dictionary<string, object> {
                ["ok"] = false,
                ["kind"] = "error",
                ["text"] = message,
                ["error"] = error
            };
            return StrictUtf8.GetBytes(JsonSerializer.Serialize(response, CompactJson));
        }

        private static string[] InspectionOperationsForProfile(string profile)
        {
            if (profile == "conventional") return CommonInspectionOperations.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return new string[] { "search" }.Concat(AuditedAgentLangInspectionOperations).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        private static bool IsInspectionOperation(string profile, string operation)
        {
            return operation != null && InspectionOperationsForProfile(profile).Contains(operation, StringComparer.Ordinal);
        }

        private static object InspectionBudgetMetadata(long maximumPayloadBytes, string profile)
        {
            return new Dictionary<string, object> {
                ["classifierVersion"] = InspectionClassifierVersion,
                ["profile"] = profile,
                ["operations"] = InspectionOperationsForProfile(profile),
                ["classificationRule"] = profile == "conventional"
                    ? "Exact conventional inspect/read/search operation names are inspection."
                    : "Exact audited AgentLang query operation names are inspection; failed-tests, test execution, example execution, evaluation, and mutations are excluded.",
                ["maximumPayloadUtf8Bytes"] = maximumPayloadBytes,
                ["payloadCounting"] = "Complete runtime response payload bytes before terminating LF; a preceding CR is included.",
                ["admission"] = "Whole complete valid JSON runtime responses, including diagnostics, are admitted atomically when payload bytes are at most the remaining budget."
            };
        }

        private static void CaptureValidObservedRuntimeResponse(byte[] bytes, bool? complete, bool accountingEnabled,
            ref byte[] rawRuntimeResponseBytes, ref string rawRuntimeCanonicalResponse, ref bool rawRuntimeResponseValid,
            ref bool? observedResponseValidJson)
        {
            if (!accountingEnabled || bytes == null || complete != true) return;
            try
            {
                rawRuntimeCanonicalResponse = CanonicalizeUtf8(bytes);
                rawRuntimeResponseBytes = bytes;
                rawRuntimeResponseValid = true;
                observedResponseValidJson = true;
            }
            catch
            {
                observedResponseValidJson = false;
            }
        }

        private static void WriteAccountedHostResponse(Stream output, TraceFile trace, string code, string message,
            string reason, int exchangeCount, ResponseAccounting accounting)
        {
            if (accounting == null)
            {
                WriteHostResponse(output, code, message);
                return;
            }

            byte[] bytes = MakeErrorResponse(code, message);
            byte[] wireBytes = WithLf(bytes);
            string canonical = CanonicalizeUtf8(bytes);
            accounting.RecordTerminalHostResponse(bytes.Length);
            trace.Write(new Dictionary<string, object> {
                ["event"] = "host-response-selected",
                ["schemaVersion"] = 2,
                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["scope"] = "terminal-control",
                ["reason"] = reason,
                ["exchangeCount"] = exchangeCount,
                ["code"] = code,
                ["selectedResponse"] = new Dictionary<string, object> {
                    ["rawLine"] = TryDecode(bytes),
                    ["canonical"] = canonical,
                    ["wireBase64"] = Convert.ToBase64String(wireBytes),
                    ["payloadUtf8Bytes"] = bytes.Length,
                    ["wireUtf8Bytes"] = wireBytes.Length,
                    ["payloadSha256"] = Sha256(bytes),
                    ["wireSha256"] = Sha256(wireBytes)
                },
                ["responseAccounting"] = accounting.Snapshot()
            });
            output.Write(bytes, 0, bytes.Length);
            output.WriteByte(10);
            output.Flush();
            accounting.RecordPipeDelivery(bytes.Length, wireBytes.Length);
            trace.Write(new Dictionary<string, object> {
                ["event"] = "response-delivered",
                ["schemaVersion"] = 2,
                ["index"] = null,
                ["exchangeCount"] = exchangeCount,
                ["atUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "host",
                ["scope"] = "terminal-control",
                ["code"] = code,
                ["pipeWriteFlushCompleted"] = true,
                ["selectedResponse"] = new Dictionary<string, object> {
                    ["payloadUtf8Bytes"] = bytes.Length,
                    ["payloadSha256"] = Sha256(bytes),
                    ["wireUtf8Bytes"] = wireBytes.Length,
                    ["wireSha256"] = Sha256(wireBytes)
                },
                ["responseAccounting"] = accounting.Snapshot()
            });
        }

        private static void WriteHostResponse(Stream output, string code, string message)
        {
            byte[] bytes = MakeErrorResponse(code, message);
            output.Write(bytes, 0, bytes.Length);
            output.WriteByte(10);
            output.Flush();
        }

        private static string CanonicalizeUtf8(byte[] bytes)
        {
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }))
            {
                ValidateNoDuplicateProperties(document.RootElement);
                return Canonicalize(document.RootElement);
            }
        }

        private static string Canonicalize(JsonElement element)
        {
            using (var stream = new MemoryStream())
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonical(element, writer);
                writer.Flush();
                return StrictUtf8.GetString(stream.ToArray());
            }
        }

        private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in element.EnumerateArray()) WriteCanonical(item, writer);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static void ValidateNoDuplicateProperties(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new RequestValidationException("TRIAL_INVALID_REQUEST", "JSON objects cannot contain duplicate property names.");
                    ValidateNoDuplicateProperties(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) ValidateNoDuplicateProperties(item);
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static object[] HashCliFiles(string cliDll)
        {
            var paths = new List<string> { cliDll, Path.ChangeExtension(cliDll, ".deps.json"), Path.ChangeExtension(cliDll, ".runtimeconfig.json") };
            return paths.Select(path => (object)new Dictionary<string, object> {
                ["name"] = Path.GetFileName(path),
                ["sha256"] = Sha256(File.ReadAllBytes(path))
            }).ToArray();
        }

        private static bool IsWithin(string directory, string path)
        {
            string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void AssertNoReparseComponents(string path, bool allowMissingLeaf)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            string current = root;
            string relative = full.Substring(root.Length);
            string[] parts = relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                bool exists = File.Exists(current) || Directory.Exists(current);
                if (!exists)
                {
                    if (i == parts.Length - 1 && !allowMissingLeaf) return;
                    continue;
                }
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing reparse path component: " + current);
            }
        }

        private sealed class RequestValidationException : Exception
        {
            public string Code;
            public RequestValidationException(string code, string message) : base(message) { Code = code; }
        }

        private sealed class TraceFile : IDisposable
        {
            private readonly FileStream _stream;
            public TraceFile(string path)
            {
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            public void Write(object value)
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, CompactJson);
                _stream.Write(json, 0, json.Length);
                _stream.WriteByte(10);
                _stream.Flush(true);
            }
            public void Dispose() { _stream.Dispose(); }
        }

        private sealed class JobObject : IDisposable
        {
            private const uint KillOnJobClose = 0x00002000;
            private IntPtr _handle;

            private JobObject(IntPtr handle) { _handle = handle; }

            public static JobObject Create()
            {
                IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create runtime child job object.");
                var limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(handle);
                    throw new System.ComponentModel.Win32Exception(error, "Could not set kill-on-close on the runtime job object.");
                }
                return new JobObject(handle);
            }

            public void Assign(IntPtr processHandle)
            {
                if (!AssignProcessToJobObject(_handle, processHandle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not bind the runtime child to the kill-on-close job object.");
            }

            public void Dispose()
            {
                if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct BasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct ExtendedLimitInformation
            {
                public BasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
'@

try {
    if ($env:OS -ne 'Windows_NT') { throw 'This trial host uses Windows job objects and currently requires Windows.' }
    if ($PSBoundParameters.ContainsKey('AdditionalCliArgumentsJson')) {
        if ($PSBoundParameters.ContainsKey('AdditionalCliArguments')) { throw 'Use either AdditionalCliArguments or AdditionalCliArgumentsJson, not both.' }
        $decodedArguments = ConvertFrom-Json -InputObject $AdditionalCliArgumentsJson -NoEnumerate
        if ($decodedArguments -isnot [System.Array] -or @($decodedArguments | Where-Object { $_ -isnot [string] }).Count -gt 0) {
            throw 'AdditionalCliArgumentsJson must be a JSON array of strings.'
        }
        $AdditionalCliArguments = @($decodedArguments)
    }
    $allowed = @($AllowedOperations | ForEach-Object { $_ -split ',' } | Sort-Object -Unique)
    if ($allowed.Count -ne @($AllowedOperations | ForEach-Object { $_ -split ',' }).Count) { throw 'AllowedOperations must contain unique operation names.' }
    $Capabilities = @($Capabilities | ForEach-Object { $_ -split ',' })
    $knownEffects = @('fs.read', 'fs.write', 'db.read', 'db.write', 'network.read', 'network.write', 'process.execute', 'clock.read', 'random.read', 'console.write')
    foreach ($capability in $Capabilities) {
        if ($knownEffects -cnotcontains $capability) { throw "Unknown host capability '$capability'." }
    }
    if (@($Capabilities | Sort-Object -Unique).Count -ne $Capabilities.Count) { throw 'Capabilities must be unique.' }
    Add-Type -TypeDefinition $hostSource -Language CSharp
    $exitCode = [AgentLang.SubagentTrialHostV2.TrialHost]::Run(
        $CliDll, $ProjectPath, $TracePath, $allowed, $Profile, $AdditionalCliArguments, $Capabilities, $ClockValue,
        $MaxRequestBytes, $MaxResponseBytes, $MaxInspectionResponseBytes, $ExchangeTimeoutMilliseconds, $MaxExchanges)
    exit $exitCode
} catch {
    [Console]::Error.WriteLine(('TRIAL_HOST_CONFIGURATION_FAILURE: ' + $_.Exception.Message))
    exit 1
}
