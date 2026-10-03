using System.Text.Json;
using TextFlow.Contracts.Insertion;
using TextFlow.Core.Diagnostics;
using TextFlow.Infrastructure.Diagnostics;

namespace TextFlow.Infrastructure.Tests.Diagnostics;

public sealed class DiagnosticFileSinkTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(-5));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "textflow-sink-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DiagnosticFileSink Sink(Func<DateTimeOffset>? clock = null) => new(_directory, clock ?? (() => Day1));

    private static ExpansionCompleted Expansion(DateTimeOffset at) =>
        new(at, "notepad.exe", InsertionStrategyKind.Clipboard, InsertionStatus.Success, 265.4, FromMenu: true, SoundPlayed: true);

    [Fact]
    public void Record_WritesOneJsonLinePerEvent_WithTypeAndFields()
    {
        using (var sink = Sink())
        {
            sink.Record(Expansion(Day1));
            sink.Record(new MenuClosed(Day1, MenuCloseReason.Escape));
        }

        var lines = File.ReadAllLines(Path.Combine(_directory, "textflow-20261002.jsonl"));
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("ExpansionCompleted", first.RootElement.GetProperty("event").GetString());
        Assert.Equal("notepad.exe", first.RootElement.GetProperty("TargetProcess").GetString());
        Assert.Equal("Clipboard", first.RootElement.GetProperty("Strategy").GetString());
        Assert.Equal(265.4, first.RootElement.GetProperty("ElapsedMs").GetDouble());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Escape", second.RootElement.GetProperty("Reason").GetString());
    }

    [Fact]
    public void Record_StartsANewFileEachDay()
    {
        var now = Day1;
        using (var sink = Sink(() => now))
        {
            sink.Record(Expansion(now));
            now = now.AddDays(1);
            sink.Record(Expansion(now));
        }

        Assert.True(File.Exists(Path.Combine(_directory, "textflow-20261002.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "textflow-20261003.jsonl")));
    }

    [Fact]
    public void Constructor_DeletesFilesOlderThanRetention()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "textflow-20260901.jsonl"), "{}");
        File.WriteAllText(Path.Combine(_directory, "textflow-20260930.jsonl"), "{}");
        File.WriteAllText(Path.Combine(_directory, "other.txt"), "keep");

        using var sink = Sink();

        Assert.False(File.Exists(Path.Combine(_directory, "textflow-20260901.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "textflow-20260930.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "other.txt")));
    }

    [Fact]
    public void Record_IsSafeFromSeveralThreads()
    {
        using (var sink = Sink())
        {
            Parallel.For(0, 200, _ => sink.Record(Expansion(Day1)));
        }

        var lines = File.ReadAllLines(Path.Combine(_directory, "textflow-20261002.jsonl"));
        Assert.Equal(200, lines.Length);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
    }

    [Fact]
    public void Recent_KeepsLastEventsInMemory_ForTheDiagnosticsPage()
    {
        using var sink = Sink();
        for (var i = 0; i < DiagnosticFileSink.RecentCapacity + 10; i++)
        {
            sink.Record(new HookReinstalled(Day1, i));
        }

        var recent = sink.Recent();
        Assert.Equal(DiagnosticFileSink.RecentCapacity, recent.Count);
        Assert.Equal(DiagnosticFileSink.RecentCapacity + 9, ((HookReinstalled)recent[^1]).TimesThisSession);
    }
}
