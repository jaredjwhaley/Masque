using Masque.Core.Query;
using Microsoft.Data.Sqlite;
using static Masque.Data.Tests.MaskingConnectionTests;
using static Masque.Data.Tests.TestSupport.MasqueHarness;

namespace Masque.Data.Tests;

public sealed class WriteAndSafetyTests
{
    /// <summary>Writes need a policy that allows them.</summary>
    [Theory]
    [InlineData("INSERT INTO Orders (Id, PersonSsn, Amount) VALUES (12, '900-44-4444', 1)")]
    [InlineData("UPDATE People SET Visits = 9 WHERE Id = 1")]
    [InlineData("DELETE FROM Orders WHERE Id = 10")]
    public void Writes_are_refused_without_write_access(string sql)
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Should.Throw<QueryRejectedException>(() => Execute(connection, sql)).Message.ShouldContain("write");
    }

    /// <summary>With write access, inserts (with values the caller supplies), updates of safe columns and deletes run.</summary>
    [Theory]
    [InlineData("INSERT INTO People (Id, FullName, Ssn, Email, Visits) VALUES (3, 'Cara Test', '900-33-3333', 'cara@example.test', 0)")]
    [InlineData("UPDATE People SET Visits = 9 WHERE Id = 1")]
    [InlineData("DELETE FROM Orders WHERE Id = 10")]
    public void Writes_run_with_write_access(string sql)
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Execute(connection, sql).ShouldBe(1);
    }

    /// <summary>A masked column is never changed through Masque.</summary>
    [Fact]
    public void Updating_a_masked_column_is_refused()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Should.Throw<QueryRejectedException>(() => Execute(connection, "UPDATE People SET Ssn = '900-00-0000' WHERE Id = 1"))
            .Message.ShouldContain("Ssn");
    }

    /// <summary>A masked value can't be copied into another column, where it would no longer be masked.</summary>
    [Fact]
    public void Copying_a_masked_column_is_refused()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Should.Throw<QueryRejectedException>(() =>
            Execute(connection, "INSERT INTO Orders (Id, PersonSsn, Amount) SELECT Id + 100, Ssn, Visits FROM People"));
    }

    /// <summary>
    /// Once wrapped, the SQLite connection itself refuses schema changes and file access, even through the raw
    /// connection, as a second line of defence behind the SQL checks.
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE Copied (Value TEXT)")]
    [InlineData("DROP TABLE Orders")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("SELECT load_extension('anything')")]
    public void Wrapped_sqlite_connection_refuses_schema_and_file_access(string sql)
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Should.Throw<SqliteException>(() => harness.Run(sql));
    }

    /// <summary>The raw connection can't write behind Masque's back either; writes only run through a checked command.</summary>
    [Fact]
    public void Wrapped_sqlite_connection_refuses_unchecked_writes()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Should.Throw<SqliteException>(() => harness.Run("UPDATE People SET Ssn = 'x'"));
    }
}
