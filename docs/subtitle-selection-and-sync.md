# Subtitle selection and automatic synchronization

HTTP/HTTPS playback in MPV is a primary requirement, including signed debrid URLs and streams without a local media file. The goal is one K press to get a good subtitle track, with timing corrected automatically when there is enough evidence. This belongs in the existing JitenMPV fork: Lua keeps the key bindings and the .NET companion owns discovery, ranking, caching and asynchronous sync jobs.

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

HTTP/HTTPS streams are the first end-to-end validation target, alongside local files. K, show selection and automatic sync must work without a local media path, including streamed movies with no episode number. A URL must not be treated as a filesystem path or rejected merely because `File.Exists` is false. The existing sentence-mining capture probe has a local-only seekability check; the sync implementation must use its own stream-capability probe rather than inheriting that exclusion.

**Prefer MPV's current demuxer cache.** Read its available timestamp ranges and obtain small, bounded excerpts using asynchronous `dump-cache` requests. Decode the selected audio from those local excerpts with FFmpeg, then run VAD and alignment on that speech signal. This reuses the authenticated media MPV has already received and avoids depending on HTTP range support or reopening an expiring URL. Cache dumps can include video packets: bound duration, estimated bytes and runtime, clean up excerpts, and benchmark playback stalls. MPV documents cache dumping as experimental; small-excerpt muxing, truncation and timestamp behavior must be tested on the installed build before relying on it.

**Use direct extraction as a capability-tested alternative.** For seekable sources that can be reopened, read MPV's effective `stream-open-filename`, selected audio source and required HTTP context, rather than assuming `path` is the final media URL. Carry only the relevant headers, cookies, user agent and referrer to the media host; never forward them to subtitle providers or logs. Handle redirected URLs, separate audio URLs, manifests and refreshed signed links explicitly. Do not download the whole video a second time.

**Non-seekable sources accumulate evidence during playback.** Fit a constant offset using cached speech windows near the current position, then validate against later windows as playback proceeds. If there is insufficient speech or cache coverage, show `Collecting speech for sync` and continue gathering bounded samples without seeking the player. Whole-episode drift and split correction need wider coverage and must remain pending until it exists. An instant correction on silence or at stream startup is not a reliable acceptance requirement.

Every excerpt needs a verified mapping from its packet timestamps back to MPV's media timeline; remuxing can rebase timestamps. Do not infer that mapping from the requested dump start alone. Account for the selected audio track, demuxer start time and audio delay, and invalidate results on track/source changes, seeks or discontinuities. Cache by stable media identity and content evidence rather than the full signed URL; URL token rotation alone should not lose a valid correction.

Sources: [MPV cache dumping and its limitations](https://mpv.io/manual/stable/#command-interface-dump-cache), [effective media URL](https://mpv.io/manual/stable/#property-list-stream-open-filename).

Package the helper and its FFmpeg dependency in Nix, expose their executable paths to JitenMPV, and include the cache directory in the existing MPV sandbox binds. The current Nix package stays pinned until the fork changes have been built and a new source hash is available. Building the checkout alone does not update the installed player.

## Acceptance criteria

Use generated speech-interval fixtures plus privately held real episodes; do not commit copyrighted video or credentials. Measure both accepted alignment error and the rate of confidently wrong corrections. The target is corrections within about 200 ms on ordinary constant-offset fixtures, and rejection of wrong episodes, silence and ambiguous signals; this is a target, not a measured claim.

Cover positive and negative 1/5/20-second shifts, already-correct subtitles, a misleading opening song, sparse dialogue, forced/sign tracks, overlapping captions, 23.976/25 fps drift, recap/cut discontinuities, alternate audio tracks, manually adjusted audio/subtitle delay, and cancellation while switching files. Verify cache invalidation when media content, selected audio, subtitle content or sync settings change.

Required stream fixtures include HTTP and HTTPS, an opaque URL with a useful MPV media title, redirects, signed URL refresh, required headers/cookies, a server without range support, HLS/DASH, separate selected audio, a streamed movie without an episode number, mid-stream startup, and empty/partial cache windows. Exercise actual playback and extraction, not only URL parsing. Metadata/IPC regression checks for K and Ctrl+E are useful but do not establish that audio-based stream synchronization works.

Before enabling automatic sync by default, compare ffsubsync and alass on the same fixtures, inspect false corrections, and verify live playback, subtitle navigation and sentence mining against the exact Nix-packaged build.
