using Dapper;

namespace WyckoffScanner.Lib.Data;

public sealed class LoadLogRepository
{
    private readonly DbConnectionFactory _db;
    public LoadLogRepository(DbConnectionFactory db) => _db = db;

    /// <summary>True if this symbol was already loaded (or legitimately had no data) for the target day.</summary>
    public async Task<bool> HasCompletedLoadAsync(int symbolId, DateOnly targetDate, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            @"SELECT EXISTS (SELECT 1 FROM load_log
                             WHERE symbol_id = @symbolId AND target_date = @targetDate
                               AND status IN ('success', 'no_data'))",
            new { symbolId, targetDate }, cancellationToken: ct));
    }

    public async Task UpsertAsync(int symbolId, DateOnly targetDate, string mode, string status,
                                  int rowsInserted, string? message, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO load_log (symbol_id, target_date, mode, status, rows_inserted, message, loaded_at)
VALUES (@symbolId, @targetDate, @mode, @status, @rowsInserted, @message, now())
ON CONFLICT (symbol_id, target_date) DO UPDATE
   SET mode = EXCLUDED.mode, status = EXCLUDED.status, rows_inserted = EXCLUDED.rows_inserted,
       message = EXCLUDED.message, loaded_at = now();";

        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { symbolId, targetDate, mode, status, rowsInserted, message }, cancellationToken: ct));
    }
}
