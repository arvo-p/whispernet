using System.Security.Cryptography;
using System.Text;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Primitives cryptographiques sécurisées pour l'identité d'auteur et le chiffrement symétrique.
/// </summary>
internal static class AccountCrypto
{
    public const int DefaultKdfIterations = 600_000;
    public const int SaltSizeBytes = 16;
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;
    private const byte BlobFormatVersion = 1;
    /// <summary>
    /// Calcule l'AuthorId comme le condensat SHA-256 (hexadécimal minuscule) de la clé publique SPKI.
    /// </summary>
    public static AuthorId ComputeAuthorId(ReadOnlySpan<byte> publicKeySpki){
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKeySpki, hash);
        string hex = Convert.ToHexStringLower(hash);
        return new AuthorId(hex);
    }

    /// <summary>
    /// Dérive une clé AES-256 depuis un mot de passe via PBKDF2-HMAC-SHA256.
    /// </summary>
    public static byte[] DeriveKeyFromPassword(string password, ReadOnlySpan<byte> salt, int iterations){
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try{
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeySizeBytes);
        }
        finally{
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Chiffre un bloc binaire avec AES-256-GCM au format [version:1][nonce:12][tag:16][ciphertext:N].
    /// </summary>
    public static byte[] EncryptAesGcm( ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData){
        byte[] output = new byte[1 + NonceSizeBytes + TagSizeBytes + plaintext.Length];
        output[0] = BlobFormatVersion;

        Span<byte> nonce = output.AsSpan(1, NonceSizeBytes);
        Span<byte> tag = output.AsSpan(1 + NonceSizeBytes, TagSizeBytes);
        Span<byte> ciphertext = output.AsSpan(1 + NonceSizeBytes + TagSizeBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>
    /// Déchiffre et authentifie un bloc AES-256-GCM. Lève <see cref="CryptographicException"/> si la clé ou l'intégrité est invalide.
    /// </summary>
    public static byte[] DecryptAesGcm( ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData){
        const int headerSize = 1 + NonceSizeBytes + TagSizeBytes;
        if (blob.Length < headerSize || blob[0] != BlobFormatVersion)
            throw new CryptographicException("Invalid encrypted payload format or unsupported version.");

        ReadOnlySpan<byte> nonce = blob.Slice(1, NonceSizeBytes);
        ReadOnlySpan<byte> tag = blob.Slice(1 + NonceSizeBytes, TagSizeBytes);
        ReadOnlySpan<byte> ciphertext = blob.Slice(headerSize);

        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        try{
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch{
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public static string ComputeBlindRoutingTagHex(ReadOnlySpan<byte> pairSecret, long epoch){
        Span<byte> epochBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(epochBytes, epoch);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(pairSecret, epochBytes, mac);
        return Convert.ToHexStringLower(mac);
    }

    public static byte[] DeriveDirectMessageKey(ReadOnlySpan<byte> pairSecret){
        byte[] key = new byte[KeySizeBytes];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            pairSecret,
            key,
            ReadOnlySpan<byte>.Empty,
            "whispernet-dm-aes256gcm-v1"u8);
        return key;
    }

    /// <summary>
    /// Applique des permissions restrictives sur Linux/macOS (0700 dossier, 0600 fichier).
    /// </summary>
    public static void ApplyRestrictivePermissions(string path, bool isDirectory){
        if (OperatingSystem.IsWindows())
            return;

        UnixFileMode mode = isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.SetUnixFileMode(path, mode);
    }
}
