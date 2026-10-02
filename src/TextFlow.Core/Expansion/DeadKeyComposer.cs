using System.Text;

namespace TextFlow.Core.Expansion;

/// <summary>
/// Composes dead key + next character ourselves, so the hook can translate keys with
/// ToUnicodeEx "don't change keyboard state" and never steal the target app's dead-key state.
/// </summary>
public static class DeadKeyComposer
{
    private static readonly Dictionary<char, char> CombiningMarks = new()
    {
        ['´'] = '́',
        ['\''] = '́',
        ['`'] = '̀',
        ['^'] = '̂',
        ['¨'] = '̈',
        ['~'] = '̃',
    };

    public static string Compose(char deadKey, char next)
    {
        if (next == ' ')
        {
            return deadKey.ToString();
        }

        if (CombiningMarks.TryGetValue(deadKey, out var mark))
        {
            var composed = string.Concat(next, mark).Normalize(NormalizationForm.FormC);
            if (composed.Length == 1)
            {
                return composed;
            }
        }

        return string.Concat(deadKey, next);
    }
}
