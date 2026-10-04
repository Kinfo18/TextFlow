using TextFlow.Core.Security;

namespace TextFlow.Core.Tests.Security;

public class UserExclusionsTests
{
    [Theory]
    [InlineData("chrome.exe", "chrome.exe")]
    [InlineData("  Chrome  ", "Chrome.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome.exe")]
    [InlineData("Code.EXE", "Code.EXE")]
    public void TryNormalize_AcceptsAProcessNameOrPath(string input, string expected)
    {
        Assert.True(UserExclusions.TryNormalize(input, out var process));
        Assert.Equal(expected, process);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a|b.exe")]
    [InlineData(@"C:\folder\")]
    public void TryNormalize_RejectsWhatCannotBeAProcess(string input)
    {
        Assert.False(UserExclusions.TryNormalize(input, out _));
    }

    [Fact]
    public void Rules_ExcludeEveryFeature_ForEachProcess_IgnoringDuplicates()
    {
        var rules = UserExclusions.Rules(["chrome.exe", "CHROME.EXE", "Code.exe"]);

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(ExclusionMatchType.Process, r.Type);
            Assert.Equal(FeatureScope.All, r.Scope);
            Assert.StartsWith("user:process:", r.Id, StringComparison.Ordinal);
        });
    }
}
