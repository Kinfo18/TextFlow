using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextFlow.App;

/// <summary>
/// Provisional settings file (<c>settings.json</c>) until SQLite <c>AppSetting</c> arrives in H2.
/// Holds paths and preferences only, never snippet content.
/// </summary>
/// <param name="ATextBackupPath">aText backup loaded as the library (H1.5, provisional until the importer in H2).</param>
/// <param name="ChimeVolume">0-1.</param>
/// <param name="SoundEnabled">Expansion chime on/off (H4.2); the volume is kept while it is off.</param>
/// <param name="PrefixTimeoutMs">Wait for an ambiguous trigger such as "dir1" while "dir12" exists (H4.3).</param>
/// <param name="ExcludedProcesses">Apps where TextFlow never expands, e.g. "chrome.exe" (H4.3); null means none.</param>
/// <param name="StartWithWindows">Kept in sync with the HKCU Run entry at every start (H1.3).</param>
/// <param name="PauseHotkey">Global pause/resume shortcut, e.g. "Ctrl+Shift+Alt+P" (H1.4); null or invalid uses the default.</param>
/// <param name="Theme">Window theme (H3.1).</param>
public sealed record AppSettings(
    string? ATextBackupPath = null,
    double ChimeVolume = 1.0,
    bool StartWithWindows = true,
    string? PauseHotkey = null,
    AppTheme Theme = AppTheme.System,
    bool SoundEnabled = true,
    int PrefixTimeoutMs = 600,
    IReadOnlyList<string>? ExcludedProcesses = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

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

/// <summary>Order matches the radio buttons in Configuración.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}
