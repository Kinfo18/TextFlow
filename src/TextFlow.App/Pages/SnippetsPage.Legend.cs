using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace TextFlow.App.Pages;

/// <summary>"¿Qué hace cada una?": what each field and variable of the editor's Insertar menu does when it expands.</summary>
public sealed partial class SnippetsPage
{
    private const double LegendWidth = 400;

    private void OnShowVariableHelp(object sender, RoutedEventArgs e)
    {
        var palette = App.Current.PaletteHotkey?.Gesture ?? "el atajo de la paleta";
        var rows = new StackPanel { Spacing = 12, Width = LegendWidth };
        rows.Children.Add(new TextBlock { Text = "Qué se pone al expandir", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        rows.Children.Add(LegendRow("{{cliente}}",
            "Campo para rellenar: al expandir, se llena con lo que copies (un campo por dato, con el nombre que quieras). "
            + "Tras la última copia, el texto se inserta donde escribiste. Con {{cliente=valor}} propone un valor."));
        rows.Children.Add(LegendRow("{{date}}", "La fecha de hoy. Con otro formato: {{date:dd/MM/yyyy}} o {{date:yyyy}}."));
        rows.Children.Add(LegendRow("{{time}}", "La hora actual. Con otro formato: {{time:HH:mm:ss}}."));
        rows.Children.Add(LegendRow("{{clipboard}}", "Lo que tengas copiado en el momento de expandir."));
        rows.Children.Add(LegendRow("{{selection}}",
            $"El texto que tenías seleccionado al abrir la paleta ({palette}); el resultado lo reemplaza, "
            + "así «{{selection}}» lo pone entre comillas. Al escribir la abreviatura no hay selección y queda vacío."));
        rows.Children.Add(LegendRow("{{cursor}}",
            "Dónde queda el cursor después de insertar. Se ignora si el comando se envía con Enter."));

        var flyout = new Flyout { Content = rows, Placement = FlyoutPlacementMode.Bottom };
        flyout.ShowAt((FrameworkElement)sender);
    }

    private static Grid LegendRow(string token, string description)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemeBrushes.Get("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = token,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = ThemeBrushes.Get("AccentTextFillColorPrimaryBrush"),
                IsTextSelectionEnabled = true,
            },
        };
        var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(chip);
        grid.Children.Add(text);
        return grid;
    }
}
