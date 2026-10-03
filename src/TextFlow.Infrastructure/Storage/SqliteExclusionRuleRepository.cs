using TextFlow.Core.Library;
using TextFlow.Core.Security;

namespace TextFlow.Infrastructure.Storage;

public sealed class SqliteExclusionRuleRepository : IExclusionRuleRepository
{
    private readonly SqliteDatabase _db;

    public SqliteExclusionRuleRepository(SqliteDatabase db) => _db = db;

    public async Task<IReadOnlyList<ExclusionRule>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, type, pattern, scope, enabled FROM exclusion_rule ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rules = new List<ExclusionRule>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rules.Add(new ExclusionRule(
                reader.GetString(0),
                ParseType(reader.GetString(1)),
                reader.GetString(2),
                (FeatureScope)reader.GetInt64(3),
                reader.GetInt64(4) != 0));
        }

        return rules;
    }

    public async Task SaveAsync(ExclusionRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exclusion_rule (id, type, pattern, scope, enabled) VALUES ($id, $type, $pattern, $scope, $enabled)
            ON CONFLICT (id) DO UPDATE SET
                type = excluded.type, pattern = excluded.pattern, scope = excluded.scope, enabled = excluded.enabled;
            """;
        command.Parameters.AddWithValue("$id", rule.Id);
        command.Parameters.AddWithValue("$type", FormatType(rule.Type));
        command.Parameters.AddWithValue("$pattern", rule.Pattern);
        command.Parameters.AddWithValue("$scope", (long)rule.Scope);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string ruleId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM exclusion_rule WHERE id = $id;";
        command.Parameters.AddWithValue("$id", ruleId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string FormatType(ExclusionMatchType type) => type switch
    {
        ExclusionMatchType.Process => "process",
        ExclusionMatchType.WindowClass => "window_class",
        ExclusionMatchType.WindowTitle => "window_title",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown exclusion type."),
    };

    private static ExclusionMatchType ParseType(string type) => type switch
    {
        "process" => ExclusionMatchType.Process,
        "window_class" => ExclusionMatchType.WindowClass,
        "window_title" => ExclusionMatchType.WindowTitle,
        _ => throw new InvalidDataException($"Unknown exclusion type '{type}' in the database."),
    };
}
