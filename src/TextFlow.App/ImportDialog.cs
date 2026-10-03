using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.Core.Import;

namespace TextFlow.App;

/// <summary>H2.3: the import report shown before an aText backup replaces the library. Counts only, no content.</summary>
internal static class ImportDialog
{
    /// <param name="stopsFollowing">aText file that will no longer be followed after this import, if any.</param>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string fileName, ImportPreview preview, string? stopsFollowing = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Importar «{fileName}»",
            Content = new ScrollViewer { Content = Report(preview, stopsFollowing), MaxHeight = 420 },
            PrimaryButtonText = preview.ReplacesExisting ? "Reemplazar biblioteca" : "Importar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static StackPanel Report(ImportPreview preview, string? stopsFollowing)
    {
        var incoming = preview.Incoming;
        var panel = new StackPanel { Spacing = 10, MaxWidth = 460 };
        panel.Children.Add(Heading("Contenido del backup"));
        panel.Children.Add(Line(
            $"{incoming.Menus} menús · {incoming.Commands} comandos · {incoming.DirectTriggers} abreviaturas directas · {preview.Groups} grupos"));

        if (preview.WaitingPrefixes > 0)
        {
            panel.Children.Add(Line(
                $"{preview.WaitingPrefixes} abreviaturas esperan 0,6 s antes de expandir porque otra más larga empieza igual (como dir → dir1)."));
        }

        var notes = new List<string>();
        if (preview.RichTextAsPlain > 0)
        {
            notes.Add($"{preview.RichTextAsPlain} con formato se importan como texto");
        }

        if (preview.DuplicateAbbreviations > 0)
        {
            notes.Add($"{preview.DuplicateAbbreviations} abreviaturas repetidas (gana la primera)");
        }

        if (preview.OtherIssues > 0)
        {
            notes.Add($"{preview.OtherIssues} avisos menores");
        }

        if (notes.Count > 0)
        {
            panel.Children.Add(Heading("Avisos"));
            panel.Children.Add(Line(Capitalize(string.Join("; ", notes)) + "."));
        }

        if (preview.ReplacesExisting)
        {
            panel.Children.Add(Heading("Frente a tu biblioteca actual"));
            panel.Children.Add(Line(preview is { Added: 0, Removed: 0, Changed: 0 }
                ? "Sin cambios: es la misma biblioteca."
                : $"{preview.Added} comandos nuevos · {preview.Changed} modificados · {preview.Removed} se eliminan."));
            panel.Children.Add(Line("Antes de importar se guarda una copia de la biblioteca actual.", subtle: true));
        }

        if (stopsFollowing is not null)
        {
            panel.Children.Add(Line(
                $"TextFlow dejará de importar los cambios de «{stopsFollowing}»: desde ahora esta copia es tu biblioteca.", subtle: true));
        }

        return panel;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private static TextBlock Line(string text, bool subtle = false) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = subtle ? 0.7 : 1 };

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
