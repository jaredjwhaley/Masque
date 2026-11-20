using Masque.Core.Tables;
using Microsoft.Data.Sqlite;

namespace Masque.Sqlite.Tests;

/// <summary>
/// Pins down working out what a write did to CSV or Excel tables loaded into SQLite, so the change can be saved
/// back to the file: only cells whose value really changed are reported, with their new value typed as SQLite
/// stored it, and deleted and inserted rows are reported by position.
/// </summary>
public sealed class TableChangesTests : IDisposable
{
    /// <summary>Id loads as INTEGER, Zip as TEXT (leading zeros), Pay as REAL, Name as TEXT.</summary>
    private static readonly TableData People = new("people", ["Id", "Zip", "Pay", "Name"],
    [
        ["1", "02134", "1.50", "Ana"],
        ["2", "60601", "980", "Lee"],
        ["3", "73301", "12.25", null],
    ]);

    private static readonly TableData Visits = new("visits", ["Id", "Day"], [["1", "Mon"]]);

    private readonly IReadOnlyList<TableData> _tables = [People, Visits];
    private readonly SqliteConnection _connection;

    public TableChangesTests() => _connection = SqliteSources.LoadTables(_tables, writable: true);

    public void Dispose() => _connection.Dispose();

    private IReadOnlyList<TableChanges> Run(string sql)
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return SqliteSources.ReadChanges(_connection, _tables);
    }

    /// <summary>Loaded tables stay read-only unless the caller asks for a writable copy.</summary>
    [Fact]
    public void LoadTables_IsReadOnlyByDefault()
    {
        using var connection = SqliteSources.LoadTables([People]);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM people";

        Should.Throw<SqliteException>(() => command.ExecuteNonQuery());
    }

    /// <summary>With nothing written, every table reports no changes, even where text such as "1.50" loaded as a number.</summary>
    [Fact]
    public void ReadChanges_NothingWritten_IsEmpty()
    {
        var changes = SqliteSources.ReadChanges(_connection, _tables);

        changes.Count.ShouldBe(2);
        changes.ShouldAllBe(c => c.IsEmpty);
    }

    /// <summary>An update reports only the cells it really changed, each with its new value as SQLite stored it.</summary>
    [Fact]
    public void ReadChanges_Update_ReportsChangedCellsWithTypedValues()
    {
        var changes = Run("UPDATE people SET Pay = 2.5, Name = 'Bo' WHERE Id = 1; UPDATE people SET Pay = Pay WHERE Id = 2");

        changes[0].Updates.ShouldBe([new CellChange(0, 2, 2.5d), new CellChange(0, 3, "Bo")]);
        changes[0].DeletedRows.ShouldBeEmpty();
        changes[0].InsertedRows.ShouldBeEmpty();
        changes[1].IsEmpty.ShouldBeTrue();
    }

    /// <summary>Setting a cell to NULL reports a null value, which the file writers save as a blank cell.</summary>
    [Fact]
    public void ReadChanges_SetToNull_ReportsNull()
    {
        var changes = Run("UPDATE people SET Name = NULL WHERE Id = 2");

        changes[0].Updates.ShouldBe([new CellChange(1, 3, null)]);
    }

    /// <summary>Deleted rows are reported by their position in the file's data; inserted rows come with their typed values.</summary>
    [Fact]
    public void ReadChanges_DeleteAndInsert_ReportsPositionsAndValues()
    {
        var changes = Run("DELETE FROM people WHERE Id = 2; INSERT INTO people VALUES (4, '00501', 7, 'Cy')");

        changes[0].DeletedRows.ShouldBe([1]);
        changes[0].InsertedRows.ShouldHaveSingleItem().ShouldBe([4L, "00501", 7d, "Cy"]);
        changes[0].Updates.ShouldBeEmpty();
    }

    /// <summary>Changes are matched to tables in load order, so two files with the same name each get their own changes.</summary>
    [Fact]
    public void ReadChanges_TablesWithTheSameName_AreKeptApart()
    {
        IReadOnlyList<TableData> tables = [People, People];
        using var connection = SqliteSources.LoadTables(tables, writable: true);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY rowid DESC LIMIT 1";
            var second = (string)command.ExecuteScalar()!;
            command.CommandText = $"DELETE FROM \"{second}\" WHERE Id = 3";
            command.ExecuteNonQuery();
        }

        var changes = SqliteSources.ReadChanges(connection, tables);

        changes[0].IsEmpty.ShouldBeTrue();
        changes[1].DeletedRows.ShouldBe([2]);
    }

    /// <summary>Binary values can't be written to a CSV or Excel cell, so they are refused without quoting data.</summary>
    [Fact]
    public void ReadChanges_BinaryValue_IsRefused()
    {
        Should.Throw<TableFileException>(() => Run("UPDATE people SET Name = x'00ff' WHERE Id = 1"))
            .Message.ShouldContain("binary");
    }
}
