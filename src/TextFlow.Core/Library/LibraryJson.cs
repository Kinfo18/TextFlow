using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextFlow.Core.Library;

/// <summary>
/// TextFlow's own library file (H2.4): versioned JSON, readable and diff-friendly. Import is strict and treats the
/// file as untrusted: format, version, ids, nesting and values are validated before anything reaches the database.
/// </summary>
public static class LibraryJson
{
    public const string Format = "textflow-library";
    public const int FormatVersion = 1;

    /// <summary>Deeper trees are refused (the user's library is 4 levels deep; recursion must stay bounded).</summary>
    public const int MaxDepth = 32;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "ñ" and emoji readable; output is a file, not HTML
        MaxDepth = 256, // the reader limit only guards the parser; LibraryJson.MaxDepth gives the user-facing error
    };

    public static string Export(LibraryGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return JsonSerializer.Serialize(new FileDto(Format, FormatVersion, ToDto(root)), Options);
    }

    /// <exception cref="InvalidDataException">Not a TextFlow library file, a newer version, or inconsistent content.</exception>
    public static LibraryGroup Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("El archivo no es JSON válido.", ex);
        }

        if (file?.Format != Format)
        {
            throw new InvalidDataException("El archivo no es una biblioteca de TextFlow.");
        }

        if (file.Version > FormatVersion)
        {
            throw new InvalidDataException($"La biblioteca es de una versión más nueva de TextFlow (formato {file.Version}).");
        }

        if (file.Version < 1 || file.Root is null)
        {
            throw new InvalidDataException("La biblioteca está incompleta.");
        }

        return FromDto(file.Root, depth: 0, new HashSet<string>(StringComparer.Ordinal));
    }

    private static GroupDto ToDto(LibraryGroup group) => new(
        group.Id,
        group.Name,
        group.Abbreviation,
        group.IgnoreCase,
        [.. group.Groups.Select(ToDto)],
        [.. group.Snippets.Select(s => new SnippetDto(
            s.Id,
            s.Name,
            s.Content,
            [.. s.Abbreviations],
            s.IsRichText ? true : null,
            s.Mode == SnippetMode.AfterDelimiter ? "after_delimiter" : null,
            s.Enabled ? null : false))]);

    private static LibraryGroup FromDto(GroupDto dto, int depth, HashSet<string> ids)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"La biblioteca tiene más de {MaxDepth} niveles de grupos.");
        }

        return new LibraryGroup(
            UniqueId(dto.Id, ids),
            dto.Name ?? throw Missing("name"),
            string.IsNullOrEmpty(dto.Abbreviation) ? null : dto.Abbreviation,
            dto.IgnoreCase ?? true,
            [.. (dto.Groups ?? []).Select(g => FromDto(g ?? throw Missing("group"), depth + 1, ids))],
            [.. (dto.Snippets ?? []).Select(s => FromDto(s ?? throw Missing("snippet"), ids))]);
    }

    private static LibrarySnippet FromDto(SnippetDto dto, HashSet<string> ids) => new(
        UniqueId(dto.Id, ids),
        dto.Name ?? throw Missing("name"),
        dto.Content ?? throw Missing("content"),
        dto.RichText ?? false,
        [.. (dto.Abbreviations ?? []).Select(a => a ?? throw Missing("abbreviation"))],
        dto.Mode switch
        {
            null or "immediate" => SnippetMode.Immediate,
            "after_delimiter" => SnippetMode.AfterDelimiter,
            _ => throw new InvalidDataException("Un snippet tiene un modo desconocido."),
        },
        dto.Enabled ?? true);

    /// <summary>Ids identify rows in the database and diffs in the import report: they must be present and unique.</summary>
    private static string UniqueId(string? id, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw Missing("id");
        }

        return ids.Add(id) ? id : throw new InvalidDataException("La biblioteca repite identificadores.");
    }

    private static InvalidDataException Missing(string field) => new($"Falta un campo obligatorio ({field}).");

    private sealed record FileDto(string? Format, int Version, GroupDto? Root);

    private sealed record GroupDto(
        string? Id, string? Name, string? Abbreviation, bool? IgnoreCase, IReadOnlyList<GroupDto?>? Groups, IReadOnlyList<SnippetDto?>? Snippets);

    private sealed record SnippetDto(
        string? Id, string? Name, string? Content, IReadOnlyList<string?>? Abbreviations, bool? RichText, string? Mode, bool? Enabled);
}
