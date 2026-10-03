using TextFlow.Core.Library;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Library;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly InMemoryLibraryRepository _repository = new();
    private readonly LibraryService _service;
    private readonly List<LibraryGroup> _changes = [];

    public LibraryServiceTests()
    {
        _service = new LibraryService(_repository);
        _service.Changed += _changes.Add;
    }

    public void Dispose() => _service.Dispose();

    private static LibrarySnippet Snippet(string id, string abbreviation) => new(id, abbreviation, $"texto {id}", false, [abbreviation]);

    private static LibraryGroup Library() => new("root", "Biblioteca", null, true,
        [new LibraryGroup("g-t", "Temples", "T1", true, [], [Snippet("s-cc", "cc")])], []);

    [Fact]
    public async Task Load_ExposesTheStoredLibrary()
    {
        await _repository.ReplaceAllAsync(Library(), CancellationToken.None);

        var root = await _service.LoadAsync(CancellationToken.None);

        Assert.Same(root, _service.Current);
        Assert.Equal("g-t", Assert.Single(root.Groups).Id);
    }

    [Fact]
    public async Task SavingASnippet_RaisesChanged_WithATreeWhoseTriggerWorks()
    {
        await _repository.ReplaceAllAsync(Library(), CancellationToken.None);
        await _service.LoadAsync(CancellationToken.None);

        await _service.SaveSnippetAsync("g-t", Snippet("s-new", "nw"), CancellationToken.None);

        var index = LibraryIndex.Build(Assert.Single(_changes));
        Assert.Contains(index.Triggers, t => t.Trigger == "nw");
    }

    [Fact]
    public async Task Import_ReplacesTheLibrary_AndRaisesChanged()
    {
        await _repository.ReplaceAllAsync(Library(), CancellationToken.None);
        var imported = new LibraryGroup("root2", "aText", null, true, [], [Snippet("s-x", "xx")]);

        await _service.ImportAsync(imported, CancellationToken.None);

        Assert.Equal("root2", _service.Current.Id);
        Assert.Equal("root2", Assert.Single(_changes).Id);
    }

    [Fact]
    public async Task IsEmpty_IsTrueOnlyForALibraryWithoutGroupsOrSnippets()
    {
        await _service.LoadAsync(CancellationToken.None);
        Assert.True(_service.IsEmpty);

        await _service.ImportAsync(Library(), CancellationToken.None);
        Assert.False(_service.IsEmpty);
    }

    [Fact]
    public async Task ReorderingGroups_RaisesChanged_InTheNewOrder()
    {
        await _service.ImportAsync(Library() with { Groups = [.. Library().Groups, new LibraryGroup("g-2", "Dos", null, true, [], [])] }, CancellationToken.None);
        _changes.Clear();

        await _service.ReorderGroupsAsync("root", ["g-2", "g-t"], CancellationToken.None);

        Assert.Equal(["g-2", "g-t"], Assert.Single(_changes).Groups.Select(g => g.Id));
    }

    [Fact]
    public async Task DeletingAGroup_RaisesChanged()
    {
        await _service.ImportAsync(Library(), CancellationToken.None);
        _changes.Clear();

        await _service.DeleteGroupAsync("g-t", CancellationToken.None);

        Assert.Empty(Assert.Single(_changes).Groups);
    }

    /// <summary>Minimal tree store with the same semantics as the SQLite repository for these tests.</summary>
    private sealed class InMemoryLibraryRepository : ILibraryRepository
    {
        private LibraryGroup _root = new("root", "Biblioteca", null, true, [], []);

        public Task<LibraryGroup> LoadAsync(CancellationToken ct) => Task.FromResult(_root);

        public Task ReplaceAllAsync(LibraryGroup root, CancellationToken ct)
        {
            _root = root;
            return Task.CompletedTask;
        }

        public Task SaveGroupAsync(GroupInfo group, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteGroupAsync(string groupId, CancellationToken ct)
        {
            _root = _root with { Groups = _root.Groups.Where(g => g.Id != groupId).ToArray() };
            return Task.CompletedTask;
        }

        public Task SaveSnippetAsync(string groupId, LibrarySnippet snippet, CancellationToken ct)
        {
            _root = _root with
            {
                Groups = _root.Groups.Select(g => g.Id == groupId ? g with { Snippets = [.. g.Snippets, snippet] } : g).ToArray(),
            };
            return Task.CompletedTask;
        }

        public Task DeleteSnippetAsync(string snippetId, CancellationToken ct) => throw new NotSupportedException();

        public Task ReorderGroupsAsync(string parentId, IReadOnlyList<string> childIds, CancellationToken ct)
        {
            var byId = _root.Groups.ToDictionary(g => g.Id);
            _root = _root with { Groups = childIds.Select(id => byId[id]).ToArray() };
            return Task.CompletedTask;
        }
    }
}
