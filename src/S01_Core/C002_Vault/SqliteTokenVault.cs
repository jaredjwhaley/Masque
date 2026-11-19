using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Masque.Core;
using Masque.Core.Vault;
using Microsoft.Data.Sqlite;

namespace Masque.Vault;

/// <summary>
/// A token vault in a local SQLite file. Real values are never stored in readable form: each row holds a keyed
/// hash of the value (for lookup), the value encrypted with AES-GCM (for reveal) and the token; recorded spellings
/// are stored the same way. The vault's
/// random master key is stored wrapped by an <see cref="IKeyProtector"/> (the OS secret store), so the file is
/// useless to anyone but the user who created it. Not thread-safe; keep one instance per process.
/// </summary>
public sealed class SqliteTokenVault : ITokenVault, IDisposable
{
    /// <summary>Schema version written to new vaults. Newer vaults are refused rather than misread.</summary>
    /// <remarks>Version 2 added the spellings table; version 1 vaults are upgraded in place when opened.</remarks>
    public const int SchemaVersion = 2;

    private const int SqliteConstraintError = 19;

    private readonly SqliteConnection _connection;
    private readonly VaultCrypto _crypto;

    private SqliteTokenVault(SqliteConnection connection, VaultCrypto crypto)
    {
        _connection = connection;
        _crypto = crypto;
    }

    /// <summary>Opens a vault file, creating it (and its folder) with a new random key if it doesn't exist.</summary>
    /// <param name="path">Path of the vault file.</param>
    /// <param name="keyProtector">Wraps the vault key; use <see cref="KeyProtectors.ForCurrentPlatform"/> outside tests.</param>
    /// <returns>The open vault. Dispose it to close the file and wipe its keys from memory.</returns>
    /// <exception cref="VaultAccessException">The key can't be unwrapped by this user, or the vault is from a newer version.</exception>
    public static SqliteTokenVault Open(string path, IKeyProtector keyProtector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keyProtector);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            EnsureSchema(connection);
            var masterKey = LoadOrCreateMasterKey(connection, keyProtector);
            try
            {
                return new SqliteTokenVault(connection, new VaultCrypto(masterKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryGetToken(SensitiveDataType category, string normalizedValue, [NotNullWhen(true)] out string? token)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT token FROM tokens WHERE category = $category AND value_index = $index;";
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$index", _crypto.Index(category, normalizedValue));

        token = command.ExecuteScalar() as string;
        return token is not null;
    }

    /// <inheritdoc />
    public bool TryGetOriginal(SensitiveDataType category, string token, [NotNullWhen(true)] out string? normalizedValue)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value_index, value_cipher FROM tokens WHERE category = $category AND token = $token;";
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$token", token);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            normalizedValue = null;
            return false;
        }

        normalizedValue = _crypto.Decrypt((byte[])reader["value_cipher"], (byte[])reader["value_index"]);
        return true;
    }

    /// <inheritdoc />
    public bool ContainsToken(SensitiveDataType category, string token)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM tokens WHERE category = $category AND token = $token;";
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$token", token);

        return command.ExecuteScalar() is not null;
    }

    /// <inheritdoc />
    public void Add(SensitiveDataType category, string normalizedValue, string token)
    {
        ArgumentNullException.ThrowIfNull(normalizedValue);
        ArgumentNullException.ThrowIfNull(token);

        var index = _crypto.Index(category, normalizedValue);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tokens (category, value_index, value_cipher, token, created_utc)
            VALUES ($category, $index, $cipher, $token, $created);
            """;
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$index", index);
        command.Parameters.AddWithValue("$cipher", _crypto.Encrypt(normalizedValue, index));
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintError)
        {
            // The table's two unique keys enforce the vault contract: one token per value, one value per token.
            throw new InvalidOperationException($"This {category} value already has a token, or the token is already assigned.", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stored like values: a keyed hash for finding duplicates and the spelling encrypted with AES-GCM. The encryption
    /// is bound to the token as well as the hash, so a row moved to another token fails to decrypt instead of making
    /// that token match someone else's value.
    /// </remarks>
    public void AddSpelling(SensitiveDataType category, string token, string spelling)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(spelling);

        var index = _crypto.Index(category, $"{token}\u001F{spelling}");

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO spellings (category, token, spelling_index, spelling_cipher)
            SELECT $category, $token, $index, $cipher
            WHERE EXISTS (SELECT 1 FROM tokens WHERE category = $category AND token = $token);
            """;
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$index", index);
        command.Parameters.AddWithValue("$cipher", _crypto.Encrypt(spelling, SpellingBinding(index, token)));

        // No row is written both for a spelling already held (fine) and for an unknown token (a caller error).
        if (command.ExecuteNonQuery() == 0 && !ContainsToken(category, token))
        {
            throw new InvalidOperationException($"This {category} token isn't in the vault, so it can't have spellings.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetSpellings(SensitiveDataType category, string token)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT spelling_index, spelling_cipher FROM spellings WHERE category = $category AND token = $token;";
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$token", token);

        var spellings = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            spellings.Add(_crypto.Decrypt((byte[])reader["spelling_cipher"], SpellingBinding((byte[])reader["spelling_index"], token)));
        }

        return spellings;
    }

    /// <summary>Closes the file and wipes the vault's keys from memory.</summary>
    public void Dispose()
    {
        _connection.Dispose();
        _crypto.Dispose();
    }

    /// <summary>Test hook: reads every stored value index, as hex, straight from a closed vault file.</summary>
    internal static IReadOnlyList<string> ReadValueIndexes(string path)
    {
        using var connection = OpenConnection(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_index FROM tokens;";

        var indexes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(Convert.ToHexString((byte[])reader[0]));
        }

        return indexes;
    }

    /// <summary>The bytes a spelling's encryption is bound to: its hash, then its token.</summary>
    private static byte[] SpellingBinding(byte[] index, string token) => [.. index, .. Encoding.UTF8.GetBytes(token)];

    /// <summary>Opens a connection without pooling, so disposing the vault releases the file at once.</summary>
    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Creates the tables on first use and refuses vaults written by a newer schema.</summary>
    private static void EnsureSchema(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE TABLE IF NOT EXISTS meta (
                name  TEXT PRIMARY KEY,
                value BLOB NOT NULL);

            CREATE TABLE IF NOT EXISTS tokens (
                category     TEXT NOT NULL,
                value_index  BLOB NOT NULL,
                value_cipher BLOB NOT NULL,
                token        TEXT NOT NULL,
                created_utc  TEXT NOT NULL,
                PRIMARY KEY (category, value_index),
                UNIQUE (category, token));

            CREATE TABLE IF NOT EXISTS spellings (
                category        TEXT NOT NULL,
                token           TEXT NOT NULL,
                spelling_index  BLOB NOT NULL,
                spelling_cipher BLOB NOT NULL,
                PRIMARY KEY (category, token, spelling_index));

            INSERT OR IGNORE INTO meta (name, value) VALUES ('schema_version', {SchemaVersion});
            """;
        create.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE name = 'schema_version';";
        var version = Convert.ToInt32(read.ExecuteScalar());
        if (version > SchemaVersion)
        {
            throw new VaultAccessException(
                $"This vault was made by a newer version of Masque (schema {version}); update Masque to open it.");
        }

        if (version < SchemaVersion)
        {
            // Version 1 lacked only the spellings table, created above; its tokens simply have no spellings yet.
            using var upgrade = connection.CreateCommand();
            upgrade.CommandText = $"UPDATE meta SET value = {SchemaVersion} WHERE name = 'schema_version';";
            upgrade.ExecuteNonQuery();
        }
    }

    /// <summary>Unwraps the stored master key, or creates, wraps and stores a new random one.</summary>
    private static byte[] LoadOrCreateMasterKey(SqliteConnection connection, IKeyProtector keyProtector)
    {
        var newKey = RandomNumberGenerator.GetBytes(VaultCrypto.MasterKeySize);
        try
        {
            // INSERT OR IGNORE keeps the first key if two processes create the vault at the same moment.
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT OR IGNORE INTO meta (name, value) VALUES ('master_key', $key);";
                insert.Parameters.AddWithValue("$key", keyProtector.Protect(newKey));
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM meta WHERE name = 'master_key';";
            var wrapped = (byte[])read.ExecuteScalar()!;

            try
            {
                return keyProtector.Unprotect(wrapped);
            }
            catch (CryptographicException ex)
            {
                throw new VaultAccessException(
                    "This vault's key can't be unlocked by the current user. It was made by another user account or on another computer, or it is damaged.",
                    ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newKey);
        }
    }
}
