using Masque.Core.Tables;

namespace Masque.Core.Tests.Query;

/// <summary>Pins down how a whole table is turned into per-column samples for detection.</summary>
public sealed class TableDataTests
{
    /// <summary>Samples are taken per column, from at most the requested number of rows; short rows give nulls.</summary>
    [Fact]
    public void ColumnSamples_TransposesUpToMaxRows()
    {
        var table = new TableData("t", ["A", "B"], [["1", "x"], ["2"], ["3", "z"]]);

        var samples = table.ColumnSamples(maxRows: 2);

        samples[0].ShouldBe(["1", "2"]);
        samples[1].ShouldBe(["x", null]);
    }
}
