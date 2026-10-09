using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;
using TextFlow.Core.Templates;

namespace TextFlow.App.Pages;

/// <summary>A snippet rewritten by the conversion, ready to save in its group.</summary>
internal sealed record ConvertedSnippet(string GroupId, LibrarySnippet Snippet);

/// <summary>
/// "Convertir XXX en campos" (H5.2 part 2): lists every snippet that still has <c>XXX</c> blanks, shows where each
/// blank is and lets the user name it. Same name = same value, empty = keep that blank.
/// </summary>
internal static class BlankConversionDialog
{
    private const int PreviewLength = 320;

    /// <returns>The snippets to save, or null when cancelled.</returns>
    public static async Task<IReadOnlyList<ConvertedSnippet>?> ShowAsync(XamlRoot root, IReadOnlyList<BlankCandidate> candidates)
    {
        var editors = candidates.Select(c => new CandidateEditor(c)).ToList();
        var list = new StackPanel { Spacing = 12 };
        list.Children.Add(new TextBlock
        {
            Text = "Ponle nombre a cada XXX, en el orden en que aparece: ese nombre es lo que verás en el recuadro al "
                + "expandir. Si un dato se repite (el mismo cliente dos veces), usa el mismo nombre. Deja vacío lo que no "
                + "quieras convertir. Antes de guardar se hace una copia de seguridad.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var editor in editors)
        {
            list.Children.Add(editor.View);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Convertir XXX en campos ({candidates.Count} comandos)",
            Content = new ScrollViewer { Content = list, MaxHeight = 520, Padding = new Thickness(0, 0, 12, 0) },
            PrimaryButtonText = "Convertir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Validate() => dialog.IsPrimaryButtonEnabled = editors.All(e => e.IsValid) && editors.Any(e => e.Converts);
        foreach (var editor in editors)
        {
            editor.Changed += Validate;
        }

        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return [.. editors.Where(e => e.Converts).Select(e => e.Result())];
    }

    private sealed class CandidateEditor
    {
        private readonly BlankCandidate _candidate;
        private readonly List<TextBox> _names = [];
        private readonly TextBlock _error;

        public CandidateEditor(BlankCandidate candidate)
        {
            _candidate = candidate;
            var snippet = candidate.Snippet;
            var card = new StackPanel { Spacing = 6, Padding = new Thickness(12) };
            View = new Border
            {
                Child = card,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrushes.Get("CardStrokeColorDefaultBrush"),
                Background = ThemeBrushes.Get("CardBackgroundFillColorDefaultBrush"),
            };

            card.Children.Add(new TextBlock
            {
                Text = snippet.TypeableAbbreviations is [var first, ..] ? first : snippet.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            card.Children.Add(Preview(snippet.Content));

            var names = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            for (var i = 0; i < candidate.Blanks; i++)
            {
                var box = new TextBox
                {
                    Header = $"XXX {i + 1}",
                    Width = 140,
                    PlaceholderText = "sin convertir",
                    Text = candidate.Blanks == 1 ? "cliente" : string.Empty, // one blank is the client's name for this user
                };
                box.TextChanged += (_, _) => OnChanged();
                _names.Add(box);
                names.Children.Add(box);
            }

            card.Children.Add(new ScrollViewer
            {
                Content = names,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            _error = new TextBlock
            {
                Foreground = ThemeBrushes.Get("SystemFillColorCriticalBrush"),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            card.Children.Add(_error);
        }

        public event Action? Changed;

        public Border View { get; }

        public bool IsValid => InvalidNames().Count == 0;

        public bool Converts => _names.Any(n => n.Text.Trim().Length > 0);

        public ConvertedSnippet Result()
        {
            var content = BlankConversion.Apply(_candidate.Snippet.Content, [.. _names.Select(n => n.Text)]);
            return new ConvertedSnippet(_candidate.GroupId, _candidate.Snippet with { Content = content });
        }

        private List<string> InvalidNames() =>
            [.. _names.Select(n => n.Text.Trim()).Where(n => n.Length > 0 && !TemplateParser.IsValidFieldName(n))];

        private void OnChanged()
        {
            var invalid = InvalidNames();
            _error.Text = invalid.Count == 0
                ? string.Empty
                : $"«{invalid[0]}» no sirve como nombre: usa letras, números o _ (sin espacios), y no date, time ni cursor.";
            _error.Visibility = invalid.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Changed?.Invoke();
        }

        /// <summary>The content with each blank shown as a numbered, highlighted marker, so the user knows which is which.</summary>
        private static TextBlock Preview(string content)
        {
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 13 };
            var shown = content.Length > PreviewLength ? content[..PreviewLength] + "…" : content;
            var parts = BlankConversion.Split(shown);
            for (var i = 0; i < parts.Length; i++)
            {
                preview.Inlines.Add(new Run { Text = parts[i] });
                if (i < parts.Length - 1)
                {
                    preview.Inlines.Add(new Run
                    {
                        Text = $" XXX {i + 1} ",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeBrushes.Get("AccentTextFillColorPrimaryBrush"),
                    });
                }
            }

            return preview;
        }
    }
}
