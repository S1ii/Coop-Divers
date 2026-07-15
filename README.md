# Dave the Diver Multiplayer

Co-op multiplayer mod for Dave the Diver using BepInEx IL2CPP.

## Layout

```text
src/Bootstrap    plugin entry point and Unity update loop
src/Networking   UDP transport and packet protocol
src/Lobby        title-screen online room UI
src/Gameplay     dive/session flow and remote player state
src/Replication  host/client world replication
build/           local verification and packaging scripts
```

## Local Build

The game installation stays outside the repository. By default the project
uses `..\DAVE THE DIVER`.

```powershell
.\build\verify-repo.ps1
dotnet build .\DaveTheDiverMP.csproj -c Release -p:DeployPlugin=false
.\build\package.ps1 -Configuration Release
```

Use `-p:GameInstallPath=...` for another installation. Use
`-p:DeployPlugin=true` only when you want the build to copy the DLL into the
game's `BepInEx\plugins\DaveTheDiverMP` folder.

## CI/CD

GitHub Actions runs repository hygiene on every push and pull request. Full
build/package jobs require a private reference repository because game and
BepInEx DLLs are not committed.

Required GitHub settings:

- Repository variable `REFERENCE_REPOSITORY`: private repo containing
  `BepInEx/core` and `BepInEx/interop` at its root.
- Repository secret `REFERENCE_REPOSITORY_TOKEN`: token with read access to
  that repository.

Push a tag such as `v0.13.0` to create a GitHub Release with the BepInEx ZIP
and SHA256 checksum. The same ZIP is the Nexus Mods upload artifact.

## License

MIT. The game, game assets, BepInEx, and generated interop assemblies are not
part of this repository.
