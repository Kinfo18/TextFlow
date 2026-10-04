using System.ComponentModel;
using System.Diagnostics;

namespace TextFlow.App.Pages;

/// <summary>An app with a visible window, offered when adding an exclusion. Never exposes window titles.</summary>
/// <param name="Process">File name, e.g. "chrome.exe".</param>
/// <param name="Description">Product description ("Google Chrome") or the process name when Windows does not tell.</param>
internal sealed record RunningApp(string Process, string Description)
{
    /// <summary>Apps with a main window, without TextFlow itself, one entry per executable, sorted by description.</summary>
    public static IReadOnlyList<RunningApp> List()
    {
        var self = Environment.ProcessId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self || !HasWindow(process))
                {
                    continue;
                }

                var name = process.ProcessName + ".exe";
                apps.TryAdd(name, new RunningApp(name, DescriptionOf(process) ?? name));
            }
        }

        return [.. apps.Values.OrderBy(a => a.Description, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static bool HasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != 0;
        }
        catch (InvalidOperationException)
        {
            return false; // exited meanwhile
        }
    }

    private static string? DescriptionOf(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // elevated or protected process: the name is enough
        }
    }
}
