using System.Text;

namespace Masque.Core.Query;

/// <summary>What a piece of SQL text is, as <see cref="SqlLexer"/> sees it.</summary>
internal enum SqlTokenKind
{
    /// <summary>A keyword or unquoted name, such as <c>SELECT</c> or <c>People</c>.</summary>
    Word,

    /// <summary>A name in <c>"double quotes"</c>, <c>[brackets]</c> or <c>`backticks`</c>.</summary>
    QuotedName,

    /// <summary>A string literal, such as <c>'abc'</c> or <c>N'abc'</c>.</summary>
    String,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A parameter placeholder, such as <c>@id</c>, <c>:id</c>, <c>$1</c> or <c>?</c>.</summary>
    Parameter,

    /// <summary>An operator or punctuation mark.</summary>
    Symbol,
}

/// <summary>One token of SQL text.</summary>
/// <param name="Kind">What the token is.</param>
/// <param name="Text">
/// The token's meaning: a name or literal without its quotes (doubled quotes undone), otherwise the text as written.
/// </param>
/// <param name="Start">Where the token starts in the SQL, including any quote or <c>N</c> prefix.</param>
/// <param name="Length">How many characters the token covers in the SQL.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Start, int Length)
{
    /// <summary>Stands in for a token before the start or past the end of the statement; it matches nothing.</summary>
    public static SqlToken None { get; } = new(SqlTokenKind.Symbol, string.Empty, -1, 0);

    /// <summary>Whether this is the given keyword (case-insensitive). Quoted names never count as keywords.</summary>
    public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is the given operator or punctuation mark.</summary>
    public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;
}

/// <summary>
/// Splits SQL into tokens, dropping whitespace and comments. Strings and quoted names are read as single tokens, so
/// keywords inside them are never mistaken for code. Anything left open (string, name, comment) is refused, because
/// it could hide code from the checks that follow.
/// </summary>
internal static class SqlLexer
{
    /// <summary>Operators longer than one character, longest first so <c>-&gt;&gt;</c> wins over <c>-&gt;</c>.</summary>
    private static readonly string[] LongSymbols = ["->>", "<>", "!=", "<=", ">=", "==", "||", "::", "->", "<<", ">>"];

    /// <summary>Splits SQL into tokens.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <returns>The tokens in order.</returns>
    /// <exception cref="QueryRejectedException">A string, quoted name or comment is never closed, or comments nest.</exception>
    public static List<SqlToken> Tokenize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var tokens = new List<SqlToken>();
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && next == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '\'')
            {
                i = ReadQuoted(sql, i, i, '\'', SqlTokenKind.String, tokens);
            }
            else if ((c is 'N' or 'n' or 'X' or 'x') && next == '\'')
            {
                // N'...' (Unicode) and X'...' (bytes) are string literals with a one-letter prefix.
                i = ReadQuoted(sql, i, i + 1, '\'', SqlTokenKind.String, tokens);
            }
            else if (c is '"' or '`')
            {
                i = ReadQuoted(sql, i, i, c, SqlTokenKind.QuotedName, tokens);
            }
            else if (c == '[')
            {
                i = ReadQuoted(sql, i, i, ']', SqlTokenKind.QuotedName, tokens);
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                i = ReadWhile(sql, i, ch => char.IsLetterOrDigit(ch) || ch is '.' or '_', SqlTokenKind.Number, tokens);
            }
            else if ((c is '@' or ':' && (IsWordStart(next) || next == '@')) || (c == '$' && char.IsLetterOrDigit(next)))
            {
                i = ReadWhile(sql, i, ch => IsWordPart(ch) || ch == '@', SqlTokenKind.Parameter, tokens, skipFirst: true);
            }
            else if (c == '?')
            {
                i = ReadWhile(sql, i, char.IsDigit, SqlTokenKind.Parameter, tokens, skipFirst: true);
            }
            else if (IsWordStart(c))
            {
                i = ReadWhile(sql, i, IsWordPart, SqlTokenKind.Word, tokens);
            }
            else
            {
                var symbol = LongSymbols.FirstOrDefault(s => string.CompareOrdinal(sql, i, s, 0, s.Length) == 0) ?? c.ToString();
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, symbol, i, symbol.Length));
                i += symbol.Length;
            }
        }

        return tokens;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '#';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>Returns the index just past a <c>/* */</c> comment.</summary>
    /// <remarks>
    /// SQL Server lets block comments nest and SQLite doesn't, so the same text can end a comment in different places.
    /// A comment opener inside a comment is refused rather than guessing which database is right.
    /// </remarks>
    private static int SkipBlockComment(string sql, int start)
    {
        var end = sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Unterminated("comment");
        }

        if (sql.IndexOf("/*", start + 2, end - (start + 2), StringComparison.Ordinal) >= 0)
        {
            throw new QueryRejectedException(
                "The query has a comment inside a comment. Databases disagree on where those end, so Masque refuses them.");
        }

        return end + 2;
    }

    /// <summary>Reads a quoted token; a doubled closing quote is an escaped quote. Returns the index past the close.</summary>
    private static int ReadQuoted(string sql, int tokenStart, int quoteIndex, char close, SqlTokenKind kind, List<SqlToken> tokens)
    {
        var text = new StringBuilder();
        var i = quoteIndex + 1;

        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }

                tokens.Add(new SqlToken(kind, text.ToString(), tokenStart, i + 1 - tokenStart));
                return i + 1;
            }

            text.Append(sql[i]);
            i++;
        }

        throw Unterminated(kind == SqlTokenKind.String ? "string" : "quoted name");
    }

    /// <summary>Reads characters while <paramref name="part"/> holds. Returns the index past the token.</summary>
    private static int ReadWhile(string sql, int start, Func<char, bool> part, SqlTokenKind kind, List<SqlToken> tokens, bool skipFirst = false)
    {
        var i = skipFirst ? start + 1 : start;
        while (i < sql.Length && part(sql[i]))
        {
            i++;
        }

        tokens.Add(new SqlToken(kind, sql[start..i], start, i - start));
        return i;
    }

    private static QueryRejectedException Unterminated(string what) =>
        new($"The query has an unterminated {what}. Close it and try again.");
}
