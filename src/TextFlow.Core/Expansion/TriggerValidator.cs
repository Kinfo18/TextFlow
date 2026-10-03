namespace TextFlow.Core.Expansion;

public enum TriggerIssueSeverity
{
    Warning,
    Error,
}

public enum TriggerIssueCode
{
    Empty,
    TooShort,
    ContainsDelimiter,
    Duplicate,
    PlainWord,
    Overlap,
}

public sealed record TriggerIssue(TriggerIssueCode Code, TriggerIssueSeverity Severity, string? ConflictingSnippetId = null);

/// <summary>Checks a trigger before saving (spec §10 "Colisiones").</summary>
public static class TriggerValidator
{
    private const int MinimumLength = 2;

    public static IReadOnlyList<TriggerIssue> Validate(
        TriggerDefinition candidate,
        IReadOnlyCollection<TriggerDefinition> existing,
        TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);

        var trigger = candidate.Trigger;
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return [new TriggerIssue(TriggerIssueCode.Empty, TriggerIssueSeverity.Error)];
        }

        var issues = new List<TriggerIssue>();

        if (trigger.Length < MinimumLength)
        {
            issues.Add(new TriggerIssue(TriggerIssueCode.TooShort, TriggerIssueSeverity.Error));
        }

        // Immediate triggers may contain spaces ("Foto valida", as in aText); AfterDelimiter ones cannot.
        if (candidate.Mode == TriggerMode.AfterDelimiter && trigger.Any(c => char.IsWhiteSpace(c) || options.Delimiters.Contains(c)))
        {
            issues.Add(new TriggerIssue(TriggerIssueCode.ContainsDelimiter, TriggerIssueSeverity.Error));
        }

        if (trigger.All(char.IsLetter))
        {
            issues.Add(new TriggerIssue(TriggerIssueCode.PlainWord, TriggerIssueSeverity.Warning));
        }

        foreach (var other in existing.Where(e => e.SnippetId != candidate.SnippetId))
        {
            var comparison = candidate.IgnoreCase || other.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(other.Trigger, trigger, comparison))
            {
                issues.Add(new TriggerIssue(TriggerIssueCode.Duplicate, TriggerIssueSeverity.Error, other.SnippetId));
            }
            else if (other.Trigger.EndsWith(trigger, comparison)
                     || trigger.EndsWith(other.Trigger, comparison)
                     || ShadowsByPrefix(candidate, other, comparison)
                     || ShadowsByPrefix(other, candidate, comparison))
            {
                issues.Add(new TriggerIssue(TriggerIssueCode.Overlap, TriggerIssueSeverity.Warning, other.SnippetId));
            }
        }

        return issues;
    }

    /// <summary>An immediate trigger that starts a longer one must wait (pending) before firing: slower, worth a warning.</summary>
    private static bool ShadowsByPrefix(TriggerDefinition shorter, TriggerDefinition longer, StringComparison comparison) =>
        shorter.Mode == TriggerMode.Immediate
        && longer.Trigger.Length > shorter.Trigger.Length
        && longer.Trigger.StartsWith(shorter.Trigger, comparison);
}
