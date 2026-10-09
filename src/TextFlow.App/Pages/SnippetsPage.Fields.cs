using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.Core.Templates;

namespace TextFlow.App.Pages;

/// <summary>"Insertar" next to the content box (H5.2 part 3): fields and variables at the text cursor.</summary>
public sealed partial class SnippetsPage
{
    private void OnInsertToken(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string token })
        {
            InsertAtCaret(token);
        }
    }

    private async void OnInsertField(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Nombre del dato", PlaceholderText = "Por ejemplo: cliente, monto, numero_pedido" };
        var error = new TextBlock
        {
            Foreground = ThemeBrushes.Get("SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var content = new StackPanel { Spacing = 8, MinWidth = 320 };
        content.Children.Add(name);
        content.Children.Add(new TextBlock
        {
            Text = "Al expandir, el recuadro te pedirá este dato y lo tomará de lo que copies. Usa el mismo nombre si el dato se repite en el texto.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Campo para rellenar",
            Content = content,
            PrimaryButtonText = "Insertar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) =>
        {
            var text = name.Text.Trim();
            var valid = TemplateParser.IsValidFieldName(text);
            dialog.IsPrimaryButtonEnabled = valid;
            error.Text = text.Length == 0 || valid ? string.Empty : "Usa letras, números o _ (sin espacios), y no date, time ni cursor.";
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            InsertAtCaret("{{" + name.Text.Trim() + "}}");
        }
    }

    /// <summary>Replaces the selection (or inserts at the caret) and leaves the caret after the token.</summary>
    private void InsertAtCaret(string token)
    {
        var start = Math.Clamp(ContentBox.SelectionStart, 0, ContentBox.Text.Length);
        var length = Math.Clamp(ContentBox.SelectionLength, 0, ContentBox.Text.Length - start);
        ContentBox.Text = ContentBox.Text.Remove(start, length).Insert(start, token); // TextChanged marks the draft dirty
        ContentBox.Focus(FocusState.Programmatic);
        ContentBox.Select(start + token.Length, 0);
    }
}
