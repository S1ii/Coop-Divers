# Dave the Diver Multiplayer

Bring a friend into Dave the Diver.

DaveTheDiverMP is a BepInEx IL2CPP mod that adds direct co-op networking to
Dave the Diver. It focuses on the parts that make a shared dive feel alive:
room setup, scene travel, remote players, catches, pickups, fish, bosses, and
host-authoritative world state.

## How It Works

Dave the Diver is a single-player game, so this mod adds the smallest
multiplayer layer that works: one host owns the game world, clients send
their actions to it, and everyone sees the same dive. Players connect
directly over UDP; there are no accounts, matchmaking servers, or launcher.

## Features

- Native title-screen online room UI.
- Direct host/client sessions over UDP.
- Host-authoritative dive and travel coordination.
- Remote avatar state and movement replication.
- Shared fish, pickups, projectiles, bosses, boat decor, and world objects.
- Mission/story progress, dialogue choice, and catch ledger synchronization.
- Host-authoritative sushi lifecycle, menu, wasabi, tables, and shift results.
- Synchronized results for mapped story and arcade minigames.
- Release packaging for BepInEx plugin installs.

## Project Status

Unofficial community mod, not supported by the game's developer or
platform. It is still in active development: expect rough edges after game
updates, unsupported story moments, and the occasional desync.

## Install

1. Install BepInEx IL2CPP for Dave the Diver.
2. Download the latest release ZIP from GitHub Releases.
3. Extract it into the game folder so the DLL lands here:

```text
BepInEx/
  plugins/
    DaveTheDiverMP/
      DaveTheDiverMP.dll
```

4. Start the game and open the online room UI from the title screen.

## Build From Source

The game installation stays outside the repository. By default, the project
looks for it at `..\DAVE THE DIVER`.

```powershell
.\build\verify-repo.ps1
dotnet build .\DaveTheDiverMP.csproj -c Release -p:DeployPlugin=false
.\build\package.ps1 -Configuration Release
```

Use another install path when needed:

```powershell
dotnet build .\DaveTheDiverMP.csproj -c Release -p:GameInstallPath="D:\Games\DAVE THE DIVER"
```

Deploy directly into the local game only when you want a development build:

```powershell
dotnet build .\DaveTheDiverMP.csproj -c Release -p:DeployPlugin=true
```

## Repository Layout

```text
src/Bootstrap    plugin entry point and Unity update loop
src/Networking   UDP transport, lobby input, and packet protocol
src/Lobby        title-screen online room UI
src/Gameplay     dive/session flow, travel, missions, and remote player state
src/Replication  host/client world replication
build/           local verification and packaging scripts
```

## CI And Releases

GitHub Actions runs repository hygiene on every push and pull request. Full
build/package jobs require a private reference repository because game and
BepInEx DLLs are not committed.

Required GitHub settings:

- Repository variable `REFERENCE_REPOSITORY`: private repo containing
  `BepInEx/core` and `BepInEx/interop` at its root.
- Repository secret `REFERENCE_REPOSITORY_SSH_KEY`: private half of a read-only
  deploy key installed on that repository.

Push a tag such as `v0.16.0` to create a GitHub Release with the BepInEx ZIP
and SHA256 checksum. The same ZIP can be uploaded to Nexus Mods.

## License

MIT. Dave the Diver, its assets, BepInEx, and generated interop assemblies are
not part of this repository.
