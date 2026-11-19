using System.Text;
using Masque.Core;
using Masque.Core.Tests.TestSupport;
using Masque.Core.Vault;

namespace Masque.Csv.Tests;

public sealed class CsvTableTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly Tokenizer _tokenizer = Tokenizer.CreateDefault(new InMemoryTokenVault(), new SeededRandomSource());

    public CsvTableTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string WriteInput(string content, bool bom = false, string name = "input.csv")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
        return path;
    }

    private string OutputPath => Path.Combine(_folder, "output.csv");

    private static List<string[]> ReadBack(string path, char delimiter = ',')
    {
        // Minimal reader for test assertions; quoted fields only appear where a test puts them.
        var rows = new List<string[]>();
        using var parser = new CsvHelper.CsvParser(new StreamReader(path),
            new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture) { Delimiter = delimiter.ToString() });
        while (parser.Read())
        {
            rows.Add(parser.Record!);
        }

        return rows;
    }

    [Fact]
    public void ReadSample_ReturnsHeadersAndColumnSamples()
    {
        var path = WriteInput("SSN,Pay\n123-45-6789,10.00\n987-65-4321,20.00\n");

        var sample = CsvTable.ReadSample(path);

        sample.Headers.ShouldBe(["SSN", "Pay"]);
        sample.ColumnSamples[0].ShouldBe(["123-45-6789", "987-65-4321"]);
        sample.ColumnSamples[1].ShouldBe(["10.00", "20.00"]);
    }

    [Fact]
    public void ReadSample_StopsAtMaxRows()
    {
        var path = WriteInput("Id\n1\n2\n3\n4\n");

        CsvTable.ReadSample(path, maxRows: 2).ColumnSamples[0].Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void ReadSample_DetectsTheDelimiter(char delimiter)
    {
        var path = WriteInput($"SSN{delimiter}Pay\n123-45-6789{delimiter}10.00\n");

        var sample = CsvTable.ReadSample(path);

        sample.Headers.ShouldBe(["SSN", "Pay"]);
        sample.Delimiter.ShouldBe(delimiter.ToString());
    }

    [Fact]
    public void ReadSample_EmptyFile_Throws()
    {
        Should.Throw<CsvTableException>(() => CsvTable.ReadSample(WriteInput("")));
    }

    [Fact]
    public void Mask_TokenizesByColumnAndKeepsHeadersAndPassThroughColumns()
    {
        var path = WriteInput("SSN,Pay\n123-45-6789,\"1,234.56\"\n");

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Ssn, SensitiveDataType.Money], _tokenizer).ShouldBe(1);

        var rows = ReadBack(OutputPath);
        rows[0].ShouldBe(["SSN", "Pay"]);
        rows[1][0].ShouldMatch(@"^9\d{2}-\d{2}-\d{4}$");
        rows[1][1].ShouldBe("1,234.56");
    }

    [Fact]
    public void Mask_SameValueOnTwoRows_GetsTheSameToken()
    {
        var path = WriteInput("SSN\n123-45-6789\n123456789\n");

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Ssn], _tokenizer);

        var rows = ReadBack(OutputPath);
        rows[2][0].ShouldBe(rows[1][0]);
    }

    [Fact]
    public void Mask_QuotedFieldsWithCommasAndLineBreaks_SurviveTheRoundTrip()
    {
        var path = WriteInput("Code,Notes\n\"A,B\",\"line one\nline two\"\n");

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Code, SensitiveDataType.FreeText], _tokenizer);

        var rows = ReadBack(OutputPath);
        rows.Count.ShouldBe(2);
        rows[1][0].ShouldBe("A,B");
        rows[1][1].ShouldMatch(@"^\[FREETEXT-[0-9A-F]{8}\]$");
    }

    [Fact]
    public void Mask_ExtraFieldsBeyondTheHeaders_AreMasked()
    {
        var path = WriteInput("Code\nA,leaked value\n");

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Code], _tokenizer);

        File.ReadAllText(OutputPath).ShouldNotContain("leaked value");
    }

    [Fact]
    public void Mask_KeepsTheDelimiter()
    {
        var path = WriteInput("Code;Pay\nA;10.00\n");

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Code, SensitiveDataType.Money], _tokenizer);

        ReadBack(OutputPath, ';')[1].ShouldBe(["A", "10.00"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mask_KeepsTheByteOrderMarkChoice(bool bom)
    {
        var path = WriteInput("Code\nA\n", bom);

        CsvTable.Mask(path, OutputPath, [SensitiveDataType.Code], _tokenizer);

        var bytes = File.ReadAllBytes(OutputPath);
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble).ShouldBe(bom);
    }

    [Fact]
    public void Mask_BadData_ReportsTheRowButNeverTheValue()
    {
        var path = WriteInput("Name\nPat \"Secret\" Smith\n");

        var error = Should.Throw<CsvTableException>(() => CsvTable.Mask(path, OutputPath, [SensitiveDataType.Name], _tokenizer));

        error.Message.ShouldContain("row 2");
        error.Message.ShouldNotContain("Secret");
        error.InnerException.ShouldBeNull();
    }
}
