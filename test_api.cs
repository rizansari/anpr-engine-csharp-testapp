/*
 * hvAnpr API test client.
 *
 * Single image:  dotnet run -- <image-path> [--api-key KEY] [--url URL]
 * Batch mode:    dotnet run -- --source <folder> --dest <folder> [--api-key KEY] [--url URL]
 *
 * Batch mode annotates each image, saves it to <dest>, and writes results.csv.
 */

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

const string DefaultUrl = "https://anprengine.hybridvision.ai";

var supportedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".jpg", ".jpeg", ".png", ".bmp", ".webp",
};

var repoEnv = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));
LoadDotEnv(repoEnv);
LoadDotEnv(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".env")));

string? imagePath = null;
string? sourceFolder = null;
string? destFolder = null;
string url = Environment.GetEnvironmentVariable("HVA_API_URL") ?? DefaultUrl;
string apiKey = Environment.GetEnvironmentVariable("HVA_API_KEY") ?? "";

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
for (var i = 0; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--url" when i + 1 < argv.Length:
            url = argv[++i];
            break;
        case "--api-key" when i + 1 < argv.Length:
            apiKey = argv[++i];
            break;
        case "--source" when i + 1 < argv.Length:
            sourceFolder = argv[++i];
            break;
        case "--dest" when i + 1 < argv.Length:
            destFolder = argv[++i];
            break;
        case "-h":
        case "--help":
            Console.WriteLine("""
                Usage:
                  Single:  test_api <image-path> [--url URL] [--api-key KEY]
                  Batch:   test_api --source <folder> --dest <folder> [--url URL] [--api-key KEY]
                """);
            return 0;
        default:
            if (!argv[i].StartsWith('-'))
                imagePath = argv[i];
            break;
    }
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Error: provide --api-key or set HVA_API_KEY");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

// ── Batch mode ──────────────────────────────────────────────────────────────
if (sourceFolder is not null || destFolder is not null)
{
    if (sourceFolder is null || destFolder is null)
    {
        Console.Error.WriteLine("Error: --source and --dest must both be provided for batch mode");
        return 1;
    }

    sourceFolder = Path.GetFullPath(sourceFolder);
    destFolder = Path.GetFullPath(destFolder);

    if (!Directory.Exists(sourceFolder))
    {
        Console.Error.WriteLine($"Error: source folder not found: {sourceFolder}");
        return 1;
    }

    Directory.CreateDirectory(destFolder);

    var images = Directory.EnumerateFiles(sourceFolder)
        .Where(f => supportedExt.Contains(Path.GetExtension(f)))
        .OrderBy(f => f)
        .ToList();

    if (images.Count == 0)
    {
        Console.Error.WriteLine($"No supported images found in: {sourceFolder}");
        return 1;
    }

    Console.Error.WriteLine($"Processing {images.Count} image(s) from {sourceFolder}");
    Console.Error.WriteLine($"Output: {destFolder}");
    Console.Error.WriteLine();

    var csvPath = Path.Combine(destFolder, "results.csv");
    await using var csv = new StreamWriter(csvPath, append: false, System.Text.Encoding.UTF8);
    await csv.WriteLineAsync(
        "file,plate_count,plate_index,plate,detection_conf,plate_conf," +
        "country,plate_type,region_conf," +
        "bbox_x1,bbox_y1,bbox_x2,bbox_y2," +
        "vehicle_bbox_x1,vehicle_bbox_y1,vehicle_bbox_x2,vehicle_bbox_y2," +
        "vehicle_detect_ms,plate_detect_ms,ocr_ms,total_ms");

    var total = images.Count;
    var failed = 0;

    for (var idx = 0; idx < images.Count; idx++)
    {
        var imgPath = images[idx];
        var fname = Path.GetFileName(imgPath);
        Console.Error.Write($"  [{idx + 1}/{total}] {fname} ... ");

        string body;
        try
        {
            body = await CallApiRawAsync(http, url, apiKey, imgPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            failed++;
            continue;
        }

        AnprResponse? data;
        try
        {
            data = JsonSerializer.Deserialize<AnprResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            Console.Error.WriteLine("ERROR: invalid JSON response");
            failed++;
            continue;
        }

        if (data is null || !data.Success)
        {
            Console.Error.WriteLine($"FAILED: {data?.Error ?? "unknown error"}");
            failed++;
            continue;
        }

        Console.Error.WriteLine($"{data.PlateCount} plate(s)");

        // Annotate and save
        using var img = Cv2.ImRead(imgPath);
        if (!img.Empty())
        {
            using var annotated = Annotate(img, data.Plates ?? []);
            Cv2.ImWrite(Path.Combine(destFolder, fname), annotated);
        }

        // CSV rows — one row per plate, or one blank-plate row if none detected
        var plates = data.Plates ?? [];
        if (plates.Count == 0)
        {
            await csv.WriteLineAsync(CsvRow(fname, data.PlateCount, null, data.Timing));
        }
        else
        {
            foreach (var p in plates)
                await csv.WriteLineAsync(CsvRow(fname, data.PlateCount, p, data.Timing));
        }
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"Done. Success: {total - failed}  Failed: {failed}");
    Console.Error.WriteLine($"CSV  → {csvPath}");
    Console.Error.WriteLine($"Images → {destFolder}");
    return failed > 0 ? 1 : 0;
}

// ── Single-image mode ────────────────────────────────────────────────────────
if (imagePath is null)
{
    Console.Error.WriteLine("Error: provide an image path or use --source/--dest for batch mode");
    return 1;
}

imagePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(imagePath));
if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"Error: file not found: {imagePath}");
    return 1;
}

{
    string body;
    try
    {
        body = await CallApiRawAsync(http, url, apiKey, imagePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    using var doc = JsonDocument.Parse(body);
    Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static async Task<string> CallApiRawAsync(HttpClient http, string baseUrl, string apiKey, string imagePath)
{
    var apiUrl = $"{baseUrl.TrimEnd('/')}/api/v1/anpr";
    await using var stream = File.OpenRead(imagePath);
    using var content = new MultipartFormDataContent();
    var fileContent = new StreamContent(stream);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    content.Add(fileContent, "image", Path.GetFileName(imagePath));

    using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
    request.Headers.Add("X-API-Key", apiKey);

    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detailEl))
                detail = detailEl.ValueKind == JsonValueKind.String
                    ? detailEl.GetString() ?? detail
                    : detailEl.GetRawText();
        }
        catch (JsonException) { }
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {detail}");
    }

    return body;
}

static string CsvRow(string file, int plateCount, PlateResult? p, TimingInfo? timing)
{
    static string Esc(string? s) => s is null ? "" : $"\"{s.Replace("\"", "\"\"")}\"";
    static string I(int? v) => v?.ToString() ?? "";
    static string F2(double? v) => v?.ToString("F2") ?? "";
    static string F1(double? v) => v?.ToString("F1") ?? "";

    var bbox = p?.Bbox is { Length: 4 } b ? b : null;
    var vbbox = p?.VehicleBbox is { Length: 4 } vb ? vb : null;

    return string.Join(",",
        Esc(file), plateCount,
        p?.Index.ToString() ?? "",
        Esc(p?.Plate),
        F2(p?.DetectionConf), F2(p?.PlateConf),
        Esc(p?.Country), Esc(p?.PlateType), F2(p?.RegionConf),
        bbox is null ? "" : I(bbox[0]), bbox is null ? "" : I(bbox[1]),
        bbox is null ? "" : I(bbox[2]), bbox is null ? "" : I(bbox[3]),
        vbbox is null ? "" : I(vbbox[0]), vbbox is null ? "" : I(vbbox[1]),
        vbbox is null ? "" : I(vbbox[2]), vbbox is null ? "" : I(vbbox[3]),
        F1(timing?.VehicleDetectMs), F1(timing?.PlateDetectMs),
        F1(timing?.OcrMs), F1(timing?.TotalMs)
    );
}

static Mat Annotate(Mat img, IReadOnlyList<PlateResult> plates)
{
    var outImg = img.Clone();
    var fontScale = Math.Clamp(outImg.Width / 1000.0, 0.4, 1.25);
    var thick = fontScale < 0.75 ? 1 : 2;
    var vehicleColor = new Scalar(0, 100, 255);
    var plateColor = new Scalar(12, 255, 36);
    var seenVehicles = new HashSet<string>();

    foreach (var p in plates)
    {
        if (p.VehicleBbox is { Length: 4 } vb)
        {
            var key = string.Join(",", vb);
            if (seenVehicles.Add(key))
                Cv2.Rectangle(outImg, new Point(vb[0], vb[1]), new Point(vb[2], vb[3]), vehicleColor, 2);
        }

        if (p.Bbox is not { Length: 4 } bbox)
            continue;

        Cv2.Rectangle(outImg, new Point(bbox[0], bbox[1]), new Point(bbox[2], bbox[3]), plateColor, 2);
        if (string.IsNullOrEmpty(p.Plate))
            continue;

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(p.Country))
        {
            var region = string.IsNullOrEmpty(p.PlateType) ? p.Country : $"{p.Country}-{p.PlateType}";
            lines.Add($"{region}  {p.RegionConf:F0}%");
        }

        lines.Add($"{p.Plate}  {p.PlateConf:F0}%");

        var (_, th) = Cv2.GetTextSize(lines[0], HersheyFonts.HersheySimplex, fontScale, thick, out _);
        var gap = Math.Max(14, (int)Math.Round(th * 0.6));
        var lh = th + gap;
        var ty = bbox[1] - 10 - ((lines.Count - 1) * lh);
        if (ty - th < 0)
            ty = bbox[3] + th + 10;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var (tw, _) = Cv2.GetTextSize(line, HersheyFonts.HersheySimplex, fontScale, thick, out _);
            var tx = Math.Min(Math.Max(bbox[0], 5), Math.Max(5, outImg.Width - tw - 5));
            var cy = Math.Min(Math.Max(ty + (i * lh), th + 5), outImg.Height - 5);
            PutOutlined(outImg, line, new Point(tx, cy), fontScale, thick);
        }
    }

    return outImg;
}

static void PutOutlined(Mat img, string text, Point origin, double fontScale, int thick)
{
    var outlineThick = thick + Math.Max(3, (int)Math.Round(fontScale * 3));
    Cv2.PutText(img, text, origin, HersheyFonts.HersheySimplex, fontScale, Scalar.Black, outlineThick, LineTypes.AntiAlias);
    Cv2.PutText(img, text, origin, HersheyFonts.HersheySimplex, fontScale, Scalar.White, thick, LineTypes.AntiAlias);
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path))
        return;

    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            continue;

        var eq = line.IndexOf('=');
        if (eq <= 0)
            continue;

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}

// ── Models ───────────────────────────────────────────────────────────────────

sealed class AnprResponse
{
    [JsonPropertyName("success")]    public bool Success { get; set; }
    [JsonPropertyName("plate_count")] public int PlateCount { get; set; }
    [JsonPropertyName("plates")]     public List<PlateResult>? Plates { get; set; }
    [JsonPropertyName("timing")]     public TimingInfo? Timing { get; set; }
    [JsonPropertyName("error")]      public string? Error { get; set; }
}

sealed class PlateResult
{
    [JsonPropertyName("index")]          public int Index { get; set; }
    [JsonPropertyName("plate")]          public string? Plate { get; set; }
    [JsonPropertyName("plate_conf")]     public double PlateConf { get; set; }
    [JsonPropertyName("detection_conf")] public double DetectionConf { get; set; }
    [JsonPropertyName("country")]        public string? Country { get; set; }
    [JsonPropertyName("plate_type")]     public string? PlateType { get; set; }
    [JsonPropertyName("region_conf")]    public double RegionConf { get; set; }
    [JsonPropertyName("bbox")]           public int[]? Bbox { get; set; }
    [JsonPropertyName("vehicle_bbox")]   public int[]? VehicleBbox { get; set; }
}

sealed class TimingInfo
{
    [JsonPropertyName("vehicle_detect_ms")] public double VehicleDetectMs { get; set; }
    [JsonPropertyName("plate_detect_ms")]   public double PlateDetectMs { get; set; }
    [JsonPropertyName("ocr_ms")]            public double OcrMs { get; set; }
    [JsonPropertyName("total_ms")]          public double TotalMs { get; set; }
}
