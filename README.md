# Whispernet

**Whispernet** est le projet de messagerie de groupe pair-à-pair (P2P) décentralisée et multiplateforme, construit en **.NET 10** sur [N0.IrohNet](https://www.nuget.org/packages/N0.IrohNet) (bindings .NET pour [Iroh](https://iroh.computer/)).

---

## 1. Par où commencer ? (Pour les collaborateurs)

1. **Lire le Plan d'implémentation** : avant d'écrire du code, consultez impérativement le document d'architecture dans [`Rapports/`](Rapports/) :
   - [`Rapports/Plan_d_implementation_1.0.tex`](Rapports/Plan_d_implementation_1.0.tex) *(ou le PDF compilé `Rapports/Plan_d_implementation_1.0.pdf`)*
   - Il détaille le fonctionnement de **HyParView**, de **Plumtree**, leur couplage **Sans-I/O**, ainsi que le rôle des **Raspberry Pi (Points de Meeting 24/24)**.
2. **Tester le prototype P2P** : un premier prototype fonctionnel de messagerie de groupe (basé sur `N0.IrohNet`) est disponible dans le répertoire [`Tests/`](Tests/). Consultez [`Tests/README.md`](Tests/README.md) pour lancer la démo automatisée à 3 pairs ou une session interactive `host` / `join`.

---

## 2. Prérequis et dépendances

### SDK & Runtime .NET 10
Vous avez uniquement besoin du **SDK .NET 10** installé sur votre machine.

#### Windows
Utilisez `dotnet` depuis Visual Studio ou installez via `winget` dans PowerShell, ou téléchargez depuis [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) :
```powershell
winget install Microsoft.DotNet.SDK.10
```

#### Linux (Arch Linux)
```bash
sudo pacman -S dotnet-sdk dotnet-runtime dotnet-targeting-pack
```
*(Pour Ubuntu/Debian/Fedora, installez `dotnet-sdk-10.0` via le dépôt de paquets Microsoft ou le script officiel `dotnet-install.sh`).*

Vérifiez l'installation :
```bash
dotnet --list-sdks
```

### Dépendance `N0.IrohNet` (NuGet)
Le projet référence directement le package NuGet [`N0.IrohNet` (`0.101.13`)](https://www.nuget.org/packages/N0.IrohNet).
- Lors de `dotnet restore` / `dotnet build`, NuGet télécharge automatiquement la bibliothèque native Iroh précompilée adaptée à votre système (`iroh_bindgen.dll` sous Windows, `libiroh_bindgen.so` sous Linux `x64`/`arm64`, `libiroh_bindgen.dylib` sous macOS).

---

## 3. Structure du dépôt (`Whispernet.slnx`)

La solution suit une architecture en couches séparant strictement le protocole P2P, le réseau et les interfaces utilisateur :

- [`src/Whispernet.Core/`](src/Whispernet.Core/) — Couche protocole pure **Sans-I/O** (HyParView, Plumtree, PeerCache, CausalDAG, PRoPHET, `GroupGovernanceEngine`, `FriendGraphEngine`), sans aucune dépendance réseau ni UI.
- [`src/Whispernet.Transport.Iroh/`](src/Whispernet.Transport.Iroh/) — Adaptateur réseau Iroh QUIC (`N0.IrohNet`) qui exécute les actions de `Core` et gère les timers.
- [`src/Whispernet.Daemon.Core/`](src/Whispernet.Daemon.Core/), [`src/Whispernet.Daemon.Linux/`](src/Whispernet.Daemon.Linux/), [`src/Whispernet.Daemon.Client/`](src/Whispernet.Daemon.Client/) — Démon d'arrière-plan (`whispernetd`) et transport IPC client pour le relais DTN aveugle et la persistance réseau.
- [`src/Whispernet.App.Messaging/`](src/Whispernet.App.Messaging/) — Services applicatifs : comptes chiffrés (`AccountService`), groupes & canaux (`GroupGovernanceService`), maillage adaptatif & synchronisation DAG (`GroupMeshService`), DMs E2EE + DTN (`DirectMessagingService`), graphe d'amis & maillage organique (`FriendService` / `IFriendDirectory`).
- [`src/Whispernet.Client.Wpf/`](src/Whispernet.Client.Wpf/) — Interface graphique **WPF** pour Windows.
- [`src/Whispernet.Client.Linux.Gui/`](src/Whispernet.Client.Linux.Gui/) — Interface graphique **Aprillz.MewUI** pour Linux (`Whisper Home` avec DMs & liste d'amis, groupes gouvernés, menu contextuel de groupe, modales d'administration et barre de statut).
- [`src/Whispernet.MeetingNode/`](src/Whispernet.MeetingNode/) — Binaire console léger 24h/24 pour **Raspberry Pi / Linux** (`Core` + `Transport.Iroh`).
- [`Rapports/`](Rapports/) — Documentation d'architecture et plan d'implémentation ([`Plan_d_implementation_1.0.tex`](Rapports/Plan_d_implementation_1.0.tex)).
- [`Tests/`](Tests/) — Suite de tests automatisés Niveaux 1, 2 et 3 (`dotnet run --project Tests/Whispernet.csproj -- test-all`).

---

## 4. Fonctionnalités récentes

- **Groupes gouvernés & canaux** :
  - Synchronisation complète du registre des membres (`GroupRosterSyncPayload` : clés publiques + `EndpointId`) et reconnexion prioritaire via les amis en ligne (`GroupMeshService.GetReconnectTargets`) avec repli sur les autres membres du salon.
- **Messagerie directe 1-on-1 (E2EE + DTN)** :
  - Chiffrement ECDH P-256 + AES-GCM, accusés de réception signés, indicateur de frappe, et bascule PRoPHET DTN à tags aveugles après 10s.
  - Paramètre *Friends-only DMs* (`RestrictDmsToFriends`) configurable dans *Whisper Home*.
- **Mécanisme d'amis & maillage organique (`FriendService`)** :
  - Demandes d'amis signées (in-band depuis les profils de groupe ou via liens `whispernet://friend/...`), états persistés (`vault/friends.json`), protocole *Accept / Reject / Remove*, et exposition via `IFriendDirectory` + maillage organique (`FriendMeshConversationId`) réutilisable par d'autres applications (ex. `App.Feed`).
  - Dans *Whisper Home*, la barre de droite affiche les onglets **Friends** (amis acceptés + demandes envoyées en attente) et **Pending (N)** (demandes entrantes à accepter/refuser).
  - Un mode `[TEST-ONLY]` de boucle locale (`test-loopback-friend-inbox` / `test-loopback-dm-inbox`) permet de tester plusieurs comptes sur le même démon local.

---

## Notes pour les collaborateurs sous Windows
- **Pare-feu Windows Defender** : lors de la première exécution d'un nœud Iroh (`Tests/`), Windows Firewall peut demander l'autorisation pour le trafic UDP. Cliquez sur **Autoriser** afin qu'Iroh puisse établir des connexions QUIC pair-à-pair directes (sinon le trafic passera par les serveurs relais d'Iroh).
