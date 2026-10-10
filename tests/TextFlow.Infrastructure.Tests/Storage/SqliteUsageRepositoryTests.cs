using Microsoft.Data.Sqlite;
using TextFlow.Core.Library;
using TextFlow.Infrastructure.Storage;

namespace TextFlow.Infrastructure.Tests.Storage;

/// <summary>Use counts per snippet (D12) on a throwaway database.</summary>
public sealed class SqliteUsageRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"textflow-usage-{Guid.NewGuid():N}.db");
    private SqliteDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_path);
        await _db.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Add_AccumulatesCounts_AndKeepsTheLatestTime()
    {
        var usage = new SqliteUsageRepository(_db);

        await usage.AddAsync(new Dictionary<string, SnippetUsage> { ["cc"] = new(2, Monday), ["nc"] = new(1, Monday) }, CancellationToken.None);
        await usage.AddAsync(new Dictionary<string, SnippetUsage> { ["cc"] = new(3, Monday.AddDays(1)) }, CancellationToken.None);

        var all = await usage.LoadAsync(CancellationToken.None);
        Assert.Equal(new SnippetUsage(5, Monday.AddDays(1)), all["cc"]);
        Assert.Equal(new SnippetUsage(1, Monday), all["nc"]);
    }

    [Fact]
    public async Task Counts_SurviveReplacingTheLibrary_SoAReimportKeepsThem()
    {
        var usage = new SqliteUsageRepository(_db);
        await usage.AddAsync(new Dictionary<string, SnippetUsage> { ["s-cc"] = new(4, Monday) }, CancellationToken.None);

        await new SqliteLibraryRepository(_db, TimeProvider.System).ReplaceAllAsync(
            new LibraryGroup("root", "Biblioteca", null, true, [], []), CancellationToken.None);

        Assert.Equal(4, (await usage.LoadAsync(CancellationToken.None))["s-cc"].Uses);
    }

    [Fact]
    public async Task Load_IsEmpty_OnANewDatabase()
    {
        Assert.Empty(await new SqliteUsageRepository(_db).LoadAsync(CancellationToken.None));
    }
}
