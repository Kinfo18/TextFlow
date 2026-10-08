using TextFlow.Core.Expansion;

namespace TextFlow.Core.Library;

public enum GroupDraftError
{
    NameRequired,

    /// <summary>Longer than the matcher buffer: the group menu could never be opened by typing.</summary>
    AbbreviationTooLong,
}

/// <summary>
/// The group dialog's form (H3.4) as plain values. <see cref="IgnoreCase"/> is inherited by the group's snippets and
/// its menu ("lc" opens "LC"); off, only the exact case works.
/// </summary>
public sealed record GroupDraft(string Id, string ParentId, string Name, string AbbreviationText, bool IgnoreCase)
{
    public static int MaxAbbreviationLength { get; } = TriggerOptions.Default.MaxBufferLength - 1;

    public static GroupDraft New(string parentId) => new($"tf-g-{Guid.NewGuid():N}", parentId, string.Empty, string.Empty, IgnoreCase: true);

    public static GroupDraft From(LibraryGroup group, string parentId)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new GroupDraft(group.Id, parentId, group.Name, group.Abbreviation ?? string.Empty, group.IgnoreCase);
    }

    public (GroupInfo? Info, IReadOnlyList<GroupDraftError> Errors) ToGroupInfo()
    {
        var name = Name.Trim();
        var abbreviation = AbbreviationText.Trim();
        var errors = new List<GroupDraftError>();

        if (name.Length == 0)
        {
            errors.Add(GroupDraftError.NameRequired);
        }

        if (abbreviation.Length > MaxAbbreviationLength)
        {
            errors.Add(GroupDraftError.AbbreviationTooLong);
        }

        return errors.Count > 0
            ? (null, errors)
            : (new GroupInfo(Id, ParentId, name, abbreviation.Length == 0 ? null : abbreviation, IgnoreCase), errors);
    }
}
