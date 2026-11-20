using System.Data.Common;
using System.Text.Json;
using Masque.Core;
using Masque.Core.Policy;
using Masque.Core.Query;
using Masque.Core.Tables;
using Masque.Csv;
using Masque.Data;
using Masque.Data.Masking;
using Masque.Excel;
using Masque.Sqlite;
using Masque.SqlServer;
using Masque.Vault;

namespace Masque.Mcp;

/// <summary>A tool's reply: the text the assistant sees, and whether it is an error.</summary>
/// <param name="Text">The reply text; never holds a real value from a masked column.</param>
/// <param name="IsError">Whether the call failed.</param>
public sealed record ToolReply(string Text, bool IsError);

/// <summary>A masked query result, ready to format.</summary>
/// <param name="Columns">How each column was masked.</param>
/// <param name="Rows">The masked values as text.</param>
/// <param name="Truncated">Whether more rows existed than were returned.</param>
public sealed record QueryTable(IReadOnlyList<ColumnMasking> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

/// <summary>
/// What the Masque MCP tools do, apart from the MCP plumbing: open a source, pass every statement through the
/// <see cref="MasqueGate"/> as the caller <see cref="Caller"/>, and turn the outcome into text. Errors become replies
/// that name the problem without quoting data.
/// </summary>
/// <remarks>
/// The tool itself is fully capable: what the assistant may see raw or write is decided by the Masque policy for
/// the <see cref="Caller"/> identity, and by the assistant's own permission settings, not by this class.
/// </remarks>
/// <param name="sources">Opens sources by name or path.</param>
/// <param name="gate">Checks statements and masks results.</param>
public sealed class MasqueToolService(ISourceResolver sources, MasqueGate gate) : IDisposable
{
    /// <summary>The caller name the policy sees for every tool call, so an administrator can set what assistants get.</summary>
    public const string Caller = "assistant";

    /// <summary>The most rows one query returns.</summary>
    public const int MaxRowsLimit = 1000;

    private readonly Lock _lock = new();

    /// <summary>Creates the service with explicit locations (used by tests).</summary>
    /// <param name="configFolder">Where table configs are saved.</param>
    /// <param name="vaultPath">The encrypted vault file.</param>
    /// <param name="sourcesPath">The sources.json file of named sources.</param>
    /// <param name="keyProtector">Protects the vault key.</param>
    /// <param name="policyPath">The user's policy file.</param>
    /// <param name="machinePolicyPath">The administrator's machine policy file, which wins over the user's.</param>
    /// <returns>The service.</returns>
    public static MasqueToolService Create(
        string configFolder, string vaultPath, string sourcesPath, IKeyProtector keyProtector, string policyPath, string machinePolicyPath)
    {
        var options = new MasqueOptions
        {
            Caller = new FixedCallerIdentityProvider(Caller),
            ConfigFolder = configFolder,
            VaultPath = vaultPath,
            KeyProtector = keyProtector,
            PolicyPath = policyPath,
            MachinePolicyPath = machinePolicyPath,
        };
        options.Dialects.Add(new SqliteDialect());
        options.Dialects.Add(new SqlServerDialect());

        return new MasqueToolService(new SourceResolver(new SourceCatalog(sourcesPath)), MasqueGate.Create(options));
    }

    /// <summary>Creates the service with the default locations under the Masque folder.</summary>
    /// <returns>The service.</returns>
    public static MasqueToolService CreateDefault() =>
        Create(MasquePaths.DefaultConfigs, MasquePaths.DefaultVault, MasquePaths.DefaultSources, KeyProtectors.ForCurrentPlatform(),
            MasquePaths.DefaultPolicy, MasquePaths.MachinePolicy);

    /// <summary>Lists a source's tables with each column's type; proposes configs for new tables.</summary>
    /// <param name="source">A named source, file or folder.</param>
    /// <returns>The reply.</returns>
    public ToolReply Describe(string source) => Guarded(() =>
    {
        using var opened = sources.Open(source);
        using var connection = gate.Wrap(opened.Connection);
        return new ToolReply(ReplyFormatter.Describe(opened.Name, gate.DescribeTables(connection)), IsError: false);
    });

    /// <summary>Runs one SELECT and returns masked rows.</summary>
    /// <param name="source">A named source, file or folder.</param>
    /// <param name="sql">One SELECT statement.</param>
    /// <param name="maxRows">The most rows to return, 1 to <see cref="MaxRowsLimit"/>.</param>
    /// <param name="allowTypes">Raw types to allow for this query; narrows the policy, never widens it.</param>
    /// <returns>The reply.</returns>
    public ToolReply Query(string source, string sql, int maxRows, IReadOnlyList<string>? allowTypes = null) => Guarded(() =>
    {
        // Check the SQL before loading anything, so a refused query costs nothing.
        if (SqlStatement.Parse(sql).Kind != StatementKind.Select)
        {
            return Error("masque_query runs SELECT statements only. Use masque_execute for INSERT, UPDATE or DELETE.");
        }

        var allowed = ParseTypes(allowTypes);
        using var opened = sources.Open(source);
        using var connection = gate.Wrap(opened.Connection);
        using var command = (MaskingDbCommand)connection.CreateCommand();
        command.CommandText = sql;
        command.AllowedRawTypes = allowed;

        try
        {
            using var reader = (MaskingDbDataReader)command.ExecuteReader();
            return new ToolReply(ReplyFormatter.Result(opened.Name, ReadAll(reader, Math.Clamp(maxRows, 1, MaxRowsLimit))), IsError: false);
        }
        catch (ReviewRequiredException ex)
        {
            return new ToolReply(ReplyFormatter.NeedsReview(ex.Pending), IsError: false);
        }
    });

    /// <summary>
    /// Runs one INSERT, UPDATE or DELETE, if the policy lets the assistant write. On CSV or Excel data the write runs
    /// on an in-memory copy and is then saved back to the files it changed, each with a backup of its previous version.
    /// </summary>
    /// <param name="source">A named source, a database, CSV or Excel file, or a folder of CSV and Excel files.</param>
    /// <param name="sql">One INSERT, UPDATE or DELETE statement.</param>
    /// <returns>The reply, with the number of rows changed and any files saved.</returns>
    public ToolReply Execute(string source, string sql) => Guarded(() =>
    {
        if (SqlStatement.Parse(sql).Kind == StatementKind.Select)
        {
            return Error("masque_execute runs INSERT, UPDATE or DELETE only. Use masque_query for SELECT.");
        }

        using var opened = sources.Open(source, writable: true);
        using var connection = gate.Wrap(opened.Connection);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        try
        {
            var changed = command.ExecuteNonQuery();
            var saved = opened.Files?.SaveChanges() ?? [];
            return new ToolReply(ReplyFormatter.Executed(opened.Name, changed, saved), IsError: false);
        }
        catch (ReviewRequiredException ex)
        {
            return new ToolReply(ReplyFormatter.NeedsReview(ex.Pending), IsError: false);
        }
    });

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();

    /// <summary>Reads up to <paramref name="maxRows"/> rows as text, noting whether more existed.</summary>
    private static QueryTable ReadAll(MaskingDbDataReader reader, int maxRows)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetMasking).ToList();
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

        while (reader.Read())
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CellText.From(reader.GetValue(i))).ToList());
        }

        return new QueryTable(columns, rows, truncated);
    }

    /// <summary>Reads the assistant's allow list; an unknown name is an error so a typo isn't silently ignored.</summary>
    private static HashSet<SensitiveDataType>? ParseTypes(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return null;
        }

        var types = new HashSet<SensitiveDataType>();
        foreach (var name in names)
        {
            if (name.All(char.IsDigit) || !Enum.TryParse<SensitiveDataType>(name, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            {
                throw new QueryRejectedException(
                    $"Unknown type {name} in allowTypes. Use any of: {string.Join(", ", Enum.GetNames<SensitiveDataType>())}.");
            }

            types.Add(type);
        }

        return types;
    }

    private static ToolReply Error(string message) => new($"Error: {message}", IsError: true);

    private ToolReply Guarded(Func<ToolReply> call)
    {
        lock (_lock)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (ex is QueryRejectedException or QueryFailedException or SourceException or PolicyException
                or CsvTableException or ExcelWorkbookException or TableFileException or VaultAccessException or TokenSpaceExhaustedException
                or PlatformNotSupportedException or NotSupportedException or FileNotFoundException or DirectoryNotFoundException
                or UnauthorizedAccessException)
            {
                // These messages are written to name files, tables, columns and causes, never data.
                return Error(ex.Message);
            }
            catch (DbException ex)
            {
                return Error($"the database reported error {ex.ErrorCode}. Details are hidden because they could include data.");
            }
            catch (JsonException)
            {
                return Error("a Masque config or sources file is not valid JSON. Fix the file and try again.");
            }
            catch (Exception ex)
            {
                // Unknown errors may carry data in their message, so only the type is shown.
                return Error($"unexpected {ex.GetType().Name}. Details are hidden so that no data is shown.");
            }
        }
    }
}
