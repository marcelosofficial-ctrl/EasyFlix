# Catalog metadata and artwork enrichment

EasyFlix keeps filesystem discovery and the private SQLite catalog authoritative. Provider
metadata is supplemental and best-effort.

Runtime behavior:

- movies use TMDB only when the existing ReelShelf-compatible TMDB secret or `TMDB_API_KEY`
  environment variable is available;
- TV and anime use TVmaze;
- low-confidence matches below 0.60 are ignored;
- existing non-empty title descriptions, years, and artwork paths are preserved;
- provider identity and confidence are stored in `ProviderMatch`;
- poster provenance is stored in `ArtworkAsset`;
- poster files are cached under `%LOCALAPPDATA%\EasyFlix\artwork`;
- artwork downloads are capped at 10 MB and converted to PNG;
- provider, network, and image-decoding failures never block local browsing;
- validation does not launch EasyFlix or call provider networks.

Enrichment is intentionally bounded during browsing so EasyFlix does not hammer provider APIs or
consume unnecessary CPU, network, or disk resources.
