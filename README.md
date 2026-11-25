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

- [`src/Whispernet.Core/`](src/Whispernet.Core/) — Couche protocole pure **Sans-I/O** (HyParView + Plumtree), sans aucune dépendance réseau ni UI.
- [`src/Whispernet.Transport.Iroh/`](src/Whispernet.Transport.Iroh/) — Adaptateur réseau Iroh QUIC (`N0.IrohNet`) qui exécute les actions de `Core` et gère les timers.
- [`src/Whispernet.App/`](src/Whispernet.App/) — Logique applicative partagée et *ViewModels* (indépendante de l'UI).
- [`src/Whispernet.Client.Wpf/`](src/Whispernet.Client.Wpf/) — Interface graphique **WPF** pour Windows (100 % UI, liée à `Whispernet.App`).
- [`src/Whispernet.Client.Linux/`](src/Whispernet.Client.Linux/) — Interface utilisateur pour Linux (100 % UI, liée à `Whispernet.App`).
- [`src/Whispernet.MeetingNode/`](src/Whispernet.MeetingNode/) — Binaire console léger 24h/24 pour **Raspberry Pi / Linux** (`Core` + `Transport.Iroh`).
- [`Rapports/`](Rapports/) — Documentation d'architecture et plan d'implémentation ([`Plan_d_implementation_1.0.tex`](Rapports/Plan_d_implementation_1.0.tex)).
- [`Tests/`](Tests/) — Prototype de test de messagerie de groupe P2P avec `N0.IrohNet` ([`Tests/README.md`](Tests/README.md)).

---

## Notes pour les collaborateurs sous Windows
- **Pare-feu Windows Defender** : lors de la première exécution d'un nœud Iroh (`Tests/`), Windows Firewall peut demander l'autorisation pour le trafic UDP. Cliquez sur **Autoriser** afin qu'Iroh puisse établir des connexions QUIC pair-à-pair directes (sinon le trafic passera par les serveurs relais d'Iroh).
