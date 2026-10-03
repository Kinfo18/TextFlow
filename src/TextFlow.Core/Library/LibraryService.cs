namespace TextFlow.Core.Library;

/// <summary>
/// The library as the app sees it: the stored tree plus a <see cref="Changed"/> notification after every write,
/// so the engine re-indexes at once (an edited snippet expands without restarting, H2.2).
/// Writes are serialized; each one reloads the tree from storage so <see cref="Current"/> is what was persisted.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly ILibraryRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibraryGroup _current = new("root", "Biblioteca", null, true, [], []);

    public LibraryService(ILibraryRepository repository) => _repository = repository;

    /// <summary>Raised (on the writer's thread) with the reloaded tree after every successful write.</summary>
    public event Action<LibraryGroup>? Changed;

    public LibraryGroup Current => Volatile.Read(ref _current);

    public bool IsEmpty => Current.Groups.Count == 0 && Current.Snippets.Count == 0;

    public async Task<LibraryGroup> LoadAsync(CancellationToken ct)
    {
        var root = await _repository.LoadAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _current, root);
        return root;
    }

    /// <summary>Replaces everything (aText import). The caller backs the database up first.</summary>
    public Task ImportAsync(LibraryGroup root, CancellationToken ct) => WriteAsync(r => r.ReplaceAllAsync(root, ct), ct);

    public Task SaveGroupAsync(GroupInfo group, CancellationToken ct) => WriteAsync(r => r.SaveGroupAsync(group, ct), ct);

    public Task DeleteGroupAsync(string groupId, CancellationToken ct) => WriteAsync(r => r.DeleteGroupAsync(groupId, ct), ct);

    public Task SaveSnippetAsync(string groupId, LibrarySnippet snippet, CancellationToken ct) =>
        WriteAsync(r => r.SaveSnippetAsync(groupId, snippet, ct), ct);

    public Task DeleteSnippetAsync(string snippetId, CancellationToken ct) => WriteAsync(r => r.DeleteSnippetAsync(snippetId, ct), ct);

    private async Task WriteAsync(Func<ILibraryRepository, Task> write, CancellationToken ct)
    {
        LibraryGroup root;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await write(_repository).ConfigureAwait(false);
            root = await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(root);
    }

    public void Dispose() => _gate.Dispose();
}
