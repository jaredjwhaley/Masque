using System.Data.Common;
using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Policy;
using Masque.Core.Vault;
using Masque.Data;
using Microsoft.Data.SqlClient;

namespace Masque.SqlServer.Tests;

/// <summary>
/// Builds <see cref="MasqueGate"/>s over the throwaway database in <see cref="LocalDbFixture"/>: a temp config folder
/// with person-reviewed configs for both seeded tables, and an in-memory vault shared by every gate it builds, so
/// tokens stay consistent across gates and connections within one test.
/// </summary>
internal sealed class SqlServerHarness : IDisposable
{
    /// <summary>Synthetic SSN of person 1, as seeded.</summary>
    public const string ZebulonSsn = "123-45-6789";

    /// <summary>Synthetic first name of person 1, as seeded.</summary>
    public const string ZebulonName = "Zebulon";

    private readonly string _connectionString;

    /// <summary>Creates the harness over <paramref name="connectionString"/> and saves reviewed configs.</summary>
    /// <param name="connectionString">The fixture's connection string (with its database).</param>
    public SqlServerHarness(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        Folder = Directory.CreateTempSubdirectory("masque-sqlserver-gate-").FullName;
        Configs = new ConfigStore(Path.Combine(Folder, "configs"));

        Configs.Save(new MaskConfig(SchemaFingerprint.Compute(["Id", "FirstName", "Ssn", "GrossPay"]), ReviewedByPerson: true,
        [
            new ColumnRule("Id", SensitiveDataType.Safe, "test"),
            new ColumnRule("FirstName", SensitiveDataType.Name, "test"),
            new ColumnRule("Ssn", SensitiveDataType.Ssn, "test"),
            new ColumnRule("GrossPay", SensitiveDataType.Money, "test"),
        ]));

        Configs.Save(new MaskConfig(SchemaFingerprint.Compute(["VisitId", "PersonId", "Reason"]), ReviewedByPerson: true,
        [
            new ColumnRule("VisitId", SensitiveDataType.Safe, "test"),
            new ColumnRule("PersonId", SensitiveDataType.Safe, "test"),
            new ColumnRule("Reason", SensitiveDataType.Safe, "test"),
        ]));
    }

    /// <summary>A temp folder deleted with the harness.</summary>
    public string Folder { get; }

    /// <summary>Where table configs are saved.</summary>
    public ConfigStore Configs { get; }

    /// <summary>The token vault every gate from this harness shares.</summary>
    public InMemoryTokenVault Vault { get; } = new();

    /// <summary>A gate whose policy gives every caller <paramref name="grant"/> (nothing raw, no writes by default).</summary>
    /// <param name="grant">What the caller may see raw and whether it may write.</param>
    /// <returns>A new gate; the caller disposes it.</returns>
    public MasqueGate Gate(AccessGrant? grant = null) =>
        new(new MaskingPolicy([], grant ?? AccessGrant.None), new FixedCallerIdentityProvider("tester"), Configs, Vault, [new SqlServerDialect()]);

    /// <summary>Opens a new SQL Server connection wrapped by <paramref name="gate"/>.</summary>
    /// <param name="gate">The gate to wrap with.</param>
    /// <returns>An open, masking connection; the caller disposes it.</returns>
    public DbConnection OpenWrapped(MasqueGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var connection = gate.Wrap(new SqlConnection(_connectionString));
        connection.Open();
        return connection;
    }

    /// <summary>Runs SQL on a raw, unwrapped connection (setup and checks only).</summary>
    /// <param name="sql">The statement.</param>
    public void RawRun(string sql) => LocalDbFixture.Execute(_connectionString, sql);

    /// <summary>Reads one value from a raw, unwrapped connection, to check what was really stored (tests only).</summary>
    /// <param name="sql">A query returning one value.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public object? RawScalar(string sql)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    /// <summary>Runs <paramref name="sql"/> on <paramref name="connection"/> and returns the first value.</summary>
    /// <param name="connection">A wrapped connection.</param>
    /// <param name="sql">The query.</param>
    /// <param name="parameters">Named parameters to add, as name and value pairs.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public static object? Scalar(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    /// <summary>Builds a command with text and named parameters.</summary>
    /// <param name="connection">A wrapped connection.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Named parameters, as name and value pairs.</param>
    /// <returns>The command; the caller disposes it.</returns>
    public static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
