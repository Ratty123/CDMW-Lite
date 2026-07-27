# Changelog

Notable changes to CDMW Archive Lite are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and package versions follow semantic versioning.

## [Unreleased]

### Added

- Initial private GitHub repository publication and collaboration documents.
- A distinct three-cell CDMW family icon for Lite windows, portable builds, and the standalone launcher.

### Changed

- Reduced large real-archive cache build and refresh time by avoiding repeated path allocations during native index sorting.
- Item Finder icon pages now decode through a single shared texture-helper invocation per page instead of one helper process per icon.
- Item Finder background icon warm-up now decodes in chunks through the same shared invocation, while still yielding to visible icon work between chunks.
- Texture decode timeouts now scale with the compression family and output size of the batch instead of using one fixed allowance, and a long decode reports progress.
- Cached texture previews now record the decoding helper build, so rebuilding the helper retires the previews it produced.
- The preview cache is now pruned periodically rather than only at worker startup, and eviction skips entries a reader is holding or was just handed.

### Fixed

- A texture batch in which one job fails is no longer discarded in full; the decode report is now authoritative and each request reports its own outcome.
- Truncated, checksum-corrupt, and over-long cached texture previews are no longer served as warm cache hits.
- A DDS that cannot decode inside the preview resource limits is now rejected from its header instead of starting a helper process that would fail or exhaust memory.
- Texture decode failures are recorded in a bounded diagnostic history instead of being discarded with the exception.
- Preview decode gates are released from their registry once idle, instead of retaining one lock per cache key for the life of the process.

## [0.6.1] - 2026-07-23

### Added

- Standalone Python-free Windows x64 application and portable packaging.
- Read-only archive browsing, text search, Item Finder, associated-asset discovery, and export.
- Text, XML, DDS, media, archive-metadata, and model preview paths.
- Persistent archive indexes, bounded caches, cancellable worker operations, and isolated native rendering.
- Focused Debug validation and verified Release packaging scripts.

### Changed

- Improved first-run layout, preview framing, texture orientation, Item Finder selection, and preview-pane resize behavior.
