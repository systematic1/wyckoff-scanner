using Dapper;
using WyckoffScanner.Models;

namespace WyckoffScanner.Data;

public sealed class JobRunRepository
{
    private readonly DbConnectionFactory _db;
    public JobRunRepository(DbConnectionFactory db) => _db = db;

    public async Task<JobRun> GetOrCreateAsync(DateOnly runDate, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO job_runs (run_date) VALUES (@runDate) ON CONFLICT (run_date) DO NOTHING",
            new { runDate }, cancellationToken: ct));

        return await conn.QuerySingleAsync<JobRun>(new CommandDefinition(
            @"SELECT run_date, loader_completed_at, analysis_completed_at, email_sent_at
              FROM job_runs WHERE run_date = @runDate",
            new { runDate }, cancellationToken: ct));
    }

    public async Task MarkStageCompleteAsync(DateOnly runDate, JobStage stage, CancellationToken ct)
    {
        // Column names come from a fixed switch (never user input).
        string column = stage switch
        {
            JobStage.Loader => "loader_completed_at",
            JobStage.Analysis => "analysis_completed_at",
            JobStage.Email => "email_sent_at",
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE job_runs SET {column} = now() WHERE run_date = @runDate",
            new { runDate }, cancellationToken: ct));
    }
}
