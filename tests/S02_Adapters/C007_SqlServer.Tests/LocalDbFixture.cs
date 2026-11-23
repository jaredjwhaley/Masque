using Microsoft.Data.SqlClient;

namespace Masque.SqlServer.Tests;

/// <summary>
/// Creates a throwaway database, seeded with synthetic rows, and drops it afterwards. Uses the server in the
/// MASQUE_TEST_SQLSERVER environment variable (a connection string without a database), else SQL Server LocalDB.
/// When neither can be reached the tests are skipped, not failed. Shared with the MCP tests as a linked file.
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

    /// <summary>The throwaway database's name.</summary>
    public string DatabaseName { get; }

    /// <summary>Null when no SQL Server is reachable.</summary>
    public string? ConnectionString { get; }

    /// <summary>Drops the throwaway database.</summary>
    public void Dispose()
    {
        if (ConnectionString is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        Execute(Server, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }

    /// <summary>Runs SQL on a raw connection (setup only).</summary>
    /// <param name="connectionString">Where to run it.</param>
    /// <param name="sql">The statements.</param>
    public static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
