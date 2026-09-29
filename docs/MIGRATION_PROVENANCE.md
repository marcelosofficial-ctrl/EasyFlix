# ReelShelf migration provenance

EasyFlix was initialized as a separate repository and imports the verified working ReelShelf
source as a migration foundation.

Verified source state captured by Dev Relay R0 V5:

- ReelShelf branch: `main`
- ReelShelf HEAD: `4dad9b0a6b6ed1f5bc1a07bbb1960d08bb029f15`
- Working-tree delta preserved: `src/ReelShelf.App/MainWindow.xaml.cs` starts a safe scan after
  the main window loads.
- Frozen ReelShelf 0.6.1 ZIP SHA-256:
  `03fe092d5a5b83da42b43037255f59ee57d38728679c88c208e25b341a4fef2e`
- Frozen ReelShelf executable SHA-256:
  `cda91f3b2a7e22d9c3de7bb8b82e76d58ad27ac3f764746bddc743f6e07382d1`

The legacy repository and frozen artifacts are not modified by this migration.

The private 2026-09-25 media catalog is deliberately not committed to this repository because
it contains local absolute paths and personal library inventory. Runtime catalog bootstrapping
and migration consume user-local data outside source control.
