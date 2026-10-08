using System.Text.Json;
using Microsoft.Data.Sqlite;
using TextFlow.Core.Import;
using TextFlow.Core.Library;
using TextFlow.Core.Menus;
using TextFlow.Core.Security;
using TextFlow.Infrastructure.Storage;

namespace TextFlow.Infrastructure.Tests.Storage;

/// <summary>Real SQLite on a throwaway file: migrations, the library tree, rules and settings.</summary>
public sealed class SqliteStorageTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"textflow-tests-{Guid.NewGuid():N}.db");
    private SqliteDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_path);
        await _db.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools(); // release the file before deleting it
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private SqliteLibraryRepository Library() => new(_db, TimeProvider.System);

    private static LibrarySnippet Snippet(string id, string content, params string[] abbreviations) =>
        new(id, abbreviations.FirstOrDefault() ?? id, content, IsRichText: false, abbreviations);

    private static LibraryGroup SampleLibrary() => new("root", "Biblioteca", null, true,
        [
            new LibraryGroup("g-lc", "Local cerrado", "LC", true,
                [new LibraryGroup("g-sub", "Sub", null, false, [], [Snippet("s-note", string.Empty, "Nota informativa")])],
                [Snippet("s-nc", "Hola 👋\r\nlínea 2 — ñandú «comillas»", "nc", "No confirmado")]),
            new LibraryGroup("g-t", "Temples", "T1", true, [],
                [Snippet("s-cc", "texto cc", "cc") with { SendEnter = true }, Snippet("s-sig", "firma", "sig") with { Mode = SnippetMode.AfterDelimiter, Enabled = false }]),
        ],
        []);

    /// <summary>Records hold lists by reference: compare the serialized trees.</summary>
    private static void AssertSameTree(LibraryGroup expected, LibraryGroup actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    [Fact]
    public async Task Initialize_AppliesMigrations_AndIsIdempotent()
    {
        Assert.Equal(SchemaMigrator.LatestVersion, await _db.GetSchemaVersionAsync(CancellationToken.None));

        await new SqliteDatabase(_path).InitializeAsync(CancellationToken.None);

        Assert.Equal(SchemaMigrator.LatestVersion, await _db.GetSchemaVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseFromANewerTextFlow_IsRejected_NotDowngraded()
    {
        await using (var connection = await _db.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {SchemaMigrator.LatestVersion + 1}";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteDatabase(_path).InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EmptyDatabase_LoadsAnEmptyRoot()
    {
        var root = await Library().LoadAsync(CancellationToken.None);

        Assert.Empty(root.Groups);
        Assert.Empty(root.Snippets);
    }

    [Fact]
    public async Task ReplaceAll_ThenLoad_RoundTripsTheTree_WithOrderUnicodeModeAndEnabled()
    {
        var library = Library();

        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        AssertSameTree(SampleLibrary(), await library.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceAll_ReplacesThePreviousLibrary()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);
        var smaller = new LibraryGroup("root2", "Otra", null, true, [], [Snippet("s-x", "x", "xx")]);

        await library.ReplaceAllAsync(smaller, CancellationToken.None);

        AssertSameTree(smaller, await library.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveSnippet_AddsUpdatesAndKeepsItsPosition()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await library.SaveSnippetAsync("g-t", Snippet("s-new", "nuevo", "nw"), CancellationToken.None);
        await library.SaveSnippetAsync("g-t", Snippet("s-cc", "cc editado", "cc", "cc2"), CancellationToken.None);

        var temples = (await library.LoadAsync(CancellationToken.None)).Groups.Single(g => g.Id == "g-t");
        Assert.Equal(["s-cc", "s-sig", "s-new"], temples.Snippets.Select(s => s.Id));
        Assert.Equal("cc editado", temples.Snippets[0].Content);
        Assert.Equal(["cc", "cc2"], temples.Snippets[0].Abbreviations);
    }

    [Fact]
    public async Task ReorderGroups_StoresTheNewOrderAmongSiblings()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await library.ReorderGroupsAsync("root", ["g-t", "g-lc"], CancellationToken.None);

        Assert.Equal(["g-t", "g-lc"], (await library.LoadAsync(CancellationToken.None)).Groups.Select(g => g.Id));
    }

    [Fact]
    public async Task ReorderGroups_RejectsGroupsThatAreNotChildrenOfTheParent()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            library.ReorderGroupsAsync("root", ["g-t", "g-sub"], CancellationToken.None));

        Assert.Equal(["g-lc", "g-t"], (await library.LoadAsync(CancellationToken.None)).Groups.Select(g => g.Id));
    }

    [Fact]
    public async Task SaveSnippet_IntoAMissingGroup_Fails()
    {
        await Assert.ThrowsAsync<SqliteException>(() =>
            Library().SaveSnippetAsync("no-such-group", Snippet("s-x", "x", "xx"), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSnippet_RemovesItAndItsAbbreviations()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await library.DeleteSnippetAsync("s-cc", CancellationToken.None);

        var root = await library.LoadAsync(CancellationToken.None);
        Assert.DoesNotContain(root.Groups.SelectMany(g => g.Snippets), s => s.Id == "s-cc");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM snippet_abbreviation WHERE snippet_id = 's-cc'"));
    }

    [Fact]
    public async Task SaveGroup_CreatesAndRenames_DeleteGroup_Cascades()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await library.SaveGroupAsync(new GroupInfo("g-new", "root", "Nuevo", "NV", IgnoreCase: false), CancellationToken.None);
        await library.SaveGroupAsync(new GroupInfo("g-lc", "root", "Local cerrado (editado)", "LC", IgnoreCase: true), CancellationToken.None);
        await library.DeleteGroupAsync("g-lc", CancellationToken.None);

        var root = await library.LoadAsync(CancellationToken.None);
        Assert.Equal(["g-t", "g-new"], root.Groups.Select(g => g.Id));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM snippet WHERE id IN ('s-nc', 's-note')"));
    }

    [SkippableFact]
    public async Task RealATextBackup_SurvivesStorage_WithTheSameTriggersAndMenus()
    {
        var backup = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BUXtendo.atext");
        Skip.IfNot(File.Exists(backup), "The user's private backup is not in this checkout.");
        LibraryGroup imported;
        await using (var file = File.OpenRead(backup))
        {
            imported = ATextBackupReader.Read(file).Root;
        }

        var library = Library();
        await library.ReplaceAllAsync(imported, CancellationToken.None);
        var stored = await library.LoadAsync(CancellationToken.None);

        AssertSameTree(imported, stored);
        var (before, after) = (LibraryIndex.Build(imported), LibraryIndex.Build(stored));
        Assert.Equal(before.Menus.Count, after.Menus.Count);
        Assert.Equal(before.Triggers.Select(t => t.Trigger), after.Triggers.Select(t => t.Trigger));
    }

    [Fact]
    public async Task EmptyDatabase_AcceptsGroupsAndSnippetsUnderTheRoot()
    {
        var library = Library();
        var root = await library.LoadAsync(CancellationToken.None);

        await library.SaveGroupAsync(new GroupInfo("g-1", root.Id, "Primero", "P1", IgnoreCase: true), CancellationToken.None);
        await library.SaveSnippetAsync(root.Id, Snippet("s-1", "hola", "hh"), CancellationToken.None);

        var loaded = await library.LoadAsync(CancellationToken.None);
        Assert.Equal(["g-1"], loaded.Groups.Select(g => g.Id));
        Assert.Equal(["s-1"], loaded.Snippets.Select(s => s.Id));
    }

    [Theory]
    [InlineData("g-lc", "g-lc")]   // its own parent
    [InlineData("g-lc", "g-sub")]  // under its own descendant
    [InlineData("root", "g-t")]    // the root under one of its groups
    public async Task SaveGroup_RejectsCycles_AndLosingTheRoot(string groupId, string newParentId)
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            library.SaveGroupAsync(new GroupInfo(groupId, newParentId, "x", null, IgnoreCase: true), CancellationToken.None));

        AssertSameTree(SampleLibrary(), await library.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MovingAGroup_PutsItLastAmongItsNewSiblings()
    {
        var library = Library();
        await library.ReplaceAllAsync(SampleLibrary(), CancellationToken.None);

        await library.SaveGroupAsync(new GroupInfo("g-sub", "root", "Sub", null, IgnoreCase: false), CancellationToken.None);
        await library.SaveSnippetAsync("g-t", Snippet("s-nc", "movido", "nc"), CancellationToken.None);

        var root = await library.LoadAsync(CancellationToken.None);
        Assert.Equal(["g-lc", "g-t", "g-sub"], root.Groups.Select(g => g.Id));
        Assert.Equal(["s-cc", "s-sig", "s-nc"], root.Groups.Single(g => g.Id == "g-t").Snippets.Select(s => s.Id));
    }

    [Fact]
    public async Task CorruptSetting_IsReportedAsInvalidData()
    {
        await using (var connection = await _db.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_setting (key, value_json) VALUES ('broken', '{not json');";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteSettingsRepository(_db).GetAsync<double>("broken", CancellationToken.None));
    }

    [Fact]
    public async Task Backup_WritesAConsistentCopy_ThatOpensWithTheSameLibrary()
    {
        await Library().ReplaceAllAsync(SampleLibrary(), CancellationToken.None);
        var copy = Path.Combine(Path.GetTempPath(), $"textflow-tests-{Guid.NewGuid():N}.db");
        try
        {
            await _db.BackupAsync(copy, CancellationToken.None);

            var restored = new SqliteDatabase(copy);
            await restored.InitializeAsync(CancellationToken.None);
            AssertSameTree(SampleLibrary(), await new SqliteLibraryRepository(restored, TimeProvider.System).LoadAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { copy, copy + "-wal", copy + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [SkippableFact]
    public void RealATextBackup_SurvivesTextFlowJson_Losslessly()
    {
        var backup = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BUXtendo.atext");
        Skip.IfNot(File.Exists(backup), "The user's private backup is not in this checkout.");
        LibraryGroup imported;
        using (var file = File.OpenRead(backup))
        {
            imported = ATextBackupReader.Read(file).Root;
        }

        AssertSameTree(imported, LibraryJson.Import(LibraryJson.Export(imported)));
    }

    [Fact]
    public async Task ExclusionRules_CanBeSavedUpdatedAndDeleted()
    {
        var rules = new SqliteExclusionRuleRepository(_db);
        var rule = new ExclusionRule("user:1", ExclusionMatchType.Process, "slack.exe", FeatureScope.Expansion);

        await rules.SaveAsync(rule, CancellationToken.None);
        await rules.SaveAsync(rule with { Enabled = false, Scope = FeatureScope.All }, CancellationToken.None);
        Assert.Equal([rule with { Enabled = false, Scope = FeatureScope.All }], await rules.GetAllAsync(CancellationToken.None));

        await rules.DeleteAsync("user:1", CancellationToken.None);
        Assert.Empty(await rules.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Settings_RoundTripTypedValues()
    {
        var settings = new SqliteSettingsRepository(_db);

        await settings.SetAsync("chime.volume", 0.4, CancellationToken.None);
        await settings.SetAsync("pause.hotkey", "Ctrl+Shift+Alt+P", CancellationToken.None);
        await settings.SetAsync("chime.volume", 0.6, CancellationToken.None);

        Assert.Equal(0.6, await settings.GetAsync<double>("chime.volume", CancellationToken.None));
        Assert.Equal("Ctrl+Shift+Alt+P", await settings.GetAsync<string>("pause.hotkey", CancellationToken.None));
        Assert.Null(await settings.GetAsync<string>("missing", CancellationToken.None));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await _db.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
