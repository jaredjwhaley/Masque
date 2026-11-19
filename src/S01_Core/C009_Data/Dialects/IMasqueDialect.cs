using System.Data;
using System.Data.Common;
using Masque.Core.Query;

namespace Masque.Data.Dialects;

/// <summary>One column of a result, and the table column it was read from when the database can say.</summary>
/// <param name="Name">The column's name in the result.</param>
/// <param name="Ordinal">Its position in the database's reader (hidden columns included).</param>
/// <param name="Table">The table it was read from, or <see langword="null"/> for computed values.</param>
/// <param name="Column">The table column it was read from, or <see langword="null"/> for computed values.</param>
public sealed record ResultColumn(string Name, int Ordinal, TableReference? Table, string? Column)
{
    /// <summary>Whether the database traced this column to a table column.</summary>
    public bool IsTraced => Table is not null && Column is not null;
}

/// <summary>
/// What Masque needs to know about one kind of database (SQLite, SQL Server, ...): how to list its tables, sample
/// them, trace result columns to their source, guard the connection and describe errors without leaking data.
/// </summary>
/// <remarks>
/// Each database package supplies its own dialect, so supporting a new database means adding a class rather than
/// changing Masque (the Open/Closed principle).
/// </remarks>
public interface IMasqueDialect
{
    /// <summary>
    /// Extra reader behavior needed for <see cref="ReadColumns"/> to see source tables, such as
    /// <see cref="CommandBehavior.KeyInfo"/> on SQL Server.
    /// </summary>
    CommandBehavior LineageBehavior { get; }

    /// <summary>Whether this dialect handles the given connection.</summary>
    /// <param name="connection">The raw connection.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    bool Supports(DbConnection connection);

    /// <summary>Lists the columns a caller sees in a result, with their source when the database reports it.</summary>
    /// <param name="reader">An open reader, before its first row.</param>
    /// <returns>The visible columns, in order; columns the database added for its own use are left out.</returns>
    IReadOnlyList<ResultColumn> ReadColumns(DbDataReader reader);

    /// <summary>Lists the user tables and views a caller may query.</summary>
    /// <param name="connection">The open raw connection.</param>
    /// <param name="transaction">The connection's current transaction, if any.</param>
    /// <returns>Every listed table, with names as the database stores them.</returns>
    IReadOnlyList<TableReference> ListTables(DbConnection connection, DbTransaction? transaction);

    /// <summary>
    /// Puts a table name into the form <see cref="ListTables"/> uses, such as filling in a default schema, so the two
    /// can be compared.
    /// </summary>
    /// <param name="table">A table named in a statement or reported by a reader.</param>
    /// <returns>The same table, normalized.</returns>
    TableReference Normalize(TableReference table);

    /// <summary>SQL that reads up to <paramref name="rows"/> rows of every column of a listed table.</summary>
    /// <param name="table">A table returned by <see cref="ListTables"/>, so its name is safe to quote into SQL.</param>
    /// <param name="rows">The most rows to read; zero reads only the column names.</param>
    /// <returns>The SQL text.</returns>
    string SampleSql(TableReference table, int rows);

    /// <summary>
    /// Adds the database's own guards to a newly opened connection, as a second line of defence behind the SQL checks.
    /// </summary>
    /// <param name="connection">The open raw connection.</param>
    /// <param name="writesAllowed">Says whether a checked write is running right now.</param>
    void Secure(DbConnection connection, Func<bool> writesAllowed);

    /// <summary>Turns a database error into one that is safe to show: messages that could quote data are replaced.</summary>
    /// <param name="exception">The database's error.</param>
    /// <returns>An exception with a data-free message and no inner exception.</returns>
    QueryFailedException Describe(DbException exception);
}
