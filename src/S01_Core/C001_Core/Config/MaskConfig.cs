using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Masque.Core.Detection;

namespace Masque.Core.Config;

/// <summary>
/// Identifies a file's shape by its headers. Sorted and normalized first, so column order, case and punctuation
/// don't matter: a file with the same columns always gets the same fingerprint, and the same saved config.
/// </summary>
public static class SchemaFingerprint
{
    /// <summary>Computes the fingerprint of a header list.</summary>
    /// <param name="headers">The file's column headers.</param>
    /// <returns>16 lowercase hexadecimal characters.</returns>
    public static string Compute(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var canonical = string.Join('\n', headers.Select(HeaderText.Normalize).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}

/// <summary>The saved decision for one column.</summary>
/// <param name="Header">The column header as it appeared.</param>
/// <param name="Category">The category to tokenize it as. People may edit this.</param>
/// <param name="Reason">Why it was chosen; never quotes a value.</param>
public sealed record ColumnRule(string Header, SensitiveDataType Category, string Reason);

/// <summary>
/// The saved categories for one file shape. It holds headers and categories only, never values, so it is safe
/// to show an AI assistant. A config must be confirmed by a person before Masque uses it.
/// </summary>
/// <param name="Fingerprint">The <see cref="SchemaFingerprint"/> of the headers.</param>
/// <param name="ReviewedByPerson">Whether a person has reviewed and accepted it.</param>
/// <param name="Columns">One rule per column.</param>
public sealed record MaskConfig(string Fingerprint, bool ReviewedByPerson, IReadOnlyList<ColumnRule> Columns)
{
    /// <summary>The category for a header, matched the same way as the fingerprint. Unknown headers fail closed.</summary>
    /// <param name="header">A column header.</param>
    /// <returns>The saved category, or <see cref="SensitiveDataType.FreeText"/> when the header isn't in the config.</returns>
    public SensitiveDataType CategoryFor(string header)
    {
        var normalized = HeaderText.Normalize(header);
        return Columns.FirstOrDefault(c => HeaderText.Normalize(c.Header) == normalized)?.Category ?? SensitiveDataType.FreeText;
    }
}

/// <summary>Saves and loads <see cref="MaskConfig"/> files, one JSON file per fingerprint.</summary>
/// <param name="folder">Folder that holds the config files; created on first save.</param>
public sealed partial class ConfigStore(string folder)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file path for a fingerprint's config.</summary>
    /// <param name="fingerprint">Letters and digits only, as <see cref="SchemaFingerprint.Compute"/> makes them.</param>
    /// <returns>The full path, whether or not the file exists.</returns>
    public string PathFor(string fingerprint)
    {
        if (!FingerprintPattern().IsMatch(fingerprint))
        {
            throw new ArgumentException("A fingerprint may contain only letters and digits.", nameof(fingerprint));
        }

        return Path.Combine(folder, $"{fingerprint}.json");
    }

    /// <summary>Loads a config, or returns null when none is saved for this fingerprint.</summary>
    /// <param name="fingerprint">The file shape's fingerprint.</param>
    /// <returns>The config, or <see langword="null"/>.</returns>
    public MaskConfig? Load(string fingerprint)
    {
        var path = PathFor(fingerprint);
        return File.Exists(path) ? JsonSerializer.Deserialize<MaskConfig>(File.ReadAllText(path), JsonOptions) : null;
    }

    /// <summary>Saves a config, replacing any earlier one for the same fingerprint.</summary>
    /// <param name="config">The config to save.</param>
    public void Save(MaskConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var path = PathFor(config.Fingerprint);
        Directory.CreateDirectory(folder);

        // Write to a temp file and move it into place, so a crash never leaves half a config.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex FingerprintPattern();
}
