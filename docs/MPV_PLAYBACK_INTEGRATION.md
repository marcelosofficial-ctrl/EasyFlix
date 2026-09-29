# mpv playback integration

EasyFlix uses mpv only when a valid mpv.exe is configured in Playback settings.

Playback settings can auto-detect mpv from PATH and common WinGet, Scoop, Chocolatey, and Program Files locations. A portable or custom installation can always be selected with Browse. Invalid executable paths are not saved.

The adapter launches mpv paused with a unique Windows named-pipe JSON IPC endpoint. It does not
parse terminal output. After mpv exposes its runtime track list, EasyFlix maps an ffprobe audio or
embedded-subtitle stream only when exactly one mpv track of the expected type has the same
`ff-index`. External subtitles are loaded explicitly and matched by `external-filename`.

If a requested track cannot be mapped uniquely, EasyFlix leaves that player selection unchanged
and reports that it was not applied. Subtitle Off is applied explicitly with `sid=no`.

EasyFlix can resume an eligible Continue Watching item by setting `time-pos` while mpv is paused,
reading `time-pos` back to verify the seek, and only then resuming playback. Windows default
playback is still available as a fallback but never claims verified track selection, resume seeking,
or progress tracking.

During playback, EasyFlix observes `time-pos` through the same IPC session and persists progress to
the catalog. A natural mpv `end-file` event with reason `eof` and known duration is persisted as
completed playback so the item no longer qualifies for Continue Watching. Manual stops and other
end reasons preserve the last trustworthy position instead of being marked complete.
