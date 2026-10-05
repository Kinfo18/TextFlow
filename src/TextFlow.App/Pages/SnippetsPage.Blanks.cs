using Microsoft.UI.Xaml;
using TextFlow.Core.Templates;

namespace TextFlow.App.Pages;

/// <summary>Hint and action to turn hand-filled XXX blanks into template fields (H5.2 part 2).</summary>
public sealed partial class SnippetsPage
{
    private bool _blankHintDismissed;

    private void UpdateBlankHint()
    {
        var count = BlankConversion.Candidates(Library).Count;
        BlankHint.Title = count == 1 ? "1 comando usa XXX para rellenar a mano" : $"{count} comandos usan XXX para rellenar a mano";
        BlankHint.IsOpen = count > 0 && !_blankHintDismissed;
        BlankHint.CloseButtonClick -= OnBlankHintClosed;
        BlankHint.CloseButtonClick += OnBlankHintClosed;
    }

    private void OnBlankHintClosed(Microsoft.UI.Xaml.Controls.InfoBar sender, object args) => _blankHintDismissed = true;

    private async void OnConvertBlanks(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmLeaveAsync())
            {
                return; // the editor keeps the user's unsaved changes
            }

            var candidates = BlankConversion.Candidates(Library);
            var converted = await BlankConversionDialog.ShowAsync(XamlRoot, candidates);
            if (converted is null || converted.Count == 0 || App.Current.Library is not { } library)
            {
                return;
            }

            await library.CreateBackupAsync(CancellationToken.None);
            var saved = await App.Current.EditLibraryAsync(XamlRoot, async service =>
            {
                foreach (var item in converted)
                {
                    await service.SaveSnippetAsync(item.GroupId, item.Snippet, CancellationToken.None);
                }
            });

            if (saved)
            {
                Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            App.Current.RecordFault(ex); // nothing half-saved goes unnoticed: the backup is there to restore
            EditorMessage.Text = "No se pudo convertir: " + ex.Message;
        }
    }
}
