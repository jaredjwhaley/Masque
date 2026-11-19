namespace Masque.Core.Query;

/// <summary>What a statement does.</summary>
public enum StatementKind
{
    /// <summary>Reads rows.</summary>
    Select,

    /// <summary>Adds rows.</summary>
    Insert,

    /// <summary>Changes rows.</summary>
    Update,

    /// <summary>Removes rows.</summary>
    Delete,
}

/// <summary>A table a statement reads or writes.</summary>
/// <param name="Schema">The schema, such as <c>dbo</c>, when the statement names one.</param>
/// <param name="Name">The table's name, without quotes.</param>
public sealed record TableReference(string? Schema, string Name)
{
    /// <summary>The name with its schema, such as <c>dbo.People</c>, or just the name.</summary>
    public string Qualified => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// A string literal compared with a masked column. If it is a token Masque issued, it stands for the real value,
/// so the caller can filter on a token they were shown.
/// </summary>
/// <param name="Literal">The literal's value, without quotes.</param>
/// <param name="Type">The masked column's type, which says which vault entries the token could match.</param>
/// <param name="Start">Where the literal starts in the SQL, including its quote (or <c>N</c> prefix).</param>
/// <param name="Length">How many characters the literal covers in the SQL.</param>
/// <param name="Comparison">
/// The condition when the literal is one side of <c>=</c> or <c>&lt;&gt;</c>; null when it is an item in an
/// <c>IN (...)</c> list.
/// </param>
public sealed record TokenFilter(string Literal, SensitiveDataType Type, int Start, int Length, TokenComparison? Comparison);

/// <summary>
/// A whole <c>column = literal</c> or <c>literal &lt;&gt; column</c> condition holding a token filter. A token can
/// stand for a value stored in several spellings, so the condition is rewritten as <c>column [NOT] IN (...)</c>.
/// </summary>
/// <param name="Start">Where the condition starts in the SQL.</param>
/// <param name="Length">How many characters the condition covers.</param>
/// <param name="ColumnStart">Where the column starts in the SQL, with any qualifier and quoting (<c>p.[SSN]</c>).</param>
/// <param name="ColumnLength">How many characters the column covers.</param>
/// <param name="Negated">True for <c>&lt;&gt;</c> and <c>!=</c>, false for <c>=</c> and <c>==</c>.</param>
public sealed record TokenComparison(int Start, int Length, int ColumnStart, int ColumnLength, bool Negated);

/// <summary>What <see cref="SqlStatement.Review"/> found in a statement that passed.</summary>
/// <param name="MaskedNames">
/// The masked column names the statement can return, including aliases given to masked columns, each with its
/// column's type (case-insensitive). A result column with one of these names is masked as that type.
/// </param>
/// <param name="TokenFilters">String literals compared with masked columns, in the order they appear.</param>
public sealed record StatementReview(IReadOnlyDictionary<string, SensitiveDataType> MaskedNames, IReadOnlyList<TokenFilter> TokenFilters);

/// <summary>
/// One SQL statement, checked before it runs. <see cref="Parse"/> accepts a single SELECT, INSERT, UPDATE or DELETE
/// (optionally after WITH) and lists the tables it touches; <see cref="Review"/> then checks how it uses the columns
/// masked for the caller. Works on SQLite and SQL Server syntax, and refuses whatever it doesn't recognise.
/// </summary>
/// <remarks>
/// This reads a token stream rather than building a full syntax tree: it tracks which parentheses open subqueries,
/// function calls or plain groups, and which clause each one is in, which is all the rules need. Refusal messages
/// name columns and rules, never values from the query, so they are safe to show.
/// </remarks>
public sealed class SqlStatement
{
    private static readonly IReadOnlyDictionary<string, SensitiveDataType> NothingMasked = new Dictionary<string, SensitiveDataType>();

    private readonly List<SqlToken> _tokens;

    private SqlStatement(List<SqlToken> tokens, StatementKind kind, IReadOnlyList<TableReference> tables)
    {
        _tokens = tokens;
        Kind = kind;
        Tables = tables;
    }

    /// <summary>What the statement does.</summary>
    public StatementKind Kind { get; }

    /// <summary>Every table the statement reads or writes, once each, in order of appearance (names from WITH excluded).</summary>
    public IReadOnlyList<TableReference> Tables { get; }

    /// <summary>Reads one statement and checks its shape.</summary>
    /// <param name="sql">The SQL text; one trailing semicolon is fine.</param>
    /// <returns>The parsed statement.</returns>
    /// <exception cref="QueryRejectedException">
    /// It is empty, holds more than one statement, changes schema or settings, runs code, reaches files or other
    /// databases, or uses syntax Masque can't trace (set operators, SELECT INTO, OUTPUT/RETURNING and the like).
    /// </exception>
    public static SqlStatement Parse(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new QueryRejectedException("The query is empty. Send one SELECT, INSERT, UPDATE or DELETE statement.");
        }

        var tokens = SqlLexer.Tokenize(sql);
        while (tokens.Count > 0 && tokens[^1].IsSymbol(";"))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var result = new SqlWalker(tokens, NothingMasked).Walk();
        return new SqlStatement(tokens, result.Kind, result.Tables);
    }

    /// <summary>Checks how the statement uses masked columns.</summary>
    /// <param name="masked">The columns masked for this caller, by column name, with their types.</param>
    /// <returns>The masked names the statement can return and the literals it compares with masked columns.</returns>
    /// <exception cref="QueryRejectedException">
    /// A masked column is sorted on, pattern-matched, range-compared, passed to a function other than COUNT, used in
    /// arithmetic, compared with an unmasked column, changed by UPDATE, or copied into another table or column.
    /// </exception>
    public StatementReview Review(IReadOnlyDictionary<string, SensitiveDataType> masked)
    {
        ArgumentNullException.ThrowIfNull(masked);

        var names = SqlWalker.TrackAliases(_tokens, masked);
        var result = new SqlWalker(_tokens, names).Walk();
        return new StatementReview(names, result.TokenFilters);
    }
}
