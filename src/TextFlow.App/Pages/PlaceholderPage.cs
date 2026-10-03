using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TextFlow.App.Pages;

/// <summary>Title and a line of text for sections that are still to be built.</summary>
public partial class PlaceholderPage : Page
{
    protected PlaceholderPage(string title, string body)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        Content = new ScrollViewer { Padding = new Thickness(36, 24, 36, 24), Content = panel };
    }
}
