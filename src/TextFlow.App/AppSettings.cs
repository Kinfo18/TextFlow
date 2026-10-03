using System.Text.Json;

namespace TextFlow.App;

/// <summary>
/// Provisional settings file (<c>settings.json</c>) until SQLite <c>AppSetting</c> arrives in H2.
/// Holds paths and preferences only, never snippet content.
/// </summary>
/// <param name="ATextBackupPath">aText backup loaded as the library (H1.5, provisional until the importer in H2).</param>
/// <param name="ChimeVolume">0-1.</param>
/// <param name="StartWithWindows">Kept in sync with the HKCU Run entry at every start (H1.3).</param>
/// <param name="PauseHotkey">Global pause/resume shortcut, e.g. "Ctrl+Shift+Alt+P" (H1.4); null or invalid uses the default.</param>
public sealed record AppSettings(
    string? ATextBackupPath = null,
    double ChimeVolume = 1.0,
    bool StartWithWindows = true,
    string? PauseHotkey = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Defaults when the file is missing; a corrupt file is reported, not silently replaced.</summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"settings.json no es válido: {ex.Message}", ex);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
