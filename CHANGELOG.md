# Changelog

Notable changes to CDMW Archive Lite are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and package versions follow semantic versioning.

## [Unreleased]

### Changed

- The standalone launcher now extracts its verified runtime beneath `%LocalAppData%\CDMW\CDMWArchiveLite\standalone\` instead of a vendor folder named after the author. An existing install re-extracts once on the next launch; the runtime left under the previous path is no longer read and can be deleted.

### Fixed

- An embedded model preview renderer now closes when it reaches the end of its host's standard input, which is what happens when the hosting application ends. The renderer's kill-on-close job object is armed just after launch, so a host that ended inside that window previously left a renderer process running with no host to display it.
- The standalone launcher now places the application it starts in a kill-on-close job object, so force-closing the launcher stops the whole Archive Lite tree instead of leaving it running with no launcher attached.
- A worker that no client ever connects to now stops after thirty seconds instead of waiting for a connection forever. The client arms its own kill-on-close job just after launch, so a client that dies during that window previously left a worker running with no pipe to notice and no window to close.

## [0.7.0] - 2026-07-27

### Added

- A preview background colour choice under Preview Settings, applied to both texture and model previews. Presets cover the neutral range plus magenta for reading transparent pixels, and a custom hexadecimal colour is accepted. Preview Settings is now reachable from a texture selection, not only a model one.
- Initial private GitHub repository publication and collaboration documents.
- A distinct three-cell CDMW family icon for Lite windows, portable builds, and the standalone launcher.

### Changed

- Reduced large real-archive cache build and refresh time by avoiding repeated path allocations during native index sorting.
- Item Finder icon pages now decode through a single shared texture-helper invocation per page instead of one helper process per icon.
- Item Finder background icon warm-up now decodes in chunks through the same shared invocation, while still yielding to visible icon work between chunks.
- Texture decode timeouts now scale with the compression family and output size of the batch instead of using one fixed allowance.
- A DDS preview that takes a while to decode now reports elapsed and allowed seconds in the preview pane instead of showing an unchanging busy state.
- Cached texture previews now record the decoding helper build, so rebuilding the helper retires the previews it produced.
- The preview cache is now pruned periodically rather than only at worker startup, and eviction skips entries a reader is holding or was just handed.
- The standalone launcher now removes runtimes it supersedes, keeping the running one and the two most recently used. Every build previously extracted its own copy and none were ever collected.
- A settings file now records which shipped catalog-column defaults it was written against, so a changed default set reaches existing portable installs once instead of only first runs. This release changes that set, so saved column choices are re-seeded a single time.

### Fixed

- DDS previews and Item Finder icons no longer green-invert normal-map rows. Rows classified as normal maps were decoded through the texture helper's `normal` slot, which flips the green channel, so the preview showed a tangent-space convention the file does not contain and diverged from Full's archive browser. Every row now decodes as `base` and reports the channels it stores. Previews cached by the previous behaviour are retired, so the first view of each normal map after upgrading is a fresh decode.
- A texture batch in which one job fails is no longer discarded in full; the decode report is now authoritative and each request reports its own outcome.
- Truncated, checksum-corrupt, and over-long cached texture previews are no longer served as warm cache hits.
- A DDS that cannot decode inside the preview resource limits is now rejected from its header instead of starting a helper process that would fail or exhaust memory.
- Texture decode failures are recorded in a bounded diagnostic history instead of being discarded with the exception, and are now written to the portable log with their reason and archive source. All worker diagnostics now reach that log rather than only a buffer kept for crash reports.
- Preview decode gates are released from their registry once idle, instead of retaining one lock per cache key for the life of the process.
- The release build no longer fails at its final NativeAOT step when started by double-clicking `BUILD-FRESH-EXE.bat`. The linker was located by running `vswhere.exe` from `PATH`, which Explorer does not supply; it is now found at the fixed path the Visual Studio Installer owns, and a missing toolchain fails immediately with an actionable message instead of an MSBuild exit code minutes in.
- A clean configure of the texture helper no longer fails resolving DirectXTex's shader compile script. The shaders are built here through an explicit script path and handed to DirectXTex as prebuilt, so its own step is never registered.

## [0.6.1] - 2026-07-23

### Added

- Standalone Python-free Windows x64 application and portable packaging.
- Read-only archive browsing, text search, Item Finder, associated-asset discovery, and export.
- Text, XML, DDS, media, archive-metadata, and model preview paths.
- Persistent archive indexes, bounded caches, cancellable worker operations, and isolated native rendering.
- Focused Debug validation and verified Release packaging scripts.

### Changed

- Improved first-run layout, preview framing, texture orientation, Item Finder selection, and preview-pane resize behavior.
