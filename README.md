# Whispernet (Application de test de messagerie de groupe P2P avec N0.IrohNet)

Une application de test de messagerie de groupe pair-à-pair multiplateforme basée sur [N0.IrohNet](https://www.nuget.org/packages/N0.IrohNet) (bindings .NET pour [Iroh](https://iroh.computer/)).

Étant donné que ce projet référence `N0.IrohNet` (`0.101.13`) directement depuis NuGet.org, il fonctionne immédiatement sous **Windows**, **Linux** et **macOS** sans avoir besoin d'installer Rust, Cargo ou Clang. NuGet télécharge automatiquement la bibliothèque native Iroh précompilée (`iroh_bindgen.dll` sous Windows, `libiroh_bindgen.so` sous Linux, `libiroh_bindgen.dylib` sous macOS).

---

## 1. Prérequis

Vous avez uniquement besoin du **SDK .NET 10** installé sur votre machine.

### Windows
Installez-le via `winget` dans PowerShell / l'Invite de commandes, ou téléchargez-le depuis [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) :
```powershell
winget install Microsoft.DotNet.SDK.10
```

### Linux (Arch Linux)
```bash
sudo pacman -S dotnet-sdk dotnet-runtime dotnet-targeting-pack
```
*(Pour Ubuntu/Debian/Fedora, installez `dotnet-sdk-10.0` via le dépôt de paquets Microsoft ou le script officiel `dotnet-install.sh`).*

Vérifiez l'installation :
```bash
dotnet --list-sdks
```

---

## 2. Lancer le test automatisé à 3 pairs (`demo`)

Pour vérifier que `N0.IrohNet` fonctionne sur votre machine, ouvrez un terminal dans ce dossier `Whispernet` et exécutez :

```bash
dotnet run -- demo
```

Cela démarre trois points de terminaison (endpoints) Iroh QUIC distincts dans le même processus (**Alice** en tant qu'hôte du salon, **Bob** et **Charlie** en tant que membres du groupe), les connecte via le ticket d'endpoint Iroh d'Alice, diffuse des messages entre les trois pairs, vérifie la réception, puis se déconnecte proprement.

---

## 3. Discussion de groupe interactive multi-pairs (`host` & `join`)

Vous pouvez héberger un salon de discussion de groupe en direct sur une machine (ou un terminal) et permettre à des collaborateurs sous Windows, Linux ou macOS de le rejoindre sur le réseau à l'aide d'un ticket d'endpoint Iroh.

### Étape 1 : Héberger un salon de groupe
Sur la machine hôte, exécutez :
```bash
dotnet run -- host Alice
```
Une fois l'endpoint lié et connecté à un relais Iroh, il affichera une commande d'invitation contenant une longue chaîne de ticket `endpoint...` :
```text
------------------------------------------------------------
Endpoint ID : <64-char base32 public key>
Invite other collaborators to join with this command:
  dotnet run -- join <TheirName> endpointaaqb2lssbuyv...
------------------------------------------------------------
```

### Étape 2 : Rejoindre le salon de groupe (Windows / Linux / macOS)
Sur la machine de n'importe quel collaborateur, ouvrez un terminal dans `Whispernet` et exécutez la commande affichée par l'hôte (en remplaçant `<TheirName>` par son nom d'affichage) :

```powershell
dotnet run -- join Bob endpointaaqb2lssbuyv...
```

### Étape 3 : Discuter
- Tapez n'importe quel message et appuyez sur **Entrée** pour le diffuser à tous les membres du groupe.
- Tapez `/quit` et appuyez sur **Entrée** pour quitter le groupe proprement.

---

## Notes pour les collaborateurs sous Windows
- **Seul le dossier `Whispernet` est nécessaire** : Vous n'avez besoin que de `Whispernet.csproj` et `Program.cs` pour compiler et exécuter le projet sous Windows.
- **Pare-feu Windows Defender** : La première fois que vous exécutez `host` ou `join`, le pare-feu Windows peut vous demander l'autorisation d'autoriser le trafic UDP. Cliquez sur **Autoriser** afin qu'Iroh puisse établir des connexions QUIC pair-à-pair directes (sinon le trafic passera par les serveurs relais d'Iroh).
