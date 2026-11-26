using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Résolution centralisée et multiplateforme des répertoires de données Whispernet.
/// Windows : %APPDATA%\Whispernet ; Linux : ~/.local/share/whispernet.
/// </summary>
public static class WhispernetPaths
{
    /// <summary>
    /// Retourne le répertoire racine des données Whispernet selon le système d'exploitation.
    /// </summary>
    public static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(appData, "Whispernet");
        }

        string localShare = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localShare))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            localShare = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(localShare, "whispernet");
    }

    /// <summary>
    /// Retourne le répertoire contenant les dossiers des comptes locaux.
    /// </summary>
    public static string GetAccountsDirectory(string? rootDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(rootDirectory) ? GetDataDirectory() : Path.GetFullPath(rootDirectory);
        return Path.Combine(root, "accounts");
    }

    /// <summary>
    /// Retourne le dossier dédié à un compte identifié par son <see cref="AuthorId"/>.
    /// </summary>
    public static string GetAccountDirectory(AuthorId authorId, string? rootDirectory = null)
    {
        if (!authorId.IsValid)
        {
            throw new ArgumentException("Invalid AuthorId.", nameof(authorId));
        }

        return Path.Combine(GetAccountsDirectory(rootDirectory), authorId.Value);
    }

    /// <summary>
    /// Retourne le chemin par défaut de la base SQLite du cache de pairs.
    /// </summary>
    public static string GetPeerCacheDbPath(string? rootDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(rootDirectory) ? GetDataDirectory() : Path.GetFullPath(rootDirectory);
        return Path.Combine(root, "peer_cache.db");
    }
}
