/*
 * hvAnpr API benchmark.
 *
 * Examples:
 *   hvanpr benchmark --image photo.jpg --requests 200 --concurrency 8 --api-key KEY --label "8c"
 *   hvanpr benchmark --folder ./samples --duration 60 --concurrency 1,2,4,8 --api-key KEY
 *   hvanpr benchmark --image photo.jpg --requests 100 --concurrency 4 --api-key KEY --output results.csv
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

static class BenchmarkCommand
{
    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp",
    };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private record RequestResult(
        bool Ok,
        int StatusCode,
        double LatencyMs,
        double? ServerTotalMs = null,
        int? PlateCount = null,
        string? Error = null);

    private record RunStats(
        string Label,
        int Concurrency,
        int Total,
        int Success,
        int Failed,
        double DurationS,
        double Rps,
        Dictionary<string, int> StatusCounts,
        Dictionary<string, double> LatencyMs,
        Dictionary<string, double> ServerTotalMs,
        List<string> SampleErrors);

    public static async Task<int> RunAsync(string[] args)
    {
        string? imagePath = null;
        string? folderPath = null;
        string url = Environment.GetEnvironmentVariable("HVA_API_URL") ?? "http://127.0.0.1:8090";
        string apiKey = Environment.GetEnvironmentVariable("HVA_API_KEY") ?? "";
        string label = "benchmark";
        int? requestsCount = null;
        double? durationS = null;
        var concurrencies = new List<int> { 1 };
        int warmup = 3;
        double timeoutSec = 120.0;
        string? output = null;
        bool skipCheck = false;
        int? shuffleSeed = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--image" when i + 1 < args.Length:        imagePath = args[++i]; break;
                case "--folder" when i + 1 < args.Length:       folderPath = args[++i]; break;
                case "--url" when i + 1 < args.Length:          url = args[++i]; break;
                case "--api-key" when i + 1 < args.Length:      apiKey = args[++i]; break;
                case "--label" when i + 1 < args.Length:        label = args[++i]; break;
                case "--requests" when i + 1 < args.Length:     requestsCount = int.Parse(args[++i]); break;
                case "--duration" when i + 1 < args.Length:     durationS = double.Parse(args[++i]); break;
                case "--warmup" when i + 1 < args.Length:       warmup = int.Parse(args[++i]); break;
                case "--timeout" when i + 1 < args.Length:      timeoutSec = double.Parse(args[++i]); break;
                case "--output" when i + 1 < args.Length:       output = args[++i]; break;
                case "--shuffle-seed" when i + 1 < args.Length: shuffleSeed = int.Parse(args[++i]); break;
                case "--skip-check": skipCheck = true; break;
                case "--concurrency" when i + 1 < args.Length:
                    concurrencies = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse)
                        .ToList();
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Error: provide --api-key or set HVA_API_KEY");
            return 1;
        }

        if (requestsCount is null && durationS is null)
        {
            Console.Error.WriteLine("Error: provide --requests or --duration");
            return 1;
        }

        if (imagePath is null && folderPath is null)
        {
            Console.Error.WriteLine("Error: provide --image or --folder");
            return 1;
        }

        List<string> images;
        try { images = CollectImages(imagePath, folderPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }

        if (shuffleSeed.HasValue)
        {
            var rng = new Random(shuffleSeed.Value);
            images = [.. images.OrderBy(_ => rng.Next())];
        }

        Console.WriteLine($"Images: {images.Count} file(s)");
        Console.WriteLine($"Target: {url}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec + 10) };

        if (!skipCheck)
        {
            var (ok, msg) = await CheckServerAsync(http, url, apiKey);
            if (!ok) { Console.Error.WriteLine($"Server check failed: {msg}"); return 1; }
            Console.WriteLine($"Server check: {msg}");
        }

        var allRuns = new List<RunStats>();

        foreach (var concurrency in concurrencies)
        {
            var runLabel = concurrencies.Count == 1 ? label : $"{label} @ c={concurrency}";
            var stats = await RunBenchmarkAsync(
                http, url, apiKey, images, concurrency,
                requestsCount, durationS, warmup, timeoutSec, runLabel);
            PrintRun(stats);
            allRuns.Add(stats);
        }

        if (output is not null)
        {
            var outPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

            if (Path.GetExtension(outPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                WriteCsv(outPath, allRuns);
            else
            {
                var payload = new { url, image_count = images.Count, runs = allRuns };
                File.WriteAllText(outPath, JsonSerializer.Serialize(payload, IndentedJson));
            }
            Console.WriteLine($"\nResults written to {outPath}");
        }

        Console.WriteLine("\nDone.");
        return 0;
    }

    private static async Task<RunStats> RunBenchmarkAsync(
        HttpClient http, string url, string apiKey, List<string> images,
        int concurrency, int? requestsCount, double? durationS,
        int warmup, double timeoutSec, string label)
    {
        var benchResults = new ConcurrentBag<RequestResult>();

        async Task DoRequest(int index, bool measure)
        {
            var image = images[index % images.Count];
            var result = await CallApiAsync(http, url, apiKey, image, timeoutSec);
            if (measure)
                benchResults.Add(result);
        }

        if (warmup > 0)
            await Task.WhenAll(Enumerable.Range(0, warmup).Select(i => DoRequest(i, false)));

        var sw = Stopwatch.StartNew();

        if (durationS.HasValue)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationS.Value));
            var inFlight = new HashSet<Task>();
            var idx = warmup;

            while (!cts.IsCancellationRequested || inFlight.Count > 0)
            {
                while (inFlight.Count < concurrency && !cts.IsCancellationRequested)
                    inFlight.Add(DoRequest(idx++, true));

                if (inFlight.Count == 0)
                    break;

                var done = await Task.WhenAny(inFlight);
                await done;
                inFlight.Remove(done);
            }
        }
        else
        {
            var sem = new SemaphoreSlim(concurrency, concurrency);

            async Task Throttled(int index)
            {
                await sem.WaitAsync();
                try { await DoRequest(index, true); }
                finally { sem.Release(); }
            }

            await Task.WhenAll(Enumerable.Range(warmup, requestsCount!.Value).Select(Throttled));
        }

        var elapsed = sw.Elapsed.TotalSeconds;
        var measured = benchResults.ToList();

        var statusCounts = new Dictionary<string, int>();
        var latencies = new List<double>(measured.Count);
        var serverTotals = new List<double>();
        var sampleErrors = new List<string>();
        var success = 0;

        foreach (var r in measured)
        {
            var key = r.StatusCode.ToString();
            statusCounts[key] = statusCounts.GetValueOrDefault(key) + 1;
            latencies.Add(r.LatencyMs);
            if (r.ServerTotalMs.HasValue)
                serverTotals.Add(r.ServerTotalMs.Value);
            if (r.Ok)
                success++;
            else if (r.Error is not null && sampleErrors.Count < 5)
                sampleErrors.Add($"HTTP {r.StatusCode}: {r.Error}");
        }

        var total = measured.Count;
        var rps = elapsed > 0 ? total / elapsed : 0.0;

        return new RunStats(
            Label: label,
            Concurrency: concurrency,
            Total: total,
            Success: success,
            Failed: total - success,
            DurationS: Math.Round(elapsed, 3),
            Rps: Math.Round(rps, 2),
            StatusCounts: statusCounts,
            LatencyMs: SummarizeLatencies(latencies),
            ServerTotalMs: SummarizeLatencies(serverTotals),
            SampleErrors: sampleErrors);
    }

    private static async Task<RequestResult> CallApiAsync(
        HttpClient http, string baseUrl, string apiKey, string imagePath, double timeoutSec)
    {
        var apiUrl = $"{baseUrl.TrimEnd('/')}/api/v1/anpr";
        var sw = Stopwatch.StartNew();
        try
        {
            await using var stream = File.OpenRead(imagePath);
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "image", Path.GetFileName(imagePath));

            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
            request.Headers.Add("X-API-Key", apiKey);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var response = await http.SendAsync(request, cts.Token);
            var latencyMs = sw.Elapsed.TotalMilliseconds;
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                double? serverTotal = null;
                int? plateCount = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("timing", out var timing) &&
                        timing.TryGetProperty("total_ms", out var totalMs))
                        serverTotal = totalMs.GetDouble();
                    if (doc.RootElement.TryGetProperty("plate_count", out var pc))
                        plateCount = pc.GetInt32();
                }
                catch { }
                return new RequestResult(true, (int)response.StatusCode, latencyMs, serverTotal, plateCount);
            }

            var detail = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("detail", out var d))
                    detail = d.ValueKind == JsonValueKind.String ? d.GetString() ?? detail : d.GetRawText();
            }
            catch { }
            return new RequestResult(false, (int)response.StatusCode, latencyMs,
                Error: detail[..Math.Min(detail.Length, 200)]);
        }
        catch (Exception ex)
        {
            return new RequestResult(false, 0, sw.Elapsed.TotalMilliseconds,
                Error: ex.Message[..Math.Min(ex.Message.Length, 200)]);
        }
    }

    private static async Task<(bool Ok, string Message)> CheckServerAsync(
        HttpClient http, string baseUrl, string apiKey)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/anpr";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-API-Key", apiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, cts.Token);
            if ((int)response.StatusCode == 401)
                return (false, "Invalid API key");
            return (true, $"Server reachable (HTTP {(int)response.StatusCode} without image body is OK)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static List<string> CollectImages(string? imagePath, string? folderPath)
    {
        if (imagePath is not null)
        {
            var path = Path.GetFullPath(imagePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Image not found: {path}");
            if (!SupportedExt.Contains(Path.GetExtension(path)))
                throw new ArgumentException($"Unsupported image type: {Path.GetExtension(path)}");
            return [path];
        }

        var root = Path.GetFullPath(folderPath!);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        var images = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExt.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        if (images.Count == 0)
            throw new InvalidOperationException($"No supported images in {root}");

        return images;
    }

    private static void PrintRun(RunStats s)
    {
        var sep = new string('=', 72);
        Console.WriteLine($"\n{sep}");
        Console.WriteLine($"  {s.Label}  |  concurrency={s.Concurrency}");
        Console.WriteLine(sep);
        Console.WriteLine($"  Requests: {s.Total}  OK: {s.Success}  Failed: {s.Failed}");
        Console.WriteLine($"  Duration: {s.DurationS}s  Throughput: {s.Rps} req/s");
        if (s.StatusCounts.Count > 0)
        {
            var codes = string.Join(", ", s.StatusCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"  Status codes: {codes}");
        }
        var lat = s.LatencyMs;
        Console.WriteLine(
            $"  Client latency (ms): min={lat["min"]} mean={lat["mean"]} " +
            $"p50={lat["p50"]} p95={lat["p95"]} p99={lat["p99"]} max={lat["max"]}");
        if (s.ServerTotalMs.TryGetValue("mean", out var srvMean) && srvMean > 0)
        {
            var srv = s.ServerTotalMs;
            Console.WriteLine(
                $"  Server total_ms:     mean={srv["mean"]} p50={srv["p50"]} " +
                $"p95={srv["p95"]} max={srv["max"]}");
        }
        foreach (var err in s.SampleErrors)
            Console.WriteLine($"    - {err}");
    }

    private static void WriteCsv(string path, List<RunStats> runs)
    {
        var lines = new List<string>
        {
            "label,concurrency,total,success,failed,duration_s,rps,status_counts," +
            "lat_min,lat_mean,lat_p50,lat_p95,lat_p99,lat_max,srv_mean,srv_p50,srv_p95"
        };

        foreach (var s in runs)
        {
            var sc = JsonSerializer.Serialize(s.StatusCounts);
            var lat = s.LatencyMs;
            var srv = s.ServerTotalMs;
            lines.Add(
                $"{EscCsv(s.Label)},{s.Concurrency},{s.Total},{s.Success},{s.Failed}," +
                $"{s.DurationS},{s.Rps},{EscCsv(sc)}," +
                $"{lat.GetValueOrDefault("min")},{lat.GetValueOrDefault("mean")}," +
                $"{lat.GetValueOrDefault("p50")},{lat.GetValueOrDefault("p95")}," +
                $"{lat.GetValueOrDefault("p99")},{lat.GetValueOrDefault("max")}," +
                $"{srv.GetValueOrDefault("mean")},{srv.GetValueOrDefault("p50")}," +
                $"{srv.GetValueOrDefault("p95")}");
        }

        File.WriteAllLines(path, lines);
    }

    private static string EscCsv(string s) =>
        s.AsSpan().IndexOfAny(',', '"', '\n') >= 0
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    private static Dictionary<string, double> SummarizeLatencies(List<double> values)
    {
        if (values.Count == 0)
            return new() { ["min"] = 0, ["mean"] = 0, ["max"] = 0, ["p50"] = 0, ["p90"] = 0, ["p95"] = 0, ["p99"] = 0 };

        values.Sort();
        var mean = values.Average();
        return new()
        {
            ["min"]  = Math.Round(values[0], 1),
            ["mean"] = Math.Round(mean, 1),
            ["max"]  = Math.Round(values[^1], 1),
            ["p50"]  = Math.Round(Percentile(values, 50), 1),
            ["p90"]  = Math.Round(Percentile(values, 90), 1),
            ["p95"]  = Math.Round(Percentile(values, 95), 1),
            ["p99"]  = Math.Round(Percentile(values, 99), 1),
        };
    }

    private static double Percentile(List<double> sorted, double pct)
    {
        if (sorted.Count == 1)
            return sorted[0];
        var k = (sorted.Count - 1) * (pct / 100.0);
        var f = (int)k;
        var c = Math.Min(f + 1, sorted.Count - 1);
        return f == c ? sorted[f] : sorted[f] + (sorted[c] - sorted[f]) * (k - f);
    }

    private static void PrintHelp() => Console.WriteLine("""
        Usage: hvanpr benchmark [options]

        Load-test the hvAnpr ANPR API and report throughput / latency.

        Source (one required):
          --image <path>          Single image to use for all requests
          --folder <path>         Folder of images (cycles through all)

        Options:
          --url <url>             Server base URL (default: HVA_API_URL or http://127.0.0.1:8090)
          --api-key <key>         API key (default: HVA_API_KEY env var)
          --requests <n>          Total requests per concurrency level (excluding warmup)
          --duration <s>          Run for this many seconds instead of a fixed request count
          --concurrency <n[,n]>   Concurrent clients; comma-separated for sweep (default: 1)
          --warmup <n>            Warmup requests before measuring (default: 3)
          --timeout <s>           Per-request timeout in seconds (default: 120)
          --label <name>          Tag for this run (default: benchmark)
          --output <path>         Write results to .json or .csv
          --shuffle-seed <n>      Shuffle image order with this random seed
          --skip-check            Skip the initial server connectivity check
          -h, --help              Show this help

        Examples:
          hvanpr benchmark --image photo.jpg --requests 200 --concurrency 8 --api-key KEY
          hvanpr benchmark --folder ./images --duration 60 --concurrency 1,2,4,8 --api-key KEY
          hvanpr benchmark --image photo.jpg --requests 100 --concurrency 4 --api-key KEY --output results.csv
        """);
}
