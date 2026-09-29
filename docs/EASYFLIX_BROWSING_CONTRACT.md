# EasyFlix browsing contract

EasyFlix should feel like opening a private streaming service, not a file browser. It may use
familiar streaming interaction patterns but must not copy Netflix branding or proprietary assets.

## Home

- dark cinematic shell with minimal chrome;
- top navigation for Home, TV Shows, Movies, Anime, My Library, Search, and Settings;
- horizontal, virtualized poster/card rows;
- fast startup from the local index while refresh work continues visibly in the background;
- rows such as Recently Added and category/genre shelves;
- Continue Watching only after playback progress has a reliable source.

## Movie detail

Before playback, show title, description, year, genres, runtime, resolution/HDR when known,
availability, audio-language chips, and subtitle-language chips.

## TV/anime detail

In addition to the movie-level metadata, show season count, episode count, season selector,
episode list, specials/extras, and per-episode audio/subtitle availability when tracks differ.

## Track truthfulness

The simple UI may show language chips, but detailed metadata must preserve provenance:

- embedded selectable subtitle;
- external human/downloaded sidecar;
- generated transcription;
- machine translation;
- hardcoded/burned-in unknown unless separately verified;
- missing/not detected.

Unknown language tags remain unknown rather than being guessed.

## Identity

A shortcut or folder is not automatically one logical title. Season-split sources that share a
strong provider identity should coalesce under one title while retaining path history and file
variants. Uncertain season mappings remain Unassigned/Extras rather than being invented.
