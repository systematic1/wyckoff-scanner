using Dapper;
using WyckoffScanner.Models;

namespace WyckoffScanner.Data;

public sealed class AnalysisRepository
{
    private readonly DbConnectionFactory _db;
    public AnalysisRepository(DbConnectionFactory db) => _db = db;

    public Task SaveAsync(DateOnly analysisDate, AnalysisResult result, CancellationToken ct)
        => SaveAsync(analysisDate, new[] { result }, ct);

    /// <summary>Ids of the symbols that already have a result for the day (used to resume after a restart).</summary>
    public async Task<IReadOnlyList<int>> GetAnalyzedSymbolIdsAsync(DateOnly analysisDate, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var ids = await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT symbol_id FROM analysis_results WHERE analysis_date = @analysisDate",
            new { analysisDate }, cancellationToken: ct));
        return ids.ToList();
    }

    public async Task SaveAsync(DateOnly analysisDate, IReadOnlyCollection<AnalysisResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;

        const string sql = @"
INSERT INTO analysis_results
    (symbol_id, analysis_date, pattern, flag, flag_message, selling_exhaustion, automatic_rally, accumulation,
     accumulation_count, spring, sign_of_strength, support_price, resistance_price, last_close,
     bars_analyzed, details)
VALUES
    (@SymbolId, @AnalysisDate, @Pattern, @Flag, @FlagMessage, @SellingExhaustion, @AutomaticRally, @Accumulation,
     @AccumulationCount, @Spring, @SignOfStrength, @Support, @Resistance, @LastClose,
     @BarsAnalyzed, @Details)
ON CONFLICT (symbol_id, analysis_date) DO UPDATE SET
    pattern = EXCLUDED.pattern, flag = EXCLUDED.flag, flag_message = EXCLUDED.flag_message,
    selling_exhaustion = EXCLUDED.selling_exhaustion, automatic_rally = EXCLUDED.automatic_rally,
    accumulation = EXCLUDED.accumulation, accumulation_count = EXCLUDED.accumulation_count,
    spring = EXCLUDED.spring, sign_of_strength = EXCLUDED.sign_of_strength,
    support_price = EXCLUDED.support_price, resistance_price = EXCLUDED.resistance_price,
    last_close = EXCLUDED.last_close, bars_analyzed = EXCLUDED.bars_analyzed,
    details = EXCLUDED.details, created_at = now();";

        var parameters = results.Select(r => new
        {
            r.SymbolId,
            AnalysisDate = analysisDate,
            Pattern = r.Pattern.ToString(),
            Flag = r.Flag.ToString(),
            FlagMessage = r.Flag == WyckoffFlag.None ? null : r.FlagMessage,
            r.SellingExhaustion,
            r.AutomaticRally,
            r.Accumulation,
            r.AccumulationCount,
            r.Spring,
            r.SignOfStrength,
            Support = r.Support.HasValue ? (decimal?)Math.Round((decimal)r.Support.Value, 4) : null,
            Resistance = r.Resistance.HasValue ? (decimal?)Math.Round((decimal)r.Resistance.Value, 4) : null,
            LastClose = (decimal?)Math.Round((decimal)r.LastClose, 4),
            r.BarsAnalyzed,
            r.Details
        }).ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, parameters, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FlaggedSymbol>> GetFlaggedAsync(DateOnly analysisDate, CancellationToken ct)
    {
        const string sql = @"
SELECT s.ticker, a.pattern, a.flag, a.flag_message, a.support_price, a.resistance_price, a.last_close, a.details
FROM   analysis_results a
JOIN   symbols s ON s.symbol_id = a.symbol_id
WHERE  a.analysis_date = @analysisDate AND a.flag <> 'None'
ORDER  BY CASE a.flag WHEN 'SosDetected' THEN 1 WHEN 'SowDetected' THEN 1
                       WHEN 'SpringDetected' THEN 2 WHEN 'UpthrustDetected' THEN 2 ELSE 3 END,
          a.pattern, s.ticker;";

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<FlaggedSymbol>(new CommandDefinition(
            sql, new { analysisDate }, cancellationToken: ct));
        return rows.ToList();
    }
}
