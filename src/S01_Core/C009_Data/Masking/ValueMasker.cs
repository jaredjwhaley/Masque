using System.Diagnostics.CodeAnalysis;
using Masque.Core;
using Masque.Core.Detection;
using Masque.Core.Tables;
using Masque.Core.Vault;

namespace Masque.Data.Masking;

/// <summary>
/// Masks single values by a column's plan, and turns tokens back into the values they stand for when a caller filters
/// on one or writes one. Serializes vault access, so one instance can serve every connection.
/// </summary>
/// <param name="tokenizer">Creates and reuses tokens.</param>
/// <param name="vault">The vault behind the tokenizer, for reverse lookups.</param>
/// <param name="classifier">Spots values that look sensitive.</param>
internal sealed class ValueMasker(Tokenizer tokenizer, ITokenVault vault, ColumnClassifier classifier)
{
    /// <summary>Every type that has tokens, for checking whether a written value is one.</summary>
    private static readonly SensitiveDataType[] SensitiveTypes =
        [.. Enum.GetValues<SensitiveDataType>().Where(type => type.IsSensitive())];

    private readonly Lock _vaultLock = new();

    /// <summary>Masks one value, recording in the vault how the data spells it so the token can be filtered on later.</summary>
    /// <param name="masking">The column's plan.</param>
    /// <param name="value">The value as the database returned it.</param>
    /// <param name="masked">Set when the value was replaced by a token.</param>
    /// <returns>The value to show: a token string, the original value, or <see cref="DBNull"/>.</returns>
    public object Mask(ColumnMasking masking, object? value, out bool masked)
    {
        masked = false;
        if (value is null or DBNull)
        {
            return DBNull.Value;
        }

        var type = masking.Action switch
        {
            MaskAction.PassThrough => (SensitiveDataType?)null,
            MaskAction.Tokenize => masking.Type,
            MaskAction.CheckValues => NeedsMasking(masking.Type, CellText.From(value)) ? SensitiveDataType.FreeText : null,
            _ => IsNumber(value) && !classifier.LooksLikeSensitiveValue(CellText.From(value)) ? null : SensitiveDataType.FreeText,
        };

        if (type is null)
        {
            return value;
        }

        masked = true;
        return Tokenize(type.Value, CellText.From(value)!);
    }

    /// <summary>
    /// Finds every form of the value a token stands for: the normalized value and each spelling the data was seen to
    /// store it in, so a filter matches the value in any source.
    /// </summary>
    /// <param name="type">The masked column's type.</param>
    /// <param name="token">A literal the caller compared with that column.</param>
    /// <param name="values">The values to compare with instead, each once; empty when the literal isn't a token.</param>
    /// <returns><see langword="true"/> when the literal is a known token.</returns>
    public bool TryResolve(SensitiveDataType type, string token, out IReadOnlyList<string> values)
    {
        lock (_vaultLock)
        {
            if (!vault.TryGetOriginal(type, token, out var normalized))
            {
                values = [];
                return false;
            }

            values = vault.GetSpellings(type, token).Prepend(normalized).Distinct(StringComparer.Ordinal).ToList();
            return true;
        }
    }

    /// <summary>
    /// Finds the one value to store when a caller writes a token into a masked column of its type. A token stands for
    /// one value but may have been seen in several spellings; the first spelling in ordinal order is used so the
    /// choice is the same every time, and a token with no recorded spelling (issued elsewhere) gives its normalized
    /// value.
    /// </summary>
    /// <param name="type">The masked column's type.</param>
    /// <param name="token">The value the caller wrote.</param>
    /// <param name="value">The real value to store, when <paramref name="token"/> is a token of <paramref name="type"/>.</param>
    /// <returns><see langword="true"/> when it is a known token of that type.</returns>
    public bool TryResolveForWrite(SensitiveDataType type, string token, [NotNullWhen(true)] out string? value)
    {
        lock (_vaultLock)
        {
            if (!vault.TryGetOriginal(type, token, out var normalized))
            {
                value = null;
                return false;
            }

            value = vault.GetSpellings(type, token).Order(StringComparer.Ordinal).FirstOrDefault() ?? normalized;
            return true;
        }
    }

    /// <summary>Whether <paramref name="text"/> is a token the vault holds for any sensitive type.</summary>
    /// <param name="text">A value a caller wrote.</param>
    /// <returns><see langword="true"/> when some type's vault entry has this token.</returns>
    public bool IsToken(string text)
    {
        lock (_vaultLock)
        {
            return SensitiveTypes.Any(type => vault.ContainsToken(type, text));
        }
    }

    private string Tokenize(SensitiveDataType type, string raw)
    {
        lock (_vaultLock)
        {
            var token = tokenizer.Tokenize(type, raw);

            // Only vault tokens map back to one value; transformed tokens (a ZIP prefix) stand for many, so they
            // can't be turned back into a filter value.
            if (vault.ContainsToken(type, token))
            {
                vault.AddSpelling(type, token, raw);
            }

            return token;
        }
    }

    /// <summary>Whether a value in a column reviewed as not sensitive must be masked anyway.</summary>
    private bool NeedsMasking(SensitiveDataType type, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (classifier.LooksLikeSensitiveValue(text))
        {
            return true;
        }

        return type switch
        {
            SensitiveDataType.Money => !MoneyDetector.LooksLikeMoney([text]),
            SensitiveDataType.Code => !CodeDetector.LooksLikeCode([text]),
            _ => false,
        };
    }

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
