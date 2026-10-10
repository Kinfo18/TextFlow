using Microsoft.Data.Sqlite;

namespace TextFlow.Infrastructure.Storage;

/// <summary>
/// Versioned schema (H2.1). The version lives in <c>PRAGMA user_version</c>; each migration runs in its own
/// transaction together with the version bump, so a failed upgrade leaves the previous schema intact.
/// Migrations are append-only: never edit one that has shipped.
/// </summary>
public static class SchemaMigrator
{
    private static readonly string[] Migrations =
    [
        // 1 — spec §19, extended for aText libraries: group tree, several abbreviations per snippet.
        """
        CREATE TABLE snippet_group (
            id           TEXT PRIMARY KEY CHECK (id <> ''),
            parent_id    TEXT REFERENCES snippet_group(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            abbreviation TEXT,
            ignore_case  INTEGER NOT NULL CHECK (ignore_case IN (0, 1)),
            sort_order   INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX ux_snippet_group_single_root ON snippet_group ((parent_id IS NULL)) WHERE parent_id IS NULL;
        CREATE INDEX ix_snippet_group_parent ON snippet_group (parent_id, sort_order);

        CREATE TABLE snippet (
            id           TEXT PRIMARY KEY CHECK (id <> ''),
            group_id     TEXT NOT NULL REFERENCES snippet_group(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            content      TEXT NOT NULL,
            content_type TEXT NOT NULL CHECK (content_type IN ('text', 'rich')),
            mode         TEXT NOT NULL CHECK (mode IN ('immediate', 'after_delimiter')),
            enabled      INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            sort_order   INTEGER NOT NULL,
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );
        CREATE INDEX ix_snippet_group ON snippet (group_id, sort_order);

        CREATE TABLE snippet_abbreviation (
            snippet_id   TEXT NOT NULL REFERENCES snippet(id) ON DELETE CASCADE,
            position     INTEGER NOT NULL,
            abbreviation TEXT NOT NULL,
            PRIMARY KEY (snippet_id, position)
        );
        CREATE INDEX ix_snippet_abbreviation ON snippet_abbreviation (abbreviation);

        CREATE TABLE exclusion_rule (
            id      TEXT PRIMARY KEY,
            type    TEXT NOT NULL CHECK (type IN ('process', 'window_class', 'window_title')),
            pattern TEXT NOT NULL,
            scope   INTEGER NOT NULL,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
        );

        CREATE TABLE app_setting (
            key        TEXT PRIMARY KEY,
            value_json TEXT NOT NULL
        );
        """,

        // 2 — per-command Enter after expanding (send a chat message), off by default (2026-10-08).
        """
        ALTER TABLE snippet ADD COLUMN send_enter INTEGER NOT NULL DEFAULT 0 CHECK (send_enter IN (0, 1));
        """,

        // 3 — use counts per snippet (D12, 2026-10-09). No foreign key: replacing the library keeps them.
        """
        CREATE TABLE snippet_usage (
            snippet_id   TEXT PRIMARY KEY CHECK (snippet_id <> ''),
            use_count    INTEGER NOT NULL CHECK (use_count >= 0),
            last_used_at TEXT NOT NULL
        );
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    /// <exception cref="InvalidDataException">The file was written by a newer TextFlow; it is left untouched.</exception>
    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        while (true)
        {
            // Not deferred = BEGIN IMMEDIATE: the write lock comes first and the version is read inside it, so two
            // processes starting at once cannot both apply the same migration.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var version = await GetVersionAsync(connection, ct, transaction).ConfigureAwait(false);
            if (version > LatestVersion)
            {
                throw new InvalidDataException(
                    $"La base de datos es de una versión más nueva de TextFlow (esquema {version}, esta versión conoce hasta {LatestVersion}).");
            }

            if (version == LatestVersion)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return;
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"{Migrations[version]}\nPRAGMA user_version = {version + 1};";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
