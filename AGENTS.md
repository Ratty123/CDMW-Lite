# CDMW Lite repository instructions

- This repository owns the standalone, Python-free, read-only CDMW Lite application.
- Keep every source, project reference, build helper, test, and runtime dependency contained in this repository. Do not add paths back to the former monorepo or another sibling checkout.
- Keep UI shells thin, long-running work cancellable, and archive access read-only.
- Preserve the worker/process boundaries used for archive queries, native previews, texture decoding, media decoding, and mesh rendering.
- Add or update managed regression coverage with behavior changes.
- Use `scripts/test_archive_lite.ps1 -Configuration Debug` as the focused nonvisual gate.
- Use `scripts/build_archive_lite.ps1` only for an explicitly authorized Release/package validation.
- Do not commit `artifacts/`, `.tools/`, native build trees, .NET output, caches, logs, crash reports, extracted archives, DDS payloads, or local game data.
- Preserve unrelated work. Stage explicit task-owned paths and commit coherent verified changes locally; never push unless explicitly requested.
