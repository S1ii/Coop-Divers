# Dave the Diver Multiplayer

Co-op multiplayer mod for Dave the Diver using BepInEx IL2CPP.

## Local build

The game installation is kept outside this repository. By default the project
looks for it at `..\DAVE THE DIVER`.

```powershell
dotnet build -c Release -p:DeployPlugin=false
```

To use another installation, pass `-p:GameInstallPath=...`. To deploy the
built DLL into `BepInEx\plugins\DaveTheDiverMP`, pass
`-p:DeployPlugin=true`.

The public repository contains source code and packaging scripts only. Game
files, generated output, and reference assemblies must stay out of Git.

For the optional full GitHub Actions build, configure the repository variable
`REFERENCE_REPOSITORY` and the secret `REFERENCE_REPOSITORY_TOKEN`. The
private repository must expose the expected `BepInEx\core` and
`BepInEx\interop` folders at its root.

## Release

Create a version tag such as `v0.13.0`. GitHub Actions packages the plugin as
a BepInEx ZIP and attaches it to a GitHub Release when the private reference
repository is configured for the repository.

## License

Source code is distributed under the MIT license. The game and its assets are
not part of this repository.
