# NOX TWEAK

Safe, reversible Windows optimizer for gaming (C# / .NET 8 / WPF). UI in English.

## Layout
- `src/NoxTweak.Core` – catalog, transactional engine (backup → apply → verify → rollback), game detection/launch, Game Mode, recommendations.
- `src/NoxTweak.UI` – view-models (no WPF dependency; tested on any OS).
- `src/NoxTweak.App` – WPF shell and views (Windows only).
- `src/NoxTweak.SelfTest` – dependency-free test runner (58 tests).

## Build the Windows EXE
Push to GitHub: `.github/workflows/build.yml` builds on windows-latest, runs the self-tests and uploads `NoxTweak.exe` (self-contained win-x64) as an artifact.
Locally on Windows: `dotnet publish src/NoxTweak.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.

## Status
The WPF views have not been compiled yet (authored on Linux). Expect to fix small XAML issues on the first Windows build.
