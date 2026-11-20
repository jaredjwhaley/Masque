using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Policy;
using Masque.Core.Vault;
using Masque.Sqlite;
using Microsoft.Data.Sqlite;

namespace Masque.Data.Tests.TestSupport;

/// <summary>
/// An in-memory SQLite database with two tables of synthetic people and orders, a temp config folder and an
/// in-memory vault, so each test can build a <see cref="MasqueGate"/> with the access it needs.
/// </summary>
internal sealed class MasqueHarness : IDisposable
{
    /// <summary>Synthetic SSN of person 1 (900-series numbers are never issued).</summary>
    public const string AliceSsn = "900-11-1111";

    /// <summary>Synthetic name of person 1.</summary>
    public const string AliceName = "Alice Example";

    /// <summary>Synthetic email of person 1.</summary>
    public const string AliceEmail = "alice@example.test";

    private static readonly string[] PeopleHeaders = ["Id", "FullName", "Ssn", "Email", "Visits"];
    private static readonly string[] OrderHeaders = ["Id", "PersonSsn", "Amount"];

    private MasqueHarness()
    {
        Folder = Directory.CreateTempSubdirectory("masque-data-tests-").FullName;
        Configs = new ConfigStore(Path.Combine(Folder, "configs"));
        Inner = new SqliteConnection("Data Source=:memory:");
        Inner.Open();
        Run("""
            CREATE TABLE People (Id INTEGER PRIMARY KEY, FullName TEXT, Ssn TEXT, Email TEXT, Visits INTEGER);
            INSERT INTO People VALUES (1, 'Alice Example', '900-11-1111', 'alice@example.test', 3);
            INSERT INTO People VALUES (2, 'Bob Sample', '900-22-2222', 'bob@example.test', 5);
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY, PersonSsn TEXT, Amount REAL);
            INSERT INTO Orders VALUES (10, '900-11-1111', 12.5);
            INSERT INTO Orders VALUES (11, '900-22-2222', 7.25);
            """);
    }

    /// <summary>The raw connection, before Masque wraps it.</summary>
    public SqliteConnection Inner { get; }

    /// <summary>A temp folder deleted with the harness.</summary>
    public string Folder { get; }

    /// <summary>Where table configs are saved.</summary>
    public ConfigStore Configs { get; }

    /// <summary>The token vault.</summary>
    public InMemoryTokenVault Vault { get; } = new();

    /// <summary>Creates the database; with <paramref name="reviewed"/>, saves person-reviewed configs for both tables.</summary>
    public static MasqueHarness Create(bool reviewed = true)
    {
        var harness = new MasqueHarness();
        if (reviewed)
        {
            harness.SaveReviewedConfigs();
        }

        return harness;
    }

    /// <summary>Saves reviewed configs: names, SSNs and emails are sensitive; ids, visits and amounts are not.</summary>
    public void SaveReviewedConfigs()
    {
        Configs.Save(new MaskConfig(SchemaFingerprint.Compute(PeopleHeaders), ReviewedByPerson: true,
        [
            new ColumnRule("Id", SensitiveDataType.Safe, "test"),
            new ColumnRule("FullName", SensitiveDataType.Name, "test"),
            new ColumnRule("Ssn", SensitiveDataType.Ssn, "test"),
            new ColumnRule("Email", SensitiveDataType.Email, "test"),
            new ColumnRule("Visits", SensitiveDataType.Safe, "test"),
        ]));

        Configs.Save(new MaskConfig(SchemaFingerprint.Compute(OrderHeaders), ReviewedByPerson: true,
        [
            new ColumnRule("Id", SensitiveDataType.Safe, "test"),
            new ColumnRule("PersonSsn", SensitiveDataType.Ssn, "test"),
            new ColumnRule("Amount", SensitiveDataType.Money, "test"),
        ]));
    }

    /// <summary>A gate whose policy gives every caller <paramref name="grant"/>.</summary>
    public MasqueGate Gate(AccessGrant? grant = null) =>
        new(new MaskingPolicy([], grant ?? AccessGrant.None), new FixedCallerIdentityProvider("tester"), Configs, Vault, [new SqliteDialect()]);

    /// <summary>A grant that shows the given types raw.</summary>
    public static AccessGrant Raw(params SensitiveDataType[] types) => new(types, canWrite: false);

    /// <summary>A grant that may write and shows nothing raw.</summary>
    public static AccessGrant Writer() => new([], canWrite: true);

    /// <summary>Runs SQL on the raw connection (setup only).</summary>
    public void Run(string sql)
    {
        using var command = Inner.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Reads one value straight from the raw connection, to check what was really stored (tests only).</summary>
    public object? RawScalar(string sql)
    {
        using var command = Inner.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Inner.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
