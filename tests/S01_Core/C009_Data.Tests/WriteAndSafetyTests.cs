using Masque.Core;
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

    /// <summary>
    /// Masque only prevents exposure, so a caller allowed to write may set a masked column to a value they supply;
    /// it is stored exactly as written.
    /// </summary>
    [Fact]
    public void Updating_a_masked_column_with_a_supplied_value_runs()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Execute(connection, "UPDATE People SET Ssn = '900-00-0000' WHERE Id = 1").ShouldBe(1);

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe("900-00-0000");
    }

    /// <summary>
    /// A token the caller was shown stands for its real value, so writing it into a masked column of its type stores
    /// the real value (here, Alice's SSN copied onto Bob), never the fake.
    /// </summary>
    [Fact]
    public void Token_written_to_a_masked_column_is_stored_as_its_real_value()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        Execute(connection, $"UPDATE People SET Ssn = '{token}' WHERE Id = 2").ShouldBe(1);

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 2").ShouldBe(AliceSsn);
    }

    /// <summary>
    /// A token in a named parameter is swapped too (Dapper and EF Core pass values that way), and the caller's command
    /// is left exactly as it was: same text, same parameters, token still in place.
    /// </summary>
    [Fact]
    public void Token_in_a_parameter_is_written_as_its_real_value()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        using var command = connection.CreateCommand();
        const string sql = "INSERT INTO Orders (Id, PersonSsn, Amount) VALUES (12, @ssn, 1)";
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ssn";
        parameter.Value = token;
        command.Parameters.Add(parameter);

        command.ExecuteNonQuery().ShouldBe(1);

        harness.RawScalar("SELECT PersonSsn FROM Orders WHERE Id = 12").ShouldBe(AliceSsn);
        command.CommandText.ShouldBe(sql);
        command.Parameters.Count.ShouldBe(1);
        command.Parameters[0].Value.ShouldBe(token);
    }

    /// <summary>
    /// When the data stores a value in several spellings, a written token takes the first spelling in ordinal order,
    /// so the choice is the same on every run ('900 11 1111' sorts before '900-11-1111': a space before a dash).
    /// </summary>
    [Fact]
    public void Token_with_several_spellings_writes_the_first_in_ordinal_order()
    {
        using var harness = Create();
        harness.Run("UPDATE Orders SET PersonSsn = '900 11 1111' WHERE Id = 10");
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;
        Scalar(connection, "SELECT PersonSsn FROM Orders WHERE Id = 10").ShouldBe(token);

        Execute(connection, $"UPDATE People SET Ssn = '{token}' WHERE Id = 2");

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 2").ShouldBe("900 11 1111");
    }

    /// <summary>A token with no recorded spelling (issued elsewhere, such as by the CLI) writes its normalized value.</summary>
    [Fact]
    public void Token_without_a_spelling_writes_its_normalized_value()
    {
        using var harness = Create();
        harness.Vault.Add(SensitiveDataType.Ssn, "900555555", "900-77-0000");
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        Execute(connection, "UPDATE People SET Ssn = '900-77-0000' WHERE Id = 2");

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 2").ShouldBe("900555555");
    }

    /// <summary>
    /// A token written where its column isn't a masked column of its type would be stored as a fake (or, swapped,
    /// land somewhere readable), so it is refused: no column list, an INSERT ... SELECT, or a column of another type.
    /// Nothing is written, and the message never quotes the value.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO People VALUES (3, 'Cara Test', '{0}', 'cara@example.test', 0)")]
    [InlineData("INSERT INTO Orders (Id, PersonSsn, Amount) SELECT 13, '{0}', 1")]
    [InlineData("UPDATE People SET FullName = '{0}' WHERE Id = 2")]
    public void Token_written_outside_a_masked_column_of_its_type_is_refused(string template)
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        var error = Should.Throw<QueryRejectedException>(() => Execute(connection, string.Format(template, token)));

        error.Message.ShouldContain("token");
        error.Message.ShouldNotContain(token);
        harness.RawScalar("SELECT COUNT(*) FROM People WHERE Id = 3").ShouldBe(0L);
        harness.RawScalar("SELECT COUNT(*) FROM Orders WHERE Id = 13").ShouldBe(0L);
        harness.RawScalar("SELECT FullName FROM People WHERE Id = 2").ShouldBe("Bob Sample");
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
