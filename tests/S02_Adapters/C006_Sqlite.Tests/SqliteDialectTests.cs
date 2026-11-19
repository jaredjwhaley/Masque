using Masque.Core.Query;
using Masque.Core.Tables;
using Masque.Data.Dialects;
using Microsoft.Data.Sqlite;

namespace Masque.Sqlite.Tests;

/// <summary>
/// Pins down the SQLite side of Masque: tables loaded from files become queryable with sensible types and stay
/// read-only, existing database files open, every result column reports the table column it came from, and errors
/// that could hold data are hidden.
/// </summary>
public sealed class SqliteDialectTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("masque-sqlite-tests-").FullName;
    private readonly SqliteDialect _dialect = new();
    private readonly SqliteConnection _loaded = SqliteSources.LoadTables(
    [
        new TableData("people", ["Id", "First Name", "Zip", "GrossPay"],
        [
            ["1", "Zebulon", "02134", "1,234.50"],
            ["2", "Lee", "90210", "980"],
            ["3", "Ana", "", "20.25"],
        ]),
        new TableData("visits", ["PersonId", "Reason"], [["1", "checkup"], ["1", "flu"], ["2", "checkup"]]),
    ]);

    public void Dispose()
    {
        _loaded.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Loaded tables are listed by their names, without a schema.</summary>
    [Fact]
    public void ListTables_ReturnsLoadedTables() =>
        _dialect.ListTables(_loaded, null).Select(t => t.Qualified).ShouldBe(["people", "visits"]);

    /// <summary>The main database's tables need no schema, so <c>main.people</c> matches <c>people</c>.</summary>
    [Fact]
    public void Normalize_DropsMainSchema() =>
        _dialect.Normalize(new TableReference("main", "people")).ShouldBe(new TableReference(null, "people"));

    /// <summary>Column names become plain SQL names (spaces become underscores) so queries don't need quoting.</summary>
    [Fact]
    public void LoadTables_SanitizesHeaders() =>
        Headers(_loaded, "people").ShouldBe(["Id", "First_Name", "Zip", "GrossPay"]);

    /// <summary>Blank, duplicate and digit-first column names get unique, valid names.</summary>
    [Fact]
    public void LoadTables_MakesColumnNamesUniqueAndValid()
    {
        using var connection = SqliteSources.LoadTables([new TableData("my sheet!", ["Id", "id", "", "2020 Total"], [["1", "2", "3", "4"]])]);

        _dialect.ListTables(connection, null).ShouldHaveSingleItem().Name.ShouldBe("my_sheet");
        Headers(connection, "my_sheet").ShouldBe(["Id", "id_2", "Column3", "c_2020_Total"]);
    }

    /// <summary>
    /// Columns whose values are all plain numbers load as numbers, so sums and comparisons work; values with
    /// leading zeros (ZIP codes, IDs) stay text so nothing is lost. Blank cells load as NULL.
    /// </summary>
    [Fact]
    public void LoadTables_TypesNumbersKeepsLeadingZerosAndNullsBlanks()
    {
        Scalar(_loaded, "SELECT SUM(GrossPay) FROM people").ShouldBe(2234.75);
        Scalar(_loaded, "SELECT COUNT(*) FROM people WHERE GrossPay > 100").ShouldBe(2L);
        Scalar(_loaded, "SELECT Zip FROM people WHERE Id = 1").ShouldBe("02134");
        Scalar(_loaded, "SELECT COUNT(*) FROM people WHERE Zip IS NULL").ShouldBe(1L);
    }

    /// <summary>Tables loaded from files are a copy, so the database refuses writes instead of losing them silently.</summary>
    [Fact]
    public void LoadTables_IsReadOnly()
    {
        var error = Should.Throw<SqliteException>(() => Scalar(_loaded, "DELETE FROM people"));

        _dialect.Describe(error).Message.ShouldContain("read-only");
    }

    /// <summary>A result column read from a table reports that table and column, even when renamed or joined.</summary>
    [Fact]
    public void ReadColumns_ReportsLineage()
    {
        var columns = Columns("SELECT p.First_Name AS who, v.Reason FROM people p JOIN visits v ON v.PersonId = p.Id");

        columns[0].ShouldBe(new ResultColumn("who", 0, new TableReference(null, "people"), "First_Name"));
        columns[1].ShouldBe(new ResultColumn("Reason", 1, new TableReference(null, "visits"), "Reason"));
    }

    /// <summary>Computed columns (aggregates, expressions) report no lineage.</summary>
    [Fact]
    public void ReadColumns_ComputedColumnsHaveNoLineage() =>
        Columns("SELECT COUNT(*), upper(First_Name) FROM people").ShouldAllBe(c => !c.IsTraced);

    /// <summary>A mistake in the query text is shown, since the message only repeats the SQL.</summary>
    [Fact]
    public void Describe_SyntaxError_IsShown() =>
        _dialect.Describe(Should.Throw<SqliteException>(() => Scalar(_loaded, "SELECT nope FROM people"))).Message.ShouldContain("nope");

    /// <summary>Other errors (a constraint that names a value, say) are hidden.</summary>
    [Fact]
    public void Describe_OtherErrors_AreHidden()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Scalar(connection, "CREATE TABLE t (v TEXT CHECK (v <> 'Zebulon'))");

        var error = Should.Throw<SqliteException>(() => Scalar(connection, "INSERT INTO t VALUES ('Zebulon')"));

        _dialect.Describe(error).Message.ShouldNotContain("Zebulon");
    }

    /// <summary>An existing database file opens, and a missing one is an error rather than a new empty file.</summary>
    [Fact]
    public void OpenDatabase_OpensExistingFilesOnly()
    {
        var path = Path.Combine(_folder, "data.db");
        Should.Throw<FileNotFoundException>(() => SqliteSources.OpenDatabase(path));

        using (var setup = new SqliteConnection($"Data Source={path};Pooling=false"))
        {
            setup.Open();
            Scalar(setup, "CREATE TABLE patients (Mrn TEXT, Name TEXT)");
        }

        using var connection = SqliteSources.OpenDatabase(path);
        _dialect.ListTables(connection, null).ShouldHaveSingleItem().Name.ShouldBe("patients");
    }

    /// <summary>A file that isn't a database is refused with a safe message.</summary>
    [Fact]
    public void OpenDatabase_NotADatabase_IsRefused()
    {
        var path = Path.Combine(_folder, "notes.db");
        File.WriteAllText(path, "this is not a database file, just text long enough to fill a header page");

        Should.Throw<QueryFailedException>(() => SqliteSources.OpenDatabase(path));
    }

    private IReadOnlyList<string> Headers(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.SampleSql(new TableReference(null, table), 0);
        using var reader = command.ExecuteReader();
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
    }

    private IReadOnlyList<ResultColumn> Columns(string sql)
    {
        using var command = _loaded.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return _dialect.ReadColumns(reader);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
