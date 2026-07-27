# Contributing

CDMW Archive Lite is a private Windows application with managed and native components. Keep changes focused, preserve its read-only archive boundary, and avoid dependencies on sibling checkouts or local game installations.

## Development setup

Install the requirements listed in [README.md](README.md), then run:

```powershell
.\scripts\test_archive_lite.ps1 -Configuration Debug
```

The focused gate verifies repository independence, managed behavior, and the synthetic native paths used in normal development.

## Change guidelines

- Put behavior in the smallest owning project and keep the WPF shell thin.
- Keep long-running work cancellable and preserve worker/process isolation.
- Add or update regression coverage for behavior changes.
- Do not commit build outputs, caches, logs, crash reports, extracted archives, DDS payloads, local game data, or downloaded tools.
- Update the nearest document when commands, dependencies, architecture, packaging, or user-visible behavior changes.
- Use concise commits that describe one coherent change.

## Pull requests

Describe what changed, why it changed, and how it was verified. Call out any validation that requires a visible application, licensed game data, or a Release package rather than presenting synthetic checks as visual proof.

Before requesting review, inspect the scoped diff and confirm that only intended files are included.

Merge with squash only; the branch is deleted on merge. Branch rulesets are unavailable on this repository's plan, so the Archive Lite workflow is not enforced as a required check — confirm it is green on the pull request before merging, and do not force-push or delete `main`.
