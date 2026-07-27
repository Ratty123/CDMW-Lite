# Changelog

Notable changes to CDMW Archive Lite are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and package versions follow semantic versioning.

## [Unreleased]

### Added

- Initial private GitHub repository publication and collaboration documents.
- A distinct three-cell CDMW family icon for Lite windows, portable builds, and the standalone launcher.

### Changed

- Reduced large real-archive cache build and refresh time by avoiding repeated path allocations during native index sorting.
- Item Finder icon pages now decode through a single shared texture-helper invocation per page instead of one helper process per icon.

### Fixed

- A texture batch in which one job fails is no longer discarded in full; the decode report is now authoritative and each request reports its own outcome.
- Truncated, checksum-corrupt, and over-long cached texture previews are no longer served as warm cache hits.

## [0.6.1] - 2026-07-23

### Added

- Standalone Python-free Windows x64 application and portable packaging.
- Read-only archive browsing, text search, Item Finder, associated-asset discovery, and export.
- Text, XML, DDS, media, archive-metadata, and model preview paths.
- Persistent archive indexes, bounded caches, cancellable worker operations, and isolated native rendering.
- Focused Debug validation and verified Release packaging scripts.

### Changed

- Improved first-run layout, preview framing, texture orientation, Item Finder selection, and preview-pane resize behavior.
