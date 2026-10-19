using System.Globalization;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using WyckoffScanner.Configuration;
using WyckoffScanner.Models;

namespace WyckoffScanner.YahooFinance;

/// <summary>
/// Scrapes the HTML table on https://finance.yahoo.com/quote/{SYMBOL}/history/ .
/// Expected columns: Date | Open | High | Low | Close | Adj Close | Volume.
/// </summary>
public sealed class YahooHtmlQuoteSource : IQuoteSource
{
    private static readonly string[] DateFormats = { "MMM d, yyyy", "MMM dd, yyyy", "MMMM d, yyyy" };

    private readonly HttpClient _http;
    private readonly YahooOptions _options;
    private readonly ILogger<YahooHtmlQuoteSource> _log;

    public YahooHtmlQuoteSource(HttpClient http, IOptions<YahooOptions> options, ILogger<YahooHtmlQuoteSource> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<DailyQuote>> GetDailyQuotesAsync(
        string ticker, DateOnly fromDate, DateOnly toDateInclusive, CancellationToken ct)
    {
        string url = YahooUrlBuilder.HistoryPageUrl(_options.QuoteBaseUrl, ticker, fromDate, toDateInclusive);
        _log.LogInformation("GET {Url}", url);

        using var response = await _http.GetAsync(url, ct);
        if (RateLimitedException.IsRateLimited(response))
            throw RateLimitedException.FromResponse(response, ticker);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Yahoo! Finance returned HTTP {(int)response.StatusCode} for {ticker}.");

        string html = await response.Content.ReadAsStringAsync(ct);
        return ParseHistoryTable(html, ticker);
    }

    internal IReadOnlyList<DailyQuote> ParseHistoryTable(string html, string ticker)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null)
        {
            throw new InvalidOperationException(
                $"No <table> found in the Yahoo! page for {ticker} (layout changed, consent page, or request blocked).");
        }

        var quotes = new Dictionary<DateOnly, DailyQuote>();

        IEnumerable<HtmlNode> rows = doc.DocumentNode.SelectNodes("//table//tr")?.AsEnumerable()
                                     ?? Enumerable.Empty<HtmlNode>();

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            // Dividend / split rows have 2 cells (colspan) and header rows use <th>; real price rows have 7.
            if (cells == null || cells.Count != 7) 
                continue;

            string[] t = cells.Select(c => HtmlEntity.DeEntitize(c.InnerText).Trim()).ToArray();

            if (!DateTime.TryParseExact(t[0], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                continue;

            if (!TryDec(t[1], out var open) || !TryDec(t[2], out var high) ||
                !TryDec(t[3], out var low) || !TryDec(t[4], out var close))
            {
                continue;
            }

            decimal? adj = TryDec(t[5], out var a) ? a : null;
            if (!TryLong(t[6], out var volume)) 
                volume = 0;

            if (high < low) 
                continue; // corrupt row

            var date = DateOnly.FromDateTime(dt);
            quotes[date] = new DailyQuote
            {
                TradeDate = date,
                OpenPrice = open,
                HighPrice = high,
                LowPrice = low,
                ClosePrice = close,
                AdjClosePrice = adj,
                Volume = volume
            };
        }

        return quotes.Values.OrderBy(q => q.TradeDate).ToList();
    }

    private static bool TryDec(string s, out decimal v) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out v);

    private static bool TryLong(string s, out long v) =>
        long.TryParse(s.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
}
