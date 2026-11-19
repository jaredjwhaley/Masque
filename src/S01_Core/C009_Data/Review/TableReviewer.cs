using System.Data.Common;
using Masque.Core.Config;
using Masque.Core.Query;
using Masque.Core.Tables;

namespace Masque.Data.Review;

/// <summary>
/// Finds the person-reviewed config for a table. Reads the table's column names first and loads a saved config for
/// that shape; only when none exists does it sample rows and save a proposal for a person to review.
/// </summary>
/// <param name="store">Where configs are saved.</param>
/// <param name="proposer">Classifies a new table shape and saves the proposal.</param>
internal sealed class TableReviewer(ConfigStore store, ConfigProposer proposer)
{
    /// <summary>How many rows are sampled to propose a config for a new table shape.</summary>
    public const int SampleRows = 500;

    /// <summary>Returns the table's reviewed config, or adds it to <paramref name="pending"/> when a person must review it.</summary>
    /// <param name="session">The connection the table is in.</param>
    /// <param name="table">A table the database lists.</param>
    /// <param name="pending">Collects configs still waiting for review.</param>
    /// <returns>The reviewed config, or <see langword="null"/> when it is pending.</returns>
    public MaskConfig? Reviewed(MasqueSession session, TableReference table, List<TableConfig> pending)
    {
        var loaded = Load(session, table);
        if (loaded.Config.ReviewedByPerson)
        {
            return loaded.Config;
        }

        pending.Add(loaded);
        return null;
    }

    /// <summary>Loads the table's saved config, or proposes and saves one; reviewed configs are remembered for the session.</summary>
    /// <param name="session">The connection the table is in.</param>
    /// <param name="table">A table the database lists.</param>
    /// <returns>The config (reviewed or proposed) and its file path.</returns>
    public TableConfig Load(MasqueSession session, TableReference table)
    {
        if (session.TryGetReviewed(table, out var cached))
        {
            return new TableConfig(table.Qualified, store.PathFor(cached.Fingerprint), cached);
        }

        var headers = Run(session, session.Dialect.SampleSql(table, 0), reader =>
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList());

        // Without a header row the "column names" are real values, and showing them would leak data.
        HeaderRow.EnsureLooksLikeHeaders(table.Qualified, headers);

        var fingerprint = SchemaFingerprint.Compute(headers);
        var config = store.Load(fingerprint) ?? proposer.LoadOrPropose(Sample(session, table, headers)).Config;
        if (config.ReviewedByPerson)
        {
            session.RememberReviewed(table, config);
        }

        return new TableConfig(table.Qualified, store.PathFor(fingerprint), config);
    }

    /// <summary>Reads the first rows of a table as text, for classifying its columns. The values never leave Masque.</summary>
    private static TableData Sample(MasqueSession session, TableReference table, IReadOnlyList<string> headers)
    {
        var rows = Run(session, session.Dialect.SampleSql(table, SampleRows), reader =>
        {
            var values = new List<IReadOnlyList<string?>>();
            while (reader.Read())
            {
                values.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CellText.From(reader.GetValue(i))).ToList());
            }

            return values;
        });

        return new TableData(table.Qualified, headers, rows);
    }

    /// <summary>Runs Masque's own SQL on the raw connection, inside its current transaction.</summary>
    private static T Run<T>(MasqueSession session, string sql, Func<DbDataReader, T> read)
    {
        try
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = session.Transaction;
            using var reader = command.ExecuteReader();
            return read(reader);
        }
        catch (DbException ex)
        {
            throw session.Dialect.Describe(ex);
        }
    }
}
