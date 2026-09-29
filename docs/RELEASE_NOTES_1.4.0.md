# EasyFlix 1.4.0 Release Notes

Status: **locally validated release candidate; not publicly released**.

EasyFlix 1.4.0 is a usability and workflow-hardening release for the local-first Windows media library.

## 1.4 changes

- Added a recoverable empty state for searches with no matches, including Clear search.
- Reconciles Browse state after returning from Setup and Library Maintenance.
- Keeps personalized browse rows synchronized after relevant history/title interactions.
- Supports durable title-level Favorites and manual watched/unwatched state.
- Allows Mark unwatched for selected offline/removable media because clearing saved progress does not require the file.
- Gives actionable feedback when Open file location targets unavailable media.
- Disables Play and Play from beginning when selected media is unavailable using the existing CanPlay authority.
- Preserves offline/removable titles in the catalog.
- Retains the trusted Add Media / --import-handoff pipeline and dedicated .easyflixhandoff integration.
- Moves new per-user settings, encrypted secrets, subtitle-job state, local Whisper model/temp state, and subtitle-provider User-Agent branding to EasyFlix naming while preserving legacy ReelShelf compatibility.
- Defines future public-source publication as a sanitized final-tree snapshot rather than publishing private development history.
- Removes developer-specific media-folder literals from distributable source/tests/docs and makes protected folders a per-user settings concern.

## Verified local candidate

- Artifact source HEAD: `921b5b6dcff4c5a6331a31124ef709f890372fe4`
- ProductVersion: `1.4.0`
- Automated tests: **219/219 PASS**
- Canonical installer/package build: **PASS**
- Isolated install/uninstall validation: **PASS**
- Source worktree after artifact validation: **CLEAN**

| Asset | Size | SHA-256 |
| --- | ---: | --- |
| `EasyFlix-Setup-1.4.0.exe` | 120,900,487 bytes | `4a8f8bafddf8c131e2b4149f02a4336c270d4f602890d024cee14fcfb45991f5` |
| `EasyFlix-v1.4.0-win-x64.exe` | 69,075,832 bytes | `5978a4645a065513b21f3749d97b20ec5dee435b04c12133d2b5d1db057b4c8c` |
| `SHA256SUMS.txt` | 187 bytes | verifies both executables |

## Release boundary

No product push, hosted GitHub Actions, tag, GitHub Release, or public publication was performed. No open-source license has been selected; publication and reuse terms remain separate deliberate decisions.
