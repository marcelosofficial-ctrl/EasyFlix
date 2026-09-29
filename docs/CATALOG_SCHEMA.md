# EasyFlix local catalog schema

R2 schema version: 1

The SQLite file lives under `%LOCALAPPDATA%\EasyFlix\catalog\easyflix.db` and is a rebuildable local catalog.

## Core tables

- `LibraryRoot`: configured root and online/offline state.
- `MediaTitle`: normalized Movie, TV Show, or Anime identity and display metadata.
- `Season`: numbered, Specials, or Unassigned season buckets.
- `Episode`: episode identity within a season.
- `MediaFile`: physical file path, root, availability state, quality, size, and timestamps.
- `AudioTrack`: cached ffprobe audio stream evidence.
- `SubtitleTrack`: cached subtitle evidence. Future migrations can expand provenance beyond the R2 `detected` value.
- `ArtworkAsset`: provider/local artwork cache records.
- `ProviderMatch`: stable provider identity and confidence/manual status.
- `ScanRun`: reconciliation history.
- `PathHistory`: future move/relink history.
- `PlaybackProgress`: reserved for trustworthy Continue Watching state.
- `UserOverride`: local corrections that must win over provider refreshes.

## Availability values

`MediaFile.Availability` uses:

- `0` Online
- `1` Offline root
- `2` Missing from an online root

The reconciler never interprets a disconnected root as mass deletion.

## Identity

R2 uses deterministic SHA-256-derived internal IDs for titles, seasons, episodes, and paths. Title identity is independent from the physical path so multiple roots/seasons can coalesce into one logical title.

Provider IDs will become stronger identity evidence as metadata enrichment is expanded. User corrections remain authoritative over provider suggestions.
