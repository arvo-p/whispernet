using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Gestionnaire de comptes locaux Whispernet avec identité ECDSA P-256,
/// dérivation de l'AuthorId par SHA-256 et chiffrement symétrique optionnel (PBKDF2 + AES-256-GCM).
/// </summary>
public sealed class AccountService : IAccountService{
    private const string ProfileFileName = "profile.json";
    private const string PlainKeyFileName = "identity.key";
    private const string EncryptedKeyFileName = "identity.key.enc";
    private const string EncryptedDekFileName = "folder.dek.enc";
    private const string VaultDirectoryName = "vault";

    private static readonly JsonSerializerOptions JsonOptions = new(){
        WriteIndented = true
    };

    private readonly string _rootDirectory;

    public AccountService(string? rootDirectory = null){
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? WhispernetPaths.GetDataDirectory()
            : Path.GetFullPath(rootDirectory);
    }

    public async Task<AccountProfile> CreateAccountAsync(
        string username,
        string? password = null,
        CancellationToken cancellationToken = default){

        string normalizedUsername = ValidateAndNormalizeUsername(username);
        bool hasPassword = !string.IsNullOrEmpty(password);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();
        byte[] privateKeyPkcs8 = ecdsa.ExportPkcs8PrivateKey();

        try{
            AuthorId authorId = AccountCrypto.ComputeAuthorId(publicKeySpki);
            string accountsDir = WhispernetPaths.GetAccountsDirectory(_rootDirectory);
            string accountDir = WhispernetPaths.GetAccountDirectory(authorId, _rootDirectory);
            string vaultDir = Path.Combine(accountDir, VaultDirectoryName);

            EnsureSecureDirectory(_rootDirectory);
            EnsureSecureDirectory(accountsDir);
            EnsureSecureDirectory(accountDir);
            EnsureSecureDirectory(vaultDir);

            string? saltBase64 = null;
            int iterations = 0;

            if (hasPassword){
                byte[] salt = RandomNumberGenerator.GetBytes(AccountCrypto.SaltSizeBytes);
                byte[] dek = RandomNumberGenerator.GetBytes(AccountCrypto.KeySizeBytes);
                byte[] kek = AccountCrypto.DeriveKeyFromPassword(password!, salt, AccountCrypto.DefaultKdfIterations);

                try{
                    byte[] encryptedKey = AccountCrypto.EncryptAesGcm(
                        kek,
                        privateKeyPkcs8,
                        BuildPrivateKeyAad(authorId));
                    byte[] encryptedDek = AccountCrypto.EncryptAesGcm(
                        kek,
                        dek,
                        BuildFolderDekAad(authorId));

                    await WriteSecureFileAsync(
                        Path.Combine(accountDir, EncryptedKeyFileName),
                        encryptedKey,
                        cancellationToken).ConfigureAwait(false);
                    await WriteSecureFileAsync(
                        Path.Combine(accountDir, EncryptedDekFileName),
                        encryptedDek,
                        cancellationToken).ConfigureAwait(false);

                    saltBase64 = Convert.ToBase64String(salt);
                    iterations = AccountCrypto.DefaultKdfIterations;
                }

                finally{
                    CryptographicOperations.ZeroMemory(kek);
                    CryptographicOperations.ZeroMemory(dek);
                }
            }

            else{
                await WriteSecureFileAsync(
                    Path.Combine(accountDir, PlainKeyFileName),
                    privateKeyPkcs8,
                    cancellationToken).ConfigureAwait(false);
            }

            var profile = new AccountProfile(
                AuthorId: authorId,
                Username: normalizedUsername,
                PublicKeySpkiBase64: Convert.ToBase64String(publicKeySpki),
                IsPasswordProtected: hasPassword,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                KdfSaltBase64: saltBase64,
                KdfIterations: iterations);

            byte[] profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
            await WriteSecureFileAsync(
                Path.Combine(accountDir, ProfileFileName),
                profileBytes,
                cancellationToken).ConfigureAwait(false);

            return profile;
        }
        finally{
            CryptographicOperations.ZeroMemory(privateKeyPkcs8);
        }
    }

    public async Task<AccountSession> UnlockAccountAsync(
        AuthorId authorId,
        string? password = null,
        CancellationToken cancellationToken = default){

        if (!authorId.IsValid)
            throw new ArgumentException("Invalid AuthorId.", nameof(authorId));

        string accountDir = WhispernetPaths.GetAccountDirectory(authorId, _rootDirectory);
        string profilePath = Path.Combine(accountDir, ProfileFileName);

        if (!File.Exists(profilePath))
            throw new FileNotFoundException($"Account not found for AuthorId {authorId}.", profilePath);

        byte[] profileBytes = await File.ReadAllBytesAsync(profilePath, cancellationToken).ConfigureAwait(false);
        AccountProfile profile = JsonSerializer.Deserialize<AccountProfile>(profileBytes, JsonOptions)
            ?? throw new InvalidDataException("Corrupted account profile.");

        byte[]? privateKeyPkcs8 = null;
        byte[]? dek = null;

        try{
            if (profile.IsPasswordProtected){
                if (string.IsNullOrEmpty(password))
                    throw new CryptographicException("A password is required to unlock this account.");

                if (string.IsNullOrWhiteSpace(profile.KdfSaltBase64) || profile.KdfIterations <= 0)
                    throw new InvalidDataException("Missing or invalid KDF parameters in account profile.");

                byte[] salt = Convert.FromBase64String(profile.KdfSaltBase64);
                byte[] kek = AccountCrypto.DeriveKeyFromPassword(password, salt, profile.KdfIterations);

                try{
                    byte[] encKeyBytes = await File.ReadAllBytesAsync(
                        Path.Combine(accountDir, EncryptedKeyFileName),
                        cancellationToken).ConfigureAwait(false);
					
                    byte[] encDekBytes = await File.ReadAllBytesAsync(
                        Path.Combine(accountDir, EncryptedDekFileName),
                        cancellationToken).ConfigureAwait(false);

                    privateKeyPkcs8 = AccountCrypto.DecryptAesGcm(kek, encKeyBytes, BuildPrivateKeyAad(authorId));
                    dek = AccountCrypto.DecryptAesGcm(kek, encDekBytes, BuildFolderDekAad(authorId));
                }
                finally{
                    CryptographicOperations.ZeroMemory(kek);
                }
            } else privateKeyPkcs8 = await File.ReadAllBytesAsync( Path.Combine(accountDir, PlainKeyFileName), cancellationToken).ConfigureAwait(false); 

            var ecdsa = ECDsa.Create();
            try{
                ecdsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
                var session = new AccountSession(profile, accountDir, ecdsa, dek);
                dek = null; // Transféré à AccountSession qui le zéroïsera dans Dispose()
                return session;
            }
            catch{
                ecdsa.Dispose();
                throw;
            }
        }
        catch{
            if (dek is not null)
                CryptographicOperations.ZeroMemory(dek);

            throw;
        }
        finally{
            if (privateKeyPkcs8 is not null)
                CryptographicOperations.ZeroMemory(privateKeyPkcs8);
        }
    }

    public async Task<IReadOnlyList<AccountProfile>> ListAccountsAsync(CancellationToken cancellationToken = default){
        string accountsDir = WhispernetPaths.GetAccountsDirectory(_rootDirectory);
        if (!Directory.Exists(accountsDir))
            return Array.Empty<AccountProfile>();

        var results = new List<AccountProfile>();
        foreach (string dir in Directory.EnumerateDirectories(accountsDir)){
            cancellationToken.ThrowIfCancellationRequested();
            string profilePath = Path.Combine(dir, ProfileFileName);
            if (!File.Exists(profilePath))
                continue;

            byte[] bytes = await File.ReadAllBytesAsync(profilePath, cancellationToken).ConfigureAwait(false);
            AccountProfile? profile = JsonSerializer.Deserialize<AccountProfile>(bytes, JsonOptions);
            if (profile is not null)
                results.Add(profile);
        }

        return results;
    }

    public async Task WriteVaultFileAsync(
        AccountSession session,
        string relativePath,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default){

        ArgumentNullException.ThrowIfNull(session);
        (string fullPath, string normalizedRelative) = ResolveVaultFilePath(session.AccountDirectory, relativePath);

        string? parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            EnsureSecureDirectory(parentDir);
        }

        byte[] payload = session.IsFolderEncrypted
            ? AccountCrypto.EncryptAesGcm(
                session.GetFolderDek(),
                plaintext.Span,
                BuildVaultFileAad(session.Profile.AuthorId, normalizedRelative))
            : plaintext.ToArray();

        await WriteSecureFileAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadVaultFileAsync(
        AccountSession session,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        (string fullPath, string normalizedRelative) = ResolveVaultFilePath(session.AccountDirectory, relativePath);

        byte[] diskBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!session.IsFolderEncrypted){
            return diskBytes;
        }

        return AccountCrypto.DecryptAesGcm(
            session.GetFolderDek(),
            diskBytes,
            BuildVaultFileAad(session.Profile.AuthorId, normalizedRelative));
    }

    public byte[] SignData(AccountSession session, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.SignData(data);
    }

    public bool VerifySignature(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public byte[] DerivePairSecret(AccountSession session, ReadOnlySpan<byte> peerPublicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.DerivePairSecret(peerPublicKeySpki);
    }

    private static string ValidateAndNormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        }

        string trimmed = username.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Username must not exceed 64 characters.", nameof(username));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Username contains forbidden control characters.", nameof(username));
        }

        return trimmed;
    }

    private static (string FullPath, string NormalizedRelative) ResolveVaultFilePath(string accountDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("File path must be relative to the account vault.", nameof(relativePath));
        }

        string vaultRoot = Path.GetFullPath(Path.Combine(accountDirectory, VaultDirectoryName));
        string candidate = Path.GetFullPath(Path.Combine(vaultRoot, relativePath));

        string vaultPrefix = vaultRoot.EndsWith(Path.DirectorySeparatorChar)
            ? vaultRoot
            : vaultRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(vaultPrefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Attempted access outside the account vault.");
        }

        string normalizedRelative = Path.GetRelativePath(vaultRoot, candidate).Replace('\\', '/');
        return (candidate, normalizedRelative);
    }

    private static void EnsureSecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        AccountCrypto.ApplyRestrictivePermissions(path, isDirectory: true);
    }

    private static async Task WriteSecureFileAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        AccountCrypto.ApplyRestrictivePermissions(path, isDirectory: false);
    }

    private static byte[] BuildPrivateKeyAad(AuthorId authorId) =>
        Encoding.UTF8.GetBytes($"{authorId.Value}:private-key");

    private static byte[] BuildFolderDekAad(AuthorId authorId) =>
        Encoding.UTF8.GetBytes($"{authorId.Value}:folder-dek");

    private static byte[] BuildVaultFileAad(AuthorId authorId, string normalizedRelativePath) =>
        Encoding.UTF8.GetBytes($"{authorId.Value}:vault:{normalizedRelativePath}");
}
