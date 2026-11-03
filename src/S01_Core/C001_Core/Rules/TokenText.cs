using System.Text;

namespace Masque.Core.Rules;

/// <summary>Small text helpers shared by the rules.</summary>
internal static class TokenText
{
    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>Returns only the ASCII digits of a value.</summary>
    public static string KeepDigits(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Returns a random number as zero-padded text of the given width.</summary>
    public static string RandomDigits(IRandomSource random, int width)
    {
        var builder = new StringBuilder(width);
        for (var i = 0; i < width; i++)
        {
            builder.Append((char)('0' + random.Next(10)));
        }

        return builder.ToString();
    }

    /// <summary>Returns random uppercase hexadecimal text of the given width.</summary>
    public static string RandomHex(IRandomSource random, int width)
    {
        var builder = new StringBuilder(width);
        for (var i = 0; i < width; i++)
        {
            builder.Append(HexDigits[random.Next(HexDigits.Length)]);
        }

        return builder.ToString();
    }
}
