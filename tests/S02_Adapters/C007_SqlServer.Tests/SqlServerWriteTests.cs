using Masque.Core.Policy;
using Masque.Core.Query;
using static Masque.SqlServer.Tests.SqlServerHarness;

namespace Masque.SqlServer.Tests;

/// <summary>
/// Runs writes through <see cref="Masque.Data.MasqueGate"/> against a real SQL Server (LocalDB), in a database of their
/// own so they can't disturb the read tests: callers without write access are refused, a token written into a masked
/// column of its type is stored as its real value, and laundering a masked value into another column is refused.
/// </summary>
public sealed class SqlServerWriteTests(LocalDbFixture fixture) : IClassFixture<LocalDbFixture>, IDisposable
{
    private const string LeeSsn = "987-65-4321";

    private readonly SqlServerHarness? _harness = fixture.ConnectionString is null ? null : new SqlServerHarness(fixture.ConnectionString);

    /// <inheritdoc />
    public void Dispose() => _harness?.Dispose();

    /// <summary>The harness, or a skip when no SQL Server is reachable.</summary>
    private SqlServerHarness Harness
    {
        get
        {
            Assert.SkipWhen(_harness is null,
                "No SQL Server reachable: start LocalDB or set MASQUE_TEST_SQLSERVER to a server connection string.");
            return _harness!;
        }
    }

    /// <summary>A grant that may write and shows nothing raw.</summary>
    private static AccessGrant Writer => new([], canWrite: true);

    /// <summary>A caller without write access is refused every kind of write, and nothing changes.</summary>
    [Theory]
    [InlineData("INSERT INTO visits (VisitId, PersonId, Reason) VALUES (90, 2, N'follow-up')")]
    [InlineData("UPDATE visits SET Reason = N'changed' WHERE VisitId = 12")]
    [InlineData("DELETE FROM visits WHERE VisitId = 12")]
    public void Writes_are_refused_without_write_access(string sql)
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        Should.Throw<QueryRejectedException>(() => Command(connection, sql).ExecuteNonQuery()).Message.ShouldContain("write");

        Harness.RawScalar("SELECT COUNT(*) FROM visits").ShouldBe(3);
        Harness.RawScalar("SELECT Reason FROM visits WHERE VisitId = 12").ShouldBe("checkup");
    }

    /// <summary>
    /// A token written as a literal into a masked column of its type is swapped for its real value before it reaches
    /// SQL Server, so the table never stores a fake value.
    /// </summary>
    [Fact]
    public void Token_literal_written_to_a_masked_column_is_stored_as_its_real_value()
    {
        using var gate = Harness.Gate(Writer);
        using var connection = Harness.OpenWrapped(gate);
        var token = (string)Scalar(connection, "SELECT Ssn FROM people WHERE Id = 2")!;

        Command(connection, $"UPDATE people SET Ssn = '{token}' WHERE Id = 3").ExecuteNonQuery().ShouldBe(1);

        Harness.RawScalar("SELECT Ssn FROM people WHERE Id = 3").ShouldBe(LeeSsn);
        Harness.RawRun("UPDATE people SET Ssn = NULL WHERE Id = 3");
    }

    /// <summary>A token passed as a SQL Server parameter is swapped for its real value the same way.</summary>
    [Fact]
    public void Token_parameter_written_to_a_masked_column_is_stored_as_its_real_value()
    {
        using var gate = Harness.Gate(Writer);
        using var connection = Harness.OpenWrapped(gate);
        var token = (string)Scalar(connection, "SELECT Ssn FROM people WHERE Id = 1")!;

        Command(connection, "INSERT INTO people (Id, FirstName, Ssn, GrossPay) VALUES (50, N'Pat', @ssn, 1)", ("@ssn", token))
            .ExecuteNonQuery().ShouldBe(1);

        Harness.RawScalar("SELECT Ssn FROM people WHERE Id = 50").ShouldBe(ZebulonSsn);
        Harness.RawRun("DELETE FROM people WHERE Id = 50");
    }

    /// <summary>
    /// Copying a masked column into another column, or a token into a column that isn't masked for its type, is
    /// refused: either would put a real value where it could be read back unmasked. Nothing changes.
    /// </summary>
    [Theory]
    [InlineData("UPDATE people SET FirstName = Ssn WHERE Id = 1")]
    [InlineData("UPDATE visits SET Reason = (SELECT Ssn FROM people WHERE Id = 1) WHERE VisitId = 10")]
    [InlineData("INSERT INTO visits (VisitId, PersonId, Reason) SELECT Id + 100, Id, Ssn FROM people")]
    [InlineData("UPDATE visits SET Reason = '{0}' WHERE VisitId = 10")]
    public void Laundering_a_masked_value_is_refused(string template)
    {
        using var gate = Harness.Gate(Writer);
        using var connection = Harness.OpenWrapped(gate);
        var token = (string)Scalar(connection, "SELECT Ssn FROM people WHERE Id = 1")!;

        Should.Throw<QueryRejectedException>(() => Command(connection, string.Format(template, token)).ExecuteNonQuery())
            .Message.ShouldNotContain(ZebulonSsn);

        Harness.RawScalar("SELECT FirstName FROM people WHERE Id = 1").ShouldBe(ZebulonName);
        Harness.RawScalar("SELECT Reason FROM visits WHERE VisitId = 10").ShouldBe("checkup");
        Harness.RawScalar("SELECT COUNT(*) FROM visits").ShouldBe(3);
    }
}
