using Microsoft.Data.Sqlite;

namespace TextFlow.Infrastructure.Storage;

/// <summary>
/// The local library file (%LOCALAPPDATA%\TextFlow\textflow.db). Every connection enforces foreign keys;
/// the file uses WAL so the UI can read while an import writes.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly string _path;

    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Creates the file if needed and brings the schema up to date. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SchemaMigrator.MigrateAsync(connection, ct).ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await SchemaMigrator.GetVersionAsync(connection, ct).ConfigureAwait(false);
    }
}
