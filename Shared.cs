using System.Text.Json.Serialization;

static class Helpers
{
    public static void LoadDotEnv(string path)
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
}

sealed class AnprResponse
{
    [JsonPropertyName("success")]     public bool Success { get; set; }
    [JsonPropertyName("plate_count")] public int PlateCount { get; set; }
    [JsonPropertyName("plates")]      public List<PlateResult>? Plates { get; set; }
    [JsonPropertyName("timing")]      public TimingInfo? Timing { get; set; }
    [JsonPropertyName("error")]       public string? Error { get; set; }
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
