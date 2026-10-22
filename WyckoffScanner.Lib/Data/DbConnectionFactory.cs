using Microsoft.Extensions.Options;
using Npgsql;
using WyckoffScanner.Lib.Configuration;

namespace WyckoffScanner.Lib.Data;

public sealed class DbConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public DbConnectionFactory(IOptions<DatabaseOptions> options)
    {
        _dataSource = NpgsqlDataSource.Create(options.Value.ConnectionString);
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) =>
        await _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
