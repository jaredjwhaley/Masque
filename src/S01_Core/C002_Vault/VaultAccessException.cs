namespace Masque.Vault;

/// <summary>
/// The vault can't be opened or read: it belongs to another user or machine, it was made by a newer Masque,
/// or its contents are damaged.
/// </summary>
public sealed class VaultAccessException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong, in words a user can act on.</param>
    public VaultAccessException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What went wrong, in words a user can act on.</param>
    /// <param name="innerException">The underlying error.</param>
    public VaultAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
