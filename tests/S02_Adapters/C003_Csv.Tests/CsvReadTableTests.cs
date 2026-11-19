using Masque.Core.Tables;

namespace Masque.Csv.Tests;

/// <summary>Pins down reading a whole CSV file as a table, so it can be loaded into a query database.</summary>
public sealed class CsvReadTableTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");

    public CsvReadTableTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The table is named after the file and holds the header row and every data row.</summary>
    [Fact]
    public void ReadTable_ReturnsNameHeadersAndAllRows()
    {
        var path = Write("people.csv", "Ssn;Pay\n123-45-6789;10.00\n987-65-4321;20.00\n");

        TableData table = CsvTable.ReadTable(path);

        table.Name.ShouldBe("people");
        table.Headers.ShouldBe(["Ssn", "Pay"]);
        table.Rows.Count.ShouldBe(2);
        table.Rows[1].ShouldBe(["987-65-4321", "20.00"]);
    }

    /// <summary>Bad quoting fails with the adapter's safe error, which names the row but not the data.</summary>
    [Fact]
    public void ReadTable_BadQuoting_ThrowsSafeError()
    {
        var path = Write("bad.csv", "Name,Note\nZebulon,\"unclosed\n");

        Should.Throw<CsvTableException>(() => CsvTable.ReadTable(path)).Message.ShouldNotContain("Zebulon");
    }
}
