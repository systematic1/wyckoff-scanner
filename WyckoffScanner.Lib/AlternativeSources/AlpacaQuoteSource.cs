using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WyckoffScanner.Lib.Models;
using WyckoffScanner.Lib.YahooFinance;

namespace WyckoffScanner.Lib.AlternativeSources;

public sealed class AlpacaOptions
{
    public const string Section = "Alpaca";
    public string KeyId { get; set; } = "________";
    public string SecretKey { get; set; } = "________";
    public string BaseUrl { get; set; } = "https://data.alpaca.markets";

    /// <summary>"iex" works on the free plan; "sip" requires a paid plan for recent data.</summary>
    public string Feed { get; set; } = "iex";

    /// <summary>Adjustment applied to Open/High/Low/Close: raw | split | dividend | all.</summary>
    public string OhlcAdjustment { get; set; } = "split";

    /// <summary>When true, a second request (adjustment=all) populates AdjClosePrice.</summary>
    public bool IncludeAdjustedClose { get; set; } = true;

    public int PageLimit { get; set; } = 10000;
    public double DelayBetweenCallsSeconds { get; set; } = 5;
}

public sealed class AlpacaQuoteSource : IQuoteSource
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    private readonly HttpClient _http;
    private readonly AlpacaOptions _options;

    public AlpacaQuoteSource(HttpClient http, AlpacaOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.KeyId) || string.IsNullOrWhiteSpace(options.SecretKey))
            throw new ArgumentException("ALPACA: Alpaca KeyId and SecretKey are required.", nameof(options));
    }

    public double GetDelaySeconds() => _options.DelayBetweenCallsSeconds;

    public bool UseThrottle() => false;
    
    public async Task<IReadOnlyList<DailyQuote>> GetDailyQuotesAsync(
        string ticker, DateOnly fromDate, DateOnly toDateInclusive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticker)) 
            throw new ArgumentException("ALPACA: Ticker is required.", nameof(ticker));
        if (toDateInclusive < fromDate) 
            throw new ArgumentException("ALPACA: toDateInclusive must be >= fromDate.");

        ticker = ticker.Trim().ToUpperInvariant();

        var bars = await FetchBarsAsync(ticker, fromDate, toDateInclusive, _options.OhlcAdjustment, ct);

        Dictionary<DateOnly, decimal>? adjCloses = null;
        if (_options.IncludeAdjustedClose)
        {
            var adjBars = string.Equals(_options.OhlcAdjustment, "all", StringComparison.OrdinalIgnoreCase)
                ? bars
                : await FetchBarsAsync(ticker, fromDate, toDateInclusive, "all", ct);

            adjCloses = new Dictionary<DateOnly, decimal>();
            foreach (var b in adjBars) adjCloses[b.Date] = b.Close;
        }

        return bars
            .OrderBy(b => b.Date)
            .Select(b => new DailyQuote
            {
                TradeDate = b.Date,
                OpenPrice = b.Open,
                HighPrice = b.High,
                LowPrice = b.Low,
                ClosePrice = b.Close,
                AdjClosePrice = adjCloses is not null && adjCloses.TryGetValue(b.Date, out var adj) ? adj : null,
                Volume = b.Volume
            })
            .ToList();
    }

    private async Task<List<Bar>> FetchBarsAsync(
        string ticker, DateOnly from, DateOnly toInclusive, string adjustment, CancellationToken ct)
    {
        var result = new Dictionary<DateOnly, Bar>();
        string? pageToken = null;

        do
        {
            // end is exclusive-ish for date-only values, so ask for the next day and filter locally.
            var url = $"{_options.BaseUrl.TrimEnd('/')}/v2/stocks/{Uri.EscapeDataString(ticker)}/bars" +
                      $"?timeframe=1Day" +
                      $"&start={from:yyyy-MM-dd}" +
                      $"&end={toInclusive.AddDays(1):yyyy-MM-dd}" +
                      $"&adjustment={Uri.EscapeDataString(adjustment.ToLowerInvariant())}" +
                      $"&feed={Uri.EscapeDataString(_options.Feed)}" +
                      $"&limit={_options.PageLimit}" +
                      (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("APCA-API-KEY-ID", _options.KeyId);
            req.Headers.Add("APCA-API-SECRET-KEY", _options.SecretKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"HTTP: Alpaca request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}) for {ticker}: {body}",
                    null, resp.StatusCode);
            }

            var page = await resp.Content.ReadFromJsonAsync<BarsResponse>(JsonOpts, ct);

            if (page?.Bars is not null)
            {
                foreach (var raw in page.Bars)
                {
                    var date = ToTradeDate(raw.Timestamp);
                    if (date < from || date > toInclusive) continue;

                    result[date] = new Bar(date, raw.Open, raw.High, raw.Low, raw.Close, raw.Volume);
                }
            }

            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result.Values.ToList();
    }

    // Daily bars are stamped at midnight US Eastern (04:00Z or 05:00Z); convert to get the trading date.
    private static DateOnly ToTradeDate(DateTimeOffset ts) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ts, Eastern).DateTime);

    private static TimeZoneInfo ResolveEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            } 
            catch (TimeZoneNotFoundException) 
            { }
        }
        return TimeZoneInfo.Utc;
    }

    private sealed record Bar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

    private sealed class BarsResponse
    {
        [JsonPropertyName("bars")] public List<RawBar>? Bars { get; set; }
        [JsonPropertyName("next_page_token")] public string? NextPageToken { get; set; }
    }

    private sealed class RawBar
    {
        [JsonPropertyName("t")] public DateTimeOffset Timestamp { get; set; }
        [JsonPropertyName("o")] public decimal Open { get; set; }
        [JsonPropertyName("h")] public decimal High { get; set; }
        [JsonPropertyName("l")] public decimal Low { get; set; }
        [JsonPropertyName("c")] public decimal Close { get; set; }
        [JsonPropertyName("v")] public long Volume { get; set; }
    }
}
