using TextFlow.Contracts.Targeting;
using TextFlow.Core.Security;

namespace TextFlow.Core.Tests.Security;

public class SecurityPolicyTests
{
    private static ActiveTarget Target(
        string process = "notepad.exe",
        string windowClass = "Notepad",
        string title = "Sin título - Bloc de notas",
        bool isPassword = false,
        bool elevated = false) =>
        new(
            WindowHandle: 1,
            FocusHandle: 2,
            ProcessId: 100,
            ThreadId: 200,
            ProcessName: process,
            WindowClass: windowClass,
            WindowTitle: title,
            Monitor: new MonitorInfo(3, @"\\.\DISPLAY1", 96, 96, new PixelRect(0, 0, 1920, 1080)),
            Control: FocusedControlInfo.Unknown with { IsPassword = isPassword },
            IsElevated: elevated,
            CapturedAt: DateTimeOffset.UnixEpoch);

    private static SecurityPolicy Policy(params ExclusionRule[] rules) => new(BuiltinExclusions.All.Concat(rules));

    [Fact]
    public void RegularEditor_IsAllowedForAllFeatures()
    {
        var policy = Policy();

        foreach (var feature in Enum.GetValues<TextFlowFeature>())
        {
            Assert.True(policy.Evaluate(Target(), feature).IsAllowed);
        }
    }

    [Fact]
    public void PasswordField_IsBlockedForEveryFeature()
    {
        var decision = Policy().Evaluate(Target(isPassword: true), TextFlowFeature.Expansion);

        Assert.False(decision.IsAllowed);
        Assert.Equal(PolicyReason.PasswordField, decision.Reason);
    }

    [Fact]
    public void ElevatedTarget_IsBlocked()
    {
        Assert.Equal(PolicyReason.ElevatedTarget, Policy().Evaluate(Target(elevated: true), TextFlowFeature.Dictation).Reason);
    }

    [Theory]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("mintty.exe")]
    [InlineData("KeePassXC.exe")]
    [InlineData("1Password.exe")]
    [InlineData("Bitwarden.exe")]
    [InlineData("consent.exe")]
    public void BuiltinSensitiveProcesses_AreBlocked(string process)
    {
        var decision = Policy().Evaluate(Target(process: process), TextFlowFeature.Expansion);

        Assert.False(decision.IsAllowed);
        Assert.Equal(PolicyReason.ExclusionRule, decision.Reason);
    }

    [Fact]
    public void ConsoleWindowClass_IsBlockedRegardlessOfProcess()
    {
        Assert.False(Policy().Evaluate(Target(process: "weird.exe", windowClass: "ConsoleWindowClass"), TextFlowFeature.Expansion).IsAllowed);
    }

    [Fact]
    public void ProcessMatching_IsCaseInsensitive()
    {
        Assert.False(Policy().Evaluate(Target(process: "CMD.EXE"), TextFlowFeature.Expansion).IsAllowed);
    }

    [Fact]
    public void CustomRule_RespectsScope()
    {
        var policy = Policy(new ExclusionRule("r1", ExclusionMatchType.Process, "slack.exe", FeatureScope.Dictation));

        Assert.True(policy.Evaluate(Target(process: "slack.exe"), TextFlowFeature.Expansion).IsAllowed);
        Assert.False(policy.Evaluate(Target(process: "slack.exe"), TextFlowFeature.Dictation).IsAllowed);
    }

    [Fact]
    public void WildcardTitleRule_Matches()
    {
        var policy = Policy(new ExclusionRule("r2", ExclusionMatchType.WindowTitle, "*Banco*", FeatureScope.All));

        var decision = policy.Evaluate(Target(process: "chrome.exe", title: "Mi Banco Online - Google Chrome"), TextFlowFeature.Correction);

        Assert.False(decision.IsAllowed);
        Assert.Equal("r2", decision.RuleId);
    }

    [Fact]
    public void DisabledRule_IsIgnored()
    {
        var policy = Policy(new ExclusionRule("r3", ExclusionMatchType.Process, "slack.exe", FeatureScope.All, Enabled: false));

        Assert.True(policy.Evaluate(Target(process: "slack.exe"), TextFlowFeature.Expansion).IsAllowed);
    }

    [Fact]
    public void Decision_NeverExposesWindowTitle()
    {
        var policy = Policy(new ExclusionRule("r4", ExclusionMatchType.WindowTitle, "*secreto*", FeatureScope.All));

        var decision = policy.Evaluate(Target(title: "documento secreto.docx"), TextFlowFeature.Expansion);

        Assert.DoesNotContain("secreto", decision.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
