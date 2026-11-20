using Masque.Core;
using Masque.Core.Query;
using Masque.EntityFrameworkCore.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Masque.EntityFrameworkCore.Tests.TestSupport.EfHarness;

namespace Masque.EntityFrameworkCore.Tests;

/// <summary>EF Core contexts registered with <c>UseMasque</c>: LINQ queries come back masked and writes are checked.</summary>
public sealed class MasqueEfCoreTests
{
    /// <summary>A LINQ query returns tokens for sensitive columns and real values for safe ones.</summary>
    [Fact]
    public void Linq_results_are_masked()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate());

        var alice = context.People.AsNoTracking().Single(p => p.Id == 1);

        alice.FullName.ShouldNotBe(AliceName);
        alice.Ssn.ShouldNotBe(AliceSsn);
        alice.Email.ShouldNotBe(AliceEmail);
        alice.Visits.ShouldBe(3);
        harness.Vault.ContainsToken(SensitiveDataType.Ssn, alice.Ssn!).ShouldBeTrue();
    }

    /// <summary>Async queries are masked the same way as sync ones.</summary>
    [Fact]
    public async Task Async_linq_results_are_masked()
    {
        using var harness = new EfHarness();
        await using var context = harness.Context(harness.Gate());

        var people = await context.People.AsNoTracking().OrderBy(p => p.Id).ToListAsync(TestContext.Current.CancellationToken);

        people.Count.ShouldBe(2);
        people[0].Ssn.ShouldNotBe(AliceSsn);
        people[1].Visits.ShouldBe(5);
    }

    /// <summary>Sorting on a masked column would leak its order, so the query is refused before it runs.</summary>
    [Fact]
    public void Sorting_by_a_masked_column_is_refused()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate());

        Should.Throw<QueryRejectedException>(() => context.People.OrderBy(p => p.Ssn).Select(p => p.Id).ToList());
    }

    /// <summary>A query tag can ask for fewer raw types than the policy allows, for one query only.</summary>
    [Fact]
    public void TagWith_narrows_raw_types()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Raw(SensitiveDataType.Email, SensitiveDataType.Name)));

        var tagged = context.People.AsNoTracking().TagWith("masque: allow Email").Single(p => p.Id == 1);
        var untagged = context.People.AsNoTracking().Single(p => p.Id == 1);

        tagged.Email.ShouldBe(AliceEmail);
        tagged.FullName.ShouldNotBe(AliceName);
        untagged.FullName.ShouldBe(AliceName);
    }

    /// <summary>
    /// EF Core sends a captured variable as a parameter, so a token the caller was shown filters through a parameter
    /// the same way it does through a literal.
    /// </summary>
    [Fact]
    public void Token_filter_through_a_variable_works()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate());
        var token = context.People.Where(p => p.Id == 1).Select(p => p.Ssn).Single();

        var id = context.People.Where(p => p.Ssn == token).Select(p => p.Id).Single();

        id.ShouldBe(1);
    }

    /// <summary>Changing a sensitive property of a loaded entity would write a token over the real value, so it is refused.</summary>
    [Fact]
    public void Updating_a_sensitive_property_is_refused()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.Single(p => p.Id == 1);

        alice.Ssn = "900-99-9999";

        Should.Throw<QueryRejectedException>(() => context.SaveChanges());
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe(AliceSsn);
    }

    /// <summary><c>Update()</c> marks every property modified, sensitive ones included, so it is refused.</summary>
    [Fact]
    public void Update_of_a_whole_entity_is_refused()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.AsNoTracking().Single(p => p.Id == 1);
        alice.Visits = 4;

        context.Update(alice);

        Should.Throw<QueryRejectedException>(() => context.SaveChanges());
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe(AliceSsn);
    }

    /// <summary>Changing only safe properties of a loaded entity is allowed, and the sensitive columns are left as they were.</summary>
    [Fact]
    public void Updating_a_safe_property_is_saved()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.Single(p => p.Id == 1);

        alice.Visits = 4;
        context.SaveChanges();

        harness.RawScalar("SELECT Visits FROM People WHERE Id = 1").ShouldBe(4L);
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe(AliceSsn);
    }

    /// <summary>A caller allowed to write can add and remove rows.</summary>
    [Fact]
    public async Task Insert_and_delete_are_saved()
    {
        using var harness = new EfHarness();
        await using var context = harness.Context(harness.Gate(Writer));
        var cancel = TestContext.Current.CancellationToken;

        context.People.Add(new Person { Id = 3, FullName = "Carol Test", Ssn = "900-33-3333", Email = "carol@example.test", Visits = 1 });
        context.People.Remove(await context.People.SingleAsync(p => p.Id == 2, cancel));
        await context.SaveChangesAsync(cancel);

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 3").ShouldBe("900-33-3333");
        harness.RawScalar("SELECT COUNT(*) FROM People WHERE Id = 2").ShouldBe(0L);
    }

    /// <summary>A caller whose policy doesn't allow writes can't save anything.</summary>
    [Fact]
    public void Caller_without_write_access_cannot_save()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate());

        context.People.Add(new Person { Id = 3, FullName = "Carol Test", Visits = 1 });

        Should.Throw<QueryRejectedException>(() => context.SaveChanges());
        harness.RawScalar("SELECT COUNT(*) FROM People").ShouldBe(2L);
    }

    /// <summary>
    /// <c>ExecuteUpdate</c> runs as plain SQL through the same review, so a writer may set a masked column to a value
    /// they supply, and a token they were shown is stored as its real value.
    /// </summary>
    [Fact]
    public async Task ExecuteUpdate_on_a_masked_column_stores_supplied_values_and_swaps_tokens()
    {
        using var harness = new EfHarness();
        await using var context = harness.Context(harness.Gate(Writer));
        var cancel = TestContext.Current.CancellationToken;

        context.People.Where(p => p.Id == 1).ExecuteUpdate(s => s.SetProperty(p => p.Ssn, "900-99-9999")).ShouldBe(1);
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe("900-99-9999");

        var bobToken = (await context.People.AsNoTracking().SingleAsync(p => p.Id == 2, cancel)).Ssn!;
        context.People.Where(p => p.Id == 1).ExecuteUpdate(s => s.SetProperty(p => p.Ssn, bobToken)).ShouldBe(1);
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe("900-22-2222");
    }

    /// <summary><c>ExecuteUpdate</c> on a safe column runs for a caller allowed to write.</summary>
    [Fact]
    public void ExecuteUpdate_on_a_safe_column_runs()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));

        var changed = context.People.Where(p => p.Id == 1).ExecuteUpdate(s => s.SetProperty(p => p.Visits, 7L));

        changed.ShouldBe(1);
        harness.RawScalar("SELECT Visits FROM People WHERE Id = 1").ShouldBe(7L);
    }
}
