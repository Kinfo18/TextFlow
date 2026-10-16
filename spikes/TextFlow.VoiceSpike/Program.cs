// V0.2 H0.3 spike: how whisper.cpp behaves inside a .NET process (Whisper.net) compared with whisper-server.
// Measures what the future TextFlow.VoiceWorker would pay: model load, first decode (CUDA kernel warm-up),
// warm latency per phrase with samples already in memory, memory, and which native runtime got loaded.
// Transcripts go only to the JSON file (the recordings are the user's voice); spikes/asr/spike_h03.py scores it.
//
// Usage: TextFlow.VoiceSpike --model <ggml.bin> --corpus <tsv> --recordings <dir> --out <json>
//                            [--runtime auto|cuda12|cpu] [--limit N] [--threads 6] [--prompt "..."] [--flash 1|0]

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;

var options = Options.Parse(args);
var process = Process.GetCurrentProcess();

RuntimeOptions.RuntimeLibraryOrder = options.Runtime switch
{
    "cuda12" => [RuntimeLibrary.Cuda12],
    "cpu" => [RuntimeLibrary.Cpu],
    _ => [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu],
};

var items = Corpus.Load(options.Corpus, options.Recordings, options.Limit);
if (items.Count == 0)
{
    Console.Error.WriteLine($"No recordings found in {options.Recordings}");
    return 1;
}

var load = Stopwatch.StartNew();
// whisper-cli/whisper-server enable flash attention by default; Whisper.net does not, and without it turbo's
// KV cache filled the 4 GB RTX 2050 and spilled into shared memory (5-12 s per phrase).
using var factory = WhisperFactory.FromPath(options.Model, new WhisperFactoryOptions { UseFlashAttention = options.FlashAttention });
var loadMs = load.Elapsed.TotalMilliseconds;
var runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
var runtimeInfo = WhisperFactory.GetRuntimeInfo();

using var processor = factory.CreateBuilder()
    .WithLanguage("es")
    .WithPrompt(options.Prompt)
    .WithThreads(options.Threads)
    .Build();

// The first decode pays CUDA kernel compilation/upload; the app would hide it behind a warm-up at startup.
var first = Stopwatch.StartNew();
await Transcribe(processor, Wav.ReadSamples(items[0].Wav));
var firstMs = first.Elapsed.TotalMilliseconds;
var readyMs = (DateTime.Now - process.StartTime).TotalMilliseconds;

var results = new List<object>();
foreach (var item in items)
{
    var samples = Wav.ReadSamples(item.Wav);
    var sw = Stopwatch.StartNew();
    var text = await Transcribe(processor, samples);
    results.Add(new { id = item.Id, text, ms = sw.Elapsed.TotalMilliseconds, audioMs = samples.Length / 16.0 });
}

process.Refresh();
var report = new
{
    runtime,
    runtimeInfo,
    loadMs,
    firstMs,
    readyMs,
    workingSetMb = process.WorkingSet64 / 1048576.0,
    privateMb = process.PrivateMemorySize64 / 1048576.0,
    items = results,
};
await File.WriteAllTextAsync(options.Out, JsonSerializer.Serialize(report, Json.Indented), Encoding.UTF8);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"runtime={runtime} load={loadMs:F0} ms first={firstMs:F0} ms ready={readyMs:F0} ms phrases={results.Count} -> {options.Out}"));
return 0;

static async Task<string> Transcribe(WhisperProcessor processor, float[] samples)
{
    var text = new StringBuilder();
    await foreach (var segment in processor.ProcessAsync(samples))
    {
        text.Append(segment.Text);
    }
    return text.ToString().Trim();
}

internal sealed record Options(string Model, string Corpus, string Recordings, string Out, string Runtime, int Limit, int Threads, string Prompt, bool FlashAttention)
{
    // Same wide prompt as spikes/asr/bench.py (H0.2: 3.0 % -> 2.4 % WER in a quiet room).
    private const string DefaultPrompt = "Soporte de pedidos a domicilio: rider, pickup, dropoff, pedido, orden, reembolso, cupón, stock, standup, dashboard, ticket, Jira, Slack.";

    public static Options Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }
        string Get(string key, string fallback) => map.TryGetValue(key, out var value) ? value : fallback;
        return new Options(
            Get("model", ""), Get("corpus", ""), Get("recordings", ""), Get("out", "spike-h03.json"),
            Get("runtime", "auto"),
            int.Parse(Get("limit", "0"), CultureInfo.InvariantCulture),
            int.Parse(Get("threads", "6"), CultureInfo.InvariantCulture),
            Get("prompt", DefaultPrompt),
            Get("flash", "1") == "1");
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

internal sealed record CorpusItem(string Id, string Wav);

internal static class Corpus
{
    public static List<CorpusItem> Load(string corpus, string recordings, int limit)
    {
        var lines = File.ReadAllLines(corpus, Encoding.UTF8);
        var header = lines[0].Split('\t');
        var idColumn = Array.IndexOf(header, "id");
        var items = lines.Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t')[idColumn])
            .Select(id => new CorpusItem(id, Path.Combine(recordings, id + ".wav")))
            .Where(item => File.Exists(item.Wav))
            .ToList();
        return limit > 0 ? items.Take(limit).ToList() : items;
    }
}

internal static class Wav
{
    // The recordings are 16 kHz mono 16-bit PCM (record-corpus.ps1); walk the chunks to find "data".
    public static float[] ReadSamples(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                var count = Math.Min(size, bytes.Length - pos - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                }
                return samples;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"No data chunk in {path}");
    }
}
