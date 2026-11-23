using System.Text.Json;
using System.Text.Json.Nodes;
using Masque.SqlServer.Tests;
using Masque.Vault.Tests.TestSupport;

namespace Masque.Mcp.Tests;

/// <summary>
/// The path an assistant actually uses for a database: the MCP tools on a SQL Server source named in sources.json,
/// against a real server (LocalDB). Describing lists columns without values; nothing is returned until a person
/// reviews the configs; queries then return tokens only, a token filter finds its row, and writes stay refused under
/// the default locked policy.
/// </summary>
public sealed class SqlServerSourceTests : IClassFixture<LocalDbFixture>, IDisposable
{
    private const string SourceName = "hr";

    // The fixture's synthetic real values; none may appear in any reply.
    private static readonly string[] RealValues = ["Zebulon", "123-45-6789", "Lee", "987-65-4321", "Ana"];

    private readonly LocalDbFixture _fixture;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly MasqueToolService _service;

    public SqlServerSourceTests(LocalDbFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_folder);
        if (fixture.ConnectionString is not null)
        {
            var sources = new JsonObject
            {
                ["sources"] = new JsonObject
                {
                    [SourceName] = new JsonObject { ["type"] = "sqlServer", ["connectionString"] = fixture.ConnectionString },
                },
            };
            File.WriteAllText(Path.Combine(_folder, "sources.json"), sources.ToJsonString());
        }

        _service = MasqueToolService.Create(
            configFolder: Path.Combine(_folder, "configs"),
            vaultPath: Path.Combine(_folder, "vault.db"),
            sourcesPath: Path.Combine(_folder, "sources.json"),
            keyProtector: new FakeKeyProtector("tester"),
            policyPath: Path.Combine(_folder, "policy.json"),
            machinePolicyPath: Path.Combine(_folder, "no-machine-policy.json"));
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private void RequireSqlServer() => Assert.SkipWhen(_fixture.ConnectionString is null,
        "No SQL Server reachable: start LocalDB or set MASQUE_TEST_SQLSERVER to a server connection string.");

    /// <summary>
    /// Describing a named SQL Server source lists its tables and columns with proposed categories and no values, and a
    /// query before review returns no rows: only the proposal and a request for a person to review it (a normal
    /// reply, not an error, so the assistant passes it on).
    /// </summary>
    [Fact]
    public void Describe_lists_columns_without_values_and_queries_wait_for_review()
    {
        RequireSqlServer();

        var described = _service.Describe(SourceName);

        described.IsError.ShouldBeFalse(described.Text);
        described.Text.ShouldContain("dbo.people");
        described.Text.ShouldContain("Ssn");
        NoRealValues(described);

        var early = _service.Query(SourceName, "SELECT Id, FirstName, Ssn FROM people", 10);
        early.Text.ShouldContain("A person must review these before Masque returns rows");
        early.Text.ShouldNotContain("| --- |");
        NoRealValues(early);
    }

    /// <summary>
    /// Once reviewed (as proposed, so Id is masked as an Identifier too), a query returns tokens only, and an SSN token
    /// from that reply, used in a later call's filter, finds the same row: its Id token matches.
    /// </summary>
    [Fact]
    public void Reviewed_query_returns_tokens_and_a_token_filter_finds_its_row()
    {
        RequireSqlServer();
        _service.Describe(SourceName);
        ReviewAllConfigs();

        var reply = _service.Query(SourceName, "SELECT Id, FirstName, Ssn FROM people WHERE Id = 1", 10);

        reply.IsError.ShouldBeFalse(reply.Text);
        reply.Text.ShouldContain("1 row");
        NoRealValues(reply);

        var row = LastRowCells(reply);
        row[2].ShouldNotBe("123-45-6789");
        var filtered = _service.Query(SourceName, $"SELECT Id FROM people WHERE Ssn = '{row[2]}'", 10);
        filtered.IsError.ShouldBeFalse(filtered.Text);
        filtered.Text.ShouldContain("1 row");
        LastRowCells(filtered)[0].ShouldBe(row[0]);
    }

    /// <summary>Under the default locked policy, masque_execute refuses a write and the table is unchanged.</summary>
    [Fact]
    public void Execute_is_refused_under_the_locked_policy()
    {
        RequireSqlServer();
        _service.Describe(SourceName);
        ReviewAllConfigs();

        var reply = _service.Execute(SourceName, "DELETE FROM visits WHERE VisitId = 12");

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("may not write");
        LocalDbCount("SELECT COUNT(*) FROM visits").ShouldBe(3);
    }

    /// <summary>Marks every proposed config reviewed, as a person would after checking it.</summary>
    private void ReviewAllConfigs()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_folder, "configs"), "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["reviewedByPerson"] = true;
            File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Fails if any synthetic real value appears in a reply.</summary>
    private static void NoRealValues(ToolReply reply)
    {
        foreach (var value in RealValues)
        {
            reply.Text.ShouldNotContain(value, Case.Insensitive);
        }
    }

    /// <summary>The cells of the last Markdown table row of a reply.</summary>
    private static string[] LastRowCells(ToolReply reply) =>
        reply.Text.Split('\n').Last(line => line.TrimStart().StartsWith('|') && !line.Contains("---", StringComparison.Ordinal))
            .Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    /// <summary>Counts rows straight from the database, bypassing Masque (tests only).</summary>
    private int LocalDbCount(string sql)
    {
        using var connection = new Microsoft.Data.SqlClient.SqlConnection(_fixture.ConnectionString);
        connection.Open();
        using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        return (int)command.ExecuteScalar();
    }
}
