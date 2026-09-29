# EasyFlix 1.4 startup performance

This document records low-risk startup optimizations that preserve EasyFlix's cached-first and non-destructive behavior.

## Catalog initialization

CatalogStore initialization is idempotent per store instance. Repeated defensive callers no longer rerun the SQLite schema/PRAGMA setup after the first successful initialization.

Cancellation before initialization still cancels, and a canceled/failed first attempt does not poison a later retry.

## Library change monitor

BrowseViewModel keeps its defensive StartChangeMonitor calls, but watcher setup is idempotent by source-root set.

If the same roots are already monitored, EasyFlix keeps the existing LibraryChangeMonitor instead of disposing and rebuilding it after startup reconciliation.

If roots change, the monitor is rebuilt so removable/configured drive changes still update the watched set.

## Non-goals

These optimizations do not skip startup reconciliation, do not contact metadata providers, do not alter media files, and do not change catalog schema or classification behavior.
