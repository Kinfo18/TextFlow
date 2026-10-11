namespace TextFlow.Core.Templates;

/// <summary>
/// A value copied to go inside an expansion. A double click on a web page also takes the space after the word, and a
/// copied cell or line brings its line break: none of that belongs in the middle of a sentence (2026-10-11).
/// </summary>
public static class CopiedText
{
    /// <summary>Removes spaces, tabs, line breaks and invisible zero-width marks from both ends; the inside is kept.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = 0;
        var end = text.Length;
        while (start < end && IsPadding(text[start]))
        {
            start++;
        }

        while (end > start && IsPadding(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary>White space (including the non-breaking space web pages use) and zero-width characters.</summary>
    private static bool IsPadding(char c) =>
        char.IsWhiteSpace(c) || c is '​' or '‌' or '‍' or '⁠' or '﻿';
}
