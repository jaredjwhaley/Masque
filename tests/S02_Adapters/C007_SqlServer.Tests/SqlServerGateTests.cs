using Masque.Core;
using Masque.Core.Policy;
using Masque.Core.Query;
using Masque.Data;
using Masque.Data.Masking;
using static Masque.SqlServer.Tests.SqlServerHarness;

namespace Masque.SqlServer.Tests;

/// <summary>
/// Runs reads through <see cref="MasqueGate"/> against a real SQL Server (LocalDB): tokens are consistent, masking
/// follows each column back to its table through joins and views, and computed text is masked. The gate's own tests
/// run on SQLite; these check the same promises hold with T-SQL and SQL Server's lineage.
/// </summary>
public sealed class SqlServerGateTests(LocalDbFixture fixture) : IClassFixture<LocalDbFixture>, IDisposable
{
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

    /// <summary>The same SSN gets the same token in separate queries and on separate connections.</summary>
    [Fact]
    public void Same_value_gets_the_same_token_across_queries_and_connections()
    {
        using var gate = Harness.Gate();
        using var first = Harness.OpenWrapped(gate);
        using var second = Harness.OpenWrapped(gate);

        var token = Scalar(first, "SELECT Ssn FROM people WHERE Id = 1");

        token.ShouldNotBeNull();
        token.ShouldNotBe(ZebulonSsn);
        Scalar(first, "SELECT Ssn FROM dbo.people WHERE Id = 1").ShouldBe(token);
        Scalar(second, "SELECT Ssn FROM people WHERE Id = 1").ShouldBe(token);
    }

    /// <summary>Across a join, each column is masked by its own table's config: the name masked, the reason not.</summary>
    [Fact]
    public void Join_masks_by_each_columns_own_table()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);
        using var command = Command(connection,
            "SELECT p.FirstName AS who, v.Reason FROM dbo.people p JOIN dbo.visits v ON v.PersonId = p.Id WHERE v.VisitId = 10");
        using var reader = (MaskingDbDataReader)command.ExecuteReader();

        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldNotBe(ZebulonName);
        reader.IsMasked(0).ShouldBeTrue();
        reader.GetString(1).ShouldBe("checkup");
        reader.IsMasked(1).ShouldBeFalse();
    }

    /// <summary>
    /// A view over a sensitive column never returns its raw value. Whether SQL Server reports the view or its base
    /// table as the source, the result is either masked or refused.
    /// </summary>
    [Fact]
    public void View_over_a_sensitive_column_never_returns_it_raw()
    {
        Harness.RawRun("CREATE OR ALTER VIEW dbo.people_view AS SELECT Id, FirstName, Ssn FROM dbo.people");
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        object? value = null;
        Exception? refusal = Record.Exception(() => value = Scalar(connection, "SELECT Ssn FROM dbo.people_view WHERE Id = 1"));

        if (refusal is null)
        {
            value.ShouldNotBeNull();
            value.ShouldNotBe(ZebulonSsn);
        }
        else
        {
            (refusal is QueryRejectedException or ReviewRequiredException).ShouldBeTrue(
                $"Expected a Masque refusal, got {refusal.GetType().Name}: {refusal.Message}");
            refusal.Message.ShouldNotContain(ZebulonSsn);
        }
    }

    /// <summary>A token written as a literal in WHERE finds the row holding its real value.</summary>
    [Fact]
    public void Token_literal_in_a_filter_finds_the_real_row()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);
        var token = (string)Scalar(connection, "SELECT Ssn FROM people WHERE Id = 1")!;

        Scalar(connection, $"SELECT Id FROM people WHERE Ssn = '{token}'").ShouldBe(1);
    }

    /// <summary>A token passed as a SQL Server parameter filters the same way as a literal.</summary>
    [Fact]
    public void Token_in_a_parameter_finds_the_real_row()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);
        var token = (string)Scalar(connection, "SELECT Ssn FROM people WHERE Id = 1")!;

        Scalar(connection, "SELECT Id FROM people WHERE Ssn = @ssn", ("@ssn", token)).ShouldBe(1);
    }

    /// <summary>
    /// A token seen on one connection filters on a later connection from a new gate, because the vault keeps every
    /// spelling (an MCP call opens a fresh connection each time).
    /// </summary>
    [Fact]
    public void Token_filter_works_on_a_later_connection()
    {
        string token;
        using (var gate = Harness.Gate())
        using (var first = Harness.OpenWrapped(gate))
        {
            token = (string)Scalar(first, "SELECT Ssn FROM people WHERE Id = 1")!;
        }

        using var laterGate = Harness.Gate();
        using var later = Harness.OpenWrapped(laterGate);
        Scalar(later, $"SELECT Id FROM people WHERE Ssn = '{token}'").ShouldBe(1);
    }

    /// <summary>
    /// T-SQL forms that could move a value out of reach of masking, or run SQL Masque never saw, are refused before
    /// they reach the server: SELECT INTO, variables, dynamic SQL, FOR JSON/XML, OPENROWSET, USE, and a second
    /// statement in the same text (T-SQL needs no semicolon between statements).
    /// </summary>
    [Theory]
    [InlineData("SELECT Ssn INTO #copied FROM people")]
    [InlineData("DECLARE @x char(11); SELECT @x = Ssn FROM people WHERE Id = 1; SELECT @x")]
    [InlineData("EXEC('SELECT Ssn FROM people')")]
    [InlineData("EXECUTE sp_executesql N'SELECT Ssn FROM people'")]
    [InlineData("SELECT Ssn FROM people FOR JSON PATH")]
    [InlineData("SELECT Ssn FROM people FOR XML PATH")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=yes', 'SELECT Ssn FROM people')")]
    [InlineData("USE master")]
    [InlineData("SELECT Id FROM people UPDATE people SET Ssn = '000-00-0000'")]
    [InlineData("SELECT Id FROM people; SELECT Ssn FROM people")]
    public void TSql_forms_that_escape_the_check_are_refused(string sql)
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        var refusal = Should.Throw<QueryRejectedException>(() => Scalar(connection, sql));

        refusal.Message.ShouldNotContain(ZebulonSsn);
        Harness.RawScalar("SELECT Ssn FROM people WHERE Id = 1").ShouldBe(ZebulonSsn);
    }

    /// <summary>
    /// Uses of a masked column that could reveal part of its value (pattern matches, lengths, substrings, ranges,
    /// sorting, casts) are refused, so a caller can't rebuild a value one guess at a time.
    /// </summary>
    [Theory]
    [InlineData("SELECT Id FROM people WHERE Ssn LIKE '1%'")]
    [InlineData("SELECT LEN(Ssn) FROM people")]
    [InlineData("SELECT SUBSTRING(Ssn, 1, 3) FROM people")]
    [InlineData("SELECT Id FROM people WHERE LEFT(Ssn, 1) = '1'")]
    [InlineData("SELECT Id FROM people WHERE Ssn > '5'")]
    [InlineData("SELECT Id FROM people WHERE Ssn BETWEEN '1' AND '2'")]
    [InlineData("SELECT Id FROM people ORDER BY Ssn")]
    [InlineData("SELECT CAST(Ssn AS varchar(3)) FROM people")]
    [InlineData("SELECT Id FROM people WHERE CHARINDEX('45', Ssn) > 0")]
    [InlineData("SELECT CASE WHEN Ssn LIKE '1%' THEN 1 ELSE 0 END FROM people")]
    public void Probing_a_masked_column_is_refused(string sql)
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        Should.Throw<QueryRejectedException>(() => Scalar(connection, sql)).Message.ShouldNotContain(ZebulonSsn);
    }

    /// <summary>
    /// Ordinary T-SQL read syntax still works, so the refusals above aren't just refusing everything: TOP, bracketed
    /// names, N'' literals and schema-qualified tables.
    /// </summary>
    [Fact]
    public void Ordinary_TSql_reads_still_run()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        Scalar(connection, "SELECT TOP (1) [Id] FROM [dbo].[people] WHERE [FirstName] IS NOT NULL ORDER BY [Id]").ShouldBe(1);
        Scalar(connection, "SELECT COUNT(*) FROM dbo.visits WHERE Reason = N'checkup'").ShouldBe(2);
    }

    /// <summary>
    /// A duplicate-key error (2601), whose raw message quotes the duplicate value, reaches the caller with only its
    /// number: the SSN it would have quoted is hidden.
    /// </summary>
    [Fact]
    public void Duplicate_key_error_hides_the_value()
    {
        Harness.RawRun("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ux_people_ssn') CREATE UNIQUE INDEX ux_people_ssn ON dbo.people (Ssn) WHERE Ssn IS NOT NULL");
        using var gate = Harness.Gate(Writer);
        using var connection = Harness.OpenWrapped(gate);

        var error = Should.Throw<QueryFailedException>(() =>
            Command(connection, $"INSERT INTO people (Id, FirstName, Ssn, GrossPay) VALUES (90, N'Pat', '{ZebulonSsn}', 1)").ExecuteNonQuery());

        error.Message.ShouldContain("2601");
        error.Message.ShouldNotContain(ZebulonSsn);
        Harness.RawScalar("SELECT COUNT(*) FROM people WHERE Id = 90").ShouldBe(0);
    }

    /// <summary>
    /// A truncation error (2628), whose raw message quotes the start of the value, is hidden the same way.
    /// </summary>
    [Fact]
    public void Truncation_error_hides_the_value()
    {
        using var gate = Harness.Gate(Writer);
        using var connection = Harness.OpenWrapped(gate);

        var error = Should.Throw<QueryFailedException>(() =>
            Command(connection, $"UPDATE people SET Ssn = '{ZebulonSsn}0' WHERE Id = 2").ExecuteNonQuery());

        error.Message.ShouldContain("2628");
        error.Message.ShouldNotContain(ZebulonSsn);
        Harness.RawScalar("SELECT Ssn FROM people WHERE Id = 2").ShouldBe("987-65-4321");
    }

    /// <summary>
    /// A failed conversion on a read (245) quotes the value it couldn't convert; through the gate the message is hidden,
    /// even for a column reviewed as safe (the data might not match its review).
    /// </summary>
    [Fact]
    public void Conversion_error_on_a_read_hides_the_value()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);

        var error = Should.Throw<QueryFailedException>(() => Scalar(connection, "SELECT CAST(Reason AS int) FROM visits"));

        error.Message.ShouldContain("245");
        error.Message.ShouldNotContain("checkup");
    }

    /// <summary>A grant that may write and shows nothing raw.</summary>
    private static AccessGrant Writer => new([], canWrite: true);

    /// <summary>
    /// Computed text has no lineage, so it comes back masked as free text, even when built only from a safe column
    /// (Masque can't tell what an expression mixed in). Functions over masked columns are refused outright (group C).
    /// </summary>
    [Fact]
    public void Computed_text_is_masked_as_free_text()
    {
        using var gate = Harness.Gate();
        using var connection = Harness.OpenWrapped(gate);
        using var command = Command(connection, "SELECT CONCAT(Reason, N'!') AS said FROM visits WHERE VisitId = 10");
        using var reader = (MaskingDbDataReader)command.ExecuteReader();

        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldNotBe("checkup!");
        reader.GetMasking(0).Action.ShouldBe(MaskAction.CheckComputed);
        reader.GetMasking(0).Type.ShouldBe(SensitiveDataType.FreeText);
    }
}
