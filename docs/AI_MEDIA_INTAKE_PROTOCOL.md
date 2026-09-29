# EasyFlix AI Media Intake Protocol

Use this document when helping the user add newly acquired legal local video files to EasyFlix.

This protocol is deliberately usable by an ordinary ChatGPT conversation. Codex, Work mode, Dev Relay, repository access, and code execution are not prerequisites for routine media intake.

## First principle

Do not treat routine media intake as software development.

The EasyFlix repository contains the application. The user's video files remain in the user's own media folders/drives.

For ordinary intake, guide the user through filesystem placement, naming, Refresh, optional explicit metadata sync, and visual verification.

## Required reading

Read `docs/ADDING_MEDIA.md` before giving intake instructions.

If development work becomes necessary because of a generic scanner defect, also respect the repository's existing release/design/continuity documents. Do not roll back or redesign an accepted product merely to add media.

## AI decision tree

If the new media will be placed under an existing configured root:

- recommend a clear generic file/folder name;
- have the user copy/move the new media into that root;
- have the user run EasyFlix Refresh;
- verify category/grouping/details;
- use explicit Sync Metadata only if metadata/artwork refresh is wanted.

If the media is outside all configured roots:

- have the user add the intended media folder as a new EasyFlix library root;
- avoid overly broad roots when a media subfolder is the intended scope;
- preserve exclusions;
- Refresh and verify representative titles.

If one item is misidentified:

- prefer improving the new item's generic name first;
- do not modify the database by hand;
- do not hardcode the title in production code.

If multiple normally named items expose the same problem:

- classify it as a generic scanner/parser defect;
- only then move into software-development work;
- require a regression test and a generic fix.

## Naming recommendations

Movie:

`Movie Title (2025)\Movie Title (2025).mkv`

TV:

`Series Title\Season 01\Series Title - S01E01 - Episode Title.mkv`

Anime:

`Series Title\Season 01\Series Title - S01E01.mkv`

Special / OVA:

`Series Title\Season 00\Series Title - S00E01 - OVA.mkv`

These are recommendations, not a demand to rename the user's entire existing collection.

## Supported handoff data and autonomous intake

The downloadable **EasyFlix Media Intake AI Handoff** is the authoritative contract for AI-assisted batch intake.

Structured fields EasyFlix actually applies are Title, Kind (Movie / TV / Anime), Year, Description, media path(s), optional Artwork path, optional Shortcut path(s), and optional Provider / Provider ID / Provider URL.

`Files`, `Audio`, `Subtitles`, and `Coverage` are informational notes preserved in the original handoff, not separate structured metadata fields. Do not invent unsupported fields such as genre, runtime, rating, original title, cast, or release date.

Verify exact title/release identity, accurate non-fabricated descriptions, duplicate-safe/coalesced grouping, required paths, supplied artwork, and complete batch coverage. Use the shared centered `UniformToFill` rule. Prefer clean portrait artwork close to 2:3; choose better artwork rather than title-specific crop hacks.

An AI with safe local EasyFlix/process/file access should complete the trusted import and final verification automatically when the environment permits. An AI without local machine access must return one complete import-ready handoff and must not claim local import occurred. Success may be reported only when every requested entry passes final verification with zero unresolved items.

## Preserve these EasyFlix behaviors

New media should flow through the existing generic system and inherit:

- canonical series identity;
- conservative metadata matching;
- local-first startup/Refresh;
- explicit provider metadata sync;
- canonical centered `UniformToFill` 190x286 poster presentation with center-anchored cropping and no per-title offsets;
- playback progress and Continue Watching;
- Watch History / Up Next where applicable;
- removable/offline semantics;
- existing exclusions;
- generic categorization rather than title-specific hacks.

## Things an AI must not do during routine intake

Do not instruct the user to:

- edit SQLite manually;
- put media in the source repository;
- rebuild EasyFlix;
- run Git commands;
- install developer tooling;
- use Codex merely to add a movie/show;
- disable exclusions;
- bulk rename or reorganize the existing library without explicit approval;
- silently run network/provider sync;
- delete an offline catalog entry merely because a drive is absent.

## Native automation CLI

A local AI, script, or automation tool should prefer the native EasyFlix entrypoint when it can safely access the machine:

`EasyFlix.exe --import-handoff "C:\path\batch.txt"`

The CLI and Add Media Center share the same trusted intake execution path. The required sequence is validate/preview -> backup -> local Refresh -> import/apply -> second local Refresh -> verify every requested entry -> write the final report. Metadata-provider sync is not part of this command.

Stable exit codes are:

- `0`: success.
- `2`: input or validation failure.
- `3`: backup, Refresh, import, or apply failure.
- `4`: final verification failure.
- `5`: unexpected/internal failure.

The final machine-readable output block contains `RESULT`, `STAGE`, `IMPORTED`, `UPDATED`, `UNRESOLVED`, `REPORT`, and `EXIT_CODE`. Automation must not report success unless `RESULT=PASS`, `EXIT_CODE=0`, and `UNRESOLVED=0`. The `REPORT` path is the durable execution record.

EasyFlix also supports the dedicated .easyflixhandoff extension. A single .easyflixhandoff path passed directly to EasyFlix.exe is treated as the same trusted native import flow, and Add Media Center can explicitly enable or disable a per-user Windows association for that dedicated extension.

Never associate EasyFlix with every .txt file. .txt and .json remain supported through Import Media Handoff and --import-handoff, but only .easyflixhandoff receives optional double-click integration.
## Minimal prompt for a future chat

Send this together with the current downloadable EasyFlix Media Intake AI Handoff and the media/list/files to add:

> Add all media I provided to EasyFlix. Use the attached EasyFlix Media Intake AI Handoff as the authoritative contract. Handle the batch autonomously, verify every item, and if you have local EasyFlix access complete import and verification; otherwise return one complete import-ready .txt. Do not ask me to manually format or edit metadata, database records, or app files.

The downloadable handoff contains the technical contract. A non-local AI should return the complete import-ready handoff instead of asking the user to reconstruct intake syntax or edit application data manually.

## Escalation rule

Only escalate from media maintenance to development when the existing generic scanner cannot correctly represent normally named files.

When escalation is necessary, preserve the accepted EasyFlix design and runtime behavior. Fix the generic rule, add regression coverage, and avoid per-title exceptions.
