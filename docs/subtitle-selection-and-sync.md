# Subtitle selection and automatic synchronization

The goal is one K press to get a good subtitle track, with timing corrected automatically when there is enough evidence. This belongs in the existing JitenMPV fork: Lua keeps the key bindings and the .NET companion owns discovery, ranking, caching and asynchronous sync jobs.

## Implemented selection changes

- K and Alt+K traverse one flattened list across plausible Jimaku show matches and the file's existing Japanese text tracks. Numbering is global, including wraparound and reverse traversal. Archives are expanded and filtered before the total is shown.
- Selecting a show in Ctrl+E scopes downloaded candidates to that show's exact title/aliases and movie/anime metadata. It does not fall back to another show when that choice has no download. Existing embedded tracks remain available because they belong to the playing file.
- Ctrl+E includes subtitle downloads for the selected show, optional explicit file selection, and a remembered style preference: clean dialogue, verbatim/closed captions, or any style.
- Ranking uses actual downloaded text as well as filenames. Clean dialogue penalizes speaker-label and reading-aid density and CC/SDH labels. Verbatim reverses the annotation preference. Matching release group and WEB/Blu-ray source improve ranking. Labelled Chinese/bilingual tracks retain their existing strong penalty.
- Content is inspected before committing the cycle order. Downloads and episode-specific API listings are cached; the list does not reshuffle on every K press. Initial discovery costs more than later cycling because it downloads text candidates and expands archives before displaying a stable total.
- Han-only text is not, by itself, classified as Chinese. Ordinary Japanese names and dialogue can contain no kana. Content ranking does not rewrite subtitle text. The pre-existing optional Chinese-line stripping behavior remains separate.

These are heuristics, not semantic understanding: they can recognize explicit readings but cannot reliably tell whether a fictional kanji spelling matches what the character said. Also, title matching is a fallback identity mechanism; stable cross-provider IDs should replace it wherever Jimaku and Jiten both expose them.

## Proposed synchronization pipeline

1. **Prefer a validated embedded dialogue reference.** Extract timings from a full-dialogue subtitle track already in the playing container, even when its language differs. Exclude forced/signs-only/karaoke tracks. Embedded does not automatically mean correct: compare its timing against the selected audio on multiple speech-rich windows before treating it as ground truth.
2. **Otherwise use the selected audio track.** Decode mono audio with FFmpeg and detect speech activity with WebRTC VAD. Align the candidate's dialogue intervals against the speech intervals. This works across languages and needs no transcription, translation service or language model.
3. **Start with a constant offset.** Try a bounded search such as plus/minus 120 seconds, covering the reported 1, 5 and 20 second errors in both directions. Exclude long signs, songs and annotation-only events from the fitting signal. Measure several dialogue-heavy windows separated through the episode; retain absolute media timestamps when sampling.
4. **Validate before applying.** Require sufficient dialogue coverage, agreement between windows, a clear best alignment relative to competing offsets, and improvement on held-out windows. Do not silently apply a weak result. Display a brief result such as `Synced +5.20s` or `Sync uncertain; kept current timing`, with retry and undo in the menu. Thresholds must be calibrated from fixtures rather than treating an algorithm's correlation score as a probability.
5. **Escalate only when the constant-offset model fails.** Try a constrained linear time scale for gradual drift. Use an alass-based split-aware fallback for different openings, recaps or edit cuts; accept it only when the additional complexity materially improves held-out alignment. Do not bend a wrong episode into a convincing-looking match.
6. **Apply one timing transform consistently.** Constant offsets use track-specific saved offsets through MPV's `sub-delay`. Drift/splits produce a retimed cached subtitle copy. Make rendering, subtitle navigation, seeking and sentence-mining audio boundaries use the same transform. Preserve ASS timing relationships, including karaoke and timed animation, or avoid nonlinear retiming for unsupported ASS constructs. Retiming must not also apply the same offset through `sub-delay`.
7. **Keep playback responsive.** Extract and cache the speech/reference signal once per media identity and selected audio stream. Cancel work on file changes or newer selection requests. Tag results with media, audio and candidate identity; discard stale results before touching MPV. Use bounded timeouts and retain working subtitles during analysis.

## Tool choice

Start by integrating **ffsubsync as an external helper**, using its language-independent global offset/framerate approach, plus our own acceptance gate. Keep the helper behind one small interface so its results can be benchmarked or replaced without coupling the menu or Jimaku client to Python.

Keep **alass as the fallback for cuts/splits**, also as an external process. Its CLI supports constant shifts, different frame rates and discontinuities. Neither tool should overwrite downloaded originals. Both support subtitle references, avoiding audio decoding when a good reference is available.

A small neural speech detector such as Silero is an optional later experiment for music-heavy audio where WebRTC VAD struggles. Whisper/forced alignment is unnecessary for the first implementation and is a poor default for translated subtitles, where written words do not match the spoken language.

Sources: [ffsubsync algorithm and limitations](https://ffsubsync.readthedocs.io/en/latest/how_it_works.html), [reference signal options](https://ffsubsync.readthedocs.io/en/latest/reference_types.html), [alass capabilities](https://github.com/kaegi/alass), [MPV subtitle delay](https://mpv.io/manual/stable/#options-sub-delay).

## Streams and packaging

Local files are the first validation target. For seekable HTTP/debrid streams, use the existing playable URL with its required headers and timestamp origin, without logging signed URLs or authorization. Sample bounded audio windows with range/seek support instead of downloading the whole video a second time. If the provider cannot support bounded extraction, report that limitation and retain current timing.

Package the helper and its FFmpeg dependency in Nix, expose their executable paths to JitenMPV, and include the cache directory in the existing MPV sandbox binds. The current Nix package stays pinned until the fork changes have been built and a new source hash is available. Building the checkout alone does not update the installed player.

## Acceptance criteria

Use generated speech-interval fixtures plus privately held real episodes; do not commit copyrighted video or credentials. Measure both accepted alignment error and the rate of confidently wrong corrections. The target is corrections within about 200 ms on ordinary constant-offset fixtures, and rejection of wrong episodes, silence and ambiguous signals; this is a target, not a measured claim.

Cover positive and negative 1/5/20-second shifts, already-correct subtitles, a misleading opening song, sparse dialogue, forced/sign tracks, overlapping captions, 23.976/25 fps drift, recap/cut discontinuities, alternate audio tracks, manually adjusted audio/subtitle delay, and cancellation while switching files. Verify cache invalidation when media content, selected audio, subtitle content or sync settings change.

Before enabling automatic sync by default, compare ffsubsync and alass on the same fixtures, inspect false corrections, and verify live playback, subtitle navigation and sentence mining against the exact Nix-packaged build.
