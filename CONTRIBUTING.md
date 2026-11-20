# Contributing

Build and test on Windows with the .NET SDK in [global.json](global.json). Use a separate branch and describe the affected behavior in the pull request.

```powershell
dotnet build Snowfall/Snowfall.csproj -c Release -warnaserror
dotnet run --project Snowfall.Tests/Snowfall.Tests.csproj -c Release
dotnet run --project Snowfall.Tests/Snowfall.Tests.csproj -c Release -- --memory-soak
```

The regression runner exercises the weather and interaction simulation, overlay rendering, and native mouse input. Run UI checks on an interactive Windows desktop. The smoke runner uses an isolated settings directory via `FLURRYVEIL_DATA_DIR`.

```powershell
dotnet run --project eng/UiSmoke/UiSmoke.csproj -c Release -- Snowfall/bin/Release/net10.0-windows/Flurryveil.exe docs/screenshots
```

Update the version in `Snowfall/Snowfall.csproj`, the changelog and `docs/release-notes.md` before tagging. Run the packaging script locally to verify both packages. Push the matching `vMAJOR.MINOR.PATCH` tag to the authorized repository to start the Windows release workflow.

Settings and diagnostic logs live under `%LOCALAPPDATA%\QuietSnow`. Use test settings and redact personal paths, process names and other identifiers before sharing logs, benchmark output or screenshots.
