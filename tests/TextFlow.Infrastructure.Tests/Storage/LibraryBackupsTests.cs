using System.Text.Json;
using Microsoft.Data.Sqlite;
using TextFlow.Core.Library;
using TextFlow.Infrastructure.Storage;

namespace TextFlow.Infrastructure.Tests.Storage;

public sealed class LibraryBackupsTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"textflow-tests-{Guid.NewGuid():N}");
    private SqliteDatabase _db = null!;
    private LibraryBackups _backups = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db = new SqliteDatabase(Path.Combine(_folder, "textflow.db"));
        await _db.InitializeAsync(CancellationToken.None);
        _backups = new LibraryBackups(_db, Path.Combine(_folder, "backups"), keep: 3);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private static LibraryGroup Library(string content) => new("root", "Biblioteca", null, true,
        [new LibraryGroup("g", "Temples", "T1", true, [], [new LibrarySnippet("s", "cc", content, false, ["cc"])])], []);

    private Task StoreAsync(LibraryGroup root) => new SqliteLibraryRepository(_db, TimeProvider.System).ReplaceAllAsync(root, CancellationToken.None);

    [Fact]
    public async Task Create_ThenRead_ReturnsTheLibraryAsItWas_EvenAfterLaterChanges()
    {
        await StoreAsync(Library("antes"));
        var backup = await _backups.CreateAsync(new DateTimeOffset(2026, 10, 3, 11, 45, 43, TimeSpan.Zero), CancellationToken.None);
        await StoreAsync(Library("después"));

        var restored = await LibraryBackups.ReadAsync(backup.Path, CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(Library("antes")), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public async Task Read_LeavesTheBackupFileUntouched()
    {
        await StoreAsync(Library("x"));
        var backup = await _backups.CreateAsync(DateTimeOffset.Now, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(backup.Path);

        await LibraryBackups.ReadAsync(backup.Path, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(backup.Path));
        Assert.False(File.Exists(backup.Path + "-wal"));
    }

    [Fact]
    public async Task List_IsNewestFirst_WithTheTimeFromTheName_AndIgnoresOtherFiles()
    {
        await StoreAsync(Library("x"));
        await _backups.CreateAsync(new DateTimeOffset(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Local)), CancellationToken.None);
        await _backups.CreateAsync(new DateTimeOffset(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Local)), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_folder, "backups", "notes.txt"), "not a backup");

        var list = _backups.List();

        Assert.Equal([new DateTime(2026, 10, 3, 9, 0, 0), new DateTime(2026, 10, 1, 9, 0, 0)], list.Select(b => b.CreatedAt));
        Assert.All(list, b => Assert.True(b.SizeBytes > 0));
    }

    [Fact]
    public async Task Create_KeepsOnlyTheNewestCopies()
    {
        await StoreAsync(Library("x"));
        for (var day = 1; day <= 5; day++)
        {
            await _backups.CreateAsync(new DateTimeOffset(new DateTime(2026, 10, day, 9, 0, 0, DateTimeKind.Local)), CancellationToken.None);
        }

        Assert.Equal([5, 4, 3], _backups.List().Select(b => b.CreatedAt.Day));
    }

    [Fact]
    public async Task Read_OfSomethingThatIsNotABackup_IsInvalidData()
    {
        var bogus = Path.Combine(_folder, "backups", "textflow-20261003-000000.db");
        Directory.CreateDirectory(Path.GetDirectoryName(bogus)!);
        await File.WriteAllTextAsync(bogus, "definitely not sqlite");

        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryBackups.ReadAsync(bogus, CancellationToken.None));
    }
}
