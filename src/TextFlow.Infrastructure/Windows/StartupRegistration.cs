using Microsoft.Win32;

namespace TextFlow.Infrastructure.Windows;

/// <summary>
/// Start with Windows through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (ADR-0004: unpackaged,
/// per user, no admin). Starts with <c>--background</c> so logon shows only the tray icon.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    private readonly string _valueName;
    private readonly string _keyPath;

    /// <param name="keyPath">HKCU-relative key; tests pass a throwaway one.</param>
    public StartupRegistration(string valueName, string executablePath, string keyPath = RunKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _valueName = valueName;
        _keyPath = keyPath;
        Command = $"\"{executablePath}\" {BackgroundArgument}";
    }

    public string Command { get; }

    /// <summary>True only if the registered command starts this executable (a moved app counts as not registered).</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return string.Equals(key?.GetValue(_valueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Registers (or repairs the path of) this executable, or removes the entry.</summary>
    public void Apply(bool enabled)
    {
        if (enabled)
        {
            if (!IsEnabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
                key.SetValue(_valueName, Command, RegistryValueKind.String);
            }

            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        existing?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
