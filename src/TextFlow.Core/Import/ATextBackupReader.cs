using System.Text.Json;
using K4os.Compression.LZ4.Streams;
using TextFlow.Core.Library;

namespace TextFlow.Core.Import;

/// <summary>
/// Reads an aText for Windows backup (.atext): UTF-8 BOM, a JSON header, a NUL byte, then an LZ4 frame
/// holding the library as JSON with numeric keys. The key meanings below were inferred from a real
/// backup (2026-10-02, S6); unknown keys are reported so a format change does not go unnoticed.
/// The file is untrusted input: payload size and JSON depth are bounded.
/// </summary>
public static class ATextBackupReader
{
    private const long MaxPayloadBytes = 64L * 1024 * 1024;
    private const int MaxHeaderBytes = 4096;
    private const int NamePreviewLength = 40;
    private const string UnnamedSnippet = "(sin nombre)";

    // Group keys: 99 = item is a group, 0 = id, 2 = name, 13 = children, 14 = abbreviation,
    // 6 / 8 / 12 = UI state, unknown flag and timestamp (ignored). Key 8 is NOT case sensitivity:
    // the user confirmed a group without it still matches in lowercase (case is a global aText setting).
    private static readonly HashSet<string> GroupKeys = ["99", "0", "2", "6", "8", "12", "13", "14"];

    // Snippet keys: 0 = id, 1 = [abbreviations] (also the menu label), 3 = "t" plain / "h" rich, 4 = content,
    // 13 = timestamp (ignored).
    private static readonly HashSet<string> SnippetKeys = ["0", "1", "3", "4", "13"];

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    /// <param name="ignoreCase">aText's global "ignore case" setting, not stored per group in the backup.</param>
    public static ATextImport Read(Stream backup, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(backup);

        SkipHeader(backup);
        using var json = ParsePayload(backup);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("aText library is not a JSON array.");
        }

        var issues = new List<ImportIssue>();
        var items = json.RootElement.EnumerateArray().ToArray();
        var root = items is [var single] && IsGroup(single)
            ? ReadGroup(single, ignoreCase, issues)
            : new LibraryGroup("root", "aText", null, ignoreCase, items.Where(IsGroup).Select(g => ReadGroup(g, ignoreCase, issues)).ToArray(),
                items.Where(i => !IsGroup(i)).Select(s => ReadSnippet(s, issues)).ToArray());

        ReportDuplicateAbbreviations(root, issues);
        return new ATextImport(root, issues);
    }

    private static void SkipHeader(Stream backup)
    {
        for (var read = 0; read < MaxHeaderBytes; read++)
        {
            switch (backup.ReadByte())
            {
                case 0:
                    return;
                case -1:
                    throw new InvalidDataException("Not an aText backup: header separator not found.");
            }
        }

        throw new InvalidDataException("Not an aText backup: header too long.");
    }

    private static JsonDocument ParsePayload(Stream backup)
    {
        using var payload = new MemoryStream();
        try
        {
            using var lz4 = LZ4Stream.Decode(backup, leaveOpen: true);
            var buffer = new byte[81920];
            int read;
            while ((read = lz4.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (payload.Length + read > MaxPayloadBytes)
                {
                    throw new InvalidDataException("aText library exceeds the supported size.");
                }

                payload.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("aText payload is not a valid LZ4 frame.", ex);
        }

        try
        {
            return JsonDocument.Parse(payload.ToArray(), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("aText payload is not valid JSON.", ex);
        }
    }

    private static bool IsGroup(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("99", out var marker) && marker.ValueKind == JsonValueKind.Number && marker.GetInt32() == 1;

    private static LibraryGroup ReadGroup(JsonElement item, bool ignoreCase, List<ImportIssue> issues)
    {
        var id = String(item, "0") ?? Guid.NewGuid().ToString();
        ReportUnknownKeys(item, id, GroupKeys, issues);

        var children = item.TryGetProperty("13", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray()
            : [];
        var abbreviation = String(item, "14");

        return new LibraryGroup(
            id,
            String(item, "2") ?? UnnamedSnippet,
            string.IsNullOrWhiteSpace(abbreviation) ? null : abbreviation,
            ignoreCase,
            children.Where(IsGroup).Select(g => ReadGroup(g, ignoreCase, issues)).ToArray(),
            children.Where(c => !IsGroup(c)).Select(s => ReadSnippet(s, issues)).ToArray());
    }

    private static LibrarySnippet ReadSnippet(JsonElement item, List<ImportIssue> issues)
    {
        var id = String(item, "0") ?? Guid.NewGuid().ToString();
        ReportUnknownKeys(item, id, SnippetKeys, issues);

        var content = String(item, "4");
        string[] abbreviations = item.TryGetProperty("1", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString()!.Trim())
                .Where(n => n.Length > 0)
                .ToArray()
            : [];
        var name = abbreviations.FirstOrDefault();
        if (name is null)
        {
            name = Preview(content);
            issues.Add(new ImportIssue(ImportIssueCode.MissingName, id, "Snippet has no name; a content preview is used."));
        }

        var isRichText = String(item, "3") == "h";
        if (isRichText)
        {
            issues.Add(new ImportIssue(ImportIssueCode.RichTextImportedAsPlain, id, $"'{name}' was rich text; formatting is dropped."));
        }

        return new LibrarySnippet(id, name, content ?? string.Empty, isRichText, abbreviations);
    }

    private static string Preview(string? content)
    {
        var line = content?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(line))
        {
            return UnnamedSnippet;
        }

        return line.Length <= NamePreviewLength ? line : line[..NamePreviewLength] + "…";
    }

    private static void ReportUnknownKeys(JsonElement item, string id, HashSet<string> known, List<ImportIssue> issues)
    {
        var unknown = item.EnumerateObject().Select(p => p.Name).Where(k => !known.Contains(k)).ToArray();
        if (unknown.Length > 0)
        {
            issues.Add(new ImportIssue(ImportIssueCode.UnknownField, id, $"Unknown keys: {string.Join(", ", unknown)}"));
        }
    }

    private static void ReportDuplicateAbbreviations(LibraryGroup root, List<ImportIssue> issues)
    {
        var seen = new List<LibraryGroup>();
        foreach (var group in Flatten(root).Where(g => g.Abbreviation is not null))
        {
            var clash = seen.FirstOrDefault(s => string.Equals(
                s.Abbreviation,
                group.Abbreviation,
                s.IgnoreCase || group.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (clash is not null)
            {
                issues.Add(new ImportIssue(
                    ImportIssueCode.DuplicateAbbreviation,
                    group.Id,
                    $"'{group.Name}' and '{clash.Name}' share the abbreviation '{group.Abbreviation}'."));
            }

            seen.Add(group);
        }
    }

    private static IEnumerable<LibraryGroup> Flatten(LibraryGroup group) => group.Groups.SelectMany(Flatten).Prepend(group);

    private static string? String(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
