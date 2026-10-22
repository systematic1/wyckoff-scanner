using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.YahooFinance;

/// <summary>
/// Alternative source: Yahoo's JSON chart endpoint (v8/finance/chart). More stable than scraping HTML.
/// Enable with  "Yahoo": { "Source": "ChartApi" }.
/// </summary>
public sealed class YahooChartQuoteSource : IQuoteSource
{
    private readonly HttpClient _http;
    private readonly YahooOptions _options;
    private readonly ILogger<YahooChartQuoteSource> _log;

    public YahooChartQuoteSource(HttpClient http, IOptions<YahooOptions> options, ILogger<YahooChartQuoteSource> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public double GetDelaySeconds() => _options.DelayBetweenCallsSeconds;

    public bool UseThrottle() => true;
    
    public async Task<IReadOnlyList<DailyQuote>> GetDailyQuotesAsync(
        string ticker, DateOnly fromDate, DateOnly toDateInclusive, CancellationToken ct)
    {
        string url = YahooUrlBuilder.ChartApiUrl(_options.ChartApiBaseUrl, ticker, fromDate, toDateInclusive);
        _log.LogInformation("GET {Url}", url);

        using var response = await _http.GetAsync(url, ct);
        if (RateLimitedException.IsRateLimited(response))
            throw RateLimitedException.FromResponse(response, ticker);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP: Yahoo! chart API returned HTTP {(int)response.StatusCode} for {ticker}.");

        string json = await response.Content.ReadAsStringAsync(ct);
        return Parse(json, ticker);
    }

    internal static IReadOnlyList<DailyQuote> Parse(string json, string ticker)
    {
        using var doc = JsonDocument.Parse(json);
        var chart = doc.RootElement.GetProperty("chart");

        if (!chart.TryGetProperty("result", out var resultArr) ||
            resultArr.ValueKind != JsonValueKind.Array || resultArr.GetArrayLength() == 0)
        {
            string err = (chart.TryGetProperty("error", out var e) ? e.ToString() : "unknown");
            throw new InvalidOperationException($"Yahoo chart API returned no result for {ticker}: {err}");
        }

        var result = resultArr[0];
        if (!result.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Array)
            return Array.Empty<DailyQuote>(); // no trading days in range

        int gmtOffset = (result.GetProperty("meta").TryGetProperty("gmtoffset", out var go) ? go.GetInt32() : 0);
        var q = result.GetProperty("indicators").GetProperty("quote")[0];

        JsonElement? adjArr = null;
        if (result.GetProperty("indicators").TryGetProperty("adjclose", out var adjc) &&
            adjc.ValueKind == JsonValueKind.Array && adjc.GetArrayLength() > 0 &&
            adjc[0].TryGetProperty("adjclose", out var ac))
        {
            adjArr = ac;
        }

        var list = new List<DailyQuote>();
        int n = ts.GetArrayLength();
        for (int i = 0; i < n; i++)
        {
            if (!TryGet(q, "open", i, out var o) || !TryGet(q, "high", i, out var h) ||
                !TryGet(q, "low", i, out var l) || !TryGet(q, "close", i, out var c))
            {
                continue; // null bar (e.g. halted day)
            }

            TryGet(q, "volume", i, out var v);
            decimal? adj = null;
            if (adjArr.HasValue && adjArr.Value.GetArrayLength() > i &&
                adjArr.Value[i].ValueKind == JsonValueKind.Number)
            {
                adj = (decimal)adjArr.Value[i].GetDouble();
            }

            var local = DateTimeOffset.FromUnixTimeSeconds(ts[i].GetInt64() + gmtOffset).UtcDateTime;
            list.Add(new DailyQuote
            {
                TradeDate = DateOnly.FromDateTime(local),
                OpenPrice = (decimal)o,
                HighPrice = (decimal)h,
                LowPrice = (decimal)l,
                ClosePrice = (decimal)c,
                AdjClosePrice = adj,
                Volume = (long)v
            });
        }

        return list.GroupBy(x => x.TradeDate).Select(g => g.Last()).OrderBy(x => x.TradeDate).ToList();
    }

    private static bool TryGet(JsonElement quote, string name, int index, out double value)
    {
        value = 0;
        if (!quote.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() <= index)
        {
            return false;
        }

        var el = arr[index];
        if (el.ValueKind != JsonValueKind.Number) 
            return false;
        
        value = el.GetDouble();
        return true;
    }
}
