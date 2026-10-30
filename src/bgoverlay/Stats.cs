using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgOverlay;

public sealed class HeroStatsFile
{
    public List<HeroStat> HeroStats { get; set; } = new();
    public DateTime? LastUpdateDate { get; set; }
    public long DataPoints { get; set; }
    public List<MmrPercentile> MmrPercentiles { get; set; } = new();
}

public sealed class HeroStat
{
    public string HeroCardId { get; set; } = "";
    public int DataPoints { get; set; }
    public int TotalOffered { get; set; }
    public int TotalPicked { get; set; }
    public double AveragePosition { get; set; }
    public List<Placement> PlacementDistribution { get; set; } = new();
    public List<TribeStat> TribeStats { get; set; } = new();
    public List<TurnWinrate> CombatWinrate { get; set; } = new();
    public List<TurnStats> WarbandStats { get; set; } = new();
}

public sealed class TurnWinrate
{
    public int Turn { get; set; }
    public double Winrate { get; set; }
}

public sealed class TurnStats
{
    public int Turn { get; set; }
    public double AverageStats { get; set; }
}

public sealed class Placement
{
    public int Rank { get; set; }
    public double Percentage { get; set; }
}

public sealed class TribeStat
{
    public int Tribe { get; set; }
    public int DataPoints { get; set; }
    public int DataPointsOnMissingTribe { get; set; }
    public double ImpactAveragePosition { get; set; }
}

public sealed class MmrPercentile
{
    public int Percentile { get; set; }
    public int Mmr { get; set; }
}

public sealed class TrinketStatsFile
{
    public List<TrinketStat> TrinketStats { get; set; } = new();
    public DateTime? LastUpdateDate { get; set; }
}

public sealed class TrinketStat
{
    public string TrinketCardId { get; set; } = "";
    public int DataPoints { get; set; }
    public double? PickRate { get; set; }
    public double? AveragePlacement { get; set; }
    public List<MmrPlacement> AveragePlacementAtMmr { get; set; } = new();
    public List<MmrPickRate> PickRateAtMmr { get; set; } = new();
}

public sealed class MmrPlacement
{
    public int Mmr { get; set; }
    public int DataPoints { get; set; }
    public double? Placement { get; set; }
}

public sealed class MmrPickRate
{
    public int Mmr { get; set; }
    public double? PickRate { get; set; }
}

/// <summary>
/// Firestone's public Battlegrounds stats (static.zerotoheroes.com), rebuilt
/// hourly from their users' games. Kept on disk so the overlay works offline
/// and starts instantly.
/// </summary>
internal sealed class StatsStore
{
    private const string Base = "https://static.zerotoheroes.com/api/bgs/";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
        Converters = { new NullAsNaN() },
    };

    /// <summary>The stats serializer writes NaN (an empty bucket) as null.</summary>
    private sealed class NullAsNaN : JsonConverter<double>
    {
        public override bool HandleNull => true;

        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Null   => double.NaN,
                JsonTokenType.String => double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN,
                _                    => reader.GetDouble(),
            };

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.Zero,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    public HeroStatsFile?    Solo     { get; private set; }
    public HeroStatsFile?    Duo      { get; private set; }
    public TrinketStatsFile? Trinkets { get; private set; }
    public int      Percentile { get; private set; }
    public DateTime? FetchedAt { get; private set; }
    public string?  Error      { get; private set; }
    public bool     Loading    { get; private set; }
    /// <summary>Bumped whenever any of the data above is replaced.</summary>
    public int      Version    { get; private set; }

    private static string CacheDir => Path.Combine(Settings.Dir, "cache");

    public async Task LoadAsync(int percentile, bool force)
    {
        if (Loading) return;
        Loading = true;
        Error = null;
        try
        {
            var solo = await Get<HeroStatsFile>($"hero-stats/mmr-{percentile}/last-patch/overview-from-hourly.gz.json",
                $"heroes-solo-{percentile}.json", force);
            var duo = await Get<HeroStatsFile>($"duo/hero-stats/mmr-{percentile}/last-patch/overview-from-hourly.gz.json",
                $"heroes-duo-{percentile}.json", force);
            var trinkets = await Get<TrinketStatsFile>("trinket-stats/last-patch/overview-from-hourly.gz.json",
                "trinkets.json", force);

            Solo = solo.Data;
            Duo = duo.Data;
            Trinkets = trinkets.Data;
            Percentile = percentile;
            FetchedAt = new[] { solo.At, duo.At, trinkets.At }.Min();
            Version++;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    private static async Task<(T Data, DateTime At)> Get<T>(string path, string cacheName, bool force) where T : class
    {
        string file = Path.Combine(CacheDir, cacheName);
        var cached = File.Exists(file) ? new FileInfo(file) : null;
        if (!force && cached is not null && DateTime.Now - cached.LastWriteTime < MaxAge)
        {
            var data = TryParse<T>(await File.ReadAllBytesAsync(file));
            if (data is not null) return (data, cached.LastWriteTime);
        }

        try
        {
            byte[] json = await Download(Base + path);
            T data;
            try { data = JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("пустой ответ"); }
            catch (JsonException ex) { throw new InvalidDataException($"непонятный ответ сервера ({path}): {ex.Message}"); }
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(file, json);
            return (data, DateTime.Now);
        }
        catch when (cached is not null)
        {
            var data = TryParse<T>(await File.ReadAllBytesAsync(file));
            if (data is not null) return (data, cached.LastWriteTime);
            throw;
        }
    }

    private static T? TryParse<T>(byte[] json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Downloads in ranges, each on a fresh connection, skipping byte 0.
    /// On some networks a response from this CDN stalls after ~25 KB, and one
    /// starting at offset 0 comes back rewritten by something in between;
    /// ranges from offset 1 on arrive intact. The first byte of a gzip file is
    /// always 0x1f, and the result is verified by decompressing it.
    /// </summary>
    private static async Task<byte[]> Download(string url)
    {
        var (probe, total) = await Range(url, 1, 1);
        bool gzip = probe[0] == 0x8b;
        var body = new byte[total];
        body[0] = gzip ? (byte)0x1f : (byte)'{';

        const int Chunk = 16 * 1024;
        var ranges = new List<(long From, long To)>();
        for (long off = 1; off < total; off += Chunk) ranges.Add((off, Math.Min(off + Chunk, total) - 1));

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(ranges.Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                var (part, _) = await Retry(() => Range(url, r.From, r.To));
                Buffer.BlockCopy(part, 0, body, (int)r.From, part.Length);
            }
            finally { gate.Release(); }
        }));

        if (!gzip) return body;
        using var gz = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        await gz.CopyToAsync(outMs);
        return outMs.ToArray();
    }

    private static async Task<(byte[] Data, long Total)> Range(string url, long from, long to)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(from, to);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        req.Headers.ConnectionClose = true;
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"сервер ответил {(int)resp.StatusCode} вместо части файла");
        long total = resp.Content.Headers.ContentRange?.Length
            ?? throw new HttpRequestException("сервер не сообщил размер файла");
        byte[] data = await resp.Content.ReadAsByteArrayAsync();
        if (data.Length != to - from + 1)
            throw new HttpRequestException($"пришло {data.Length} байт из {to - from + 1}");
        return (data, total);
    }

    private static async Task<T> Retry<T>(Func<Task<T>> f)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await f(); }
            catch when (attempt < 4) { await Task.Delay(300 * attempt); }
        }
    }
}
