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

    /// <summary>Masque prevents exposure, not change: a writer may set a masked property to a value they supply.</summary>
    [Fact]
    public void Updating_a_masked_property_with_a_supplied_value_is_saved()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.Single(p => p.Id == 1);

        alice.Ssn = "900-99-9999";
        context.SaveChanges();

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe("900-99-9999");
    }

    /// <summary>
    /// A token saved into a masked property of its type is stored as the real value it stands for, while the entity
    /// keeps the token and ends the save unchanged, so the caller never holds the real value.
    /// </summary>
    [Fact]
    public void Saving_a_token_stores_its_real_value_and_the_entity_keeps_the_token()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.Single(p => p.Id == 1);
        var bobToken = context.People.AsNoTracking().Single(p => p.Id == 2).Ssn!;

        alice.Ssn = bobToken;
        context.SaveChanges();

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe("900-22-2222");
        alice.Ssn.ShouldBe(bobToken);
        context.Entry(alice).State.ShouldBe(EntityState.Unchanged);
    }

    /// <summary>
    /// <c>Update()</c> marks every property modified, so a loaded entity's tokens are written back too; each is stored
    /// as its real value, leaving the masked columns as they were.
    /// </summary>
    [Fact]
    public async Task Update_of_a_whole_entity_keeps_the_real_values()
    {
        using var harness = new EfHarness();
        await using var context = harness.Context(harness.Gate(Writer));
        var cancel = TestContext.Current.CancellationToken;
        var alice = await context.People.AsNoTracking().SingleAsync(p => p.Id == 1, cancel);
        alice.Visits = 4;

        context.Update(alice);
        await context.SaveChangesAsync(cancel);

        harness.RawScalar("SELECT Visits FROM People WHERE Id = 1").ShouldBe(4L);
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe(AliceSsn);
        harness.RawScalar("SELECT FullName FROM People WHERE Id = 1").ShouldBe(AliceName);
        harness.RawScalar("SELECT Email FROM People WHERE Id = 1").ShouldBe(AliceEmail);
        alice.Ssn.ShouldNotBe(AliceSsn);
    }

    /// <summary>A token in a new entity's masked property is stored as its real value.</summary>
    [Fact]
    public void Inserting_a_token_stores_its_real_value()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var bobToken = context.People.AsNoTracking().Single(p => p.Id == 2).Ssn!;

        context.People.Add(new Person { Id = 3, FullName = "Carol Test", Ssn = bobToken, Visits = 1 });
        context.SaveChanges();

        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 3").ShouldBe("900-22-2222");
    }

    /// <summary>
    /// A token saved where it isn't masked as its own type would be stored as a fake (or swapped, become readable),
    /// so the save is refused and nothing is written.
    /// </summary>
    [Fact]
    public void Saving_a_token_outside_a_masked_property_of_its_type_is_refused()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(new([SensitiveDataType.Email], canWrite: true)));
        var alice = context.People.Single(p => p.Id == 1);
        var ssnToken = alice.Ssn!;

        alice.Email = ssnToken;
        Should.Throw<QueryRejectedException>(() => context.SaveChanges());

        alice.Email = AliceEmail;
        alice.FullName = ssnToken;
        Should.Throw<QueryRejectedException>(() => context.SaveChanges());

        harness.RawScalar("SELECT Email FROM People WHERE Id = 1").ShouldBe(AliceEmail);
        harness.RawScalar("SELECT FullName FROM People WHERE Id = 1").ShouldBe(AliceName);
    }

    /// <summary>When the database rejects a save, the entities get their tokens back, still marked for saving.</summary>
    [Fact]
    public void Failed_save_puts_the_tokens_back()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.Single(p => p.Id == 1);
        var bobToken = context.People.AsNoTracking().Single(p => p.Id == 2).Ssn!;

        alice.Ssn = bobToken;
        context.People.Add(new Person { Id = 2, FullName = "Duplicate Id", Visits = 1 });

        Should.Throw<DbUpdateException>(() => context.SaveChanges());

        alice.Ssn.ShouldBe(bobToken);
        context.Entry(alice).State.ShouldBe(EntityState.Modified);
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

    /// <summary>
    /// An entity whose key is a masked column holds a token as its key, and SaveChanges finds the row by that key
    /// (<c>WHERE Ssn = @p</c>). Matching on a sensitive key changes nothing and copies it nowhere, so the save must
    /// reach the real row: the token in the key must stand for its real value there, as it does in a query's filter.
    /// </summary>
    [Fact]
    public void Saving_an_entity_whose_key_is_masked_updates_its_row()
    {
        using var harness = new EfHarness();
        using var context = harness.Context(harness.Gate(Writer));
        var member = context.Members.Single();
        member.Ssn.ShouldNotBe(AliceSsn);

        member.Visits = 4;
        context.SaveChanges();

        harness.RawScalar("SELECT Visits FROM Members WHERE Ssn = '900-11-1111'").ShouldBe(4L);
        context.Entry(member).State.ShouldBe(EntityState.Unchanged);
    }

    /// <summary>
    /// One SSN stored in two spellings gets one token with two spellings. Writing that token back into a row (here
    /// <c>Update()</c>, which marks every property modified) must keep the spelling the row already had: picking one
    /// spelling for every row would quietly reformat the others' stored values.
    /// </summary>
    [Fact]
    public void Writing_back_a_token_with_several_spellings_keeps_the_row_spelling()
    {
        using var harness = new EfHarness();
        harness.Run("INSERT INTO People VALUES (10, 'Alice Duplicate', '900111111', NULL, 1)");
        using var context = harness.Context(harness.Gate(Writer));
        var alice = context.People.AsNoTracking().Single(p => p.Id == 1);
        var duplicate = context.People.AsNoTracking().Single(p => p.Id == 10);
        duplicate.Ssn.ShouldBe(alice.Ssn);
        duplicate.Visits = 2;

        context.Update(duplicate);
        context.SaveChanges();

        harness.RawScalar("SELECT Visits FROM People WHERE Id = 10").ShouldBe(2L);
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 10").ShouldBe("900111111");
        harness.RawScalar("SELECT Ssn FROM People WHERE Id = 1").ShouldBe(AliceSsn);
    }
}
