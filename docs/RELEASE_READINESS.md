# EasyFlix 1.4 release readiness

Current application version: **1.4.0**.

## Automated local gate

Before final release:

1. Restore the solution.
2. Build Release.
3. Run the full local test suite.
4. Confirm the Git worktree is clean.

Automated validation intentionally does not launch EasyFlix or mpv and does not enumerate private
media.

## Verified local candidate evidence

- Artifact source HEAD: `921b5b6dcff4c5a6331a31124ef709f890372fe4`
- Automated tests: **219/219 PASS**
- Canonical `scripts/build-installer.ps1 -Version 1.4.0`: **PASS**
- Portable ProductVersion: **1.4.0**
- Installer ProductVersion: **1.4.0**
- Isolated installer/uninstaller validation: **PASS**
- `EasyFlix-v1.4.0-win-x64.exe`: 69,075,832 bytes; SHA-256 `5978a4645a065513b21f3749d97b20ec5dee435b04c12133d2b5d1db057b4c8c`
- `EasyFlix-Setup-1.4.0.exe`: 120,900,487 bytes; SHA-256 `4a8f8bafddf8c131e2b4149f02a4336c270d4f602890d024cee14fcfb45991f5`
- `SHA256SUMS.txt`: 187 bytes; contents match both recomputed executable hashes
- Source worktree at artifact validation: **CLEAN**
- Runtime privacy hardening: new EasyFlix settings/secrets/jobs/model paths with legacy ReelShelf compatibility: **PASS**
- Public-source strategy: sanitized final-tree snapshot only; private development history and private orchestration material excluded
- Developer-specific media-path literals removed from public-candidate source/tests/docs: **PASS**
- Hard exclusions configured from per-user `ExcludedPaths` rather than shipped machine-specific constants: **PASS**

## Manual acceptance

Before final 1.4, verify a small user-selected real-media sample:

- first-run setup and reopening Setup and readiness;
- library browsing and search;
- exact title or episode selection;
- audio and subtitle selection through configured mpv;
- exact resume from Continue Watching;
- natural completion behavior;
- episodic Up Next;
- Watch History filtering and progress clearing;
- offline or removable-media behavior.

## Release boundary

A passing development checkpoint does not by itself authorize distribution. Packaging, repository
synchronization, installer or signing work, and public release remain separate deliberate steps.
