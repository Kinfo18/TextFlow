using Microsoft.Win32;
using TextFlow.Infrastructure.Windows;

namespace TextFlow.Infrastructure.Tests.Windows;

/// <summary>Uses a throwaway HKCU key instead of the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string Exe = @"C:\Apps\TextFlow\TextFlow.exe";
    private readonly string _keyPath = $@"Software\TextFlow.Tests\{Guid.NewGuid():N}";

    private StartupRegistration Create(string exe = Exe) => new("TextFlow", exe, _keyPath);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\TextFlow.Tests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\TextFlow.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Command_QuotesThePath_AndStartsInTheBackground()
    {
        Assert.Equal("\"C:\\Apps\\TextFlow\\TextFlow.exe\" --background", Create().Command);
    }

    [Fact]
    public void Apply_True_RegistersTheCommand()
    {
        var registration = Create();

        registration.Apply(enabled: true);

        Assert.True(registration.IsEnabled);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Equal(registration.Command, key?.GetValue("TextFlow"));
    }

    [Fact]
    public void Apply_False_RemovesTheValue()
    {
        var registration = Create();
        registration.Apply(enabled: true);

        registration.Apply(enabled: false);

        Assert.False(registration.IsEnabled);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Null(key?.GetValue("TextFlow"));
    }

    [Fact]
    public void Apply_True_RepairsAStalePath_AfterTheAppMoved()
    {
        Create(@"C:\Old\TextFlow.exe").Apply(enabled: true);
        var current = Create();
        Assert.False(current.IsEnabled);

        current.Apply(enabled: true);

        Assert.True(current.IsEnabled);
    }

    [Fact]
    public void Apply_False_WhenNothingIsRegistered_DoesNotThrow()
    {
        Create().Apply(enabled: false);
    }
}
