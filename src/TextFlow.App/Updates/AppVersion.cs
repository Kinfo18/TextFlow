using System.Reflection;

namespace TextFlow.App.Updates;

internal static class AppVersion
{
    /// <summary>Version as published (0.1.0-beta.2), without the "+commit" the SDK appends to the informational version.</summary>
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "?";
}
