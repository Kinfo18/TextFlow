using System.Globalization;
using Microsoft.Data.Sqlite;
using TextFlow.Core.Library;

namespace TextFlow.Infrastructure.Storage;

/// <summary>The snippet library in SQLite. All SQL is parameterized; multi-row writes run in one transaction.</summary>
public sealed class SqliteLibraryRepository : ILibraryRepository
{
    private const string EmptyRootId = "root";

    private readonly SqliteDatabase _db;
    private readonly TimeProvider _time;

    public SqliteLibraryRepository(SqliteDatabase db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<LibraryGroup> LoadAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var groups = await ReadGroupsAsync(connection, ct).ConfigureAwait(false);
        var abbreviations = await ReadAbbreviationsAsync(connection, ct).ConfigureAwait(false);
        var snippets = await ReadSnippetsAsync(connection, abbreviations, ct).ConfigureAwait(false);

        var root = groups.FirstOrDefault(g => g.ParentId is null);
        return root is null
            ? new LibraryGroup(EmptyRootId, "Biblioteca", null, true, [], [])
            : Build(root, groups.Where(g => g.ParentId is not null).ToLookup(g => g.ParentId!), snippets, []);
    }

    public async Task ReplaceAllAsync(LibraryGroup root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM snippet_group;", ct).ConfigureAwait(false); // cascades
        var now = Now();
        await InsertTreeAsync(connection, transaction, root, parentId: null, sortOrder: 0, now, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveGroupAsync(GroupInfo group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (group.ParentId is not null)
        {
            await EnsureRootAsync(connection, transaction, ct).ConfigureAwait(false);
        }

        await ValidatePlacementAsync(connection, transaction, group, ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // A group moved to another parent goes last among its new siblings; staying put keeps its position.
            command.CommandText = """
                INSERT INTO snippet_group (id, parent_id, name, abbreviation, ignore_case, sort_order)
                VALUES ($id, $parent, $name, $abbreviation, $ignoreCase,
                        (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM snippet_group WHERE parent_id IS $parent))
                ON CONFLICT (id) DO UPDATE SET
                    sort_order = CASE WHEN snippet_group.parent_id IS excluded.parent_id
                                      THEN snippet_group.sort_order ELSE excluded.sort_order END,
                    parent_id = excluded.parent_id, name = excluded.name,
                    abbreviation = excluded.abbreviation, ignore_case = excluded.ignore_case;
                """;
            AddGroupParameters(command, group.Id, group.ParentId, group.Name, group.Abbreviation, group.IgnoreCase);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tree must stay a tree with one root: a group cannot sit under itself or its descendants, the root cannot
    /// be moved under a group, and a second root cannot appear. LoadAsync would otherwise silently drop subtrees.
    /// </summary>
    private static async Task ValidatePlacementAsync(SqliteConnection connection, SqliteTransaction transaction, GroupInfo group, CancellationToken ct)
    {
        if (group.ParentId == group.Id)
        {
            throw new InvalidOperationException("Un grupo no puede estar dentro de sí mismo.");
        }

        var rootId = await ScalarAsync(connection, transaction, "SELECT id FROM snippet_group WHERE parent_id IS NULL;", ct).ConfigureAwait(false) as string;
        if (group.ParentId is null)
        {
            if (rootId is not null && rootId != group.Id)
            {
                throw new InvalidOperationException("La biblioteca ya tiene un grupo raíz.");
            }

            return;
        }

        if (rootId == group.Id)
        {
            throw new InvalidOperationException("El grupo raíz no puede moverse dentro de otro grupo.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE subtree(id) AS (
                SELECT $id
                UNION
                SELECT g.id FROM snippet_group g JOIN subtree s ON g.parent_id = s.id)
            SELECT EXISTS (SELECT 1 FROM subtree WHERE id = $parent);
            """;
        command.Parameters.AddWithValue("$id", group.Id);
        command.Parameters.AddWithValue("$parent", group.ParentId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidOperationException("Un grupo no puede moverse dentro de uno de sus subgrupos.");
        }
    }

    /// <summary>An empty library exposes a root with id "root" (see LoadAsync): make it real before the first child.</summary>
    private static async Task EnsureRootAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snippet_group (id, parent_id, name, abbreviation, ignore_case, sort_order)
            SELECT $id, NULL, 'Biblioteca', NULL, 1, 0
            WHERE NOT EXISTS (SELECT 1 FROM snippet_group WHERE parent_id IS NULL);
            """;
        command.Parameters.AddWithValue("$id", EmptyRootId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async Task ReorderGroupsAsync(string parentId, IReadOnlyList<string> childIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(childIds);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < childIds.Count; i++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE snippet_group SET sort_order = $order WHERE id = $id AND parent_id = $parent;";
            command.Parameters.AddWithValue("$order", i);
            command.Parameters.AddWithValue("$id", childIds[i]);
            command.Parameters.AddWithValue("$parent", parentId);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("El orden incluye un grupo que no está dentro de ese grupo.");
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false); // nothing is stored unless every id matched
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snippet_group WHERE id = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveSnippetAsync(string groupId, LibrarySnippet snippet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EnsureRootAsync(connection, transaction, ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO snippet (id, group_id, name, content, content_type, mode, enabled, sort_order, created_at, updated_at)
                VALUES ($id, $group, $name, $content, $type, $mode, $enabled,
                        (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM snippet WHERE group_id = $group), $now, $now)
                ON CONFLICT (id) DO UPDATE SET
                    sort_order = CASE WHEN snippet.group_id = excluded.group_id
                                      THEN snippet.sort_order ELSE excluded.sort_order END,
                    group_id = excluded.group_id, name = excluded.name, content = excluded.content,
                    content_type = excluded.content_type, mode = excluded.mode, enabled = excluded.enabled,
                    updated_at = excluded.updated_at;
                """;
            AddSnippetParameters(command, groupId, snippet, Now());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ReplaceAbbreviationsAsync(connection, transaction, snippet, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteSnippetAsync(string snippetId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snippet WHERE id = $id;";
        command.Parameters.AddWithValue("$id", snippetId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertTreeAsync(
        SqliteConnection connection, SqliteTransaction transaction, LibraryGroup group, string? parentId, int sortOrder, string now, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO snippet_group (id, parent_id, name, abbreviation, ignore_case, sort_order)
                VALUES ($id, $parent, $name, $abbreviation, $ignoreCase, $order);
                """;
            AddGroupParameters(command, group.Id, parentId, group.Name, group.Abbreviation, group.IgnoreCase);
            command.Parameters.AddWithValue("$order", sortOrder);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < group.Snippets.Count; i++)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO snippet (id, group_id, name, content, content_type, mode, enabled, sort_order, created_at, updated_at)
                    VALUES ($id, $group, $name, $content, $type, $mode, $enabled, $order, $now, $now);
                    """;
                AddSnippetParameters(command, group.Id, group.Snippets[i], now);
                command.Parameters.AddWithValue("$order", i);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await ReplaceAbbreviationsAsync(connection, transaction, group.Snippets[i], ct).ConfigureAwait(false);
        }

        for (var i = 0; i < group.Groups.Count; i++)
        {
            await InsertTreeAsync(connection, transaction, group.Groups[i], group.Id, i, now, ct).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceAbbreviationsAsync(SqliteConnection connection, SqliteTransaction transaction, LibrarySnippet snippet, CancellationToken ct)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM snippet_abbreviation WHERE snippet_id = $id;";
            delete.Parameters.AddWithValue("$id", snippet.Id);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < snippet.Abbreviations.Count; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO snippet_abbreviation (snippet_id, position, abbreviation) VALUES ($id, $position, $abbreviation);";
            insert.Parameters.AddWithValue("$id", snippet.Id);
            insert.Parameters.AddWithValue("$position", i);
            insert.Parameters.AddWithValue("$abbreviation", snippet.Abbreviations[i]);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static void AddGroupParameters(SqliteCommand command, string id, string? parentId, string name, string? abbreviation, bool ignoreCase)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$abbreviation", (object?)abbreviation ?? DBNull.Value);
        command.Parameters.AddWithValue("$ignoreCase", ignoreCase ? 1 : 0);
    }

    private static void AddSnippetParameters(SqliteCommand command, string groupId, LibrarySnippet snippet, string now)
    {
        command.Parameters.AddWithValue("$id", snippet.Id);
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$name", snippet.Name);
        command.Parameters.AddWithValue("$content", snippet.Content);
        command.Parameters.AddWithValue("$type", snippet.IsRichText ? "rich" : "text");
        command.Parameters.AddWithValue("$mode", snippet.Mode == SnippetMode.AfterDelimiter ? "after_delimiter" : "immediate");
        command.Parameters.AddWithValue("$enabled", snippet.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
    }

    private sealed record GroupRow(string Id, string? ParentId, string Name, string? Abbreviation, bool IgnoreCase);

    private sealed record SnippetRow(string GroupId, LibrarySnippet Snippet);

    private static async Task<List<GroupRow>> ReadGroupsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, parent_id, name, abbreviation, ignore_case FROM snippet_group ORDER BY sort_order, rowid;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<GroupRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GroupRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0));
        }

        return rows;
    }

    private static async Task<ILookup<string, string>> ReadAbbreviationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snippet_id, abbreviation FROM snippet_abbreviation ORDER BY snippet_id, position;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<(string SnippetId, string Abbreviation)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows.ToLookup(r => r.SnippetId, r => r.Abbreviation);
    }

    private static async Task<ILookup<string, LibrarySnippet>> ReadSnippetsAsync(
        SqliteConnection connection, ILookup<string, string> abbreviations, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, group_id, name, content, content_type, mode, enabled FROM snippet ORDER BY sort_order, rowid;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<SnippetRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            rows.Add(new SnippetRow(reader.GetString(1), new LibrarySnippet(
                id,
                reader.GetString(2),
                reader.GetString(3),
                IsRichText: reader.GetString(4) == "rich",
                abbreviations[id].ToArray(),
                reader.GetString(5) == "after_delimiter" ? SnippetMode.AfterDelimiter : SnippetMode.Immediate,
                Enabled: reader.GetInt64(6) != 0)));
        }

        return rows.ToLookup(r => r.GroupId, r => r.Snippet);
    }

    /// <param name="visited">Guards against a cycle that slipped in by hand-editing the file: never recurse forever.</param>
    private static LibraryGroup Build(
        GroupRow row, ILookup<string, GroupRow> children, ILookup<string, LibrarySnippet> snippets, HashSet<string> visited)
    {
        visited.Add(row.Id);
        return new LibraryGroup(
            row.Id,
            row.Name,
            row.Abbreviation,
            row.IgnoreCase,
            children[row.Id].Where(child => !visited.Contains(child.Id)).Select(child => Build(child, children, snippets, visited)).ToArray(),
            snippets[row.Id].ToArray());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
