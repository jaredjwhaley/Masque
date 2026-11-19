using System.Security.Cryptography;
using System.Text;
using Masque.Core;

namespace Masque.Vault;

/// <summary>
/// The vault's encryption. From one random 256-bit master key it derives two keys:
/// an index key, for a keyed hash (HMAC) that lets the vault find a value without storing it, and an
/// encryption key, for AES-GCM so the value can be recovered for reveal.
/// </summary>
internal sealed class VaultCrypto : IDisposable
{
    /// <summary>Master key size in bytes (256 bits).</summary>
    public const int MasterKeySize = 32;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _indexKey;
    private readonly AesGcm _aes;

    /// <summary>Derives the working keys. The caller should wipe <paramref name="masterKey"/> afterwards.</summary>
    public VaultCrypto(byte[] masterKey)
    {
        if (masterKey.Length != MasterKeySize)
        {
            throw new VaultAccessException("The vault key has the wrong length; the vault may be damaged.");
        }

        // HKDF turns one master key into independent keys, one per purpose, so neither use weakens the other.
        _indexKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, MasterKeySize, info: "masque/index/v1"u8.ToArray());
        var encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, MasterKeySize, info: "masque/encrypt/v1"u8.ToArray());
        _aes = new AesGcm(encryptionKey, TagSize);
        CryptographicOperations.ZeroMemory(encryptionKey);
    }

    /// <summary>
    /// Keyed hash of a value, used as its lookup key. Without the vault key it can't be reversed or
    /// guessed, even for values with few possibilities such as SSNs.
    /// </summary>
    public byte[] Index(SensitiveDataType category, string normalizedValue) =>
        HMACSHA256.HashData(_indexKey, Encoding.UTF8.GetBytes($"{category}\u001F{normalizedValue}"));

    /// <summary>Encrypts a value. Output layout: nonce, then tag, then ciphertext.</summary>
    /// <param name="value">The real value.</param>
    /// <param name="associatedData">Bytes bound to the ciphertext (its index), so it can't be moved to another row.</param>
    public byte[] Encrypt(string value, byte[] associatedData)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var blob = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        _aes.Encrypt(nonce, plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), associatedData);
        CryptographicOperations.ZeroMemory(plaintext);
        return blob;
    }

    /// <summary>Decrypts a value made by <see cref="Encrypt"/>.</summary>
    /// <exception cref="VaultAccessException">The entry was altered or belongs to another row.</exception>
    public string Decrypt(byte[] blob, byte[] associatedData)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new VaultAccessException("A vault entry is damaged.");
        }

        var plaintext = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            _aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new VaultAccessException("A vault entry is damaged or was altered.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Wipes the derived keys from memory.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_indexKey);
        _aes.Dispose();
    }
}
