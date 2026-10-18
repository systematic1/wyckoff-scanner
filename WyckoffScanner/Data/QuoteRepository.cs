using Dapper;
using WyckoffScanner.Models;

namespace WyckoffScanner.Data;

public sealed class QuoteRepository
{
    private readonly DbConnectionFactory _db;
    public QuoteRepository(DbConnectionFactory db) => _db = db;

    /// <summary>
    /// Inserts quotes, silently skipping any (symbol, date) that already exists
    /// (ON CONFLICT DO NOTHING on the unique constraint). Returns rows actually inserted.
    /// </summary>
    public async Task<int> InsertNewAsync(int symbolId, IReadOnlyCollection<DailyQuote> quotes, CancellationToken ct)
    {
        if (quotes.Count == 0) return 0;

        const string sql = @"
INSERT INTO daily_quotes
    (symbol_id, trade_date, open_price, high_price, low_price, close_price, adj_close_price, volume)
VALUES
    (@SymbolId, @TradeDate, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @AdjClosePrice, @Volume)
ON CONFLICT (symbol_id, trade_date) DO NOTHING;";

        var parameters = quotes.Select(q => new
        {
            SymbolId = symbolId,
            q.TradeDate,
            q.OpenPrice,
            q.HighPrice,
            q.LowPrice,
            q.ClosePrice,
            q.AdjClosePrice,
            q.Volume
        }).ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int inserted = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<DailyQuote>> GetRangeAsync(int symbolId, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        const string sql = @"
SELECT trade_date, open_price, high_price, low_price, close_price, adj_close_price, volume
FROM   daily_quotes
WHERE  symbol_id = @symbolId AND trade_date >= @fromDate AND trade_date <= @toDate
ORDER  BY trade_date;";

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<DailyQuote>(new CommandDefinition(
            sql, new { symbolId, fromDate, toDate }, cancellationToken: ct));
        return rows.ToList();
    }
}
