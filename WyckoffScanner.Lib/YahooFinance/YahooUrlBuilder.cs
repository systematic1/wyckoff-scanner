namespace WyckoffScanner.Lib.YahooFinance;

internal static class YahooUrlBuilder
{
    /// <summary>Unix epoch seconds for 00:00 UTC of the given date.</summary>
    public static long ToEpoch(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>
    /// period1 = start of <paramref name="fromDate"/>;
    /// period2 = start of the day AFTER <paramref name="toDateInclusive"/> (Yahoo treats period2 as exclusive,
    /// so this is what makes the "previous trading day" bar itself come back).
    /// </summary>
    public static (long Period1, long Period2) Periods(DateOnly fromDate, DateOnly toDateInclusive) =>
        (ToEpoch(fromDate), ToEpoch(toDateInclusive.AddDays(1)));

    /// <summary>https://finance.yahoo.com/quote/{SYMBOL}/history/?period1=..&amp;period2=..&amp;interval=1d&amp;filter=history&amp;frequency=1d</summary>
    public static string HistoryPageUrl(string baseUrl, string ticker, DateOnly fromDate, DateOnly toDateInclusive)
    {
        var (p1, p2) = Periods(fromDate, toDateInclusive);
        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(ticker.ToUpperInvariant())}" +
               $"/history/?period1={p1}&period2={p2}&interval=1d&filter=history&frequency=1d";
    }

    public static string ChartApiUrl(string baseUrl, string ticker, DateOnly fromDate, DateOnly toDateInclusive)
    {
        var (p1, p2) = Periods(fromDate, toDateInclusive);
        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(ticker.ToUpperInvariant())}" +
               $"?period1={p1}&period2={p2}&interval=1d&events=history&includeAdjustedClose=true";
    }
}
