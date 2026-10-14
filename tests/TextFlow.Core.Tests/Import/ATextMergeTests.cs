using System.Text.Json;
using TextFlow.Core.Import;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Import;

public sealed class ATextMergeTests
{
    private static LibrarySnippet Snippet(string id, string abbreviation, string content = "x") => new(id, abbreviation, content, false, [abbreviation]);

    private static LibraryGroup Group(string id, string? abbreviation, LibraryGroup[] groups, params LibrarySnippet[] snippets) =>
        new(id, id, abbreviation, true, groups, snippets);

    private static LibraryGroup Group(string id, string? abbreviation, params LibrarySnippet[] snippets) => Group(id, abbreviation, [], snippets);

    /// <summary>The user's library: root "root" with one group.</summary>
    private static LibraryGroup Current() => new("root", "Biblioteca", null, true,
        [Group("g-lc", "LC", Snippet("s-1", "lc1"), Snippet("s-2", "lc2"))], []);

    /// <summary>What the aText reader returns for a backup with several top-level groups.</summary>
    private static LibraryGroup Synthetic(params LibraryGroup[] groups) =>
        new(ATextBackupReader.SyntheticRootId, "aText", null, true, groups, []);

    private static int _ids;

    private static string NewId() => $"new-{++_ids}";

    private static string Json(LibraryGroup group) => JsonSerializer.Serialize(group);

    [Fact]
    public void Append_ASecondLibrary_GoesLastAsOneGroupNamedAfterTheFile_LeavingTheRestAlone()
    {
        var other = Synthetic(Group("g-op", "OP", Snippet("s-op", "op1")), Group("g-x", null, Snippet("s-x", "x1")));

        var merged = ATextMerge.Append(Current(), other, "Operaciones", NewId);

        Assert.Equal(Json(Current().Groups[0]), Json(merged.Root.Groups[0]));
        var container = merged.Root.Groups[1];
        Assert.Equal("Operaciones", container.Name);
        Assert.NotEqual(ATextBackupReader.SyntheticRootId, container.Id);
        Assert.Equal(["g-op", "g-x"], container.Groups.Select(g => g.Id));
        Assert.Equal(2, merged.Added);
        Assert.Equal(0, merged.Updated);
    }

    [Fact]
    public void Append_ABackupWithASingleGroup_AddsThatGroupWithItsOwnName()
    {
        var single = Group("g-op", "OP", Snippet("s-op", "op1"));

        var merged = ATextMerge.Append(Current(), single, "archivo", NewId);

        Assert.Equal(["g-lc", "g-op"], merged.Root.Groups.Select(g => g.Id));
        Assert.Equal("g-op", merged.Root.Groups[1].Name);
    }

    [Fact]
    public void Append_TheSameLibraryAgain_UpdatesInPlace_WithoutDuplicates()
    {
        var current = Current();
        var again = Synthetic(Group("g-lc", "LC", Snippet("s-1", "lc1", content: "nuevo"), Snippet("s-2", "lc2")));

        var merged = ATextMerge.Append(current, again, "BUXtendo", NewId);

        Assert.Equal(["g-lc"], merged.Root.Groups.Select(g => g.Id)); // no empty container
        Assert.Equal("nuevo", merged.Root.Groups[0].Snippets[0].Content);
        Assert.Equal(0, merged.Added);
        Assert.Equal(1, merged.Updated);
    }

    [Fact]
    public void Append_KeepsWhereTheUserMovedAnUpdatedCommand_AndPutsNewOnesInTheirExistingGroup()
    {
        var current = new LibraryGroup("root", "Biblioteca", null, true,
            [Group("g-lc", "LC", Snippet("s-1", "lc1")), Group("g-mine", null, Snippet("s-2", "lc2"))], []);
        var again = Synthetic(Group("g-lc", "LC", Snippet("s-1", "lc1"), Snippet("s-2", "lc2", content: "nuevo"), Snippet("s-3", "lc3")));

        var merged = ATextMerge.Append(current, again, "BUXtendo", NewId);

        Assert.Equal(["s-1", "s-3"], merged.Root.Groups[0].Snippets.Select(s => s.Id));
        Assert.Equal("nuevo", Assert.Single(merged.Root.Groups[1].Snippets).Content);
        Assert.Equal(2, merged.Root.Groups.Count);
        Assert.Equal(1, merged.Added);
        Assert.Equal(1, merged.Updated);
    }

    [Fact]
    public void Append_ANewSubgroupOfAnExistingGroup_GoesInsideIt()
    {
        var again = Synthetic(Group("g-lc", "LC", [Group("g-sub", null, Snippet("s-9", "lc9"))], Snippet("s-1", "lc1")));

        var merged = ATextMerge.Append(Current(), again, "BUXtendo", NewId);

        Assert.Equal("g-sub", Assert.Single(merged.Root.Groups[0].Groups).Id);
        Assert.Single(merged.Root.Groups);
    }

    [Fact]
    public void Append_DoesNotRenameTheLibraryRoot_WhenTheBackupRootIsTheSameGroup()
    {
        var current = new LibraryGroup("g-bux", "Mi biblioteca", null, true, [], [Snippet("s-1", "lc1")]);
        var again = new LibraryGroup("g-bux", "BUXtendo", "BX", true, [], [Snippet("s-1", "lc1"), Snippet("s-2", "lc2")]);

        var merged = ATextMerge.Append(current, again, "BUXtendo", NewId);

        Assert.Equal("Mi biblioteca", merged.Root.Name);
        Assert.Null(merged.Root.Abbreviation);
        Assert.Equal(["s-1", "s-2"], merged.Root.Snippets.Select(s => s.Id));
    }

    [Fact]
    public void Append_CountsNewCommandsWhoseAbbreviationAlreadyExists()
    {
        var other = Synthetic(Group("g-op", "lc", Snippet("s-a", "LC1"), Snippet("s-b", "op1"), Snippet("s-c", "lc2")));

        var merged = ATextMerge.Append(Current(), other, "Operaciones", NewId);

        Assert.Equal(2, merged.AbbreviationClashes); // LC1 (case ignored) and lc2; the group abbreviation shares a menu instead
    }

    [Fact]
    public void OfAppend_ReportsWhatIsAddedAndUpdated_AndNeverARemoval()
    {
        var current = Current();
        var again = Synthetic(Group("g-lc", "LC", Snippet("s-1", "lc1", content: "nuevo"), Snippet("s-3", "lc2")));
        var merged = ATextMerge.Append(current, again, "BUXtendo", NewId);

        var preview = ImportPreview.OfAppend(new ATextImport(again, []), current, merged);

        Assert.True(preview.Appends);
        Assert.False(preview.ReplacesExisting);
        Assert.Equal((1, 1, 0, 1), (preview.Added, preview.Changed, preview.Removed, preview.AbbreviationClashes));
        Assert.Equal(2, preview.Incoming.Commands); // the backup's content, not the merged library's
    }

    [Fact]
    public void Append_ABackupThatRepeatsAnId_InsertsItOnce()
    {
        var malformed = Synthetic(Group("g-a", null, Snippet("s-dup", "d1")), Group("g-b", null, Snippet("s-dup", "d1"), Snippet("s-ok", "ok")));

        var merged = ATextMerge.Append(Current(), malformed, "roto", NewId);

        Assert.Equal(2, merged.Added);
        Assert.Single(merged.Root.Groups[1].Groups.SelectMany(g => g.Snippets), s => s.Id == "s-dup");
    }

    [Fact]
    public void Append_AnEmptyBackup_ChangesNothing()
    {
        var merged = ATextMerge.Append(Current(), Synthetic(), "vacío", NewId);

        Assert.Equal(Json(Current()), Json(merged.Root));
    }
}
