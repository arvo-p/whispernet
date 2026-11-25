# Whispernet (N0.IrohNet P2P Group Messaging Test App)

A cross-platform peer-to-peer group messaging test application built on [N0.IrohNet](https://www.nuget.org/packages/N0.IrohNet) (.NET bindings for [Iroh](https://iroh.computer/)).

Because this project references `N0.IrohNet` (`0.101.13`) directly from NuGet.org, it works out of the box on **Windows**, **Linux**, and **macOS** without needing Rust, Cargo, or Clang installed. NuGet automatically downloads the pre-compiled native Iroh library (`iroh_bindgen.dll` on Windows, `libiroh_bindgen.so` on Linux, `libiroh_bindgen.dylib` on macOS).

---

## 1. Prerequisites

You only need the **.NET 10 SDK** installed on your machine.

### Windows
Install via `winget` in PowerShell / Command Prompt, or download from [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/10.0):
```powershell
winget install Microsoft.DotNet.SDK.10
```

### Linux (Arch Linux)
```bash
sudo pacman -S dotnet-sdk dotnet-runtime dotnet-targeting-pack
```
*(For Ubuntu/Debian/Fedora, install `dotnet-sdk-10.0` via Microsoft's package feed or the official `dotnet-install.sh` script).*

Verify the installation:
```bash
dotnet --list-sdks
```

---

## 2. Running the Automated 3-Peer Test (`demo`)

To verify that `N0.IrohNet` works on your machine, open a terminal in this `Whispernet` folder and run:

```bash
dotnet run -- demo
```

This starts three separate Iroh QUIC endpoints in-process (**Alice** as the room host, **Bob** and **Charlie** as group members), connects them via Alice's Iroh endpoint ticket, broadcasts messages across all three peers, verifies delivery, and cleanly disconnects.

---

## 3. Interactive Multi-Peer Group Chat (`host` & `join`)

You can host a live group chat room on one machine (or terminal) and have collaborators on Windows, Linux, or macOS join it over the network using an Iroh Endpoint Ticket.

### Step 1: Host a Group Room
On the host machine, run:
```bash
dotnet run -- host Alice
```
Once the endpoint binds and connects to an Iroh relay, it will print an invite command containing a long `endpoint...` ticket string:
```text
------------------------------------------------------------
Endpoint ID : <64-char base32 public key>
Invite other collaborators to join with this command:
  dotnet run -- join <TheirName> endpointaaqb2lssbuyv...
------------------------------------------------------------
```

### Step 2: Join the Group Room (Windows / Linux / macOS)
On any collaborator's machine, open a terminal in `Whispernet` and run the command printed by the host (replacing `<TheirName>` with their display name):

```powershell
dotnet run -- join Bob endpointaaqb2lssbuyv...
```

### Step 3: Chat
- Type any message and press **Enter** to broadcast it to everyone in the group.
- Type `/quit` and press **Enter** to leave the group cleanly.

---

## Notes for Windows Collaborators
- **Only the `Whispernet` folder is required**: You only need `Whispernet.csproj` and `Program.cs` to build and run on Windows.
- **Windows Defender Firewall**: The first time you run `host` or `join`, Windows Firewall may ask for permission to allow UDP traffic. Click **Allow** so Iroh can establish direct peer-to-peer QUIC connections (otherwise it will route traffic through Iroh's relay servers).
