using System.Text.RegularExpressions;

namespace TextFlow.Core.Security;

/// <summary>
/// A password field whose "show password" toggle is on becomes a plain text box (UIA IsPassword = false). Its label
/// or id still says what it is, so those are checked too. Inputs are field labels, never field values; the result
/// is used in memory only and nothing here is logged.
/// </summary>
public static partial class SensitiveFieldHeuristic
{
    public static bool LooksLikePassword(string? name, string? automationId) =>
        Matches(name) || Matches(automationId);

    private static bool Matches(string? text) => !string.IsNullOrWhiteSpace(text) && PasswordWords().IsMatch(text);

    // Substrings for unambiguous words (they also hit camelCase ids such as "loginPassword"); whole words for short
    // ones that would otherwise match unrelated text ("pin" in "Spinner").
    [GeneratedRegex(
        @"pass(word|wd|code|phrase)|contrase(ñ|n)a|\bpwd|pwd\b|_pwd|\bclave\b|\bpin\b|mot de passe|\bsenha\b|kennwort",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex PasswordWords();
}
