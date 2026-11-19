using System.Text.Json;
using System.Text.Json.Serialization;

namespace Masque.Core.Policy;

/// <summary>A policy file could not be read. Masque refuses to run rather than guess at the owner's intent.</summary>
public sealed class PolicyException(string message) : Exception(message);

/// <summary>
/// Reads <see cref="MaskingPolicy"/> files. An administrator's machine-wide file wins over the user's own, so a
/// workplace can set the rules centrally; with neither, the policy is <see cref="MaskingPolicy.Locked"/>.
/// </summary>
/// <remarks>
/// File shape:
/// <code>
/// { "default": { "rawTypes": [], "canWrite": false },
///   "rules": [ { "name": "hr", "users": [], "groups": ["CONTOSO\\HR"], "rawTypes": ["Name"], "canWrite": true } ] }
/// </code>
/// </remarks>
public static class PolicyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads one policy file.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>The policy, or <see langword="null"/> when the file doesn't exist.</returns>
    /// <exception cref="PolicyException">The file exists but can't be read as a policy.</exception>
    public static MaskingPolicy? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<PolicyFile>(File.ReadAllText(path), JsonOptions)
                ?? throw new PolicyException($"The policy file {path} is empty.");
            return file.ToPolicy();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The parser's message can quote the file's text, so only the path and the error kind are reported.
            throw new PolicyException($"The policy file {path} could not be read ({ex.GetType().Name}). Fix or remove it.");
        }
    }

    /// <summary>The policy in force: the machine-wide file if present, else the user's, else locked.</summary>
    /// <param name="machinePath">The administrator's file, such as <see cref="MasquePaths.MachinePolicy"/>.</param>
    /// <param name="userPath">The user's file, such as <see cref="MasquePaths.DefaultPolicy"/>.</param>
    /// <returns>The policy to apply.</returns>
    public static MaskingPolicy LoadEffective(string machinePath, string userPath) =>
        Load(machinePath) ?? Load(userPath) ?? MaskingPolicy.Locked;

    /// <summary>The JSON shape of a policy file.</summary>
    private sealed record PolicyFile(GrantEntry? Default, IReadOnlyList<RuleEntry>? Rules)
    {
        public MaskingPolicy ToPolicy() => new(
            (Rules ?? []).Select(rule => rule.ToRule()).ToList(),
            Default?.ToGrant() ?? AccessGrant.None);
    }

    /// <summary>The JSON shape of a grant.</summary>
    private sealed record GrantEntry(IReadOnlyList<SensitiveDataType>? RawTypes, bool CanWrite)
    {
        public AccessGrant ToGrant() => new(RawTypes ?? [], CanWrite);
    }

    /// <summary>The JSON shape of a rule.</summary>
    private sealed record RuleEntry(
        string? Name, IReadOnlyList<string>? Users, IReadOnlyList<string>? Groups, IReadOnlyList<SensitiveDataType>? RawTypes, bool CanWrite)
    {
        public AccessRule ToRule() => new(Name ?? string.Empty, Users ?? [], Groups ?? [], RawTypes ?? [], CanWrite);
    }
}
