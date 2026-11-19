using Dapper;
using Masque.Core;
using Masque.Core.Policy;
using Masque.Data.Tests.TestSupport;
using Masque.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static Masque.Data.Tests.MaskingConnectionTests;
using static Masque.Data.Tests.TestSupport.MasqueHarness;

namespace Masque.Data.Tests;

public sealed class IntegrationTests
{
    /// <summary>Dapper maps masked rows like any others; sensitive properties hold tokens.</summary>
    [Fact]
    public void Dapper_queries_come_back_masked()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email)).Wrap(harness.Inner);

        var people = connection.Query<PersonRow>("SELECT Id, FullName, Ssn, Email, Visits FROM People ORDER BY Id").ToList();

        people.Count.ShouldBe(2);
        people[0].Id.ShouldBe(1);
        people[0].FullName.ShouldNotBe(AliceName);
        people[0].Ssn.ShouldNotBe(AliceSsn);
        people[0].Email.ShouldBe(AliceEmail);
        people[0].Visits.ShouldBe(3);
    }

    /// <summary>Dapper parameters reach the database untouched.</summary>
    [Fact]
    public void Dapper_parameters_work()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        connection.ExecuteScalar<long>("SELECT Visits FROM People WHERE Id = @id", new { id = 2 }).ShouldBe(5);
    }

    /// <summary>The <c>WithMasque</c> extension is the same as <see cref="MasqueGate.Wrap"/>.</summary>
    [Fact]
    public void WithMasque_extension_wraps_the_connection()
    {
        using var harness = Create();

        using var connection = harness.Inner.WithMasque(harness.Gate());

        connection.ShouldBeOfType<MaskingDbConnection>();
        Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1").ShouldNotBe(AliceSsn);
    }

    /// <summary><c>AddMasque</c> registers one gate built from the options.</summary>
    [Fact]
    public void AddMasque_registers_a_gate_from_options()
    {
        using var harness = Create();
        var services = new ServiceCollection();
        services.AddMasque(options => Configure(options, harness, new MaskingPolicy([], Raw(SensitiveDataType.Email))));

        using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<MasqueGate>();

        provider.GetRequiredService<MasqueGate>().ShouldBeSameAs(gate);
        using var connection = gate.Wrap(harness.Inner);
        Scalar(connection, "SELECT Email FROM People WHERE Id = 1").ShouldBe(AliceEmail);
    }

    /// <summary>An administrator's machine-wide policy file wins over a policy set in code.</summary>
    [Fact]
    public void Machine_policy_file_wins_over_code()
    {
        using var harness = Create();
        var machinePath = Path.Combine(harness.Folder, "machine-policy.json");
        File.WriteAllText(machinePath, """{ "default": { "rawTypes": [], "canWrite": false } }""");

        using var gate = MasqueGate.Create(Configure(new MasqueOptions(), harness,
            new MaskingPolicy([], Raw(SensitiveDataType.Email)), machinePath));
        using var connection = gate.Wrap(harness.Inner);

        Scalar(connection, "SELECT Email FROM People WHERE Id = 1").ShouldNotBe(AliceEmail);
    }

    /// <summary>A policy set in code wins over the user's own policy file.</summary>
    [Fact]
    public void Code_policy_wins_over_the_user_file()
    {
        using var harness = Create();
        var options = Configure(new MasqueOptions(), harness, new MaskingPolicy([], AccessGrant.None));
        options.PolicyPath = Path.Combine(harness.Folder, "user-policy.json");
        File.WriteAllText(options.PolicyPath, """{ "default": { "rawTypes": ["Email"], "canWrite": false } }""");

        using var gate = MasqueGate.Create(options);
        using var connection = gate.Wrap(harness.Inner);

        Scalar(connection, "SELECT Email FROM People WHERE Id = 1").ShouldNotBe(AliceEmail);
    }

    /// <summary>With no policy anywhere, nothing sensitive is shown raw and nothing can be written.</summary>
    [Fact]
    public void No_policy_means_locked()
    {
        using var harness = Create();
        using var gate = MasqueGate.Create(Configure(new MasqueOptions(), harness, policy: null));
        using var connection = gate.Wrap(harness.Inner);

        Scalar(connection, "SELECT Email FROM People WHERE Id = 1").ShouldNotBe(AliceEmail);
        Should.Throw<Masque.Core.Query.QueryRejectedException>(() => Execute(connection, "DELETE FROM Orders"));
    }

    private static MasqueOptions Configure(MasqueOptions options, MasqueHarness harness, MaskingPolicy? policy, string? machinePath = null)
    {
        options.Policy = policy;
        options.PolicyPath = Path.Combine(harness.Folder, "no-user-policy.json");
        options.MachinePolicyPath = machinePath ?? Path.Combine(harness.Folder, "no-machine-policy.json");
        options.Caller = new FixedCallerIdentityProvider("tester");
        options.ConfigFolder = Path.Combine(harness.Folder, "configs");
        options.Vault = harness.Vault;
        options.Dialects.Add(new SqliteDialect());
        return options;
    }

    /// <summary>A row of the People table, as Dapper maps it.</summary>
    private sealed class PersonRow
    {
        public long Id { get; set; }

        public string FullName { get; set; } = string.Empty;

        public string Ssn { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public long Visits { get; set; }
    }
}
