namespace TextFlow.Core.Security;

/// <summary>High-risk surfaces excluded by default (spec §16). Users may disable them per rule.</summary>
public static class BuiltinExclusions
{
    private static readonly string[] Terminals =
    [
        "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe",
        "powershell.exe", "pwsh.exe", "mintty.exe", "wsl.exe", "bash.exe",
    ];

    private static readonly string[] PasswordManagers =
    [
        "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "Dashlane.exe", "NordPass.exe",
    ];

    private static readonly string[] SecureSurfaces =
    [
        "consent.exe", "LogonUI.exe", "CredentialUIBroker.exe", "LockApp.exe",
    ];

    private static readonly string[] ConsoleClasses =
    [
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow",
    ];

    public static IReadOnlyList<ExclusionRule> All { get; } =
    [
        .. Terminals.Select(p => ProcessRule("terminal", p)),
        .. PasswordManagers.Select(p => ProcessRule("password-manager", p)),
        .. SecureSurfaces.Select(p => ProcessRule("secure", p)),
        .. ConsoleClasses.Select(c => new ExclusionRule($"builtin:console-class:{c}", ExclusionMatchType.WindowClass, c, FeatureScope.All)),
    ];

    /// <summary>
    /// <see cref="All"/> plus TextFlow's own process: its windows (library editor, settings) must never be captured
    /// or expanded into, and inspecting them with UI Automation from inside the process stalls its UI thread.
    /// </summary>
    public static IReadOnlyList<ExclusionRule> For(string selfProcessName) => [.. All, ProcessRule("self", selfProcessName)];

    private static ExclusionRule ProcessRule(string category, string process) =>
        new($"builtin:{category}:{process}", ExclusionMatchType.Process, process, FeatureScope.All);
}
