# Historical offline bootstrap

EasyFlix may retain known historical B: and F: titles from a private runtime manifest without
enumerating those drives. The real manifest stays outside Git.

Default path:

`%LOCALAPPDATA%\EasyFlix\bootstrap\historical-offline.json`

The version 1 document contains `version` and a `titles` array. Each title supplies `kind`,
`displayTitle`, optional `year`, `rootPath`, `path`, and `quality`.

Safety rules:

- only B: and F: roots are accepted;
- each path must remain inside its declared root;
- EasyFlix hard-protected paths are rejected;
- importing does not enumerate or modify media;
- imported entries are Offline;
- an already-online matching file is never downgraded;
- private catalog data is never committed.
