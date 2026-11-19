namespace Masque.Core.Query;

/// <summary>
/// Masque refused a query before running it (not read-only, more than one statement, or a table without a
/// usable header row). The message explains why and never quotes the query or any data, so it is safe to show.
/// </summary>
/// <param name="message">The reason, free of data.</param>
public sealed class QueryRejectedException(string message) : Exception(message);

/// <summary>
/// The source failed to run a query or read a table. Adapters write the message so it holds no data; database
/// errors that can quote values are replaced with a generic message, and no inner exception is attached.
/// </summary>
/// <param name="message">What went wrong, free of data.</param>
public sealed class QueryFailedException(string message) : Exception(message);
