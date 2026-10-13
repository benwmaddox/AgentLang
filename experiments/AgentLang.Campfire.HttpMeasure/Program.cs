using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed record Config(string Uri, string Cookie, string UserAgent,
    int ExpectedStatus, string ExpectedBodySha256, int Concurrency = 1,
    int Seconds = 10, int WarmupRequests = 20, int ServerPid = 0,
    string Method = "GET", int MaxBodyBytes = 1048576);

internal sealed class Samples
{
    // 1 us resolution below 100 ms; 10 ms above it through 60 s.
    public readonly long[] Buckets = new long[105992];
    public long Correct, Failed, Bytes;
    public readonly Dictionary<string, long> Errors = new();
    public static int Index(double micros) => micros < 100000
        ? Math.Max(0, (int)micros)
        : micros < 60000000 ? 100000 + (int)((micros - 100000) / 10000) : 105991;
    public static double UpperMicros(int index) => index < 100000 ? index + 1
        : index == 105991 ? double.PositiveInfinity : 100000 + (index - 100000 + 1) * 10000;
    public void Error(string category)
    {
        Failed++;
        Errors[category] = Errors.GetValueOrDefault(category) + 1;
    }
    public double? Quantile(double fraction)
    {
        if (Correct == 0) return null;
        long target = (long)Math.Ceiling(Correct * fraction), count = 0;
        for (int i = 0; i < Buckets.Length; i++)
        {
            count += Buckets[i];
            if (count >= target) return double.IsFinite(UpperMicros(i)) ? UpperMicros(i) / 1000 : null;
        }
        throw new InvalidOperationException("Histogram count mismatch");
    }
}

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            var samples = new Samples { Correct = 4 };
            foreach (double n in new double[] { 0, 99, 99999, 100000 }) samples.Buckets[Samples.Index(n)]++;
            if (samples.Quantile(.5) != .1 || samples.Quantile(1) != 110 ||
                Samples.Index(59999999) != 105989 || Samples.Index(60000000) != 105991)
                throw new InvalidOperationException("Histogram boundary failure");
            Console.WriteLine("Histogram boundaries and quantiles passed");
            return 0;
        }
        if (args.Length != 2) throw new ArgumentException("Usage: HttpMeasure config.json result.json");
        Config config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(args[0]), Json)
            ?? throw new ArgumentException("Missing config");
        var uri = new Uri(config.Uri);
        if (!uri.IsLoopback || uri.Scheme != "http" || uri.UserInfo.Length != 0 ||
            config.Concurrency is < 1 or > 256 || config.Seconds is < 1 or > 300 ||
            config.WarmupRequests is < 0 or > 10000 || config.ExpectedStatus is < 100 or > 599 ||
            config.MaxBodyBytes is < 1 or > 16777216 || config.Method is not ("GET" or "HEAD"))
            throw new ArgumentException("Invalid bounded loopback configuration");
        byte[] expectedHash = Convert.FromHexString(config.ExpectedBodySha256);
        if (expectedHash.Length != 32) throw new ArgumentException("Expected SHA256 required");
        using var handler = new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = config.Concurrency
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        async Task<bool> Request(Samples? samples)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(config.Method), uri) {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                if (config.Cookie.Length != 0) request.Headers.Add("Cookie", config.Cookie);
                if (config.UserAgent.Length != 0) request.Headers.Add("User-Agent", config.UserAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                await response.Content.LoadIntoBufferAsync(config.MaxBodyBytes);
                byte[] body = await response.Content.ReadAsByteArrayAsync();
                if ((int)response.StatusCode != config.ExpectedStatus)
                {
                    samples?.Error("status:" + (int)response.StatusCode);
                    return false;
                }
                if (!SHA256.HashData(body).AsSpan().SequenceEqual(expectedHash))
                {
                    samples?.Error("body-sha256");
                    return false;
                }
                if (samples != null)
                {
                    samples.Correct++; samples.Bytes += body.Length;
                    samples.Buckets[Samples.Index(Stopwatch.GetElapsedTime(started).TotalMicroseconds)]++;
                }
                return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                samples?.Error(exception.GetType().Name);
                return false;
            }
        }
        for (int i = 0; i < config.WarmupRequests; i++)
            if (!await Request(null)) throw new InvalidOperationException("Warmup response mismatch/failure");
        using Process? server = config.ServerPid == 0 ? null : Process.GetProcessById(config.ServerPid);
        long peakWorkingSet = 0, peakPrivate = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long deadline = 0;
        Task<Samples>[] workers = Enumerable.Range(0, config.Concurrency).Select(async _ => {
            var samples = new Samples();
            await start.Task;
            while (Stopwatch.GetTimestamp() < deadline) await Request(samples);
            return samples;
        }).ToArray();
        var timer = Stopwatch.StartNew();
        deadline = Stopwatch.GetTimestamp() + (long)(config.Seconds * Stopwatch.Frequency);
        start.SetResult();
        Task<Samples[]> completion = Task.WhenAll(workers);
        while (!completion.IsCompleted)
        {
            if (server != null)
            {
                server.Refresh();
                if (server.HasExited) throw new InvalidOperationException("Server exited during measurement");
                peakWorkingSet = Math.Max(peakWorkingSet, server.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, server.PrivateMemorySize64);
            }
            await Task.WhenAny(completion, Task.Delay(100));
        }
        Samples[] results = await completion;
        timer.Stop();
        var merged = new Samples();
        foreach (Samples sample in results)
        {
            merged.Correct += sample.Correct; merged.Failed += sample.Failed; merged.Bytes += sample.Bytes;
            for (int i = 0; i < merged.Buckets.Length; i++) merged.Buckets[i] += sample.Buckets[i];
            foreach (var error in sample.Errors) merged.Errors[error.Key] = merged.Errors.GetValueOrDefault(error.Key) + error.Value;
        }
        var report = new {
            uri = uri.GetLeftPart(UriPartial.Path), method = config.Method,
            config.Concurrency, requestedSeconds = config.Seconds, measuredSeconds = timer.Elapsed.TotalSeconds,
            correctResponses = merged.Correct, failures = merged.Failed,
            correctRequestsPerSecond = merged.Correct / timer.Elapsed.TotalSeconds,
            responseBytes = merged.Bytes, p50MillisecondsUpperBound = merged.Quantile(.5),
            p95MillisecondsUpperBound = merged.Quantile(.95), p99MillisecondsUpperBound = merged.Quantile(.99),
            latencyOverflowAt60Seconds = merged.Buckets[105991], errors = merged.Errors,
            sampledServerWorkingSetPeak = peakWorkingSet, sampledServerPrivateBytesPeak = peakPrivate,
            config.ServerPid, config.WarmupRequests, config.ExpectedBodySha256,
            methodology = "Closed-loop HTTP/1.1 keepalive; no redirects/decompression; complete body SHA256 on every request; in-flight drain included; correct-response latency only; 100ms process-memory sampling excludes kernel buffers. Client costs are included equally in both workloads."
        };
        await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return merged.Failed == 0 && merged.Correct > 0 ? 0 : 1;
    }
}
