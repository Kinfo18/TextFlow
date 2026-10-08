using TextFlow.Contracts.Insertion;
using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Tests.Diagnostics;

public sealed class DiagnosticSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(-5));

    private static ExpansionCompleted Expansion(
        double ms, InsertionStatus status = InsertionStatus.Success, string process = "chrome.exe", bool fromMenu = false) =>
        new(T0, process, InsertionStrategyKind.Clipboard, status, ms, fromMenu, SoundPlayed: true);

    [Fact]
    public void NoEvents_GiveAnEmptySummary()
    {
        var summary = DiagnosticSummary.From([]);

        Assert.Equal(0, summary.Expansions);
        Assert.Null(summary.SuccessRate);
        Assert.Null(summary.MedianMs);
        Assert.Null(summary.LastStartupMs);
        Assert.Empty(summary.TopApps);
    }

    [Fact]
    public void Expansions_CountSuccessesAndFailuresByStatus()
    {
        var summary = DiagnosticSummary.From(
        [
            Expansion(200),
            Expansion(300),
            Expansion(250),
            Expansion(10, InsertionStatus.TargetChanged),
        ]);

        Assert.Equal(4, summary.Expansions);
        Assert.Equal(3, summary.Successes);
        Assert.Equal(0.75, summary.SuccessRate);
        Assert.Equal(1, summary.Failures[InsertionStatus.TargetChanged]);
        Assert.False(summary.Failures.ContainsKey(InsertionStatus.Success));
    }

    [Fact]
    public void Latency_UsesSuccessfulExpansionsOnly()
    {
        var summary = DiagnosticSummary.From(
        [
            Expansion(100),
            Expansion(200),
            Expansion(300),
            Expansion(400),
            Expansion(5000, InsertionStatus.Failed),
        ]);

        Assert.Equal(250, summary.MedianMs);
        Assert.Equal(400, summary.P95Ms);
        Assert.Equal(400, summary.MaxMs);
    }

    [Fact]
    public void TopApps_AreOrderedByExpansions()
    {
        var summary = DiagnosticSummary.From(
        [
            Expansion(1, process: "notepad.exe"),
            Expansion(1, process: "chrome.exe"),
            Expansion(1, process: "chrome.exe"),
        ]);

        Assert.Equal([("chrome.exe", 2), ("notepad.exe", 1)], summary.TopApps);
    }

    [Fact]
    public void MenusFieldsAndRejections_AreCountedByReason()
    {
        var summary = DiagnosticSummary.From(
        [
            new MenuShown(T0, "chrome.exe", AnchoredToCaret: true),
            new MenuShown(T0, "chrome.exe", AnchoredToCaret: false),
            new MenuClosed(T0, MenuCloseReason.Chosen),
            new MenuClosed(T0, MenuCloseReason.OtherKey),
            new FieldsShown(T0, "chrome.exe", 2, AnchoredToCaret: true),
            new FieldsClosed(T0, FieldsCloseReason.Inserted),
            new TargetRejected(T0, "x.exe", RejectionReason.PolicyDenied),
            new TargetRejected(T0, "x.exe", RejectionReason.PolicyDenied),
        ]);

        Assert.Equal(2, summary.MenusShown);
        Assert.Equal(1, summary.MenusAnchoredToCaret);
        Assert.Equal(1, summary.MenuCloses[MenuCloseReason.Chosen]);
        Assert.Equal(1, summary.MenuCloses[MenuCloseReason.OtherKey]);
        Assert.Equal(1, summary.FieldsShown);
        Assert.Equal(1, summary.FieldsCloses[FieldsCloseReason.Inserted]);
        Assert.Equal(2, summary.Rejections[RejectionReason.PolicyDenied]);
    }

    [Fact]
    public void Robustness_CountsHookReinstallsFaultsAndLastStartup()
    {
        var summary = DiagnosticSummary.From(
        [
            new EngineStateChanged(T0, EngineState.Starting),
            new EngineStateChanged(T0.AddMilliseconds(900), EngineState.Running),
            new HookReinstalled(T0.AddMinutes(5), 1),
            new EngineFault(T0.AddMinutes(6), "IOException"),
            new EngineStateChanged(T0.AddHours(1), EngineState.Starting),
            new EngineStateChanged(T0.AddHours(1).AddMilliseconds(400), EngineState.Running),
            new EngineStateChanged(T0.AddHours(2), EngineState.Paused),
            new EngineStateChanged(T0.AddHours(2).AddMinutes(1), EngineState.Running), // resume, not a startup
        ]);

        Assert.Equal(1, summary.HookReinstalls);
        Assert.Equal(1, summary.Faults);
        Assert.Equal(2, summary.Startups);
        Assert.Equal(400, summary.LastStartupMs);
    }
}
