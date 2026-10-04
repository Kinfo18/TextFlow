using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TextFlow.Core.Security;
using Windows.System;

namespace TextFlow.App.Pages;

/// <summary>Per-app exclusions (H4.3): the user's list plus the built-in rules shown read-only.</summary>
public sealed partial class SettingsPage
{
    private static readonly string DeleteGlyph = ((char)0xE74D).ToString();

    private void LoadExclusions()
    {
        BuiltinList.Text = string.Join("  ·  ", BuiltinExclusions.All
            .Where(r => r.Type == ExclusionMatchType.Process)
            .Select(r => r.Pattern)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        RenderExclusions();
    }

    private void RenderExclusions()
    {
        ExclusionList.Children.Clear();
        foreach (var process in App.Current.ExcludedProcesses)
        {
            ExclusionList.Children.Add(CreateExclusionRow(process));
        }

        NoExclusions.Visibility = App.Current.ExcludedProcesses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid CreateExclusionRow(string process)
    {
        var row = new Grid
        {
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        row.Children.Add(new TextBlock { Text = process, VerticalAlignment = VerticalAlignment.Center });

        var remove = new Button { Content = new FontIcon { Glyph = DeleteGlyph, FontSize = 12 }, Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(remove, $"Quitar {process}");
        AutomationProperties.SetName(remove, $"Quitar {process}");
        remove.Click += (_, _) => RemoveExclusion(process);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return row;
    }

    private void OnAddExclusion(object sender, RoutedEventArgs e) => AddExclusion(ExclusionInput.Text);

    private void OnExclusionInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            AddExclusion(ExclusionInput.Text);
        }
    }

    private void OnRunningAppsOpening(object sender, object e)
    {
        RunningAppsFlyout.Items.Clear();
        var excluded = App.Current.ExcludedProcesses;
        var apps = RunningApp.List().Where(a => !excluded.Contains(a.Process, StringComparer.OrdinalIgnoreCase)).ToList();
        if (apps.Count == 0)
        {
            RunningAppsFlyout.Items.Add(new MenuFlyoutItem { Text = "No hay otras apps abiertas", IsEnabled = false });
            return;
        }

        foreach (var app in apps)
        {
            var sameName = string.Equals(app.Description, app.Process, StringComparison.OrdinalIgnoreCase);
            var item = new MenuFlyoutItem { Text = sameName ? app.Process : $"{app.Description}  ({app.Process})" };
            item.Click += (_, _) => AddExclusion(app.Process);
            RunningAppsFlyout.Items.Add(item);
        }
    }

    private void AddExclusion(string input)
    {
        if (!UserExclusions.TryNormalize(input, out var process))
        {
            ShowExclusionStatus("Escribe el nombre del programa, por ejemplo chrome.exe.");
            return;
        }

        var current = App.Current.ExcludedProcesses;
        if (current.Contains(process, StringComparer.OrdinalIgnoreCase))
        {
            ShowExclusionStatus($"{process} ya está en la lista.");
            return;
        }

        App.Current.SetExcludedProcesses([.. current, process]);
        ExclusionInput.Text = string.Empty;
        ExclusionStatus.Visibility = Visibility.Collapsed;
        RenderExclusions();
    }

    private void RemoveExclusion(string process)
    {
        App.Current.SetExcludedProcesses(
            [.. App.Current.ExcludedProcesses.Where(p => !string.Equals(p, process, StringComparison.OrdinalIgnoreCase))]);
        RenderExclusions();
    }

    private void ShowExclusionStatus(string text)
    {
        ExclusionStatus.Text = text;
        ExclusionStatus.Visibility = Visibility.Visible;
    }
}
