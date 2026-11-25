using System.Security.Cryptography;
using Whispernet.Core;

namespace Whispernet.App;

/// <summary>
/// Métadonnées publiques persistées d'un compte auteur Whispernet.
/// </summary>
public sealed record AccountProfile(
    AuthorId AuthorId,
    string Username,
    string PublicKeySpkiBase64,
    bool IsPasswordProtected,
    DateTimeOffset CreatedAtUtc,
    string? KdfSaltBase64 = null,
    int KdfIterations = 0);

/// <summary>
/// Session applicative déverrouillée contenant la clé de signature et la DEK du dossier.
/// L'appel à <see cref="Dispose"/> efface la clé symétrique de la mémoire et libère la clé ECDSA.
/// </summary>
public sealed class AccountSession : IDisposable
{
    private readonly ECDsa _signingKey;
    private readonly byte[]? _folderDek;
    private bool _disposed;

    internal AccountSession(
        AccountProfile profile,
        string accountDirectory,
        ECDsa signingKey,
        byte[]? folderDek)
    {
        Profile = profile;
        AccountDirectory = accountDirectory;
        _signingKey = signingKey;
        _folderDek = folderDek;
    }

    public AccountProfile Profile { get; }
    public string AccountDirectory { get; }
    public bool IsFolderEncrypted => _folderDek is not null;

    internal byte[] SignData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _signingKey.SignData(data, HashAlgorithmName.SHA256);
    }

    internal ReadOnlySpan<byte> GetFolderDek()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _folderDek ?? throw new InvalidOperationException("This account does not use a folder encryption key.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signingKey.Dispose();
        if (_folderDek is not null)
        {
            CryptographicOperations.ZeroMemory(_folderDek);
        }
    }
}

/// <summary>
/// Service de création, déverrouillage, signature et stockage chiffré des comptes locaux.
/// </summary>
public interface IAccountService
{
    Task<AccountProfile> CreateAccountAsync(
        string username,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<AccountSession> UnlockAccountAsync(
        AuthorId authorId,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountProfile>> ListAccountsAsync(CancellationToken cancellationToken = default);

    Task WriteVaultFileAsync(
        AccountSession session,
        string relativePath,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadVaultFileAsync(
        AccountSession session,
        string relativePath,
        CancellationToken cancellationToken = default);

    byte[] SignData(AccountSession session, ReadOnlySpan<byte> data);

    bool VerifySignature(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
}
