# cdmw-preview-core

Native preview/package, archive-name-index, and mesh rebuild service for CDMW.
It decodes archive entries, resolves authoritative material/texture inputs,
prepares schema-v8 D3D11 packages, and emits deterministic reports. Native
preview jobs do not inject synthetic textures or silently enable Python
fallback.

`src/main.cpp` is only the executable adapter. Ordered protocol, archive,
geometry, material, package, report, rebuild, index, and command owners live in
`src/owners/`. CMake compiles those owners in one named unity group because the
legacy implementation has translation-unit-private types and helpers. There
are no source-level `.cpp` includes; each owner uses the shared 1,000-line
default ceiling, and each real function stays at or below 150 lines.

## Build

```powershell
cmake -S native/cdmw_preview_core -B native/cdmw_preview_core/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/cdmw_preview_core/build --config Release
```

## Commands

```powershell
cdmw-preview-core.exe self-test
cdmw-preview-core.exe preview-job job.json report.json
cdmw-preview-core.exe --service
cdmw-preview-core.exe mesh-audit-job input.bin report.json [filename]
cdmw-preview-core.exe mesh-parse-job input.bin report.json [filename]
cdmw-preview-core.exe mesh-rebuild-job job.json output.bin report.json
cdmw-preview-core.exe name-index-job input.tsv output.bin report.json [progress.json]
```

`preview-job` reads a Python-written job file and writes a JSON report. On
supported entries it returns `status=ok` and a package path. Unsupported or
unsafe inputs produce an explicit error/fallback reason; callers decide how to
surface that result.

For a `.pac` the package also carries the rig, which `src/owners/skeleton_pab.cpp`
resolves. That owner has no counterpart in the full CDMW workbench, where the
same reading is done in Python; it is the one file in this component that
Archive Lite does not share. It decodes the six skin influences a PAC vertex
record holds, parses the `.pab` skeleton at its fixed layout, and recovers the
file's bone palette by keeping the candidate table whose hashes all resolve
against a skeleton the mesh's own path nominates. The manifest states which of
`rigged`, `rigid`, `palette_unresolved` or `not_skinned` it found and why, so a
caller can tell a mesh that names no bone from one whose palette would not
resolve. Skin rows are written per batch in the same order as the export
geometry beside them, because the exported vertex array is not the parser's. Full-CDMW archive-v2 callers also send an authoritative,
bounded `archive_dependency_entries` snapshot. The native core resolves
cross-PAMT basenames and paths from that snapshot and reads its prepared files;
legacy callers retain the Archive Lite basename-index and package-scan fallback.
