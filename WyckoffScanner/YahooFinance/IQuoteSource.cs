using WyckoffScanner.Models;

namespace WyckoffScanner.YahooFinance;

public interface IQuoteSource
{
    /// <summary>
    /// Downloads daily OHLCV bars for one symbol. Both dates are inclusive.
    /// One call = one HTTP request to Yahoo! Finance.
    /// </summary>
    Task<IReadOnlyList<DailyQuote>> GetDailyQuotesAsync(
        string ticker, DateOnly fromDate, DateOnly toDateInclusive, CancellationToken ct);

    double GetDelaySeconds();

    bool UseThrottle();
}
