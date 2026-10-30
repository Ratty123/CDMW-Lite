# CDMW Archive Core

`cdmw-archive-core.dll` is the read-only native archive backend for CDMW Archive
Lite. It scans PAMT metadata into the versioned `archive_index_v1` binary format
and decodes selected PAZ entry bytes through a narrow C ABI.

The ABI has no archive mutation, replacement, patch, or restore functions. The
source archive tree is opened read-only. Index publication is staged beside the
cache destination and then replaced atomically.

Both legacy and October 2026 compact PAMT layouts are supported. Compact names
use backward parent distances and a separate extension table; the upper flag
byte selects an extension, while only bits 4–7 select encryption. PAZ identifiers
come from the directory and need not be consecutive. Invalid name, extension or
PAZ references are rejected. Partial PAM (including version `0x01001806`) and
PAMLOD geometry blocks decode through the repository-owned shared static-mesh
reader, with size and offset checks.

Callers that need user-facing progress can use
`cdmw_archive_build_index_with_progress_utf8`. Its callback reports discovery,
PAMT parsing, entry sorting, index writing, and atomic publication. Phases with
known work expose exact completed/total counts; returning non-zero requests
cooperative cancellation and prevents publication of an incomplete index. The
original `cdmw_archive_build_index_utf8` entry point remains available for ABI
compatibility.

Build and test:

```powershell
cmake -S native/cdmw_archive_core -B native/cdmw_archive_core/build
cmake --build native/cdmw_archive_core/build --config Release
ctest --test-dir native/cdmw_archive_core/build -C Release --output-on-failure
```
