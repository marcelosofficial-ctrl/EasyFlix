# EasyFlix playback launcher contract

EasyFlix must not claim to apply an audio or subtitle choice unless the active player adapter can
map the catalog/probe metadata to that player's real command-line contract.

The first launcher implementation is the Windows default-player adapter.

It can:

- launch an available media file through the Windows shell;
- report launch failure without crashing title details.

It intentionally cannot:

- force a selected audio stream;
- force a selected embedded or external subtitle track;
- report playback progress.

The launcher contract carries the selected ffprobe audio stream index and the selected
`CatalogSubtitleStreamInfo` so a future player-specific adapter can make an explicit capability
decision. A future adapter must set its audio/subtitle capability flags to true only after its
mapping is tested against that player's documented behavior.

Until then, the title-details UI labels inspected track selections as informational. Continue
Watching remains deferred because the current launcher has no trustworthy progress source.
