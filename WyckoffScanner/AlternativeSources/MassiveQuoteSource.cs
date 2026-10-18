// =====================================================================================================
// MassiveQuoteSource.cs  -  optional add-on for WyckoffScanner
//
// Alternative daily-OHLCV source: Massive.com (formerly Polygon.io), "Custom Bars (OHLC)" endpoint
//   GET https://api.massive.com/v2/aggs/ticker/{ticker}/range/1/day/{from}/{to}?adjusted=true&sort=asc&limit=50000
//
// HOW TO ADD IT (nothing else in the project needs to change)
//   1. Drop this file into the project (e.g. WyckoffScanner/MassiveApi/MassiveQuoteSource.cs).
//   2. In Program.cs, add ONE line AFTER the existing "services.AddSingleton<IQuoteSource>(...)" block:
//          services.AddMassiveQuoteSource(cfg);
//   3. Add to appsettings.json (keep the key out of the file: env var  Massive__ApiKey=...  works too):
//          "Massive": { "Enabled": true, "ApiKey": "YOUR_KEY" }
//      Set "Enabled": false (or remove the section) to go back to Yahoo! Finance.
//
// Everything else is reused: DataLoaderService still calls IQuoteSource, RequestThrottle still spaces the calls
// (3 min + jitter, persisted), and an HTTP 429 raises the same RateLimitedException so the cooldown logic applies.
// Note the spacing setting is still named Yahoo:DelayBetweenCallsMinutes - it governs whichever source is active.
// Massive's plans have their own request limits, so you may safely lower it if your plan allows.
// =====================================================================================================

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WyckoffScanner.Models;
using WyckoffScanner.YahooFinance;   // IQuoteSource, RateLimitedException

namespace WyckoffScanner.AlternativeSources;

public sealed class MassiveOptions
{
    public const string Section = "Massive";

    /// <summary>When true, Massive replaces Yahoo! Finance as the IQuoteSource.</summary>
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.massive.com";

    /// <summary>true = split-adjusted bars (same convention as the Yahoo sources). false = raw prices.</summary>
    public bool Adjusted { get; set; } = true;

    /// <summary>Yahoo spells share classes "BRK-B"; Massive uses "BRK.B". Convert automatically.</summary>
    public bool ConvertDashToDot { get; set; } = true;

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Safety cap on next_url pages per request (one year of daily bars is a single page).</summary>
    public int MaxPages { get; set; } = 5;
}

public sealed class MassiveQuoteSource : IQuoteSource
{
    private static readonly TimeZoneInfo EasternTime = FindEastern();

    private readonly HttpClient _http;
    private readonly MassiveOptions _options;
    private readonly ILogger<MassiveQuoteSource> _log;

    public MassiveQuoteSource(HttpClient http, IOptions<MassiveOptions> options, ILogger<MassiveQuoteSource> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<DailyQuote>> GetDailyQuotesAsync(
        string ticker, DateOnly fromDate, DateOnly toDateInclusive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Massive:ApiKey is not configured.");

        string symbol = MapTicker(ticker);
        string fromStr = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toStr = toDateInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string? url = $"{_options.BaseUrl.TrimEnd('/')}/v2/aggs/ticker/{Uri.EscapeDataString(symbol)}" +
                      $"/range/1/day/{fromStr}/{toStr}?adjusted={(_options.Adjusted ? "true" : "false")}&sort=asc&limit=50000";

        var quotes = new Dictionary<DateOnly, DailyQuote>();
        int page = 0;

        while (url != null && ++page <= _options.MaxPages)
        {
            _log.LogInformation("GET {Url}", url);   // logged WITHOUT the API key

            using var response = await _http.GetAsync(WithApiKey(url), ct);

            if (RateLimitedException.IsRateLimited(response))
                throw RateLimitedException.FromResponse(response, ticker);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HttpRequestException(
                    $"Massive rejected the request for {ticker} (HTTP {(int)response.StatusCode}): " +
                    "invalid API key, or your plan does not include this data.");

            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Massive returned HTTP {(int)response.StatusCode} for {ticker}: {Truncate(body, 300)}");

            url = ParsePage(body, ticker, quotes);
        }

        return quotes.Values.OrderBy(q => q.TradeDate).ToList();
    }

    /// <summary>Reads one response page into <paramref name="into"/>; returns next_url (or null when finished).</summary>
    internal static string? ParsePage(string json, string ticker, Dictionary<DateOnly, DailyQuote> into)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
            string.Equals(status.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            string msg = root.TryGetProperty("error", out var e) ? e.ToString()
                       : root.TryGetProperty("message", out var m) ? m.ToString() : "unknown error";
            throw new InvalidOperationException($"Massive error for {ticker}: {msg}");
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                if (!Num(r, "o", out var o) || !Num(r, "h", out var h) || !Num(r, "l", out var l) ||
                    !Num(r, "c", out var c) || !Num(r, "t", out var t))
                    continue;

                Num(r, "v", out var v);
                if (h < l) continue;

                // Daily bars are stamped at the start of the trading day in US Eastern time.
                var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)t).UtcDateTime;
                var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, EasternTime));

                into[date] = new DailyQuote
                {
                    TradeDate = date,
                    OpenPrice = (decimal)o,
                    HighPrice = (decimal)h,
                    LowPrice = (decimal)l,
                    ClosePrice = (decimal)c,
                    AdjClosePrice = null,                 // Massive bars carry no dividend-adjusted close
                    Volume = (long)Math.Round(v)
                };
            }
        }

        return root.TryGetProperty("next_url", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;
    }

    private string MapTicker(string ticker)
    {
        string t = ticker.Trim().ToUpperInvariant();
        return _options.ConvertDashToDot ? t.Replace('-', '.') : t;
    }

    private string WithApiKey(string url) =>
        url + (url.Contains('?') ? "&" : "?") + "apiKey=" + Uri.EscapeDataString(_options.ApiKey);

    private static bool Num(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        value = el.GetDouble();
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static TimeZoneInfo FindEastern()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}

public static class MassiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Massive source. If "Massive:Enabled" is true it also becomes THE IQuoteSource
    /// (the last IQuoteSource registration wins), so call this after the Yahoo registration in Program.cs.
    /// </summary>
    public static IServiceCollection AddMassiveQuoteSource(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MassiveOptions>(config.GetSection(MassiveOptions.Section));

        services.AddHttpClient<MassiveQuoteSource>((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<MassiveOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });

        if (config.GetValue<bool>($"{MassiveOptions.Section}:Enabled"))
        {
            if (string.IsNullOrWhiteSpace(config[$"{MassiveOptions.Section}:ApiKey"]))
                throw new InvalidOperationException("Massive:Enabled is true but Massive:ApiKey is not configured.");

            services.AddSingleton<IQuoteSource>(sp => sp.GetRequiredService<MassiveQuoteSource>());
        }

        return services;
    }
}
