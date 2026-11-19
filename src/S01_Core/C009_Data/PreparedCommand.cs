using System.Data.Common;
using Masque.Core.Policy;
using Masque.Core.Query;

namespace Masque.Data;

/// <summary>
/// A command Masque checked and is about to run. It holds what the result masking needs, and on dispose takes the
/// token-filter rewrite back out of the raw command, so real values don't linger on it.
/// </summary>
internal sealed class PreparedCommand : IDisposable
{
    private readonly DbCommand _inner;
    private readonly string _originalText;
    private readonly IReadOnlyList<DbParameter> _added;
    private readonly IDisposable? _writeScope;
    private bool _disposed;

    /// <summary>Creates the prepared command; the raw command already holds the rewritten text and parameters.</summary>
    public PreparedCommand(
        DbCommand inner, string originalText, IReadOnlyList<DbParameter> added, MasqueSession session, AccessGrant grant,
        SqlStatement statement, StatementReview review)
    {
        _inner = inner;
        _originalText = originalText;
        _added = added;
        Session = session;
        Grant = grant;
        Statement = statement;
        Review = review;
        _writeScope = statement.Kind == StatementKind.Select ? null : session.AllowWrites();
    }

    /// <summary>The connection it runs on.</summary>
    public MasqueSession Session { get; }

    /// <summary>What this command's caller may see raw.</summary>
    public AccessGrant Grant { get; }

    /// <summary>The parsed statement.</summary>
    public SqlStatement Statement { get; }

    /// <summary>The statement's review: masked names and token filters.</summary>
    public StatementReview Review { get; }

    /// <summary>Ends the write allowance, as soon as the database has prepared the statement.</summary>
    public void EndWrites() => _writeScope?.Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndWrites();
        foreach (var parameter in _added)
        {
            _inner.Parameters.Remove(parameter);
        }

        _inner.CommandText = _originalText;
    }
}
