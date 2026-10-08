using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TextFlow.Core.Diagnostics;

namespace TextFlow.Infrastructure.Diagnostics;

/// <summary>
/// Writes <see cref="DiagnosticEvent"/>s as JSON lines to <c>textflow-yyyyMMdd.jsonl</c> (one file per day,
/// <see cref="RetentionDays"/> kept) and keeps the last <see cref="RecentCapacity"/> in memory for the
/// Diagnostics page. Events are content-free by type (see <see cref="SafeToLogAttribute"/>), so nothing here filters.
/// </summary>
public sealed class DiagnosticFileSink : IDiagnosticSink, IDisposable
{
    public const int RecentCapacity = 200;
    public const int RetentionDays = 7;

    private const string Prefix = "textflow-";
    private const string Extension = ".jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Event name (as written in the "event" field) → its record type, for reading logs back.</summary>
    private static readonly Dictionary<string, Type> EventTypes = typeof(DiagnosticEvent).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(DiagnosticEvent)) && !t.IsAbstract)
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly Queue<DiagnosticEvent> _recent = new();
    private StreamWriter? _writer;
    private DateOnly _writerDay;

    /// <param name="directory">Usually <c>%LOCALAPPDATA%\TextFlow\logs</c>.</param>
    public DiagnosticFileSink(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Directory.CreateDirectory(directory);
        DeleteExpired(DateOnly.FromDateTime(_clock().Date));
    }

    public void Record(DiagnosticEvent diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var line = Serialize(diagnostic);

        lock (_gate)
        {
            _recent.Enqueue(diagnostic);
            if (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }

            WriterFor(DateOnly.FromDateTime(_clock().Date)).WriteLine(line);
        }
    }

    public IReadOnlyList<DiagnosticEvent> Recent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    /// <summary>Days that still have a log (at most <see cref="RetentionDays"/> + today), newest first.</summary>
    public IReadOnlyList<DateOnly> Days() =>
        Directory.EnumerateFiles(_directory, $"{Prefix}*{Extension}")
            .Select(DayOf)
            .OfType<DateOnly>()
            .OrderDescending()
            .ToArray();

    /// <summary>
    /// The events of <paramref name="day"/> (H5.4). Lines that are broken or come from an event this version does not
    /// know are skipped: a log must never stop the Diagnóstico page from opening.
    /// </summary>
    public IReadOnlyList<DiagnosticEvent> Read(DateOnly day)
    {
        var path = PathFor(day);
        if (!File.Exists(path))
        {
            return [];
        }

        // Today's file is open for appending: share it instead of waiting for the writer.
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var events = new List<DiagnosticEvent>();
        while (reader.ReadLine() is { } line)
        {
            if (Parse(line) is { } diagnostic)
            {
                events.Add(diagnostic);
            }
        }

        return events;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string Serialize(DiagnosticEvent diagnostic)
    {
        var node = JsonSerializer.SerializeToNode(diagnostic, diagnostic.GetType(), Json)!.AsObject();
        node.Insert(0, "event", diagnostic.GetType().Name);
        return node.ToJsonString();
    }

    private static DiagnosticEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(line);
            return node?["event"]?.GetValue<string>() is { } name && EventTypes.TryGetValue(name, out var type)
                ? node.Deserialize(type, Json) as DiagnosticEvent
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private string PathFor(DateOnly day) =>
        Path.Combine(_directory, $"{Prefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Extension}");

    private static DateOnly? DayOf(string file)
    {
        var stamp = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
        return DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is null || day != _writerDay)
        {
            _writer?.Dispose();
            var path = PathFor(day);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            _writerDay = day;
        }

        return _writer;
    }

    private void DeleteExpired(DateOnly today)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, $"{Prefix}*{Extension}"))
        {
            if (DayOf(file) is { } day && day < today.AddDays(-RetentionDays))
            {
                File.Delete(file);
            }
        }
    }
}
