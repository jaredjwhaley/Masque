using System.Data;
using System.Data.Common;
using Masque.Core;
using Masque.Core.Query;
using Masque.Data.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using static Masque.Data.Tests.TestSupport.MasqueHarness;

namespace Masque.Data.Tests;

public sealed class MaskingConnectionTests
{
    /// <summary>Sensitive columns come back as tokens and say so; safe columns come back unchanged.</summary>
    [Fact]
    public void Select_masks_sensitive_columns_and_marks_them()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT Id, FullName, Ssn, Email, Visits FROM People WHERE Id = 1");
        reader.Read().ShouldBeTrue();

        reader.GetInt64(0).ShouldBe(1);
        reader.GetString(1).ShouldNotBe(AliceName);
        reader.GetString(2).ShouldNotBe(AliceSsn);
        reader.GetString(3).ShouldNotBe(AliceEmail);
        reader.GetInt64(4).ShouldBe(3);

        reader.IsMasked(0).ShouldBeFalse();
        reader.IsMasked(1).ShouldBeTrue();
        reader.IsMasked(2).ShouldBeTrue();
        reader.IsMasked(3).ShouldBeTrue();
        reader.IsMasked(4).ShouldBeFalse();
        reader.GetMasking(2).Type.ShouldBe(SensitiveDataType.Ssn);
    }

    /// <summary>A masked column reports itself as text, since a token is always a string.</summary>
    [Fact]
    public void Masked_columns_are_typed_as_text()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT Id, Ssn FROM People");

        reader.GetFieldType(1).ShouldBe(typeof(string));
        reader.FieldCount.ShouldBe(2);
        reader.GetName(1).ShouldBe("Ssn");
    }

    /// <summary>The same value always gets the same token, so results from separate queries still line up.</summary>
    [Fact]
    public void Same_value_gets_the_same_token_in_every_query()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        var fromPeople = Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1");
        var fromOrders = Scalar(connection, "SELECT PersonSsn FROM Orders WHERE Id = 10");

        fromPeople.ShouldBe(fromOrders);
        fromPeople.ShouldNotBe(AliceSsn);
    }

    /// <summary>Types the caller's policy shows raw pass through; the rest stay masked.</summary>
    [Fact]
    public void Policy_raw_types_pass_through()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email)).Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT FullName, Email FROM People WHERE Id = 1");
        reader.Read().ShouldBeTrue();

        reader.GetString(0).ShouldNotBe(AliceName);
        reader.GetString(1).ShouldBe(AliceEmail);
        reader.IsMasked(1).ShouldBeFalse();
    }

    /// <summary>A connection can ask for fewer raw types than the policy allows.</summary>
    [Fact]
    public void Connection_allow_list_narrows_the_policy()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email, SensitiveDataType.Name))
            .Wrap(harness.Inner, allowRaw: [SensitiveDataType.Email]);

        using var reader = Read(connection, "SELECT FullName, Email FROM People WHERE Id = 1");
        reader.Read().ShouldBeTrue();

        reader.GetString(0).ShouldNotBe(AliceName);
        reader.GetString(1).ShouldBe(AliceEmail);
    }

    /// <summary>Asking for a type the policy doesn't allow never shows it.</summary>
    [Fact]
    public void Connection_allow_list_never_widens_the_policy()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner, allowRaw: [SensitiveDataType.Ssn]);

        Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1").ShouldNotBe(AliceSsn);
    }

    /// <summary>One command can narrow further than its connection.</summary>
    [Fact]
    public void Command_allow_list_narrows_the_connection()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email, SensitiveDataType.Name)).Wrap(harness.Inner);

        using var command = (MaskingDbCommand)connection.CreateCommand();
        command.CommandText = "SELECT FullName, Email FROM People WHERE Id = 1";
        command.AllowedRawTypes = [SensitiveDataType.Name];

        using var reader = (MaskingDbDataReader)command.ExecuteReader();
        reader.Read().ShouldBeTrue();

        reader.GetString(0).ShouldBe(AliceName);
        reader.GetString(1).ShouldNotBe(AliceEmail);
    }

    /// <summary>A <c>-- masque: allow</c> comment narrows like <see cref="MaskingDbCommand.AllowedRawTypes"/>, for callers that only send SQL.</summary>
    [Fact]
    public void Allow_tag_in_the_sql_narrows_the_command()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email, SensitiveDataType.Name)).Wrap(harness.Inner);

        using var reader = Read(connection, "-- masque: allow Name\nSELECT FullName, Email FROM People WHERE Id = 1");
        reader.Read().ShouldBeTrue();

        reader.GetString(0).ShouldBe(AliceName);
        reader.GetString(1).ShouldNotBe(AliceEmail);
    }

    /// <summary>A tag naming no real type narrows to nothing rather than being ignored.</summary>
    [Fact]
    public void Allow_tag_with_unknown_names_shows_nothing_raw()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Email)).Wrap(harness.Inner);

        Scalar(connection, "-- masque: allow Everything\nSELECT Email FROM People WHERE Id = 1").ShouldNotBe(AliceEmail);
    }

    /// <summary>A table with no reviewed config stops the query and saves a proposal for a person to review.</summary>
    [Fact]
    public void Unreviewed_table_requires_review_first()
    {
        using var harness = Create(reviewed: false);
        using var connection = harness.Gate().Wrap(harness.Inner);

        var ex = Should.Throw<ReviewRequiredException>(() => Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1"));

        var pending = ex.Pending.ShouldHaveSingleItem();
        pending.Table.ShouldBe("People");
        pending.Config.ReviewedByPerson.ShouldBeFalse();
        File.Exists(pending.ConfigPath).ShouldBeTrue();
    }

    /// <summary>Once a person marks the proposal reviewed, the same query runs.</summary>
    [Fact]
    public void Query_runs_once_the_proposal_is_reviewed()
    {
        using var harness = Create(reviewed: false);
        using var connection = harness.Gate().Wrap(harness.Inner);
        var pending = Should.Throw<ReviewRequiredException>(() => Scalar(connection, "SELECT Ssn FROM People")).Pending.Single();

        harness.Configs.Save(pending.Config with { ReviewedByPerson = true });

        Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1").ShouldNotBe(AliceSsn);
    }

    /// <summary>Tables the database doesn't list as user tables (system tables, typos) are refused.</summary>
    [Theory]
    [InlineData("SELECT * FROM sqlite_schema")]
    [InlineData("SELECT * FROM Nope")]
    public void Unlisted_tables_are_refused(string sql)
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Should.Throw<QueryRejectedException>(() => Scalar(connection, sql));
    }

    /// <summary>Statement-level checks run through the decorator: sorting on a masked column is refused.</summary>
    [Fact]
    public void Revealing_uses_of_masked_columns_are_refused()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Should.Throw<QueryRejectedException>(() => Scalar(connection, "SELECT Id FROM People ORDER BY Ssn"))
            .Message.ShouldContain("Ssn");
    }

    /// <summary>A column the policy shows raw may be sorted on, because nothing is hidden.</summary>
    [Fact]
    public void Raw_columns_may_be_sorted()
    {
        using var harness = Create();
        using var connection = harness.Gate(Raw(SensitiveDataType.Ssn)).Wrap(harness.Inner);

        Scalar(connection, "SELECT Id FROM People ORDER BY Ssn DESC").ShouldBe(2L);
    }

    /// <summary>A token the caller was shown can be used to filter, and matches the real value's rows.</summary>
    [Fact]
    public void Token_in_a_filter_matches_its_real_value()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        Scalar(connection, $"SELECT Id FROM People WHERE Ssn = '{token}'").ShouldBe(1L);
        Scalar(connection, $"SELECT o.Id FROM Orders o JOIN People p ON p.Ssn = o.PersonSsn WHERE p.Ssn = '{token}'").ShouldBe(10L);
    }

    /// <summary>
    /// A token shown on one connection still filters on another (the MCP server opens one per call), even though the
    /// vault holds the value normalized (digits only) and the table stores it with dashes.
    /// </summary>
    [Fact]
    public void Token_filter_works_on_a_later_connection()
    {
        using var harness = Create();

        // Two wrappers are two sessions with nothing shared but the vault. The first stays undisposed: disposing a
        // wrapper closes the inner connection, which would drop this in-memory database.
        var first = harness.Gate().Wrap(harness.Inner);
        var token = (string)Scalar(first, "SELECT Ssn FROM People WHERE Id = 1")!;
        using var second = harness.Gate().Wrap(harness.Inner);

        Scalar(second, $"SELECT Id FROM People WHERE Ssn = '{token}'").ShouldBe(1L);
        Scalar(second, $"SELECT Id FROM People WHERE '{token}' <> Ssn").ShouldBe(2L);
        Scalar(second, $"SELECT COUNT(*) FROM People WHERE Ssn IN ('{token}', 'not-a-token')").ShouldBe(1L);
    }

    /// <summary>
    /// When two tables store the same value spelled differently, a token filter matches both spellings Masque has
    /// seen, so joins and lookups across sources still line up.
    /// </summary>
    [Fact]
    public void Token_filter_matches_every_spelling_seen()
    {
        using var harness = Create();
        harness.Run("UPDATE Orders SET PersonSsn = '900 11 1111' WHERE Id = 10");
        using var connection = harness.Gate().Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;
        Scalar(connection, "SELECT PersonSsn FROM Orders WHERE Id = 10").ShouldBe(token);

        Scalar(connection, $"SELECT Id FROM Orders WHERE PersonSsn = '{token}'").ShouldBe(10L);
    }

    /// <summary>
    /// A token passed in a named parameter filters like a literal one, in = comparisons and IN lists, and the
    /// caller's parameters are left as they were after the command runs.
    /// </summary>
    [Fact]
    public void Token_in_a_parameter_filters_like_a_literal()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM People WHERE Ssn = @ssn";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ssn";
        parameter.Value = token;
        command.Parameters.Add(parameter);

        command.ExecuteScalar().ShouldBe(1L);
        command.CommandText = "SELECT COUNT(*) FROM People WHERE Ssn IN (@ssn)";
        command.ExecuteScalar().ShouldBe(1L);

        command.Parameters.Count.ShouldBe(1);
        command.Parameters[0].Value.ShouldBe(token);
    }

    /// <summary>The command's text is unchanged after it runs, so a token filter never leaves a real value behind.</summary>
    [Fact]
    public void Token_filter_rewrite_is_undone_after_running()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);
        var token = (string)Scalar(connection, "SELECT Ssn FROM People WHERE Id = 1")!;

        using var command = connection.CreateCommand();
        var sql = $"SELECT Id FROM People WHERE Ssn = '{token}'";
        command.CommandText = sql;
        command.ExecuteScalar();

        command.CommandText.ShouldBe(sql);
        command.Parameters.Count.ShouldBe(0);
    }

    /// <summary>Computed text has no source column to check, so it is masked as free text.</summary>
    [Fact]
    public void Computed_text_is_masked_as_free_text()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT 'hello' || ' world' AS Greeting");
        reader.Read().ShouldBeTrue();

        reader.GetString(0).ShouldNotBe("hello world");
        reader.GetMasking(0).Type.ShouldBe(SensitiveDataType.FreeText);
    }

    /// <summary>Computed numbers (counts, sums) pass when they don't look like sensitive values.</summary>
    [Fact]
    public void Computed_counts_pass_through()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Scalar(connection, "SELECT COUNT(*) FROM People").ShouldBe(2L);
        Scalar(connection, "SELECT COUNT(DISTINCT Ssn) FROM People").ShouldBe(2L);
    }

    /// <summary>A safe column whose value looks like an SSN is masked anyway (the config could be wrong).</summary>
    [Fact]
    public void Safe_column_values_that_look_sensitive_are_masked()
    {
        using var harness = Create();
        harness.Run("UPDATE Orders SET Amount = '900-33-3333' WHERE Id = 11");
        using var connection = harness.Gate().Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT Amount FROM Orders ORDER BY Id");
        reader.Read().ShouldBeTrue();
        reader.GetDouble(0).ShouldBe(12.5);
        reader.Read().ShouldBeTrue();
        reader.GetValue(0).ShouldNotBe("900-33-3333");
    }

    /// <summary>ExecuteScalar goes through the same masking as a reader.</summary>
    [Fact]
    public void ExecuteScalar_masks_too()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Scalar(connection, "SELECT FullName FROM People WHERE Id = 1").ShouldNotBe(AliceName);
    }

    /// <summary>Async readers mask the same way.</summary>
    [Fact]
    public async Task Async_reader_masks_too()
    {
        using var harness = Create();
        await using var connection = harness.Gate().Wrap(harness.Inner);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Ssn FROM People WHERE Id = 1";

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        reader.GetString(0).ShouldNotBe(AliceSsn);
    }

    /// <summary>Only one result set is ever returned.</summary>
    [Fact]
    public void Reader_has_one_result_set()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        using var reader = Read(connection, "SELECT Id FROM People");
        reader.NextResult().ShouldBeFalse();
    }

    /// <summary>Wrapping a wrapped connection returns it unchanged, so masking never runs twice.</summary>
    [Fact]
    public void Wrapping_twice_returns_the_same_connection()
    {
        using var harness = Create();
        var gate = harness.Gate();
        using var connection = gate.Wrap(harness.Inner);

        gate.Wrap(connection).ShouldBeSameAs(connection);
    }

    /// <summary>Switching databases would bypass the listed-table check.</summary>
    [Fact]
    public void ChangeDatabase_is_refused()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Should.Throw<NotSupportedException>(() => connection.ChangeDatabase("main"));
    }

    /// <summary>Database errors that could quote data are replaced with a generic message.</summary>
    [Fact]
    public void Database_errors_hide_details_that_could_hold_data()
    {
        using var harness = Create();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        var ex = Should.Throw<QueryFailedException>(() =>
            Execute(connection, "INSERT INTO People (Id, FullName) VALUES (1, 'Dup Person')"));

        ex.Message.ShouldNotContain("Dup Person");
        ex.InnerException.ShouldBeNull();
    }

    /// <summary>Errors about the query text itself (a misspelt column) are shown, since they only repeat the SQL.</summary>
    [Fact]
    public void Query_text_errors_are_shown()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);

        Should.Throw<QueryFailedException>(() => Scalar(connection, "SELECT Nope FROM People")).Message.ShouldContain("Nope");
    }

    /// <summary>Only SQL text commands are supported; stored procedures and direct table reads can't be checked.</summary>
    [Fact]
    public void Non_text_commands_are_refused()
    {
        using var harness = Create();
        using var connection = harness.Gate().Wrap(harness.Inner);
        using var command = connection.CreateCommand();
        command.CommandText = "People";
        command.CommandType = CommandType.TableDirect;

        Should.Throw<QueryRejectedException>(() => command.ExecuteReader());
    }

    /// <summary>Queries inside a transaction work, including Masque's own sampling of the tables.</summary>
    [Fact]
    public void Queries_run_inside_a_transaction()
    {
        using var harness = Create(reviewed: false);
        harness.SaveReviewedConfigs();
        using var connection = harness.Gate(Writer()).Wrap(harness.Inner);

        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM Orders WHERE Id = 11", transaction).ShouldBe(1);
        Scalar(connection, "SELECT COUNT(*) FROM Orders", transaction).ShouldBe(1L);
        transaction.Rollback();

        Scalar(connection, "SELECT COUNT(*) FROM Orders").ShouldBe(2L);
    }

    internal static MaskingDbDataReader Read(DbConnection connection, string sql)
    {
        // Not disposed here: Microsoft.Data.Sqlite closes a command's reader when the command is disposed.
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return (MaskingDbDataReader)command.ExecuteReader();
    }

    internal static object? Scalar(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    internal static int Execute(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }
}
