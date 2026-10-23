using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Data;
using WyckoffScanner.Lib.Models;
using WyckoffScanner.Lib.Scheduling;
using WyckoffScanner.Lib.YahooFinance;

namespace WyckoffScanner.Lib.Loading;

/// <summary>
/// 1) Back-loads one year of daily OHLCV for every symbol that has not been back-loaded yet (happens once per symbol).
/// 2) Loads the previous trading day for every symbol.
/// One symbol per Yahoo call, with the configured delay (3 min) between calls.
/// Existing (symbol, date) rows are never inserted twice.
/// </summary>
public sealed class DataLoaderService
{
    private readonly IQuoteSource _source;
    private readonly RequestThrottle _throttle;
    private readonly SymbolRepository _symbols;
    private readonly QuoteRepository _quotes;
    private readonly LoadLogRepository _loadLog;
    private readonly TradingCalendar _calendar;
    private readonly YahooOptions _yahoo;
    private readonly ILogger<DataLoaderService> _log;

    public DataLoaderService(
        IQuoteSource source, RequestThrottle throttle, SymbolRepository symbols, QuoteRepository quotes,
        LoadLogRepository loadLog, TradingCalendar calendar, IOptions<YahooOptions> yahoo,
        ILogger<DataLoaderService> log)
    {
        _source = source;
        _throttle = throttle;
        _symbols = symbols;
        _quotes = quotes;
        _loadLog = loadLog;
        _calendar = calendar;
        _yahoo = yahoo.Value;
        _log = log;
    }

    /// <param name="onSymbolUpdated">
    /// Invoked once per symbol (per run) as soon as that symbol is up to date - or its load attempt has finished -
    /// so the analyzer can start on it while the remaining symbols are still being fetched. Symbols that need no
    /// Yahoo call are reported first, immediately. A failing callback is logged and never stops the loader.
    /// </param>
    public async Task RunAsync(DateOnly runDate, CancellationToken ct,
        Func<SymbolUpdate, CancellationToken, Task>? onSymbolUpdated = null)
    {
        var notified = new HashSet<int>();

        async Task Notify(SymbolRecord sym, bool dataChanged)
        {
            if (onSymbolUpdated == null || !notified.Add(sym.SymbolId)) 
                return;
            try
            {
                _log.LogInformation($"Loader: Notify ticker data changed => {sym.Ticker}: {dataChanged}");
                await onSymbolUpdated(new SymbolUpdate(sym, dataChanged), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "{Ticker}: post-load callback failed.", sym.Ticker);
            }
        }

        DateOnly target = _calendar.PreviousTradingDay(runDate);
        var symbols = await _symbols.GetActiveAsync(ct);
        _log.LogInformation(
            "Loader: {Count} active symbols, target (previous trading day) = {Target:yyyy-MM-dd}",
            symbols.Count, 
            target
            );

        // ---- Phase 0: symbols that are already complete need no Yahoo call - hand them over right away ----------
        if (onSymbolUpdated != null)
        {
            foreach (var sym in symbols.Where(s => s.BackfillCompletedAt != null))
            {
                ct.ThrowIfCancellationRequested();
                if (await _loadLog.HasCompletedLoadAsync(sym.SymbolId, target, ct))
                    await Notify(sym, dataChanged: false);
            }
        }

        // ---- Phase 1: one-time 1-year back-load ------------------------------------------------
        var pendingBackfill = symbols.Where(s => s.BackfillCompletedAt == null).ToList();
        _log.LogInformation("Loader: {Count} symbols need the 1-year back-load.", pendingBackfill.Count);

        foreach (var sym in pendingBackfill)
        {
            ct.ThrowIfCancellationRequested();
            bool ok = await BackfillAsync(sym, runDate, target, ct);
            if (ok) 
                await Notify(sym, dataChanged: true);
        }

        // ---- Phase 2: previous trading day for everyone ---------------------------------------
        foreach (var sym in symbols)
        {
            ct.ThrowIfCancellationRequested();
            if (await _loadLog.HasCompletedLoadAsync(sym.SymbolId, target, ct))
            {
                _log.LogDebug("{Ticker}: {Target:yyyy-MM-dd} already loaded, skipping.", sym.Ticker, target);
                await Notify(sym, dataChanged: false);   // no-op if already reported
                continue;
            }
            bool changed = await DailyLoadAsync(sym, target, ct);
            await Notify(sym, changed);   // also on failure/no data: analyse what is stored, as before
        }

        _log.LogInformation("Loader finished.");
    }

    /// <returns>True when the back-load succeeded.</returns>
    private async Task<bool> BackfillAsync(SymbolRecord sym, DateOnly runDate, DateOnly target, CancellationToken ct)
    {
        DateOnly fromDate = runDate.AddYears(-1);
        _log.LogInformation(" => {Ticker}: back-loading {From:yyyy-MM-dd} .. {To:yyyy-MM-dd}", sym.Ticker, fromDate, target);

        var (quotes, error) = await FetchWithRetryAsync(sym.Ticker, fromDate, target, ct);

        if (quotes == null || quotes.Count == 0)
        {
            string msg = error ?? "No rows returned for back-load.";
            _log.LogWarning(" => {Ticker}: back-load failed - {Message}", sym.Ticker, msg);
            await _loadLog.UpsertAsync(sym.SymbolId, target, "backfill", "failed", 0, msg, ct);
            return false; // not marked complete -> retried at the next run
        }

        int inserted = await _quotes.InsertNewAsync(sym.SymbolId, quotes, ct);
        await _symbols.MarkBackfillCompletedAsync(sym.SymbolId, ct);
        await _loadLog.UpsertAsync(sym.SymbolId, target, "backfill", "success", inserted, null, ct);
        _log.LogInformation(
            "<=  {Ticker}: back-load done, {Fetched} rows fetched, {Inserted} new.",
            sym.Ticker, 
            quotes.Count, 
            inserted);
        return true;
    }

    /// <returns>True when new rows were stored.</returns>
    private async Task<bool> DailyLoadAsync(SymbolRecord sym, DateOnly target, CancellationToken ct)
    {
        _log.LogInformation(" => {Ticker}: loading {Target:yyyy-MM-dd}", sym.Ticker, target);

        var (quotes, error) = await FetchWithRetryAsync(sym.Ticker, target, target, ct);

        if (quotes == null)
        {
            _log.LogWarning(" => {Ticker}: daily load failed - {Message}", sym.Ticker, error);
            await _loadLog.UpsertAsync(sym.SymbolId, target, "daily", "failed", 0, error, ct);
            return false;
        }

        if (quotes.Count == 0)
        {
            // Not a failure: e.g. an unannounced market closure. Recorded so we don't call Yahoo again today.
            await _loadLog.UpsertAsync(sym.SymbolId, target, "daily", "no_data", 0, "No rows returned.", ct);
            _log.LogInformation(" => {Ticker}: no data for {Target:yyyy-MM-dd}.", sym.Ticker, target);
            return false;
        }

        int inserted = await _quotes.InsertNewAsync(sym.SymbolId, quotes, ct);
        await _loadLog.UpsertAsync(sym.SymbolId, target, "daily", "success", inserted, null, ct);
        _log.LogInformation("<=  {Ticker}: {Inserted} new row(s) inserted.", sym.Ticker, inserted);
        return inserted > 0;
    }

    /// <summary>Each attempt is one Yahoo call and therefore waits for the 3-minute throttle first.</summary>
    public async Task<(IReadOnlyList<DailyQuote>? Quotes, string? Error)> FetchWithRetryAsync(
        string ticker, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        string? lastError = null;
        int attempts = Math.Max(1, _yahoo.MaxAttemptsPerSymbol);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (_source.UseThrottle())
                await _throttle.WaitAsync(ct);
            
            try
            {
                var quotes = await _source.GetDailyQuotesAsync(ticker, fromDate, toDate, ct);
                if (_source.UseThrottle())
                    _throttle.ReportSuccess();
                else 
                    DelayNextApiCall();
                
                return (quotes, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (RateLimitedException)
            {
                throw; // never retry a 429 immediately; the worker triggers the cooldown
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _log.LogWarning("** {Ticker}: attempt {Attempt}/{Max} failed - {Message}", ticker, attempt, attempts, ex.Message);
            }
        }

        return (null, lastError);
    }

    private void DelayNextApiCall()
    {
        int delayTime = (int)_source.GetDelaySeconds() * 1000;
        Thread.Sleep(delayTime);
    }

}
