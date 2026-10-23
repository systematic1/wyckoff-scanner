using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WyckoffScanner.Lib.Data;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Reads a symbol's last year of stored quotes, runs the analyzer, and stores the result immediately.
/// Symbols are consumed from a channel by <see cref="AnalyzerOptions.MaxConcurrency"/> parallel workers, so analysis
/// runs while the loader is still fetching the remaining symbols.
/// </summary>
public sealed class AnalysisService
{
    private readonly SymbolRepository _symbols;
    private readonly QuoteRepository _quotes;
    private readonly AnalysisRepository _results;
    private readonly WyckoffAnalyzer _analyzer;
    private readonly AnalyzerOptions _options;
    private readonly ILogger<AnalysisService> _log;

    public AnalysisService(
        SymbolRepository symbols, QuoteRepository quotes, AnalysisRepository results,
        WyckoffAnalyzer analyzer, IOptions<AnalyzerOptions> options, ILogger<AnalysisService> log) 
    {
        _symbols = symbols;
        _quotes = quotes;
        _results = results;
        _analyzer = analyzer;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Analyses every active symbol that has no result for <paramref name="runDate"/> yet. Used when the loader
    /// stage is already finished (e.g. after a restart) so only the analysis remains.
    /// </summary>
    public async Task RunAsync(DateOnly runDate, CancellationToken ct)
    {
        var symbols = await _symbols.GetActiveAsync(ct);
        var channel = Channel.CreateUnbounded<SymbolUpdate>();
        foreach (var sym in symbols)
            channel.Writer.TryWrite(new SymbolUpdate(sym, DataChanged: false));
        
        channel.Writer.Complete();

        await ProcessAsync(runDate, channel.Reader, ct);
    }

    /// <summary>
    /// Analyses symbols as they arrive on <paramref name="updates"/> until the channel is completed and drained.
    /// A symbol whose data did not change is skipped when it already has a result for the day (restart/resume).
    /// Analysis errors are logged per symbol and do not stop the others; if any result could not be *saved* the
    /// method throws after draining so the stage is not marked complete and is retried.
    /// </summary>
    public async Task ProcessAsync(DateOnly runDate, ChannelReader<SymbolUpdate> updates, CancellationToken ct)
    {
        DateOnly fromDate = runDate.AddYears(-_options.LookbackYears);
        var alreadyAnalyzed = new ConcurrentDictionary<int, byte>();
        foreach (int id in await _results.GetAnalyzedSymbolIdsAsync(runDate, ct))
            alreadyAnalyzed[id] = 0;

        int workers = Math.Max(1, _options.MaxConcurrency);
        var counts = new int[Enum.GetValues<WyckoffFlag>().Length];
        int analyzed = 0, 
            skipped = 0, 
            analysisErrors = 0, 
            saveErrors = 0;

        _log.LogInformation("Analyzer: streaming mode, {Workers} worker(s), window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}",
            workers, fromDate, runDate);

        async Task WorkerAsync()
        {
            await foreach (var update in updates.ReadAllAsync(ct))
            {
                var sym = update.Symbol;
                if (!update.DataChanged && alreadyAnalyzed.ContainsKey(sym.SymbolId))
                {
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                AnalysisResult result;
                try
                {
                    var quotes = await _quotes.GetRangeAsync(sym.SymbolId, fromDate, runDate, ct);
                    result = _analyzer.Analyze(sym.Ticker, quotes);
                    result.SymbolId = sym.SymbolId;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref analysisErrors);
                    _log.LogError(ex, "{Ticker}: analysis failed", sym.Ticker);
                    continue;
                }

                try
                {
                    await _results.SaveAsync(runDate, result, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref saveErrors);
                    _log.LogError(ex, "{Ticker}: could not save analysis result", sym.Ticker);
                    continue;
                }

                alreadyAnalyzed[sym.SymbolId] = 0;
                Interlocked.Increment(ref analyzed);
                Interlocked.Increment(ref counts[(int)result.Flag]);

                if (result.Flag != WyckoffFlag.None)
                    _log.LogInformation("{Ticker}: {Flag} ({Pattern})", sym.Ticker, result.FlagMessage, result.Pattern);
                else
                    _log.LogDebug("{Ticker}: analysed, no flag.", sym.Ticker);
            }
        }

        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++) 
            tasks[i] = Task.Run(WorkerAsync, ct);
        
        await Task.WhenAll(tasks);

        _log.LogInformation(
            "Analyzer finished: {Analyzed} analysed ({Skipped} already done, {Errors} analysis error(s)). " +
            "Accumulation - likely {L}, spring {Sp}, SoS {Sos}. Distribution - likely {DL}, upthrust {Ut}, SoW {Sow}.",
            analyzed, skipped, analysisErrors,
            counts[(int)WyckoffFlag.LikelyProgressing], counts[(int)WyckoffFlag.SpringDetected], counts[(int)WyckoffFlag.SosDetected],
            counts[(int)WyckoffFlag.LikelyDistribution], counts[(int)WyckoffFlag.UpthrustDetected], counts[(int)WyckoffFlag.SowDetected]);

        if (saveErrors > 0)
            throw new InvalidOperationException($"{saveErrors} analysis result(s) could not be saved; the analysis stage will be retried.");
    }
}
