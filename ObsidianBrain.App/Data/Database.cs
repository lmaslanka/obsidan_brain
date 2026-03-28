using Npgsql;

namespace ObsidianBrain.App.Data;

public sealed class Database : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public Database(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
