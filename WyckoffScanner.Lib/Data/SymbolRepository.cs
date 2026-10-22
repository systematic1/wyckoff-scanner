using Dapper;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Data;

public sealed class SymbolRepository
{
    private readonly DbConnectionFactory _db;
    public SymbolRepository(DbConnectionFactory db) => _db = db;

    public async Task<IReadOnlyList<SymbolRecord>> GetActiveAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SymbolRecord>(new CommandDefinition(
            "SELECT symbol_id, ticker, backfill_completed_at FROM symbols WHERE is_active ORDER BY ticker",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task MarkBackfillCompletedAsync(int symbolId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE symbols SET backfill_completed_at = now() WHERE symbol_id = @symbolId",
            new { symbolId }, cancellationToken: ct));
    }
}
