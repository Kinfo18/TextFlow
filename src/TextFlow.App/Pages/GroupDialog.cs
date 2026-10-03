using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>Create or edit a group (H3.4): name, abbreviation that opens its menu, and "ignore case".</summary>
internal static class GroupDialog
{
    /// <returns>The validated group, or null when cancelled.</returns>
    public static async Task<GroupInfo?> EditAsync(XamlRoot root, GroupDraft draft, bool isNew)
    {
        var name = new TextBox { Header = "Nombre", Text = draft.Name, PlaceholderText = "Por ejemplo: Local cerrado" };
        var abbreviation = new TextBox
        {
            Header = "Abreviatura que abre su menú (opcional)",
            Text = draft.AbbreviationText,
            PlaceholderText = "Por ejemplo: LC",
        };
        var ignoreCase = new ToggleSwitch
        {
            Header = "Ignorar mayúsculas",
            IsOn = draft.IgnoreCase,
            OnContent = "lc también abre LC (y vale para sus comandos)",
            OffContent = "Solo con las mayúsculas exactas",
        };
        var error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 380 };
        panel.Children.Add(name);
        panel.Children.Add(abbreviation);
        panel.Children.Add(ignoreCase);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = isNew ? "Nuevo grupo" : "Editar grupo",
            Content = panel,
            PrimaryButtonText = isNew ? "Crear" : "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        GroupInfo? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var (info, errors) = (draft with { Name = name.Text, AbbreviationText = abbreviation.Text, IgnoreCase = ignoreCase.IsOn }).ToGroupInfo();
            if (info is null)
            {
                error.Text = string.Join(" ", errors.Select(Describe));
                args.Cancel = true; // keep the dialog open with the reason
                return;
            }

            result = info;
        };

        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    private static string Describe(GroupDraftError error) => error switch
    {
        GroupDraftError.NameRequired => "El grupo necesita un nombre.",
        GroupDraftError.AbbreviationTooLong => "La abreviatura es demasiado larga (máximo 63 caracteres).",
        _ => "No se puede guardar.",
    };
}
