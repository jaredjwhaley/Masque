using System.Text;
using Masque.Core.Tables;

namespace Masque.Csv.Tests;

/// <summary>
/// Pins down saving changes back to a CSV file: the delimiter, byte order mark, line endings and the text of every
/// untouched cell are kept, the previous version is kept as a backup, and a file changed on disk since it was read
/// is never overwritten.
/// </summary>
public sealed class CsvFileTests : IDisposable
{
    private const string Original = "Id;Zip;Pay;Name\n1;02134;1.50;Ana\n2;60601;980;Lee\n3;73301;12.25;\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly string _path;

    public CsvFileTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "people.csv");
        File.WriteAllText(_path, Original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TableChanges Changes(
        IReadOnlyList<CellChange>? updates = null,
        IReadOnlyList<int>? deleted = null,
        IReadOnlyList<IReadOnlyList<object?>>? inserted = null) =>
        new(updates ?? [], deleted ?? [], inserted ?? []);

    private string Text() => File.ReadAllText(_path, Encoding.UTF8);

    /// <summary>The file is read as one table named after the file, like <see cref="CsvTable.ReadTable"/>.</summary>
    [Fact]
    public void Open_ReadsTheTable()
    {
        var file = CsvFile.Open(_path);

        file.Path.ShouldBe(_path);
        var table = file.Tables.ShouldHaveSingleItem();
        table.Name.ShouldBe("people");
        table.Headers.ShouldBe(["Id", "Zip", "Pay", "Name"]);
        table.Rows.Count.ShouldBe(3);
    }

    /// <summary>
    /// Only changed cells are rewritten: numbers are written in invariant form, NULL as a blank, and untouched text
    /// such as "1.50" and "02134" stays exactly as it was, with the same delimiter, BOM and line endings.
    /// </summary>
    [Fact]
    public void Save_Update_RewritesOnlyChangedCells()
    {
        var file = CsvFile.Open(_path);

        file.Save([Changes(updates: [new CellChange(1, 2, 2.5d), new CellChange(1, 3, null), new CellChange(2, 3, "Cy")])]);

        Text().ShouldBe("Id;Zip;Pay;Name\n1;02134;1.50;Ana\n2;60601;2.5;\n3;73301;12.25;Cy\n");
        File.ReadAllBytes(_path).Take(3).ShouldBe(Encoding.UTF8.Preamble.ToArray());
    }

    /// <summary>Deleted rows are removed and inserted rows are appended at the end.</summary>
    [Fact]
    public void Save_DeleteAndInsert_RemovesAndAppendsRows()
    {
        var file = CsvFile.Open(_path);

        file.Save([Changes(deleted: [0, 2], inserted: [[4L, "00501", 7d, "Cy; Jr"]])]);

        Text().ShouldBe("Id;Zip;Pay;Name\n2;60601;980;Lee\n4;00501;7;\"Cy; Jr\"\n");
    }

    /// <summary>Files with Windows line endings keep them.</summary>
    [Fact]
    public void Save_KeepsWindowsLineEndings()
    {
        File.WriteAllText(_path, "Id,Name\r\n1,Ana\r\n");
        var file = CsvFile.Open(_path);

        file.Save([Changes(updates: [new CellChange(0, 1, "Bo")])]);

        Text().ShouldBe("Id,Name\r\n1,Bo\r\n");
    }

    /// <summary>The previous version is kept beside the file as a dated backup that a folder scan won't load as data.</summary>
    [Fact]
    public void Save_KeepsTheOriginalAsABackup()
    {
        var file = CsvFile.Open(_path);

        var backup = file.Save([Changes(deleted: [0])]);

        backup.ShouldStartWith(_path + ".");
        backup.ShouldEndWith(".masque-backup");
        File.ReadAllText(backup, Encoding.UTF8).ShouldBe(Original);
        Directory.GetFiles(_folder).Length.ShouldBe(2);
    }

    /// <summary>A file another program holds open (as Excel does) is reported as such, and no backup or temporary file is left.</summary>
    [Fact]
    public void Save_FileLockedByAnotherProgram_SaysSo()
    {
        var file = CsvFile.Open(_path);
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Should.Throw<TableFileException>(() => file.Save([Changes(deleted: [0])])).Message.ShouldContain("open in another program");
        }

        Text().ShouldBe(Original);
        Directory.GetFiles(_folder).ShouldHaveSingleItem();
    }

    /// <summary>A file changed on disk after it was read is left alone, so nobody's edits are lost.</summary>
    [Fact]
    public void Save_FileChangedOnDisk_IsRefused()
    {
        var file = CsvFile.Open(_path);
        File.AppendAllText(_path, "9;99999;1;Zed\n");

        Should.Throw<TableFileException>(() => file.Save([Changes(deleted: [0])])).Message.ShouldContain("changed");

        Text().ShouldEndWith("9;99999;1;Zed\n");
        Directory.GetFiles(_folder).ShouldHaveSingleItem();
    }
}
