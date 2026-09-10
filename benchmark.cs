/*
 * hvAnpr API benchmark.
 *
 * Examples:
 *   hvanpr benchmark --image photo.jpg --requests 200 --concurrency 8 --api-key KEY --label "8c"
 *   hvanpr benchmark --folder ./samples --duration 60 --concurrency 1,2,4,8 --api-key KEY
 *   hvanpr benchmark --image photo.jpg --requests 100 --concurrency 4 --api-key KEY --output results.csv
 *   hvanpr benchmark --folder ./samples --requests 50 --concurrency 4 --api-key KEY --results-csv anpr.csv
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

static class BenchmarkCommand
{
    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp",
    };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private record RequestResult(
        bool Ok,
        int StatusCode,
        double LatencyMs,
        string ImagePath = "",
        int RequestIndex = 0,
        double? ServerTotalMs = null,
        int PlateCount = 0,
        AnprResponse? Anpr = null,
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
        List<string> SampleErrors,
        int PlatesDetected,
        List<RequestResult> Results);

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
        string? resultsCsv = null;
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
                case "--requests" when i + 1 < args.Length:     requestsCount = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--duration" when i + 1 < args.Length:     durationS = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--warmup" when i + 1 < args.Length:       warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--timeout" when i + 1 < args.Length:      timeoutSec = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--output" when i + 1 < args.Length:       output = args[++i]; break;
                case "--results-csv" when i + 1 < args.Length:  resultsCsv = args[++i]; break;
                case "--shuffle-seed" when i + 1 < args.Length: shuffleSeed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--skip-check": skipCheck = true; break;
                case "--concurrency" when i + 1 < args.Length:
                    concurrencies = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
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

        if (concurrencies.Any(c => c < 1))
        {
            Console.Error.WriteLine("Error: --concurrency values must be >= 1");
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

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(256, concurrencies.Max() * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec + 10) };

        if (!skipCheck)
        {
            var (ok, msg) = await CheckServerAsync(http, url, apiKey);
            if (!ok) { Console.Error.WriteLine($"Server check failed: {msg}"); return 1; }
            Console.WriteLine($"Server check: {msg}");
        }

        var allRuns = new List<RunStats>();
        var captureAnpr = resultsCsv is not null;

        foreach (var concurrency in concurrencies)
        {
            var runLabel = concurrencies.Count == 1 ? label : $"{label} @ c={concurrency}";
            var stats = await RunBenchmarkAsync(
                http, url, apiKey, images, concurrency,
                requestsCount, durationS, warmup, timeoutSec, runLabel, captureAnpr);
            PrintRun(stats);
            allRuns.Add(stats);
        }

        if (output is not null)
        {
            var outPath = Path.GetFullPath(output);
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            if (Path.GetExtension(outPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                WriteStatsCsv(outPath, allRuns);
            else
            {
                var payload = new
                {
                    url,
                    image_count = images.Count,
                    runs = allRuns.Select(s => new
                    {
                        s.Label, s.Concurrency, s.Total, s.Success, s.Failed,
                        s.DurationS, s.Rps, s.StatusCounts, s.LatencyMs, s.ServerTotalMs, s.SampleErrors,
                    }),
                };
                File.WriteAllText(outPath, JsonSerializer.Serialize(payload, IndentedJson));
            }
            Console.WriteLine($"\nBenchmark stats written to {outPath}");
        }

        if (resultsCsv is not null)
        {
            var csvPath = Path.GetFullPath(resultsCsv);
            var csvDir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(csvDir))
                Directory.CreateDirectory(csvDir);
            WriteAnprResultsCsv(csvPath, allRuns);
            Console.WriteLine($"ANPR results written to {csvPath}");
        }

        Console.WriteLine("\nDone.");
        return 0;
    }

    private static async Task<RunStats> RunBenchmarkAsync(
        HttpClient http, string url, string apiKey, List<string> images,
        int concurrency, int? requestsCount, double? durationS,
        int warmup, double timeoutSec, string label, bool captureAnpr)
    {
        var benchResults = new ConcurrentBag<RequestResult>();

        async Task DoRequest(int index, bool measure)
        {
            var image = images[index % images.Count];
            var result = await CallApiAsync(http, url, apiKey, image, timeoutSec, index, captureAnpr && measure);
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
        var platesDetected = 0;

        foreach (var r in measured)
        {
            var key = r.StatusCode.ToString();
            statusCounts[key] = statusCounts.GetValueOrDefault(key) + 1;
            latencies.Add(r.LatencyMs);
            if (r.ServerTotalMs.HasValue)
                serverTotals.Add(r.ServerTotalMs.Value);
            if (r.Ok)
            {
                success++;
                platesDetected += r.PlateCount;
            }
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
            SampleErrors: sampleErrors,
            PlatesDetected: platesDetected,
            Results: captureAnpr ? measured : []);
    }

    private static async Task<RequestResult> CallApiAsync(
        HttpClient http, string baseUrl, string apiKey, string imagePath, double timeoutSec,
        int requestIndex, bool captureAnpr)
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
                AnprResponse? anpr = null;
                double? serverTotal = null;
                var plateCount = 0;

                if (captureAnpr)
                {
                    try
                    {
                        anpr = JsonSerializer.Deserialize<AnprResponse>(body, CaseInsensitive);
                        if (anpr is not null)
                        {
                            plateCount = anpr.PlateCount;
                            serverTotal = anpr.Timing?.TotalMs;
                        }
                    }
                    catch { }
                }

                if (anpr is null)
                {
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
                }

                return new RequestResult(
                    true, (int)response.StatusCode, latencyMs, imagePath, requestIndex,
                    serverTotal, plateCount, anpr);
            }

            var detail = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("detail", out var d))
                    detail = d.ValueKind == JsonValueKind.String ? d.GetString() ?? detail : d.GetRawText();
            }
            catch { }
            return new RequestResult(false, (int)response.StatusCode, latencyMs, imagePath, requestIndex,
                Error: detail[..Math.Min(detail.Length, 200)]);
        }
        catch (Exception ex)
        {
            return new RequestResult(false, 0, sw.Elapsed.TotalMilliseconds, imagePath, requestIndex,
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
        if (s.PlatesDetected > 0)
            Console.WriteLine($"  Plates detected (sum): {s.PlatesDetected}");
        foreach (var err in s.SampleErrors)
            Console.WriteLine($"    - {err}");
    }

    private static void WriteStatsCsv(string path, List<RunStats> runs)
    {
        var lines = new List<string>
        {
            "label,concurrency,total,success,failed,duration_s,rps,status_counts," +
            "lat_min,lat_mean,lat_p50,lat_p90,lat_p95,lat_p99,lat_max,srv_mean,srv_p50,srv_p95"
        };

        foreach (var s in runs)
        {
            var sc = JsonSerializer.Serialize(s.StatusCounts);
            var lat = s.LatencyMs;
            var srv = s.ServerTotalMs;
            lines.Add(
                $"{EscCsv(s.Label)},{s.Concurrency},{s.Total},{s.Success},{s.Failed}," +
                $"{F(s.DurationS)},{F(s.Rps)},{EscCsv(sc)}," +
                $"{F(lat.GetValueOrDefault("min"))},{F(lat.GetValueOrDefault("mean"))}," +
                $"{F(lat.GetValueOrDefault("p50"))},{F(lat.GetValueOrDefault("p90"))}," +
                $"{F(lat.GetValueOrDefault("p95"))},{F(lat.GetValueOrDefault("p99"))}," +
                $"{F(lat.GetValueOrDefault("max"))}," +
                $"{F(srv.GetValueOrDefault("mean"))},{F(srv.GetValueOrDefault("p50"))}," +
                $"{F(srv.GetValueOrDefault("p95"))}");
        }

        File.WriteAllLines(path, lines);
    }

    private static void WriteAnprResultsCsv(string path, List<RunStats> runs)
    {
        var lines = new List<string>
        {
            "label,concurrency,request_index,file,ok,status_code,latency_ms,error," +
            "plate_count,plate_index,plate,detection_conf,plate_conf," +
            "country,plate_type,region_conf," +
            "bbox_x1,bbox_y1,bbox_x2,bbox_y2," +
            "vehicle_bbox_x1,vehicle_bbox_y1,vehicle_bbox_x2,vehicle_bbox_y2," +
            "vehicle_detect_ms,plate_detect_ms,ocr_ms,total_ms"
        };

        foreach (var run in runs)
        {
            foreach (var r in run.Results.OrderBy(x => x.RequestIndex))
            {
                var file = Path.GetFileName(r.ImagePath);
                var plates = r.Anpr?.Plates;
                var timing = r.Anpr?.Timing;

                if (r.Ok && plates is { Count: > 0 })
                {
                    foreach (var p in plates)
                        lines.Add(AnprCsvRow(run, r, file, r.PlateCount, p, timing));
                }
                else
                {
                    lines.Add(AnprCsvRow(run, r, file, r.PlateCount, null, timing));
                }
            }
        }

        File.WriteAllLines(path, lines);
    }

    private static string AnprCsvRow(
        RunStats run, RequestResult r, string file, int plateCount, PlateResult? p, TimingInfo? timing)
    {
        var bbox = p?.Bbox is { Length: 4 } b ? b : null;
        var vbbox = p?.VehicleBbox is { Length: 4 } vb ? vb : null;

        return string.Join(",",
            EscCsv(run.Label),
            run.Concurrency.ToString(CultureInfo.InvariantCulture),
            r.RequestIndex.ToString(CultureInfo.InvariantCulture),
            EscCsv(file),
            r.Ok ? "1" : "0",
            r.StatusCode.ToString(CultureInfo.InvariantCulture),
            F(r.LatencyMs),
            EscCsv(r.Error ?? ""),
            plateCount.ToString(CultureInfo.InvariantCulture),
            p?.Index.ToString(CultureInfo.InvariantCulture) ?? "",
            EscCsv(p?.Plate ?? ""),
            F2(p?.DetectionConf), F2(p?.PlateConf),
            EscCsv(p?.Country ?? ""), EscCsv(p?.PlateType ?? ""), F2(p?.RegionConf),
            bbox is null ? "" : bbox[0].ToString(CultureInfo.InvariantCulture),
            bbox is null ? "" : bbox[1].ToString(CultureInfo.InvariantCulture),
            bbox is null ? "" : bbox[2].ToString(CultureInfo.InvariantCulture),
            bbox is null ? "" : bbox[3].ToString(CultureInfo.InvariantCulture),
            vbbox is null ? "" : vbbox[0].ToString(CultureInfo.InvariantCulture),
            vbbox is null ? "" : vbbox[1].ToString(CultureInfo.InvariantCulture),
            vbbox is null ? "" : vbbox[2].ToString(CultureInfo.InvariantCulture),
            vbbox is null ? "" : vbbox[3].ToString(CultureInfo.InvariantCulture),
            F1(timing?.VehicleDetectMs), F1(timing?.PlateDetectMs),
            F1(timing?.OcrMs), F1(timing?.TotalMs ?? r.ServerTotalMs));
    }

    private static string EscCsv(string s) =>
        s.AsSpan().IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static string F2(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
    private static string F1(double? v) => v?.ToString("F1", CultureInfo.InvariantCulture) ?? "";

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
          --output <path>         Write benchmark stats to .json or .csv
          --results-csv <path>    Write per-request ANPR plate results to CSV
          --shuffle-seed <n>      Shuffle image order with this random seed
          --skip-check            Skip the initial server connectivity check
          -h, --help              Show this help

        Examples:
          hvanpr benchmark --image photo.jpg --requests 200 --concurrency 8 --api-key KEY
          hvanpr benchmark --folder ./images --duration 60 --concurrency 1,2,4,8 --api-key KEY
          hvanpr benchmark --image photo.jpg --requests 100 --concurrency 4 --api-key KEY --output results.csv
          hvanpr benchmark --folder ./images --requests 50 --concurrency 4 --api-key KEY --results-csv anpr.csv
        """);
}
