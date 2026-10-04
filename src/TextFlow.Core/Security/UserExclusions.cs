namespace TextFlow.Core.Security;

/// <summary>Apps the user excluded in Configuración (H4.3): whole processes, every feature.</summary>
public static class UserExclusions
{
    /// <summary>Accepts "chrome", "chrome.exe" or a full path; returns the file name with ".exe" when it had no extension.</summary>
    public static bool TryNormalize(string? input, out string process)
    {
        process = string.Empty;
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        var name = Path.GetFileName(trimmed);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        process = Path.HasExtension(name) ? name : name + ".exe";
        return true;
    }

    public static IReadOnlyList<ExclusionRule> Rules(IEnumerable<string> processes) =>
        processes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ExclusionRule($"user:process:{p}", ExclusionMatchType.Process, p, FeatureScope.All))
            .ToArray();
}
