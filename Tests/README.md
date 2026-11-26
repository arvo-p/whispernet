# Whispernet — Suite de Tests & Prototype P2P

Ce répertoire regroupe :
1. **La suite de tests complète (`test-all`)** validant l'infrastructure **Whispernet** (`Whispernet.Core`, `Whispernet.Transport.Iroh`, `Whispernet.App.Messaging`) sur 3 niveaux (Sans-I/O, SQLite, Iroh QUIC + Mainline DHT).
2. **Le prototype interactif de messagerie de groupe P2P** (`demo`, `host`, `join`) basé sur [N0.IrohNet](https://www.nuget.org/packages/N0.IrohNet).

> **Note sur les binaires natifs Mainline DHT :**
> `Tests/Whispernet.csproj` référence `N0.IrohNet` (`0.101.13`) depuis NuGet ainsi que les projets `src/` du dépôt, et copie automatiquement après chaque build nos bibliothèques natives précompilées avec **Mainline DHT** situées dans `../native/runtimes/` (`linux-x64/native/libiroh_bindgen.so` et `win-x64/native/iroh_bindgen.dll`). Vous n'avez **pas** besoin d'installer Rust, Cargo ou Clang.

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

## 2. Lancer la suite de tests complète (`test-all`)

Depuis la racine du dépôt `Whispernet/` (ou depuis `Tests/`), exécutez :

```bash
dotnet run --project Tests -- test-all
```
*(ou depuis `Tests/` : `dotnet run -- test-all`)*

Cette commande exécute successivement les **3 niveaux de tests** ([InfrastructureTests.cs](InfrastructureTests.cs)) :

1. **Niveau 1 — Simulateur Sans-I/O en mémoire (`Whispernet.Core`)** :
   - `1.1 HyParView` : `JOIN`, `FORWARD_JOIN`, `SHUFFLE` sur 6 nœuds et vérification de `CheckConsistencyInvariant()`.
   - `1.2 Plumtree` : diffusion `GOSSIP`, élagage des doublons (`PRUNE`), panne brutale d'un relais (`OnConnectionLost`) et auto-réparation via `IHAVE` + `IHaveTimer` + `GRAFT`.
   - `1.3 PeerCache` : tri de priorité (`MeetingPoint` $\rightarrow$ pairs récents/fidèles $\rightarrow$ anciens $\rightarrow$ probation), tolérance de 5 échecs consécutifs, immunité des `MeetingPoint`, et *Grace Fallback* sur historique périmé (`> 7 jours`).
   - `1.4 Anti-Îlots` : fusion de deux partitions réseau isolées (`{A, B, C}` et `{D, E, F}`) déclenchée par `PartitionHealTimer`.
2. **Niveau 2 — Persistance SQLite réelle (`Whispernet.App.Messaging`)** :
   - `2.1 SqlitePeerCacheRepository` : création de la table `peer_cache`, insertions/mises à jour transactionnelles, ordre de lecture SQL et purge protégeant les `MeetingPoint`.
3. **Niveau 3 — Réseau réel Iroh QUIC + Mainline DHT (`Whispernet.Transport.Iroh` + `GroupMeshService`)** :
   - `3.1` : `Alice` et `Bob` démarrent deux vrais endpoints Iroh (`DiscoveryConfig.DISCOVERY_CONFIG_ALL`) et s'envoient un message 1-to-1 **uniquement par `NodeId` (sans ticket)** via `ConnectSmartAsync` et un vrai stream QUIC.

---

## 3. Lancer le prototype automatisé à 3 pairs (`demo`)

Pour tester uniquement le salon de groupe à 3 endpoints Iroh dans le même processus (**Alice**, **Bob** et **Charlie**) :

```bash
dotnet run --project Tests -- demo
```

---

## 4. Discussion de groupe interactive multi-pairs (`host` & `join`)

Vous pouvez héberger un salon de discussion en direct sur une machine et permettre à des collaborateurs sous Windows, Linux ou macOS de le rejoindre via un ticket d'endpoint Iroh.

### Étape 1 : Héberger un salon de groupe
Sur la machine hôte, exécutez :
```bash
dotnet run --project Tests -- host Alice
```
Une fois l'endpoint lié et connecté à un relais Iroh, il affichera une commande d'invitation contenant un ticket `endpoint...` :
```text
------------------------------------------------------------
Endpoint ID : <64-char base32 public key>
Invite other collaborators to join with this command:
  dotnet run -- join <TheirName> endpointaaqb2lssbuyv...
------------------------------------------------------------
```

### Étape 2 : Rejoindre le salon (Windows / Linux / macOS)
Sur la machine d'un collaborateur, depuis la racine `Whispernet/`, exécutez :

```powershell
dotnet run --project Tests -- join Bob endpointaaqb2lssbuyv...
```

### Étape 3 : Discuter
- Tapez un message et appuyez sur **Entrée** pour le diffuser à tous les membres du groupe.
- Tapez `/quit` et appuyez sur **Entrée** pour quitter proprement.

---

## Notes pour les collaborateurs sous Windows
- **Dépendances du projet de test** : `Tests/Whispernet.csproj` utilise les projets `src/` et les binaires natifs de `native/runtimes/` du dépôt `Whispernet` ; conservez l'ensemble du dépôt cloné.
- **Pare-feu Windows Defender** : lors de la première exécution (`test-all`, `host` ou `join`), autorisez le trafic UDP dans Windows Firewall afin qu'Iroh puisse établir des connexions QUIC pair-à-pair directes.
