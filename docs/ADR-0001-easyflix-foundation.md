# ADR 0001: EasyFlix 1.0 foundation

Status: Accepted for the 1.0 migration foundation.

## Decision

EasyFlix remains a .NET 10 Windows desktop application using WPF for the first 1.0 release.
The proven ReelShelf scanner, parser, provider, subtitle-safety, shortcut, and local-Whisper
logic is migrated rather than rewritten.

The first migration checkpoint intentionally preserves the existing administrative/setup UI.
The Netflix-like browsing shell will be layered on top of a normalized local catalog after
the inherited safety behavior is compiling and tested in the new repository.

## Boundaries

- The media filesystem remains the source of truth.
- Original video files are read-only by default.
- The two protected F: paths are enforced in code, not only by editable settings.
- Offline/removable roots must not be interpreted as mass deletion.
- Existing ReelShelf local settings, DPAPI secrets, model cache, and job history remain readable
  during migration. Their literal legacy storage names are intentionally not renamed yet.
- Provider credentials must never enter source control, test output, or diagnostics.
- No acquisition/torrent functionality belongs in EasyFlix.
- No hosted CI, push, release, or publication is part of this checkpoint.

## Next architecture slice

The next checkpoint adds the rebuildable normalized catalog and browsing shell:

`LibraryRoot -> MediaTitle -> Season -> Episode -> MediaFile -> AudioTrack / SubtitleTrack`

SQLite is the selected local catalog candidate. FileSystemWatcher is only an incremental hint;
debounced reconciliation remains authoritative. Cached posters/backdrops are rebuildable.
