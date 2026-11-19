using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Policy;
using Masque.Core.Vault;
using Masque.Data;
using Masque.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Masque.EntityFrameworkCore.Tests.TestSupport;

/// <summary>A person, mapped to the People table.</summary>
public sealed class Person
{
    public long Id { get; set; }

    public string? FullName { get; set; }

    public string? Ssn { get; set; }

    public string? Email { get; set; }

    public long Visits { get; set; }
}

/// <summary>A context over the test database, with Masque registered the way an application would.</summary>
public sealed class ClinicContext(DbContextOptions<ClinicContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Person>().ToTable("People");
}

/// <summary>
/// An in-memory SQLite database of synthetic people, a temp config folder with a person-reviewed config and an
/// in-memory vault, so each test can open EF Core contexts through a <see cref="MasqueGate"/> with the access it needs.
/// </summary>
internal sealed class EfHarness : IDisposable
{
    /// <summary>Synthetic SSN of person 1 (900-series numbers are never issued).</summary>
    public const string AliceSsn = "900-11-1111";

    /// <summary>Synthetic name of person 1.</summary>
    public const string AliceName = "Alice Example";

    /// <summary>Synthetic email of person 1.</summary>
    public const string AliceEmail = "alice@example.test";

    private static readonly string[] PeopleHeaders = ["Id", "FullName", "Ssn", "Email", "Visits"];

    public EfHarness()
    {
        Folder = Directory.CreateTempSubdirectory("masque-ef-tests-").FullName;
        Configs = new ConfigStore(Path.Combine(Folder, "configs"));
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();
        Run("""
            CREATE TABLE People (Id INTEGER PRIMARY KEY, FullName TEXT, Ssn TEXT, Email TEXT, Visits INTEGER NOT NULL);
            INSERT INTO People VALUES (1, 'Alice Example', '900-11-1111', 'alice@example.test', 3);
            INSERT INTO People VALUES (2, 'Bob Sample', '900-22-2222', 'bob@example.test', 5);
            """);

        Configs.Save(new MaskConfig(SchemaFingerprint.Compute(PeopleHeaders), ReviewedByPerson: true,
        [
            new ColumnRule("Id", SensitiveDataType.Safe, "test"),
            new ColumnRule("FullName", SensitiveDataType.Name, "test"),
            new ColumnRule("Ssn", SensitiveDataType.Ssn, "test"),
            new ColumnRule("Email", SensitiveDataType.Email, "test"),
            new ColumnRule("Visits", SensitiveDataType.Safe, "test"),
        ]));
    }

    /// <summary>
    /// The raw connection, kept open: an in-memory database lives only while a connection to it is open, and EF Core
    /// leaves a connection it was handed open.
    /// </summary>
    public SqliteConnection Connection { get; }

    /// <summary>A temp folder deleted with the harness.</summary>
    public string Folder { get; }

    /// <summary>Where table configs are saved.</summary>
    public ConfigStore Configs { get; }

    /// <summary>The token vault, shared by every gate the harness builds.</summary>
    public InMemoryTokenVault Vault { get; } = new();

    /// <summary>A grant that may write and shows nothing raw.</summary>
    public static AccessGrant Writer { get; } = new([], canWrite: true);

    /// <summary>A grant that shows the given types raw and may not write.</summary>
    public static AccessGrant Raw(params SensitiveDataType[] types) => new(types, canWrite: false);

    /// <summary>A gate whose policy gives every caller <paramref name="grant"/>.</summary>
    public MasqueGate Gate(AccessGrant? grant = null) =>
        new(new MaskingPolicy([], grant ?? AccessGrant.None), new FixedCallerIdentityProvider("tester"), Configs, Vault, [new SqliteDialect()]);

    /// <summary>A context on the shared connection with Masque registered through <paramref name="gate"/>.</summary>
    public ClinicContext Context(MasqueGate gate) =>
        new(new DbContextOptionsBuilder<ClinicContext>().UseSqlite(Connection).UseMasque(gate).Options);

    /// <summary>Runs SQL on the raw connection, outside Masque (setup and checks only).</summary>
    public void Run(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Reads one value straight from the database, outside Masque (checks only).</summary>
    public object? RawScalar(string sql)
    {
        // A fresh raw connection would see a different in-memory database, so read on the shared one with the
        // Masque guard's write window irrelevant: this is a read.
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Connection.Dispose();
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
