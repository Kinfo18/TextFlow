using System.Text.Json;
using TextFlow.Core.Library;

namespace TextFlow.Infrastructure.Storage;

/// <summary>AppSetting rows: one JSON value per key.</summary>
public sealed class SqliteSettingsRepository : ISettingsRepository
{
    private readonly SqliteDatabase _db;

    public SqliteSettingsRepository(SqliteDatabase db) => _db = db;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"El ajuste '{key}' guardado no es válido.", ex);
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_setting (key, value_json) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value_json = excluded.value_json;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
