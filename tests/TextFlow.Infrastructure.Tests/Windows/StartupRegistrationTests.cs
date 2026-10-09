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

    [Fact]
    public void Apply_True_WithoutTakeOver_LeavesTheEntryOfAnotherCopyThatExists()
    {
        var installed = Path.GetTempFileName(); // the installed copy's exe, present on disk
        try
        {
            var other = Create(installed);
            other.Apply(enabled: true);

            Create().Apply(enabled: true, takeOverOtherCopy: false); // a dev build or portable zip starting up

            Assert.True(other.IsEnabled);
        }
        finally
        {
            File.Delete(installed);
        }
    }

    [Fact]
    public void Apply_True_WithoutTakeOver_StillRepairsAnEntryWhoseExeIsGone()
    {
        Create(@"C:\Old\TextFlow.exe").Apply(enabled: true);
        var current = Create();

        current.Apply(enabled: true, takeOverOtherCopy: false);

        Assert.True(current.IsEnabled);
    }

    [Fact]
    public void Apply_True_WithTakeOver_ReplacesTheEntryOfAnotherCopy()
    {
        var other = Path.GetTempFileName();
        try
        {
            Create(other).Apply(enabled: true);
            var current = Create();

            current.Apply(enabled: true, takeOverOtherCopy: true); // the installed copy, or the user's own toggle

            Assert.True(current.IsEnabled);
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Theory]
    [InlineData(@"""C:\Apps\TextFlow.exe"" --background", @"C:\Apps\TextFlow.exe")]
    [InlineData(@"C:\Apps\TextFlow.exe --background", @"C:\Apps\TextFlow.exe")]
    [InlineData("", null)]
    public void ExecutableOf_ReadsThePathOfARegisteredCommand(string command, string? expected)
    {
        Assert.Equal(expected, StartupRegistration.ExecutableOf(command));
    }
}
