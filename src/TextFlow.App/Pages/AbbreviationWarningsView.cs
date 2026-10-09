using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>Shows the abbreviation advice (H3.5) under the field being typed: one line per warning, never blocking.</summary>
internal static class AbbreviationWarningsView
{
    private const int MaxShown = 6;

    public static void Render(Panel target, IReadOnlyList<AbbreviationWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Children.Clear();
        var lines = Lines(warnings).ToArray();
        foreach (var line in lines.Take(MaxShown))
        {
            target.Children.Add(Line(line));
        }

        if (lines.Length > MaxShown)
        {
            target.Children.Add(Line($"Y {lines.Length - MaxShown} avisos más."));
        }

        target.Visibility = lines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A short abbreviation can wait for dozens of longer ones ("de" → devo1, depo1…): one line each would be noise.</summary>
    private static IEnumerable<string> Lines(IReadOnlyList<AbbreviationWarning> warnings)
    {
        foreach (var warning in warnings.Where(w => w.Kind != AbbreviationWarningKind.WaitsForLonger))
        {
            yield return Describe(warning);
        }

        foreach (var waits in warnings.Where(w => w.Kind == AbbreviationWarningKind.WaitsForLonger).GroupBy(w => w.Abbreviation))
        {
            var longer = waits.Select(w => w.OtherAbbreviation).Distinct().ToArray();
            yield return longer.Length == 1
                ? Describe(waits.First())
                : $"«{waits.Key}» esperará 0,6 s porque {longer.Length} abreviaturas empiezan igual ({string.Join(", ", longer.Take(3))}{(longer.Length > 3 ? "…" : string.Empty)}).";
        }
    }

    private static StackPanel Line(string text)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA", // Warning
            FontSize = 12,
            Foreground = ThemeBrushes.Get("CautionTextBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        line.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        return line;
    }

    private static string Describe(AbbreviationWarning w) => w.Kind switch
    {
        AbbreviationWarningKind.UsedByCommand => $"«{w.Abbreviation}» ya la usa «{w.Other}»: solo se expandirá uno de los dos.",
        AbbreviationWarningKind.UsedByMenu => $"«{w.Abbreviation}» abre el menú «{w.Other}»: el menú gana y esto no se expandirá.",
        AbbreviationWarningKind.WaitsForLonger => $"«{w.Abbreviation}» esperará 0,6 s porque «{w.OtherAbbreviation}» empieza igual.",
        AbbreviationWarningKind.MakesShorterWait => $"«{w.OtherAbbreviation}» esperará 0,6 s desde ahora porque «{w.Abbreviation}» empieza igual.",
        AbbreviationWarningKind.CommonWord =>
            $"«{w.Abbreviation}» es una palabra común: saltaría al escribirla en un texto normal. Usa otra o «Tras un espacio o signo».",
        _ => w.Abbreviation,
    };
}
