using System.Globalization;
using System.Text.Json;
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

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is null || day != _writerDay)
        {
            _writer?.Dispose();
            var path = Path.Combine(_directory, $"{Prefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Extension}");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            _writerDay = day;
        }

        return _writer;
    }

    private void DeleteExpired(DateOnly today)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, $"{Prefix}*{Extension}"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < today.AddDays(-RetentionDays))
            {
                File.Delete(file);
            }
        }
    }
}
