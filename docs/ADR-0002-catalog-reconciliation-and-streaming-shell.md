# ADR-0002: Rebuildable SQLite catalog, reconciliation, and streaming shell

Status: Accepted for EasyFlix 1.0 R2

## Context

EasyFlix needs to feel like a private streaming service while the media filesystem remains authoritative. The library spans fixed and removable drives, including roots that may be disconnected. The 2026-09-25 planning snapshot contains 150 title entries and 2,301 file records, but that snapshot is private bootstrap evidence rather than the application's permanent database.

The inherited ReelShelf scanner already provides safety-first discovery, title normalization, series grouping, ffprobe inspection, subtitle tooling, provider integrations, and shortcut compatibility. R2 must reuse those capabilities without turning the existing maintenance grid into the primary product UI.

## Decision

1. Keep .NET 10 WPF and the migrated EasyFlix.Core foundation.
2. Add a local SQLite catalog under LocalAppData. The catalog is rebuildable and never replaces the filesystem as source of truth.
3. Store normalized roots, titles, seasons, episodes, files, audio tracks, subtitle tracks, artwork, provider matches, scan runs, path history, playback progress, and user overrides.
4. Reconcile each configured root independently.
   - Online root: scan safely, mark previously known files missing first, then mark observed files online.
   - Offline root: retain all rows and mark them offline rather than deleted.
   - Missing files remain as reviewable tombstones. R2 does not auto-delete them.
5. FileSystemWatcher is only a change hint. Events are debounced and trigger the same reconciler. Manual/full reconciliation remains authoritative.
6. Preserve each user's configured excluded paths as hard exclusions in every path that enumerates media. User-specific protected folders belong in per-user settings, never in distributable source.
7. Build a new streaming-style BrowseWindow with top navigation, a cinematic hero, horizontal media rows, search, section pages, and a title-details window.
8. Preserve the inherited setup/scanner/subtitle/shortcut interface as `LibraryMaintenanceWindow` rather than deleting it.
9. Show audio/subtitle availability before playback by lazily probing the selected movie or episode with ffprobe and caching the detected stream snapshot in SQLite.
10. Continue Watching is intentionally deferred until playback progress has a reliable source. The schema is prepared for it, but the UI must not fabricate progress.

## Privacy and bootstrap data

The private 2026-09-25 catalog JSON/CSV/handoff files are not committed. They informed the schema and UX contract, especially season/episode grouping, offline retention, and language/provenance requirements. R2 rebuilds its operational catalog from configured media roots and retains future offline state after it has observed a root.

A later checkpoint may add a one-time user-controlled import of the private planning snapshot so currently disconnected roots can be pre-populated without reconnecting first. That importer must strip or localize sensitive path data and remain outside source control.

## Consequences

- First launch may perform a larger scan before every row is available.
- Subsequent launches can render cached catalog rows immediately and reconcile in the background.
- Offline roots remain visible after they have been indexed.
- The two roots that were already offline before the first EasyFlix SQLite index is built cannot be recreated from filesystem data alone. Importing the private planning snapshot or reconnecting those drives is required to seed them.
- Missing and offline are separate catalog states.
- The streaming UI can evolve independently from maintenance tooling.
