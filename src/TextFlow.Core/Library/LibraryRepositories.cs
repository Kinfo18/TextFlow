using TextFlow.Core.Security;

namespace TextFlow.Core.Library;

/// <summary>A group's own fields (not its children), for creating or editing one group.</summary>
/// <param name="ParentId">Null only for the library root.</param>
public sealed record GroupInfo(string Id, string? ParentId, string Name, string? Abbreviation, bool IgnoreCase);

/// <summary>The user's snippet library (spec §19): one tree with a single root group.</summary>
public interface ILibraryRepository
{
    /// <summary>The whole tree; an empty root when nothing was stored yet.</summary>
    Task<LibraryGroup> LoadAsync(CancellationToken ct);

    /// <summary>Replaces the whole library in one transaction (imports). Nothing changes if it fails.</summary>
    Task ReplaceAllAsync(LibraryGroup root, CancellationToken ct);

    /// <summary>Creates or updates a group's own fields; a new group goes last among its siblings.</summary>
    Task SaveGroupAsync(GroupInfo group, CancellationToken ct);

    /// <summary>Deletes a group with all its subgroups and snippets.</summary>
    Task DeleteGroupAsync(string groupId, CancellationToken ct);

    /// <summary>Creates or updates a snippet (content, abbreviations, mode, enabled) inside <paramref name="groupId"/>.</summary>
    Task SaveSnippetAsync(string groupId, LibrarySnippet snippet, CancellationToken ct);

    Task DeleteSnippetAsync(string snippetId, CancellationToken ct);
}

/// <summary>User exclusion rules (spec §16); built-in ones live in code, not here.</summary>
public interface IExclusionRuleRepository
{
    Task<IReadOnlyList<ExclusionRule>> GetAllAsync(CancellationToken ct);

    Task SaveAsync(ExclusionRule rule, CancellationToken ct);

    Task DeleteAsync(string ruleId, CancellationToken ct);
}

/// <summary>Preferences as JSON values by key (spec §19 AppSetting). Never snippet content or typed text.</summary>
public interface ISettingsRepository
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);

    Task SetAsync<T>(string key, T value, CancellationToken ct);
}
