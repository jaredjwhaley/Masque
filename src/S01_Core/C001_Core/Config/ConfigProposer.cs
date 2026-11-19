using Masque.Core.Detection;
using Masque.Core.Tables;

namespace Masque.Core.Config;

/// <summary>A table's saved config and where it lives, so a person can find and review it.</summary>
/// <param name="Table">The table's name in its source.</param>
/// <param name="ConfigPath">The config file's full path.</param>
/// <param name="Config">The saved config (confirmed or still proposed).</param>
public sealed record TableConfig(string Table, string ConfigPath, MaskConfig Config);

/// <summary>
/// Finds the saved config for a table's shape, or classifies its columns and saves an unconfirmed proposal for
/// a person to review. Shared by every way into Masque (CLI, query tools), so they all treat a shape the same.
/// </summary>
/// <param name="store">Where configs are saved.</param>
/// <param name="classifier">Classifies the columns of a new shape.</param>
public sealed class ConfigProposer(ConfigStore store, ColumnClassifier classifier)
{
    /// <summary>Loads the config for a table's headers, proposing and saving one first if none exists.</summary>
    /// <param name="table">The table's name; used only for display.</param>
    /// <param name="headers">The table's column names.</param>
    /// <param name="columnSamples">Sampled values per column, in header order.</param>
    /// <returns>The config and its file path.</returns>
    public TableConfig LoadOrPropose(string table, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> columnSamples)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(columnSamples);

        var fingerprint = SchemaFingerprint.Compute(headers);
        var config = store.Load(fingerprint) ?? Propose(fingerprint, headers, columnSamples);
        return new TableConfig(table, store.PathFor(fingerprint), config);
    }

    /// <summary>Loads or proposes the config for a sampled table.</summary>
    /// <param name="sample">The table's headers and first rows.</param>
    /// <returns>The config and its file path.</returns>
    public TableConfig LoadOrPropose(TableData sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return LoadOrPropose(sample.Name, sample.Headers, sample.ColumnSamples(sample.Rows.Count));
    }

    /// <summary>Classifies each column and saves the result as an unconfirmed config.</summary>
    private MaskConfig Propose(string fingerprint, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> columnSamples)
    {
        var columns = headers
            .Select((header, i) => classifier.Classify(i, header, i < columnSamples.Count ? columnSamples[i] : []))
            .Select(plan => new ColumnRule(plan.Header, plan.Category, plan.Reason))
            .ToList();

        var config = new MaskConfig(fingerprint, ReviewedByPerson: false, columns);
        store.Save(config);
        return config;
    }
}
