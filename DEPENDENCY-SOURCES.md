# Repository-owned dependency sources

CDMW Lite is self-contained at the repository boundary. The projects below are source snapshots owned and built here; they are not links to another checkout and do not require a sibling repository.

| Source | Local owner | Provenance or pin |
| --- | --- | --- |
| Archive content classification and semantic analyzers | `src/Cdmw.Archive.Content/` and `schemas/` | Imported from the former source repository at commit `45d35c1` |
| Archive ABI, model preview, item catalog, mesh export, texture decode, and common diagnostics | `native/` | Imported from the former source repository at commit `45d35c1` |
| .NET/Vortice model renderer | `tools/dotnet_mesh_editor_experiment/` | Imported from the former source repository at commit `45d35c1`; NuGet versions are now owned by `Directory.Packages.props` |
| DirectXTex | CMake `FetchContent` in `native/cd_texture_dx/` | Commit `bf256afaed1c789ddd444fb45105ffbcab283efe`; HLSL shaders are built by this repository, see below |
| vgmstream Windows runtime | `scripts/ensure_vgmstream.ps1` into ignored `.tools/vgmstream/` | Release `r1980`, build commit `21bfb6f0a513271f2e18a51322128756bb59f365`, archive SHA-256 `110f9087e60057c4af6cff84e26c214159c224792421affdddd3aaa2091f2641` |

## DirectXTex HLSL shaders

`native/cd_texture_dx/CMakeLists.txt` builds the DirectXTex BC6H/BC7 compute shaders itself and configures DirectXTex with its supported `USE_PREBUILT_SHADERS` option, so the upstream shader step never runs.

DirectXTex invokes `CompileShaders.cmd` by bare name and relies on `WORKING_DIRECTORY` to locate it. CMake 3.31 resolves that program against `PATH` only, so a clean configure fails with `no such file or directory`. Upstream `main` still carries the same invocation, so advancing the pin does not resolve it. This requires the Windows SDK legacy shader compiler (`FXC.EXE`); set `CDMW_FXC_TOOL` if it is installed outside the default Windows Kits location.

Future changes to the imported source components are made and reviewed in this repository. There is no automatic synchronization with their former copies. `scripts/verify_repository_independence.ps1` rejects project references or legacy layout references that escape this repository.
