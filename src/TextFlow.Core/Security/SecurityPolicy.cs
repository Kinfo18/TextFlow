using System.Text.RegularExpressions;
using TextFlow.Contracts.Targeting;

namespace TextFlow.Core.Security;

public enum TextFlowFeature
{
    Expansion,
    Dictation,
    Correction,
}

[Flags]
public enum FeatureScope
{
    None = 0,
    Expansion = 1,
    Dictation = 2,
    Correction = 4,
    All = Expansion | Dictation | Correction,
}

public enum ExclusionMatchType
{
    Process,
    WindowClass,
    WindowTitle,
}

/// <param name="Pattern">Case-insensitive wildcard: <c>*</c> any run, <c>?</c> one character.</param>
public sealed record ExclusionRule(string Id, ExclusionMatchType Type, string Pattern, FeatureScope Scope, bool Enabled = true);

public enum PolicyReason
{
    None,
    PasswordField,
    ElevatedTarget,
    ExclusionRule,
}

/// <summary>Content-free decision; safe to log (never includes the window title).</summary>
public sealed record PolicyDecision(bool IsAllowed, PolicyReason Reason, string? RuleId)
{
    public static PolicyDecision Allowed { get; } = new(true, PolicyReason.None, null);
}

/// <summary>Evaluated before any capture or insertion starts (spec §28.5).</summary>
public sealed class SecurityPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private readonly (ExclusionRule Rule, Regex Matcher)[] _rules;

    public SecurityPolicy(IEnumerable<ExclusionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules
            .Where(r => r.Enabled)
            .Select(r => (r, WildcardToRegex(r.Pattern)))
            .ToArray();
    }

    public PolicyDecision Evaluate(ActiveTarget target, TextFlowFeature feature)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Control.IsPassword)
        {
            return new PolicyDecision(false, PolicyReason.PasswordField, null);
        }

        if (target.IsElevated)
        {
            return new PolicyDecision(false, PolicyReason.ElevatedTarget, null);
        }

        var scope = ToScope(feature);
        foreach (var (rule, matcher) in _rules)
        {
            if ((rule.Scope & scope) != 0 && matcher.IsMatch(SelectValue(target, rule.Type)))
            {
                return new PolicyDecision(false, PolicyReason.ExclusionRule, rule.Id);
            }
        }

        return PolicyDecision.Allowed;
    }

    private static string SelectValue(ActiveTarget target, ExclusionMatchType type) => type switch
    {
        ExclusionMatchType.Process => target.ProcessName,
        ExclusionMatchType.WindowClass => target.WindowClass,
        ExclusionMatchType.WindowTitle => target.WindowTitle,
        _ => string.Empty,
    };

    private static FeatureScope ToScope(TextFlowFeature feature) => feature switch
    {
        TextFlowFeature.Expansion => FeatureScope.Expansion,
        TextFlowFeature.Dictation => FeatureScope.Dictation,
        TextFlowFeature.Correction => FeatureScope.Correction,
        _ => FeatureScope.None,
    };

    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }
}
