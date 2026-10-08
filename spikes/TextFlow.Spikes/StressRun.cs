using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TextFlow.Core.Library;
using TextFlow.Core.Templates;
using TextFlow.Infrastructure.Storage;

namespace TextFlow.Spikes;

/// <summary>
/// H6.1 stress test against the running TextFlow: types real abbreviations of plain snippets into the focused editor
/// (Notepad, Word, a Chrome textarea), waits for each ExpansionCompleted in the log, then copies the whole document
/// and compares it with the expected text. Reads the library read-only. Prints counts and positions only, never
/// snippet text or abbreviations (spec §20).
/// </summary>
internal static class StressRun
{
    private const int CountdownSeconds = 5;
    private const int KeyDelayMs = 15;
    private const int AfterExpansionDelayMs = 60;
    private static readonly TimeSpan ExpansionTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string[] args)
    {
        var count = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1000;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFlow");
        var library = await new SqliteLibraryRepository(new SqliteDatabase(Path.Combine(root, "textflow.db")), TimeProvider.System)
            .LoadAsync(CancellationToken.None);
        var candidates = PickCandidates(library);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No hay snippets de texto plano con abreviatura tecleable sin ambigüedad.");
            return 1;
        }

        Console.WriteLine($"{candidates.Count} snippets candidatos; {count} expansiones. Abre un documento VACÍO y enfócalo.");
        Console.WriteLine("No toques el teclado ni el ratón hasta el final (cambiar de ventana aborta la prueba).");
        await Services.CountdownAsync(CountdownSeconds);

        var target = Native.GetForegroundWindow();
        var log = new LogTail(Path.Combine(root, "logs"));
        var expected = new StringBuilder();
        var expectedEnds = new List<int>(count);
        var timeouts = 0;
        var random = new Random(20261005); // fixed seed: runs are repeatable
        var started = DateTime.Now;

        for (var i = 0; i < count; i++)
        {
            if (Native.GetForegroundWindow() != target)
            {
                Console.WriteLine($"\nCambió la ventana activa en la expansión {i + 1}: prueba abortada.");
                count = i;
                break;
            }

            var (abbreviation, content) = candidates[random.Next(candidates.Count)];
            TypeAscii(abbreviation);
            if (!await log.WaitForExpansionAsync(ExpansionTimeout))
            {
                timeouts++;
            }

            await Task.Delay(AfterExpansionDelayMs);
            Native.Tap(Native.VkReturn);
            expected.Append(content).Append('\n');
            expectedEnds.Add(expected.Length);
            if ((i + 1) % 50 == 0)
            {
                Console.Write($"\r  {i + 1}/{count}");
            }
        }

        await Task.Delay(1000); // let the last clipboard restore finish before copying the document
        var actual = CopyDocument();
        var elapsed = DateTime.Now - started;

        Console.WriteLine($"\n\nTiempo: {elapsed:mm\\:ss} · expansiones tecleadas: {count} · sin evento en {ExpansionTimeout.TotalSeconds:0} s: {timeouts}");
        Console.WriteLine(log.Summary());
        Report(Normalize(expected.ToString()), actual is null ? null : Normalize(actual), expectedEnds);
        return 0;
    }

    /// <summary>
    /// Plain immediate snippets whose abbreviation is typeable with the US/ES base layer (no AltGr) and cannot be
    /// pre-empted: no other trigger appears inside it, and it is not the start of a longer one (no prefix wait).
    /// </summary>
    private static List<(string Abbreviation, string Content)> PickCandidates(LibraryGroup root)
    {
        var groups = Flatten(root).ToList();
        var snippets = groups.SelectMany(g => g.Snippets).ToList();
        var triggers = snippets.Where(s => s.Enabled).SelectMany(s => s.TypeableAbbreviations)
            .Concat(groups.Select(g => g.Abbreviation).OfType<string>())
            .Where(t => t.Length > 0)
            .Select(t => t.ToUpperInvariant())
            .ToHashSet();

        var result = new List<(string, string)>();
        foreach (var snippet in snippets.Where(s => s.Enabled && s.Mode == SnippetMode.Immediate && !s.IsInfoOnly))
        {
            var parsed = TemplateParser.Parse(snippet.Content);
            if (!parsed.Segments.All(s => s is LiteralSegment))
            {
                continue;
            }

            var text = string.Concat(parsed.Segments.Cast<LiteralSegment>().Select(s => s.Text));
            foreach (var abbreviation in snippet.TypeableAbbreviations)
            {
                if (abbreviation.All(Native.CanType) && !IsAmbiguous(abbreviation.ToUpperInvariant(), triggers))
                {
                    result.Add((abbreviation, text));
                    break;
                }
            }
        }

        return result;
    }

    private static bool IsAmbiguous(string trigger, HashSet<string> triggers)
    {
        if (triggers.Any(t => t.Length > trigger.Length && t.StartsWith(trigger, StringComparison.Ordinal)))
        {
            return true;
        }

        for (var end = 1; end < trigger.Length; end++)
        {
            for (var start = 0; start < end; start++)
            {
                if (triggers.Contains(trigger[start..end]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<LibraryGroup> Flatten(LibraryGroup group) =>
        new[] { group }.Concat(group.Groups.SelectMany(Flatten));

    private static void TypeAscii(string text)
    {
        foreach (var c in text)
        {
            Native.TypeChar(c);
            Thread.Sleep(KeyDelayMs);
        }
    }

    private static string? CopyDocument()
    {
        Native.Chord(Native.VkControl, 'A');
        Thread.Sleep(150);
        Native.Chord(Native.VkControl, 'C');
        Thread.Sleep(300);
        string? text = null;
        var thread = new Thread(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Native.Tap(Native.VkEnd, ctrl: true); // drop the selection so a stray key cannot replace the document
        return text;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

    private static void Report(string expected, string? actual, List<int> ends)
    {
        if (actual is null)
        {
            Console.WriteLine("No se pudo leer el documento del portapapeles.");
            return;
        }

        if (actual == expected)
        {
            Console.WriteLine($"Documento: IDÉNTICO al esperado ({expected.Length} caracteres). 0 errores de destino.");
            return;
        }

        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        var expansion = ends.FindIndex(e => e > at) + 1;
        Console.WriteLine($"Documento: DIFERENTE. Esperado {expected.Length} caracteres, obtenido {actual.Length}.");
        Console.WriteLine($"Primera diferencia en el carácter {at}, dentro de la expansión nº {expansion}.");
    }

    /// <summary>Follows today's diagnostic log from the moment the run starts (content-free JSON lines).</summary>
    private sealed class LogTail(string directory)
    {
        private readonly string _path = Path.Combine(directory, $"textflow-{DateTime.Now:yyyyMMdd}.jsonl");
        private readonly Dictionary<string, int> _counts = [];
        private long _offset = File.Exists(Path.Combine(directory, $"textflow-{DateTime.Now:yyyyMMdd}.jsonl"))
            ? new FileInfo(Path.Combine(directory, $"textflow-{DateTime.Now:yyyyMMdd}.jsonl")).Length
            : 0;

        public async Task<bool> WaitForExpansionAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (ReadNew().Contains("ExpansionCompleted"))
                {
                    return true;
                }

                await Task.Delay(10);
            }

            return false;
        }

        public string Summary()
        {
            ReadNew();
            return _counts.Count == 0
                ? "Log: sin eventos."
                : "Log: " + string.Join(" · ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"));
        }

        private List<string> ReadNew()
        {
            var events = new List<string>();
            if (!File.Exists(_path))
            {
                return events;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var chunk = reader.ReadToEnd();
            var complete = chunk.LastIndexOf('\n');
            if (complete < 0)
            {
                return events;
            }

            _offset += Encoding.UTF8.GetByteCount(chunk.AsSpan(0, complete + 1));
            foreach (var line in chunk[..complete].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                var name = json.RootElement.GetProperty("event").GetString() ?? "?";
                if (name == "ExpansionCompleted" && json.RootElement.TryGetProperty("Status", out var status))
                {
                    name = $"Expansion.{status.GetString()}";
                    events.Add("ExpansionCompleted");
                }

                _counts[name] = _counts.GetValueOrDefault(name) + 1;
            }

            return events;
        }
    }

    private static class Native
    {
        public const ushort VkReturn = 0x0D;
        public const ushort VkControl = 0x11;
        public const ushort VkEnd = 0x23;
        private const ushort VkShift = 0x10;
        private const uint KeyUp = 0x0002;
        private const uint InputKeyboard = 1;

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short VkKeyScanW(char ch);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        /// <summary>Base layer or Shift only: AltGr characters depend on the layout and the hook translates real keys.</summary>
        public static bool CanType(char c)
        {
            var scan = VkKeyScanW(c);
            return scan != -1 && (scan >> 8 & ~1) == 0;
        }

        public static void TypeChar(char c)
        {
            var scan = VkKeyScanW(c);
            var vk = (ushort)(scan & 0xFF);
            var shift = (scan >> 8 & 1) != 0;
            var inputs = new List<Input>();
            if (shift)
            {
                inputs.Add(Key(VkShift, false));
            }

            inputs.Add(Key(vk, false));
            inputs.Add(Key(vk, true));
            if (shift)
            {
                inputs.Add(Key(VkShift, true));
            }

            Send(inputs);
        }

        public static void Tap(ushort vk, bool ctrl = false)
        {
            var inputs = new List<Input>();
            if (ctrl)
            {
                inputs.Add(Key(VkControl, false));
            }

            inputs.Add(Key(vk, false));
            inputs.Add(Key(vk, true));
            if (ctrl)
            {
                inputs.Add(Key(VkControl, true));
            }

            Send(inputs);
        }

        public static void Chord(ushort modifier, char key) =>
            Send([Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true)]);

        private static void Send(List<Input> inputs)
        {
            if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) != inputs.Count)
            {
                throw new InvalidOperationException($"SendInput bloqueado (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        private static Input Key(ushort vk, bool up) => new()
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput { Vk = vk, Flags = up ? KeyUp : 0 },
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public KeyboardInput Keyboard;
            private readonly long _padding; // the union is as large as MOUSEINPUT
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public nint ExtraInfo;
        }
    }
}
