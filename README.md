# CDMW Archive Lite

CDMW Archive Lite is a standalone, read-only Windows desktop application for browsing Crimson Desert PAMT/PAZ archives. It can search, inspect, preview, and export archive content without modifying the source archives.

The application is self-contained in this repository and keeps archive queries, decoding, media conversion, and mesh rendering outside the UI process.

## Highlights

- Browse large archives with flat, folder, and category views, paging, filters, sorting, and persistent indexes.
- Search text across archives or loose folders with cancellation, result limits, and in-file navigation.
- Find known items by localized name and follow exact or related archive links.
- Preview text, XML, images, DDS textures, supported media, archive metadata, and read-only PAC/PAM/PAMLOD/PAT geometry.
- Discover and export associated assets without altering game data.
- Run as a portable Windows application or a single self-extracting executable.
- Keep settings, caches, logs, and crash reports isolated from the full CDMW workbench.

## Safety boundary

Archive Lite has no archive-write, replacement, patching, backup, restore, or mod-building authority. PAMT, PAZ, and PATHC sources are opened read-only, and exports are written to a separate destination selected by the user.

The repository does not contain game archives, extracted game assets, or other local game data.

## Requirements

- Windows 11 x64
- .NET 10 SDK
- CMake 3.24 or newer
- Visual Studio 2022 Build Tools with the Desktop C++ workload
- PowerShell

Pinned third-party sources are documented in [DEPENDENCY-SOURCES.md](DEPENDENCY-SOURCES.md).

## Run from source

First run the focused nonvisual gate:

```powershell
.\scripts\test_archive_lite.ps1 -Configuration Debug
```

Then launch the desktop application:

```powershell
dotnet run --project .\src\Cdmw.ArchiveLite.App\Cdmw.ArchiveLite.App.csproj -c Debug
```

The launch command opens a visible desktop window and is intentionally separate from automated validation.

## Build

For a fresh standalone executable, run:

```text
BUILD-FRESH-EXE.bat
```

For the complete verified Release package:

```powershell
.\scripts\build_archive_lite.ps1
```

Build outputs are written beneath the ignored `artifacts/` directory. On a clean checkout, pinned native dependencies are downloaded into the ignored `.tools/` directory.

## Architecture

```text
CdmwArchiveLite.exe                  WPF presentation and selection state
        |
        | private named pipe, request IDs, cancellation
        v
CdmwArchiveLite.Worker.exe           archive queries, search, preview, export
        |
        +-- cdmw-archive-core.dll     read-only PAMT/PAZ/PATHC decoding
        +-- native helper processes   indexing, DDS, model and media preparation
        +-- managed content library   semantic archive documents

CdmwArchiveLite.exe
        |
        +-- cdmw-mesh-dotnet-editor.exe
            read-only Vortice D3D11 mesh viewport
```

The UI owns presentation. Long-running work remains cancellable in the worker or a dedicated native process, and the renderer runs in its own child process.

## Repository layout

- `src/` — WPF application, worker, shared contracts, and managed archive services
- `native/` — read-only archive and preview implementations
- `tools/` — repository-owned helper and renderer projects
- `tests/` — managed regression coverage and synthetic fixtures
- `scripts/` — focused validation and packaging entry points
- `schemas/` — versioned repository-owned data contracts
- `.github/` — GitHub Actions, dependency update configuration, and collaboration templates

## Local data

Portable builds keep settings, caches, logs, and crash reports beside the executable. The single-file launcher extracts its verified runtime beneath `%LocalAppData%\CDMW\CDMWArchiveLite\standalone\`; it does not place user settings, game data, or exports there.

These paths and all build outputs are excluded from version control.

## Project documents

- [TESTING.md](TESTING.md) - validation matrix and proof boundaries
- [PREVIEW-MATERIAL-AUDIT.md](PREVIEW-MATERIAL-AUDIT.md) - how a preview is checked against its source, what is confirmed authored, and which explanations were refuted
- [CONTRIBUTING.md](CONTRIBUTING.md) - development and review expectations
- [SECURITY.md](SECURITY.md) - supported versions and private reporting
- [CHANGELOG.md](CHANGELOG.md) - notable version changes
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) - bundled component notices
