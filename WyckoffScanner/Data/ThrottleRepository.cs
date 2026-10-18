using Dapper;

namespace WyckoffScanner.Data;

public sealed class ThrottleState
{
    public DateTime? LastCallUtc { get; set; }
    public DateTime? BlockedUntilUtc { get; set; }
}

/// <summary>Persists the Yahoo throttle (last call time, rate-limit cooldown) so restarts cannot bypass it.</summary>
public sealed class ThrottleRepository
{
    private readonly DbConnectionFactory _db;
    public ThrottleRepository(DbConnectionFactory db) => _db = db;

    public async Task<ThrottleState> GetAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ThrottleState>(new CommandDefinition(
            "SELECT last_call_utc, blocked_until_utc FROM throttle_state WHERE id = 1", cancellationToken: ct));
        row ??= new ThrottleState();
        if (row.LastCallUtc.HasValue) row.LastCallUtc = DateTime.SpecifyKind(row.LastCallUtc.Value, DateTimeKind.Utc);
        if (row.BlockedUntilUtc.HasValue) row.BlockedUntilUtc = DateTime.SpecifyKind(row.BlockedUntilUtc.Value, DateTimeKind.Utc);
        return row;
    }

    public async Task SetLastCallAsync(DateTime lastCallUtc, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO throttle_state (id, last_call_utc) VALUES (1, @lastCallUtc)
              ON CONFLICT (id) DO UPDATE SET last_call_utc = EXCLUDED.last_call_utc",
            new { lastCallUtc }, cancellationToken: ct));
    }

    public async Task SetBlockedUntilAsync(DateTime blockedUntilUtc, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO throttle_state (id, blocked_until_utc) VALUES (1, @blockedUntilUtc)
              ON CONFLICT (id) DO UPDATE SET blocked_until_utc = EXCLUDED.blocked_until_utc",
            new { blockedUntilUtc }, cancellationToken: ct));
    }
}
