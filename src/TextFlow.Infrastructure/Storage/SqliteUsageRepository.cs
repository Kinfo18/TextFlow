using System.Globalization;
using TextFlow.Core.Library;

namespace TextFlow.Infrastructure.Storage;

/// <summary>
/// Use counts per snippet (D12): snippet id, count and last use, never content. Kept apart from the snippet table
/// so replacing the library (an aText reimport keeps its ids) does not reset them.
/// </summary>
public sealed class SqliteUsageRepository
{
    private readonly SqliteDatabase _db;

    public SqliteUsageRepository(SqliteDatabase db) => _db = db;

    public async Task<IReadOnlyDictionary<string, SnippetUsage>> LoadAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snippet_id, use_count, last_used_at FROM snippet_usage;";
        var result = new Dictionary<string, SnippetUsage>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new SnippetUsage(
                reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }

        return result;
    }

    /// <summary>Adds a batch of uses; one transaction, so a flush is all or nothing.</summary>
    public async Task AddAsync(IReadOnlyDictionary<string, SnippetUsage> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (id, use) in batch)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO snippet_usage (snippet_id, use_count, last_used_at) VALUES ($id, $count, $at)
                ON CONFLICT (snippet_id) DO UPDATE SET
                    use_count = use_count + excluded.use_count,
                    last_used_at = max(last_used_at, excluded.last_used_at);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$count", use.Uses);
            command.Parameters.AddWithValue("$at", use.LastUsedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
