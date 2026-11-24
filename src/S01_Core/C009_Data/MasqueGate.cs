using System.Data;
using System.Data.Common;
using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Detection;
using Masque.Core.Policy;
using Masque.Core.Query;
using Masque.Core.Vault;
using Masque.Data.Access;
using Masque.Data.Dialects;
using Masque.Data.Masking;
using Masque.Data.Review;

namespace Masque.Data;

/// <summary>
/// The gate every query passes through. It wraps connections so their results come back masked, checks each
/// statement against what the caller may see and do, and owns the vault, configs and policy behind that. Create one
/// per application (<see cref="Create"/>, or <c>services.AddMasque(...)</c>) and wrap each connection with it.
/// </summary>
public sealed class MasqueGate : IDisposable
{
    private readonly MaskingPolicy _policy;
    private readonly ICallerIdentityProvider _caller;
    private readonly ConfigStore _configs;
    private readonly IReadOnlyList<IMasqueDialect> _dialects;
    private readonly TableReviewer _reviewer;
    private readonly ColumnPlanner _planner;
    private readonly ValueMasker _masker;
    private readonly IDisposable? _ownedVault;

    /// <summary>Creates a gate from its parts. Most applications use <see cref="Create"/> or <c>AddMasque</c> instead.</summary>
    /// <param name="policy">Who may see which types raw, and who may write.</param>
    /// <param name="caller">Says who is calling when a connection is wrapped.</param>
    /// <param name="configs">Where reviewed table configs are kept.</param>
    /// <param name="vault">Where tokens and the values behind them are kept.</param>
    /// <param name="dialects">The databases this gate can handle.</param>
    /// <param name="classifier">Classifies new tables and spots sensitive-looking values; defaults to the standard set.</param>
    /// <param name="random">Randomness for new tokens; defaults to a cryptographic source.</param>
    public MasqueGate(
        MaskingPolicy policy,
        ICallerIdentityProvider caller,
        ConfigStore configs,
        ITokenVault vault,
        IEnumerable<IMasqueDialect> dialects,
        ColumnClassifier? classifier = null,
        IRandomSource? random = null)
        : this(policy, caller, configs, vault, dialects, classifier, random, ownedVault: null)
    {
    }

    private MasqueGate(
        MaskingPolicy policy,
        ICallerIdentityProvider caller,
        ConfigStore configs,
        ITokenVault vault,
        IEnumerable<IMasqueDialect> dialects,
        ColumnClassifier? classifier,
        IRandomSource? random,
        IDisposable? ownedVault)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        ArgumentNullException.ThrowIfNull(vault);
        _dialects = dialects?.ToList() ?? throw new ArgumentNullException(nameof(dialects));

        var detection = classifier ?? ColumnClassifier.Default;
        _reviewer = new TableReviewer(configs, new ConfigProposer(configs, detection));
        _planner = new ColumnPlanner(_reviewer);
        _masker = new ValueMasker(Tokenizer.CreateDefault(vault, random ?? CryptoRandomSource.Shared), vault, detection);
        _ownedVault = ownedVault;
    }

    /// <summary>Builds a gate from options, loading the policy files and opening the default vault as needed.</summary>
    /// <param name="options">The options; see <see cref="MasqueOptions"/> for defaults.</param>
    /// <returns>The gate. Dispose it to close a vault it opened.</returns>
    /// <exception cref="PolicyException">A policy file exists but can't be read.</exception>
    public static MasqueGate Create(MasqueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var policy = options.ResolvePolicy();
        var configs = new ConfigStore(options.ConfigFolder ?? MasquePaths.DefaultConfigs);
        var (vault, owned) = options.ResolveVault();
        try
        {
            return new MasqueGate(policy, options.Caller ?? DefaultCaller(), configs, vault,
                options.Dialects, options.Classifier, options.Random, owned);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>The signed-in Windows user and their groups; elsewhere, the OS user name with no groups.</summary>
    private static ICallerIdentityProvider DefaultCaller() =>
        OperatingSystem.IsWindows() ? new WindowsCallerIdentityProvider() : new FixedCallerIdentityProvider(Environment.UserName);

    /// <summary>Wraps a connection so every command run through it is checked and its results masked.</summary>
    /// <param name="connection">The raw connection, open or not. The wrapper owns it from now on.</param>
    /// <param name="allowRaw">
    /// Raw types to allow on this connection; narrows the caller's policy and never widens it. Null keeps the policy.
    /// </param>
    /// <returns>The wrapper; or <paramref name="connection"/> itself when it is already wrapped.</returns>
    /// <exception cref="NotSupportedException">No dialect handles this kind of connection.</exception>
    public MaskingDbConnection Wrap(DbConnection connection, IEnumerable<SensitiveDataType>? allowRaw = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection is MaskingDbConnection wrapped)
        {
            return wrapped;
        }

        return new MaskingDbConnection(this, connection, Attach(connection), CurrentGrant(allowRaw));
    }

    /// <summary>Starts Masque's record of a raw connection, using the dialect that handles it.</summary>
    /// <param name="connection">The raw connection, open or not.</param>
    /// <returns>A new session for the connection.</returns>
    /// <exception cref="NotSupportedException">No dialect handles this kind of connection.</exception>
    internal MasqueSession Attach(DbConnection connection)
    {
        var dialect = _dialects.FirstOrDefault(d => d.Supports(connection))
            ?? throw new NotSupportedException(
                $"Masque has no dialect for {connection.GetType().Name}. Add one to MasqueOptions.Dialects (for example, new SqliteDialect()).");

        return new MasqueSession(connection, dialect);
    }

    /// <summary>What the current caller may see raw and whether they may write, narrowed by <paramref name="allowRaw"/>.</summary>
    /// <param name="allowRaw">Raw types to allow; narrows the caller's policy and never widens it. Null keeps the policy.</param>
    /// <returns>The grant.</returns>
    internal AccessGrant CurrentGrant(IEnumerable<SensitiveDataType>? allowRaw) => _policy.GrantFor(_caller.Current()).Narrow(allowRaw);

    /// <summary>
    /// The columns of one table that are masked for a caller, by column name: sensitive in the table's reviewed
    /// config and not shown raw by the grant. Used to stop writes that would change a value the caller can't see.
    /// </summary>
    /// <param name="session">The connection's session; the connection must be open.</param>
    /// <param name="table">The table, as an ORM or statement names it.</param>
    /// <param name="grant">What the caller may see raw.</param>
    /// <returns>Masked column names (case-insensitive) with their types.</returns>
    /// <exception cref="QueryRejectedException">The database doesn't list the table.</exception>
    /// <exception cref="ReviewRequiredException">The table's config still needs a person's review.</exception>
    internal IReadOnlyDictionary<string, SensitiveDataType> ProtectedColumns(MasqueSession session, TableReference table, AccessGrant grant)
    {
        var pending = new List<TableConfig>();
        var masked = new Dictionary<string, SensitiveDataType>(StringComparer.OrdinalIgnoreCase);
        AddMaskedColumns(masked, table, session, grant, pending);
        return pending.Count > 0 ? throw new ReviewRequiredException(pending) : masked;
    }

    /// <summary>
    /// Lists a connection's tables with their column settings: the reviewed config, or a proposal saved for a person
    /// to review. Shows names and types only, never values.
    /// </summary>
    /// <param name="connection">A connection this gate wrapped; opened if it is closed.</param>
    /// <returns>One entry per listed table.</returns>
    public IReadOnlyList<TableConfig> DescribeTables(MaskingDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ReferenceEquals(connection.Gate, this))
        {
            throw new ArgumentException("The connection was wrapped by a different Masque gate.", nameof(connection));
        }

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        try
        {
            return connection.Session.ListedTables().Select(table => _reviewer.Load(connection.Session, table)).ToList();
        }
        catch (DbException ex)
        {
            throw connection.Session.Dialect.Describe(ex);
        }
    }

    /// <summary>Narrows a grant by a command's allow list and any <c>-- masque: allow</c> tags in its SQL.</summary>
    /// <param name="grant">The connection's grant.</param>
    /// <param name="commandAllow">The command's allow list, or null.</param>
    /// <param name="commandText">The SQL text.</param>
    /// <returns>The command's grant.</returns>
    internal static AccessGrant GrantFor(AccessGrant grant, IEnumerable<SensitiveDataType>? commandAllow, string? commandText) =>
        grant.Narrow(commandAllow).Narrow(AllowTag.Parse(commandText));

    /// <summary>
    /// Checks a statement and readies the raw command to run it: refuses what the caller may not do, loads reviewed
    /// configs for its tables, and swaps tokens the caller filters on or writes for parameters holding the real values.
    /// </summary>
    /// <param name="inner">The raw command; its text and parameters are changed until the result is disposed.</param>
    /// <param name="commandText">The SQL the caller sent.</param>
    /// <param name="commandType">The caller's command type; only text is supported.</param>
    /// <param name="session">The connection's session.</param>
    /// <param name="grant">What the caller may see raw and whether they may write.</param>
    /// <returns>The prepared command.</returns>
    /// <exception cref="QueryRejectedException">The statement isn't allowed.</exception>
    /// <exception cref="ReviewRequiredException">A table's config still needs a person's review.</exception>
    internal PreparedCommand Prepare(DbCommand inner, string? commandText, CommandType commandType, MasqueSession session, AccessGrant grant)
    {
        if (commandType != CommandType.Text)
        {
            throw new QueryRejectedException(
                $"Masque runs SQL text only; {commandType} commands can't be checked. Send the SQL itself.");
        }

        var statement = SqlStatement.Parse(commandText);
        if (statement.Kind != StatementKind.Select && !grant.CanWrite)
        {
            throw new QueryRejectedException(
                $"This caller may not write: the Masque policy doesn't allow {statement.Kind.ToString().ToUpperInvariant()} statements for them.");
        }

        var masked = MaskedColumns(statement, session, grant);
        var review = statement.Review(masked);

        var added = new List<DbParameter>();
        try
        {
            var edits = TokenFilterEdits(inner, commandText!, review, added);
            edits.AddRange(WrittenValueEdits(inner, review, added));
            inner.CommandText = ApplyEdits(commandText!, edits);
        }
        catch
        {
            // A refusal part-way leaves no real value behind on the caller's command.
            foreach (var parameter in added)
            {
                inner.Parameters.Remove(parameter);
            }

            throw;
        }

        return new PreparedCommand(inner, commandText!, added, session, grant, statement, review);
    }

    /// <summary>Wraps a raw reader so its values are masked by plan.</summary>
    /// <param name="reader">The raw reader.</param>
    /// <param name="prepared">The command that produced it; disposed with the reader.</param>
    /// <returns>The masking reader.</returns>
    internal MaskingDbDataReader WrapReader(DbDataReader reader, PreparedCommand prepared)
    {
        try
        {
            var columns = prepared.Session.Dialect.ReadColumns(reader);
            var plans = _planner.Plan(columns, prepared.Session, prepared.Grant, prepared.Review);
            return new MaskingDbDataReader(reader, columns, plans, _masker, prepared);
        }
        catch
        {
            reader.Dispose();
            prepared.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _ownedVault?.Dispose();

    /// <summary>Collects the columns masked for this caller across the statement's tables, by column name.</summary>
    private Dictionary<string, SensitiveDataType> MaskedColumns(SqlStatement statement, MasqueSession session, AccessGrant grant)
    {
        var pending = new List<TableConfig>();
        var masked = new Dictionary<string, SensitiveDataType>(StringComparer.OrdinalIgnoreCase);

        foreach (var named in statement.Tables)
        {
            AddMaskedColumns(masked, named, session, grant, pending);
        }

        return pending.Count > 0 ? throw new ReviewRequiredException(pending) : masked;
    }

    /// <summary>
    /// Adds one table's masked columns to <paramref name="masked"/>, or the table's config to
    /// <paramref name="pending"/> when it still needs review.
    /// </summary>
    private void AddMaskedColumns(
        Dictionary<string, SensitiveDataType> masked, TableReference named, MasqueSession session, AccessGrant grant, List<TableConfig> pending)
    {
        var table = session.FindListed(named) ?? throw new QueryRejectedException(
            $"{named.Qualified} isn't a table Masque can read: the database doesn't list it as a user table or view.");

        var config = _reviewer.Reviewed(session, table, pending);
        if (config is null)
        {
            return;
        }

        foreach (var rule in config.Columns.Where(r => r.Category.IsSensitive() && !grant.Shows(r.Category)))
        {
            // The same column name with different types in two tables: free text covers either, and tokens of
            // either type can't be resolved by mistake.
            masked[rule.Header] = masked.TryGetValue(rule.Header, out var existing) && existing != rule.Category
                ? SensitiveDataType.FreeText
                : rule.Category;
        }
    }

    /// <summary>
    /// Swaps each known token compared with a masked column for parameters holding every form of its real value (the
    /// normalized value and each spelling seen in the data), since a source may store the value in any of them:
    /// <c>col = 'token'</c> becomes <c>col IN (@a, @b)</c>, <c>'token' &lt;&gt; col</c> becomes
    /// <c>col NOT IN (@a, @b)</c>, and an IN-list item becomes <c>@a, @b</c>. Literals that aren't tokens stay as
    /// written.
    /// </summary>
    private List<TextEdit> TokenFilterEdits(DbCommand inner, string text, StatementReview review, List<DbParameter> added)
    {
        var edits = new List<TextEdit>();
        foreach (var filter in review.TokenFilters)
        {
            var candidate = filter.Kind == TokenFilterKind.Literal ? filter.Text : ParameterText(inner, filter.Text);
            if (candidate is null || !_masker.TryResolve(filter.Type, candidate, out var values))
            {
                continue;
            }

            var list = string.Join(", ", values.Select(value => AddMasqueParameter(inner, value, added)));
            edits.Add(filter.Comparison is { } comparison
                ? new TextEdit(comparison.Start, comparison.Length,
                    $"{text.AsSpan(comparison.ColumnStart, comparison.ColumnLength)} {(comparison.Negated ? "NOT IN" : "IN")} ({list})")
                : new TextEdit(filter.Start, filter.Length, list));
        }

        return edits;
    }

    /// <summary>
    /// Decides what to store for a value a caller writes. A token written as the whole value of a masked column of its
    /// type is stored as the real value it stands for, so what the caller saw is stored as what it stands for. A token
    /// written anywhere else would be stored as a fake (or, swapped, land where it could be read), so it is refused.
    /// Values that aren't tokens are stored as given. SQL writes and EF Core's SaveChanges both use this rule.
    /// </summary>
    /// <param name="maskedType">The type of the masked column being written, or null when the column isn't masked for the caller (or isn't known).</param>
    /// <param name="value">The value as the caller wrote it.</param>
    /// <returns>The real value to store instead, or null when <paramref name="value"/> isn't a token and is stored as given.</returns>
    /// <exception cref="QueryRejectedException">The value is a token, written outside a masked column of its own type.</exception>
    internal string? ResolveWrittenValue(SensitiveDataType? maskedType, string value)
    {
        if (maskedType is { } type && _masker.TryResolveForWrite(type, value, out var real))
        {
            return real;
        }

        if (!_masker.IsToken(value))
        {
            return null;
        }

        // Never quote the value: it is a token, but the message may reach the caller's logs.
        throw new QueryRejectedException(maskedType is null
            ? "A value being written is a Masque token, but not as the whole value of a masked column, so it would be " +
                "stored as a fake. Write a token only as the whole value of a masked column of its type (in SQL, name " +
                "the INSERT's columns)."
            : "A value being written to a masked column is a Masque token of a different type, so it would be stored as " +
                "a fake. Write a token only into a masked column of its own type.");
    }

    /// <summary>
    /// Every form a token's real value may be stored in: its normalized value and each spelling the data was seen to
    /// store it in. Adapters that must find or keep a row's own spelling (EF Core's SaveChanges) compare with these.
    /// </summary>
    /// <param name="maskedType">The masked column's type.</param>
    /// <param name="value">A value that may be a token of that type.</param>
    /// <returns>The stored forms, or null when <paramref name="value"/> isn't a token of <paramref name="maskedType"/>.</returns>
    internal IReadOnlyList<string>? StoredForms(SensitiveDataType maskedType, string value) =>
        _masker.TryResolve(maskedType, value, out var forms) ? forms : null;

    /// <summary>Swaps each written token, as <see cref="ResolveWrittenValue"/> decides, for a parameter holding its real value.</summary>
    /// <exception cref="QueryRejectedException">A token is written outside a masked column of its own type.</exception>
    private List<TextEdit> WrittenValueEdits(DbCommand inner, StatementReview review, List<DbParameter> added)
    {
        var edits = new List<TextEdit>();
        foreach (var written in review.WrittenValues)
        {
            var candidate = written.Kind == TokenFilterKind.Literal ? written.Text : ParameterText(inner, written.Text);
            if (candidate is not null && ResolveWrittenValue(written.Type, candidate) is { } value)
            {
                edits.Add(new TextEdit(written.Start, written.Length, AddMasqueParameter(inner, value, added)));
            }
        }

        return edits;
    }

    /// <summary>Applies non-overlapping edits back to front, so each one's position in the original text still holds.</summary>
    private static string ApplyEdits(string text, IEnumerable<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            text = string.Concat(text.AsSpan(0, edit.Start), edit.Replacement, text.AsSpan(edit.Start + edit.Length));
        }

        return text;
    }

    /// <summary>
    /// The string value of the parameter a filter names, or null when the command has no such parameter or its value
    /// isn't a string (only strings can be tokens). Providers differ on whether a stored name keeps its prefix
    /// (<c>@</c>, <c>:</c>, <c>$</c>), so both forms are tried.
    /// </summary>
    private static string? ParameterText(DbCommand inner, string nameInSql)
    {
        var bare = nameInSql[1..];
        string[] candidates = [nameInSql, bare, $"@{bare}", $":{bare}", $"${bare}"];

        foreach (var name in candidates)
        {
            var index = inner.Parameters.IndexOf(name);
            if (index >= 0)
            {
                return inner.Parameters[index].Value as string;
            }
        }

        return null;
    }

    /// <summary>Adds one string parameter holding a real value for a token, and returns its name.</summary>
    /// <exception cref="QueryRejectedException">The caller already uses Masque's reserved parameter name.</exception>
    private static string AddMasqueParameter(DbCommand inner, string value, List<DbParameter> added)
    {
        var name = $"@masque_value_{added.Count}";
        if (inner.Parameters.Contains(name) || inner.Parameters.Contains(name[1..]))
        {
            throw new QueryRejectedException("Parameter names starting with masque_value_ are reserved for Masque.");
        }

        var parameter = inner.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        inner.Parameters.Add(parameter);
        added.Add(parameter);
        return name;
    }

    /// <summary>One replacement in the command text: <paramref name="Length"/> characters at <paramref name="Start"/>.</summary>
    private sealed record TextEdit(int Start, int Length, string Replacement);
}
