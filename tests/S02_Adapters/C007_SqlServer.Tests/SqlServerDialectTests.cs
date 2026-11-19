using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Policy;
using Masque.Core.Query;
using Masque.Core.Vault;
using Masque.Data;
using Masque.Data.Dialects;
using Microsoft.Data.SqlClient;

namespace Masque.SqlServer.Tests;

/// <summary>
/// Creates a throwaway database, seeded with synthetic rows, and drops it afterwards. Uses the server in the
/// MASQUE_TEST_SQLSERVER environment variable (a connection string without a database), else SQL Server LocalDB.
/// When neither can be reached the tests are skipped, not failed.
/// </summary>
public sealed class LocalDbFixture : IDisposable
{
    private static readonly string Server = Environment.GetEnvironmentVariable("MASQUE_TEST_SQLSERVER")
        ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15";

    public LocalDbFixture()
    {
        DatabaseName = $"MasqueTest_{Guid.NewGuid():N}";
        try
        {
            Execute(Server, $"CREATE DATABASE [{DatabaseName}]");
            ConnectionString = $"{Server};Database={DatabaseName}";
            Execute(ConnectionString, """
                CREATE TABLE dbo.people (Id int PRIMARY KEY, FirstName nvarchar(50), Ssn char(11), GrossPay decimal(10,2));
                CREATE TABLE dbo.visits (VisitId int PRIMARY KEY, PersonId int, Reason nvarchar(50));
                INSERT dbo.people VALUES (1, N'Zebulon', '123-45-6789', 1234.50), (2, N'Lee', '987-65-4321', 980.00), (3, N'Ana', NULL, 20.25);
                INSERT dbo.visits VALUES (10, 1, N'checkup'), (11, 1, N'flu'), (12, 2, N'checkup');
                """);
        }
        catch (SqlException)
        {
            ConnectionString = null;
        }
    }

    public string DatabaseName { get; }

    /// <summary>Null when LocalDB isn't available.</summary>
    public string? ConnectionString { get; }

    public void Dispose()
    {
        if (ConnectionString is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        Execute(Server, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }

    public static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Pins down the SQL Server dialect against a real server: tables are listed with their schema, result columns
/// report their source column through browse mode, hidden key columns never show, errors that can quote data are
/// hidden, and a wrapped connection masks results end to end.
/// </summary>
public sealed class SqlServerDialectTests(LocalDbFixture fixture) : IClassFixture<LocalDbFixture>, IDisposable
{
    private readonly SqlServerDialect _dialect = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("masque-sqlserver-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void RequireLocalDb() => Assert.SkipWhen(fixture.ConnectionString is null,
        "No SQL Server reachable: start LocalDB or set MASQUE_TEST_SQLSERVER to a server connection string.");

    /// <summary>Tables and views are listed with their schema, the same form lineage uses.</summary>
    [Fact]
    public void ListTables_ReturnsSchemaQualifiedNames()
    {
        RequireLocalDb();
        using var connection = Open();

        _dialect.ListTables(connection, null).Select(t => t.Qualified).ShouldBe(["dbo.people", "dbo.visits"]);
    }

    /// <summary>A name without a schema means dbo, as SQL Server reads it.</summary>
    [Fact]
    public void Normalize_FillsInDbo() =>
        _dialect.Normalize(new TableReference(null, "people")).ShouldBe(new TableReference("dbo", "people"));

    /// <summary>Sample SQL quotes names, so a bracket in a name can't end the identifier.</summary>
    [Fact]
    public void SampleSql_QuotesNames() =>
        _dialect.SampleSql(new TableReference("dbo", "odd]name"), 5).ShouldBe("SELECT TOP (5) * FROM [dbo].[odd]]name]");

    /// <summary>Result columns report their source column, even when aliased, and across joins.</summary>
    [Fact]
    public void ReadColumns_ReportsLineage()
    {
        RequireLocalDb();

        var columns = Columns("SELECT p.FirstName AS who, v.Reason FROM dbo.people p JOIN dbo.visits v ON v.PersonId = p.Id");

        columns[0].ShouldBe(new ResultColumn("who", 0, new TableReference("dbo", "people"), "FirstName"));
        columns[1].ShouldBe(new ResultColumn("Reason", 1, new TableReference("dbo", "visits"), "Reason"));
    }

    /// <summary>Aggregates and expressions report no lineage.</summary>
    [Fact]
    public void ReadColumns_ComputedColumnsHaveNoLineage()
    {
        RequireLocalDb();
        Columns("SELECT COUNT(*) AS n, UPPER(FirstName) AS u FROM dbo.people GROUP BY FirstName").ShouldAllBe(c => !c.IsTraced);
    }

    /// <summary>Key columns SQL Server adds to report lineage are hidden and never returned.</summary>
    [Fact]
    public void ReadColumns_LeavesOutHiddenKeyColumns()
    {
        RequireLocalDb();
        Columns("SELECT FirstName FROM dbo.people").ShouldHaveSingleItem().Name.ShouldBe("FirstName");
    }

    /// <summary>Errors about the query text (a wrong column name) are shown, so the caller can fix the query.</summary>
    [Fact]
    public void Describe_QueryMistake_ShowsMessage()
    {
        RequireLocalDb();
        Fail("SELECT nope FROM dbo.people").Message.ShouldContain("nope");
    }

    /// <summary>Errors that can quote a value (a failed conversion) are hidden.</summary>
    [Fact]
    public void Describe_DataError_HidesMessage()
    {
        RequireLocalDb();

        var error = Fail("SELECT CAST(FirstName AS int) AS x FROM dbo.people");

        error.Message.ShouldNotContain("Zebulon");
        error.Message.ShouldContain("245");
    }

    /// <summary>A wrapped SQL Server connection masks sensitive columns and passes safe ones.</summary>
    [Fact]
    public void WrappedConnection_MasksResults()
    {
        RequireLocalDb();
        var configs = new ConfigStore(_folder);
        configs.Save(new MaskConfig(SchemaFingerprint.Compute(["Id", "FirstName", "Ssn", "GrossPay"]), ReviewedByPerson: true,
        [
            new ColumnRule("Id", SensitiveDataType.Safe, "test"),
            new ColumnRule("FirstName", SensitiveDataType.Name, "test"),
            new ColumnRule("Ssn", SensitiveDataType.Ssn, "test"),
            new ColumnRule("GrossPay", SensitiveDataType.Money, "test"),
        ]));
        using var gate = new MasqueGate(MaskingPolicy.Locked, new FixedCallerIdentityProvider("tester"), configs, new InMemoryTokenVault(), [_dialect]);
        using var connection = gate.Wrap(new SqlConnection(fixture.ConnectionString));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FirstName, Ssn, GrossPay FROM people WHERE Id = 1";
        using var reader = (MaskingDbDataReader)command.ExecuteReader();
        reader.Read().ShouldBeTrue();

        reader.FieldCount.ShouldBe(4);
        reader.GetInt32(0).ShouldBe(1);
        reader.GetString(1).ShouldNotBe("Zebulon");
        reader.GetString(2).ShouldNotBe("123-45-6789");
        reader.GetDecimal(3).ShouldBe(1234.50m);
        reader.IsMasked(2).ShouldBeTrue();
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(fixture.ConnectionString);
        connection.Open();
        return connection;
    }

    private IReadOnlyList<ResultColumn> Columns(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader(_dialect.LineageBehavior);
        return _dialect.ReadColumns(reader);
    }

    private QueryFailedException Fail(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection);
        var error = Should.Throw<SqlException>(() =>
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
            }
        });
        return _dialect.Describe(error);
    }
}
