using System.Text.Json;

namespace Masque.Mcp;

/// <summary>Kinds of named source.</summary>
public enum SourceType
{
    /// <summary>A SQL Server database; needs a connection string.</summary>
    SqlServer,

    /// <summary>A SQLite database file; needs a path.</summary>
    Sqlite,

    /// <summary>A CSV or Excel file, or a folder of them; needs a path.</summary>
    File,
}

/// <summary>One named source from sources.json.</summary>
/// <param name="Type">What kind of source it is.</param>
/// <param name="ConnectionString">For SQL Server: the connection string. Prefer integrated security over a password.</param>
/// <param name="Path">For SQLite and files: the file or folder path.</param>
public sealed record SourceEntry(SourceType Type, string? ConnectionString, string? Path);

/// <summary>
/// Named sources, read from a local JSON file the user edits, for example:
/// <code>{ "sources": { "hr": { "type": "sqlServer", "connectionString": "Server=...;Integrated Security=true" } } }</code>
/// The assistant refers to a source by name only, so connection strings and credentials never pass through it.
/// </summary>
/// <param name="path">The sources file; it is fine for it not to exist.</param>
public sealed class SourceCatalog(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The sources file's path.</summary>
    public string FilePath { get; } = path;

    /// <summary>Looks a source up by name (case-insensitive). The file is re-read each time, so edits apply at once.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="entry">The source, when found.</param>
    /// <returns><see langword="true"/> when the name is defined.</returns>
    /// <exception cref="JsonException">The file isn't valid JSON.</exception>
    public bool TryGet(string name, out SourceEntry entry)
    {
        var match = Load().FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        entry = match.Value!;
        return match.Value is not null;
    }

    /// <summary>The names of all defined sources.</summary>
    /// <returns>Source names, sorted.</returns>
    public IReadOnlyList<string> Names() => [.. Load().Keys.Order(StringComparer.OrdinalIgnoreCase)];

    private Dictionary<string, SourceEntry> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<SourcesFile>(File.ReadAllText(FilePath), JsonOptions)?.Sources ?? [];
    }

    /// <summary>The file's shape.</summary>
    private sealed record SourcesFile(Dictionary<string, SourceEntry>? Sources);
}
