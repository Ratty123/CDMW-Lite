# Repository-owned dependency sources

CDMW Lite is self-contained at the repository boundary. The projects below are source snapshots owned and built here; they are not links to another checkout and do not require a sibling repository.

| Source | Local owner | Provenance or pin |
| --- | --- | --- |
| Archive content classification and semantic analyzers | `src/Cdmw.Archive.Content/` and `schemas/` | Imported from the former source repository at commit `45d35c1` |
| Archive ABI, model preview, item catalog, mesh export, texture decode, and common diagnostics | `native/` | Imported from the former source repository at commit `45d35c1` |
| .NET/Vortice model renderer | `tools/dotnet_mesh_editor_experiment/` | Imported from the former source repository at commit `45d35c1`; NuGet versions are now owned by `Directory.Packages.props` |
| DirectXTex | CMake `FetchContent` in `native/cd_texture_dx/` | Commit `bf256afaed1c789ddd444fb45105ffbcab283efe` |
| vgmstream Windows runtime | `scripts/ensure_vgmstream.ps1` into ignored `.tools/vgmstream/` | Release `r1980`, build commit `21bfb6f0a513271f2e18a51322128756bb59f365`, archive SHA-256 `110f9087e60057c4af6cff84e26c214159c224792421affdddd3aaa2091f2641` |

Future changes to the imported source components are made and reviewed in this repository. There is no automatic synchronization with their former copies. `scripts/verify_repository_independence.ps1` rejects project references or legacy layout references that escape this repository.
