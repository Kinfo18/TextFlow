namespace TextFlow.App;

/// <summary>Per-user locations. Everything lives under %LOCALAPPDATA%\TextFlow (never roaming: no sync of content).</summary>
public sealed record AppPaths(string Root)
{
    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFlow"));

    public string Logs => Path.Combine(Root, "logs");

    public string Database => Path.Combine(Root, "textflow.db");

    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Database snapshots taken before each import (H2.5 restores them).</summary>
    public string Backups => Path.Combine(Root, "backups");
}
