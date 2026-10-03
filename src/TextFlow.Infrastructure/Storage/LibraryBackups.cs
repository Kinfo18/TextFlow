using System.Globalization;
using Microsoft.Data.Sqlite;
using TextFlow.Core.Library;

namespace TextFlow.Infrastructure.Storage;

/// <summary>A database snapshot in the backups folder. <see cref="CreatedAt"/> is local time, from the file name.</summary>
public sealed record LibraryBackup(string Path, DateTime CreatedAt, long SizeBytes);

/// <summary>
/// Snapshots of the library database taken before every import (H2.5): create, list newest first, prune, and read
/// one back without modifying it (it is opened as a temporary copy, migrated if its schema is older).
/// </summary>
public sealed class LibraryBackups
{
    private const string Prefix = "textflow-";
    private const string TimeFormat = "yyyyMMdd-HHmmss";

    private readonly SqliteDatabase _db;
    private readonly string _folder;
    private readonly int _keep;

    public LibraryBackups(SqliteDatabase db, string folder, int keep = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        _db = db;
        _folder = folder;
        _keep = keep;
    }

    public async Task<LibraryBackup> CreateAsync(DateTimeOffset now, CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        var path = UniquePath(now.LocalDateTime);
        await _db.BackupAsync(path, ct).ConfigureAwait(false);
        Prune();
        return new LibraryBackup(path, now.LocalDateTime, new FileInfo(path).Length);
    }

    public IReadOnlyList<LibraryBackup> List()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        return new DirectoryInfo(_folder).GetFiles($"{Prefix}*.db")
            .Select(file => TryParse(file.Name, out var created) ? new LibraryBackup(file.FullName, created, file.Length) : null)
            .OfType<LibraryBackup>()
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <exception cref="InvalidDataException">The file is not a TextFlow database (or comes from a newer TextFlow).</exception>
    public static async Task<LibraryGroup> ReadAsync(string backupPath, CancellationToken ct)
    {
        var copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"textflow-restore-{Guid.NewGuid():N}.db");
        File.Copy(backupPath, copy);
        try
        {
            var database = new SqliteDatabase(copy);
            await database.InitializeAsync(ct).ConfigureAwait(false);
            return await new SqliteLibraryRepository(database, TimeProvider.System).LoadAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("El archivo no es una copia de seguridad de TextFlow.", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the temporary file before deleting it
            foreach (var file in new[] { copy, copy + "-wal", copy + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    private void Prune()
    {
        foreach (var old in List().Skip(_keep))
        {
            File.Delete(old.Path);
        }
    }

    /// <summary>Two imports in the same second must not overwrite each other's snapshot.</summary>
    private string UniquePath(DateTime local)
    {
        var name = $"{Prefix}{local.ToString(TimeFormat, CultureInfo.InvariantCulture)}";
        var path = System.IO.Path.Combine(_folder, $"{name}.db");
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(_folder, $"{name}-{n}.db");
        }

        return path;
    }

    private static bool TryParse(string fileName, out DateTime created)
    {
        var stamp = fileName.Length >= Prefix.Length + TimeFormat.Length
            ? fileName.Substring(Prefix.Length, TimeFormat.Length)
            : string.Empty;
        return DateTime.TryParseExact(stamp, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out created);
    }
}
