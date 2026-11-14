using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JitenMPV.Core.Config;
using JitenMPV.Core.Sync;
using JitenMPV.Core.Api.Models;
using JitenMPV.Core.Tracking;
using JitenMPV.Core.Interaction;
using JitenMPV.Core.MediaInfo;
using JitenMPV.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace JitenMPV.Core.Jimaku;

/// One request runs at a time, and a file change cancels it.
public sealed partial class JimakuService : IDisposable
{
    public const string ActionMessage = "jiten-jimaku";
    public const string QueryMessage = "jiten-jimaku-query";
    private const string LuaScriptName = "jiten_mpv";

    /// mpv publishes the new path before the file's tracks, and auto-loaded external subtitles
    /// arrive after file-loaded.
    private static readonly TimeSpan FileSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TrackPollInterval = TimeSpan.FromMilliseconds(500);
    private const int TrackPollAttempts = 20;

    /// Per kind of search. Jimaku allows 25 requests a minute; title retries have to leave room for
    /// the file listings.
    private const int MaxSearchRequests = 5;

    private const int NoticeDurationMs = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly MpvIpcClient _ipc;
    private readonly StatusOverlay _status;
    private readonly ILogger _logger;
    private readonly HttpMessageHandler? _handler;
    private readonly string _cacheDir;
    private readonly SubtitleSyncService? _synchronization;
    private readonly SubtitleReleasePreferences _releases;
    private string? _expectedSubtitle;
    private DateTimeOffset _trackChoicesAfter = DateTimeOffset.MaxValue;
    private readonly SemaphoreSlim _busy = new(1, 1);

    private volatile PluginSettings _settings;
    private volatile JimakuClient _client;
    private CancellationTokenSource? _fileCts;
    private Session? _session;
    private readonly Func<SubtitleShowChoice?>? _preferredShow;
    private readonly Dictionary<(int Entry, int? Episode), List<JimakuFile>> _fileLists = [];
    private string? _requestedUrl;

    private sealed class Session(string mediaPath)
    {
        public string MediaPath { get; } = mediaPath;
        public string? MediaName { get; init; }
        public int? Episode { get; init; }
        public List<JimakuEntry> Entries { get; init; } = [];
        public string? Scope { get; init; }
        public List<Candidate> Candidates { get; set; } = [];
        public int Index { get; set; } = -1;
    }

    private sealed record Candidate(string Name, string? Url, string? LocalPath, int? TrackId = null,
        JimakuEntry? Entry = null, string? SourceUrl = null);

    private sealed record RememberedChoice(JimakuEntry Entry, int? Episode, List<Candidate> Candidates, int Index,
        List<JimakuEntry>? Entries = null, string? Scope = null);

    public JimakuService(MpvIpcClient ipc, StatusOverlay status, PluginSettings settings, ILogger logger,
        HttpMessageHandler? handler = null, string? cacheDir = null,
        Func<SubtitleShowChoice?>? preferredShow = null, SubtitleSyncService? synchronization = null)
    {
        _ipc = ipc;
        _status = status;
        _settings = settings;
        _logger = logger;
        _handler = handler;
        _preferredShow = preferredShow;
        _synchronization = synchronization;
        _cacheDir = cacheDir ?? Path.Combine(AppPaths.CacheDir, "jimaku");
        _releases = new(cacheDir is null ? SubtitleReleasePreferences.DefaultPath : Path.Combine(cacheDir, "subtitle-releases.json"));
        _client = new JimakuClient(settings.JimakuApiKey, logger, handler);
    }

    public void UpdateSettings(PluginSettings newSettings)
    {
        var previous = _settings;
        _settings = newSettings;
        if (previous.JimakuApiKey != newSettings.JimakuApiKey)
            _client = new JimakuClient(newSettings.JimakuApiKey, _logger, _handler);
    }

    public void OnFileChanged(CancellationToken lifetime)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var previous = Interlocked.Exchange(ref _fileCts, next);
        TaskHelper.CancelAndDispose(ref previous);

        _expectedSubtitle = null;
        _trackChoicesAfter = DateTimeOffset.UtcNow + FileSettleDelay;
        var settings = _settings;
        if (!settings.JimakuEnabled || !settings.JimakuAutoFetch) return;

        var token = next.Token;
        _ = TaskHelper.RunSafe(async () =>
        {
            await Task.Delay(FileSettleDelay, token);
            await _busy.WaitAsync(token);
            try
            {
                await RunGuardedAsync(() => AutoFetchAsync(token), userInitiated: false, token);
            }
            finally
            {
                _busy.Release();
            }
        }, _logger, "Jimaku auto-fetch");
    }

    public void OnAction(string action, CancellationToken lifetime)
    {
        if (!_settings.JimakuEnabled) return;
        switch (action)
        {
            case "search":
                RunExclusive(ForceSearchAsync, lifetime);
                break;
            case "next":
                RunExclusive(NextAsync, lifetime);
                break;
            case "prev":
                RunExclusive(PreviousAsync, lifetime);
                break;
            default:
                _logger.LogWarning("Unknown Jimaku action: {Action}", action);
                break;
        }
    }

    public void CancelPendingSelection(CancellationToken lifetime)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var previous = Interlocked.Exchange(ref _fileCts, next);
        TaskHelper.CancelAndDispose(ref previous);
    }

    public void OnQuery(string query, CancellationToken lifetime)
    {
        if (!_settings.JimakuEnabled || string.IsNullOrWhiteSpace(query)) return;
        RunExclusive(token => SearchAsync(query.Trim(), token), lifetime);
    }

    private void RunExclusive(Func<CancellationToken, Task> run, CancellationToken lifetime)
    {
        var token = _fileCts is { } cts ? cts.Token : lifetime;
        if (!_busy.Wait(0))
        {
            _ = TaskHelper.RunSafe(() => NotifyAsync("Jimaku: still busy with the last request", false, token),
                _logger);
            return;
        }

        _ = TaskHelper.RunSafe(async () =>
        {
            try
            {
                await RunGuardedAsync(() => run(token), userInitiated: true, token);
            }
            finally
            {
                _busy.Release();
            }
        }, _logger, "Jimaku");
    }

    private async Task RunGuardedAsync(Func<Task> run, bool userInitiated, CancellationToken ct)
    {
        try
        {
            await run();
        }
        catch (JimakuException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Jimaku {Kind}: {Message}", ex.Kind, ex.Message);
            if (ex.Kind == JimakuErrorKind.MissingKey && !userInitiated) return;
            await NotifyAsync(ErrorText(ex), important: true, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not save Jimaku subtitles");
            await NotifyAsync($"Jimaku: could not save subtitles: {ex.Message}", important: true, ct);
        }
    }

    private static string ErrorText(JimakuException ex) => ex.Kind switch
    {
        JimakuErrorKind.MissingKey => "Jimaku: no API key set. Add one in the settings (Ctrl+J).",
        JimakuErrorKind.Unauthorized => "Jimaku: the API key was refused. Check it in the settings (Ctrl+J).",
        JimakuErrorKind.RateLimited => ex.RetryAfter is { } wait
            ? $"Jimaku: too many requests, try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s"
            : "Jimaku: too many requests, try again shortly",
        _ => $"Jimaku: {ex.Message}"
    };

    private async Task AutoFetchAsync(CancellationToken ct)
    {
        var tracks = await ReadTracksAsync(ct, waitForTracks: true);
        var releaseProps = await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct);
        var releaseIdentity = releaseProps is null ? null : MediaIdentityResolver.Resolve(releaseProps);
        if (releaseProps?.Path is { } releasePath && _releases.Get(releaseIdentity) is not null)
        {
            await SearchAndLoadAsync(releasePath, releaseProps.Filename, releaseIdentity, null, false, ct);
            return;
        }
        var (state, track) = JapaneseTrackDetector.Find(tracks, IsJapaneseTrack);
        if (state == JapaneseTrackState.Selected)
        {
            if (_synchronization is { Enabled: true }) await _synchronization.SyncCurrentAsync(ct);
            return;
        }

        if (state == JapaneseTrackState.Available)
        {
            _logger.LogInformation("Selecting existing Japanese subtitle track {Id}", track!.Id);
            _expectedSubtitle = "track:" + track.Id;
            await _ipc.SetPropertyAsync("sid", track.Id, ct);
            if (_synchronization is { Enabled: true }) await _synchronization.SyncCurrentAsync(ct);
            await NotifyAsync($"Selected Japanese subtitles: {track.Title ?? track.Lang}", false, ct);
            return;
        }

        var props = await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct);
        if (props?.Path is not { } path) return;
        var identity = MediaIdentityResolver.Resolve(props);
        if (identity is { IsExtra: true }) return;

        if (await TryLoadRememberedAsync(path, props.Filename, ct)) return;
        if (!_client.HasKey)
        {
            _logger.LogInformation("No Japanese subtitles, and no Jimaku API key to fetch them with");
            return;
        }

        await SearchAndLoadAsync(path, props.Filename, identity, null, userInitiated: false, ct);
    }

    private async Task SearchAsync(string? query, CancellationToken ct)
    {
        var props = await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct);
        if (props?.Path is not { } path)
        {
            await NotifyAsync("Jimaku: no file loaded", true, ct);
            return;
        }
        await SearchAndLoadAsync(path, props.Filename, MediaIdentityResolver.Resolve(props), query, true, ct);
    }

    public async Task RefreshSelectionAsync(CancellationToken ct, string? subtitleUrl = null)
    {
        var token = _fileCts?.Token ?? ct;
        await _busy.WaitAsync(token);
        try
        {
            _session = null;
            _requestedUrl = subtitleUrl;
            await RunGuardedAsync(() => SearchAsync(null, token), true, token);
        }
        finally
        {
            _requestedUrl = null;
            _busy.Release();
        }
    }

    public void OnSubtitleChanged(CancellationToken lifetime)
    {
        if (!_settings.JimakuEnabled || _busy.CurrentCount == 0 || DateTimeOffset.UtcNow < _trackChoicesAfter) return;
        var token = _fileCts?.Token ?? lifetime;
        _ = TaskHelper.RunSafe(async () =>
        {
            await Task.Delay(150, token);
            if (_busy.CurrentCount == 0) return;
            var sid = await _ipc.GetPropertyAsync<int?>("sid", token);
            var track = (await ReadTracksAsync(token, false)).FirstOrDefault(t => t.Id == sid);
            if (track?.ExternalFilename is not { } file || !IsJapaneseTrack(track)
                || _expectedSubtitle == "file:" + file || _expectedSubtitle == "track:" + sid) return;
            var name = track.Title?.Replace("Jimaku: ", "") ?? "";
            if (JimakuFileRanker.ReleaseGroupOf(name) is null)
            {
                if (_synchronization?.Original(file) != file && _synchronization is not null) return;
                name = Path.GetFileName(file);
            }
            if (JimakuFileRanker.ReleaseGroupOf(name) is null) return;
            await RememberReleaseAsync(name, token);
            _expectedSubtitle = null;
            CancelPendingSelection(lifetime);
        }, _logger, "Remember subtitle release");
    }

    private async Task RememberReleaseAsync(string name, CancellationToken ct)
    {
        var group = JimakuFileRanker.ReleaseGroupOf(name.Replace("Jimaku: ", ""));
        if (group is null) return;
        var props = await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct);
        var identity = props is null ? null : MediaIdentityResolver.Resolve(props);
        if (identity is not null) _releases.Set(identity, group);
    }

    private string? SelectionScope => _preferredShow?.Invoke() is { } choice
        ? $"{choice.Deck.DeckId}:{choice.Episode}:{choice.Preference}" : null;

    private async Task ForceSearchAsync(CancellationToken ct)
    {
        var props = await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct);
        if (props?.Path is null)
        {
            await NotifyAsync("Jimaku: no file loaded", true, ct);
            return;
        }

        var title = SearchQueries(MediaIdentityResolver.Resolve(props)).FirstOrDefault() ?? "";
        await _ipc.SendScriptMessageAsync(LuaScriptName, "jiten-jimaku-prompt", title, "search", ct);
    }

    private async Task<List<JimakuFile>> GetFilesAsync(int entry, int? episode, CancellationToken ct)
    {
        if (_fileLists.TryGetValue((entry, episode), out var files)) return files;
        files = await _client.GetFilesAsync(entry, episode, ct);
        _fileLists[(entry, episode)] = files;
        return files;
    }

    public async Task<IReadOnlyList<SubtitleSuggestion>> FindSubtitlesAsync(
        MediaDeck deck, int? episode, CancellationToken ct)
    {
        if (!_client.HasKey) return [];
        await _busy.WaitAsync(ct);
        try
        {
            var entries = await FindEntriesAsync([.. deck.Titles.Distinct()], null, episode, ct, deck);
            var suggestions = new List<SubtitleSuggestion>();
            foreach (var entry in entries)
                suggestions.AddRange(JimakuFileRanker.Rank(await GetFilesAsync(entry.Id, episode, ct),
                    f => f.Name, episode, null).Select(f => new SubtitleSuggestion(f.Name, f.Url)));
            return suggestions;
        }
        finally { _busy.Release(); }
    }

    private async Task NextAsync(CancellationToken ct)
    {
        var path = (await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct))?.Path;
        if (_session is not { } session || session.MediaPath != path || session.Scope != SelectionScope)
        {
            await SearchAsync(null, ct);
            return;
        }
        await CycleAsync(session, 1, ct);
    }

    private async Task PreviousAsync(CancellationToken ct)
    {
        var path = (await MediaIdentityReader.ReadPropertiesAsync(_ipc, _logger, ct))?.Path;
        if (_session is not { } session || session.MediaPath != path || session.Scope != SelectionScope)
        {
            await SearchAsync(null, ct);
            return;
        }
        await CycleAsync(session, -1, ct);
    }

    private async Task CycleAsync(Session session, int step, CancellationToken ct)
    {
        var count = session.Candidates.Count;
        var start = session.Index;
        for (var n = 1; n <= count; n++)
        {
            var index = (start + step * n + count * 2) % count;
            if (await AdvanceAsync(session, index, step, true, ct, single: true)) return;
        }
        await NotifyAsync("Jimaku: none of the subtitles could be loaded", true, ct);
    }

    private async Task SearchAndLoadAsync(
        string path, string? mediaName, MediaIdentity? identity, string? query, bool userInitiated,
        CancellationToken ct)
    {
        var choice = _preferredShow?.Invoke();
        var scope = SelectionScope;
        List<string> queries;
        int? episode = choice is null ? identity?.Episode : choice.Episode;
        int? season = identity?.Season;
        if (choice is not null)
            queries = [.. choice.Deck.Titles.Distinct()];
        else if (query is not null)
        {
            var parsed = ReleaseNameParser.Parse(query);
            queries = [parsed.IsJunkTitle ? query : parsed.Title];
            episode = parsed.Episode ?? episode;
            season = parsed.Season;
        }
        else queries = SearchQueries(identity);

        var (fileTracks, selected) = await FileTracksAsync(ct);
        if (queries.Count == 0 && fileTracks.Count == 0)
        {
            if (userInitiated) await PromptAsync("", ct);
            return;
        }

        List<JimakuEntry> entries = [];
        if (_client.HasKey && queries.Count > 0)
        {
            await NotifyAsync($"Jimaku: searching {queries[0]}", false, ct);
            entries = await FindEntriesAsync(queries, season, episode, ct, choice?.Deck);
            if (choice is not null)
                entries = entries.Where(e => MatchesSelectedShow(e, choice.Deck)).ToList();
        }
        else if (fileTracks.Count == 0)
            throw new JimakuException(JimakuErrorKind.MissingKey, "No Jimaku API key set");

        var session = new Session(path)
        {
            MediaName = mediaName,
            Episode = episode,
            Entries = entries,
            Scope = scope,
            Candidates = fileTracks
        };
        foreach (var entry in entries)
        {
            List<JimakuFile> files;
            try
            {
                files = await GetFilesAsync(entry.Id, episode, ct);
            }
            catch (JimakuException ex) when (ex.Kind is JimakuErrorKind.NotFound or JimakuErrorKind.BadResponse
                or JimakuErrorKind.Network)
            {
                _logger.LogWarning(ex, "Skipping subtitle entry {Id}", entry.Id);
                continue;
            }
            foreach (var file in JimakuFileRanker.Rank(files, f => f.Name, episode, mediaName))
            {
                var candidate = new Candidate(file.Name, file.Url, null, Entry: entry);
                if (JimakuFileRanker.IsArchive(file.Name))
                {
                    try
                    {
                        var local = await DownloadCandidateAsync(candidate, ct);
                        var directory = local + ".d";
                        var members = ArchiveEpisodeFilter.Filter(
                            await SubtitleArchive.ExtractAsync(local, directory, _logger, ct), episode);
                        session.Candidates.AddRange(JimakuFileRanker.Rank(members, Path.GetFileName, null, mediaName)
                            .Select(m => new Candidate($"{file.Name}/{Path.GetRelativePath(directory, m)}", null, m,
                                Entry: entry, SourceUrl: file.Url)));
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is JimakuException
                    { Kind: JimakuErrorKind.NotFound or JimakuErrorKind.BadResponse or JimakuErrorKind.Network })
                    {
                        _logger.LogWarning(ex, "Skipping archive {Name}", file.Name);
                    }
                }
                else session.Candidates.Add(candidate);
            }
        }
        session.Candidates = session.Candidates.DistinctBy(c => c.TrackId is { } id ? $"track:{id}"
            : c.Url ?? c.LocalPath ?? c.Name).ToList();
        var preference = choice?.Preference ?? SubtitlePreference.Dialogue;
        var preferredRelease = _releases.Get(identity);
        var scored = new List<(Candidate Candidate, int Score, int Index)>();
        for (var i = 0; i < session.Candidates.Count; i++)
        {
            var candidate = session.Candidates[i];
            string? content = null;
            try
            {
                if (candidate.TrackId is null && candidate.LocalPath is null)
                    candidate = candidate with { LocalPath = await DownloadCandidateAsync(candidate, ct) };
                if (candidate.LocalPath is { } local && JimakuFileRanker.IsText(local))
                    content = await File.ReadAllTextAsync(local, ct);
            }
            catch (JimakuException ex) when (ex.Kind is JimakuErrorKind.NotFound or JimakuErrorKind.BadResponse
                or JimakuErrorKind.Network)
            {
                _logger.LogWarning(ex, "Skipping {Name}", candidate.Name);
                continue;
            }
            var score = SubtitleContentRanker.Score(candidate.Name, content, mediaName, preference);
            if (candidate.TrackId is not null) score += 20;
            if (preferredRelease is not null && string.Equals(preferredRelease,
                    JimakuFileRanker.ReleaseGroupOf(candidate.Name.Replace("Jimaku: ", "")), StringComparison.OrdinalIgnoreCase)) score += 1000;
            scored.Add((candidate, score, i));
        }
        session.Candidates = [.. scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index).Select(x => x.Candidate)];
        if (scope != SelectionScope) return;
        _session = session;
        var selectedTrack = selected >= 0 ? fileTracks[selected].TrackId : null;
        var selectedIndex = selectedTrack is null ? -1
            : session.Candidates.FindIndex(c => c.TrackId == selectedTrack);
        var requestedIndex = _requestedUrl is null ? -1 : session.Candidates.FindIndex(c => c.Url == _requestedUrl || c.SourceUrl == _requestedUrl);
        var start = requestedIndex >= 0 ? requestedIndex
            : userInitiated && query is null && selectedIndex >= 0 ? selectedIndex + 1 : 0;
        if (await AdvanceAsync(session, start, 1, userInitiated, ct)) return;
        if (start > 0 && await AdvanceAsync(session, 0, 1, userInitiated, ct)) return;
        await NotifyAsync(choice is null ? "Jimaku: no usable subtitles found"
            : $"Jimaku: no usable subtitles for {choice.Deck.DisplayTitle}", userInitiated, ct);
    }

    private static bool MatchesSelectedShow(JimakuEntry entry, MediaDeck deck)
        => entry.Flags.Movie == (deck.ChildrenDeckCount == 0)
            && entry.Flags.Anime == (deck.MediaType == MediaDeck.Anime)
            && new[] { entry.Name, entry.EnglishName, entry.JapaneseName }.OfType<string>()
                .Any(name => deck.Titles.Concat(deck.Aliases ?? [])
                    .Any(title => JimakuEntryMatcher.TitleScore(name, title) >= 2));

    internal static List<string> SearchQueries(MediaIdentity? identity)
    {
        if (identity is null) return [];
        var queries = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !ReleaseNameParser.IsJunkTitle(value) && !queries.Contains(value))
                queries.Add(value);
        }

        Add(identity.Title);
        Add(identity.SeriesTitle);
        Add(identity.FolderTitle);
        foreach (var candidate in identity.TitleCandidates)
        {
            Add(candidate);
            Add(ReleaseNameParser.Parse(candidate).SeriesTitle);
        }
        return queries;
    }

    private async Task<List<JimakuEntry>> FindEntriesAsync(
        List<string> queries, int? season, int? episode, CancellationToken ct, MediaDeck? selectedShow = null)
    {
        foreach (var anime in new[] { true, false })
        {
            var requests = 0;
            foreach (var query in queries)
            {
                if (requests >= MaxSearchRequests) break;
                requests++;
                var results = await _client.SearchAsync(query, anime, ct);

                var cleaned = Whitespace().Replace(AsciiPunctuation().Replace(query, " "), " ").Trim();
                if (results.Count == 0 && cleaned != query && cleaned.Length > 0 && requests < MaxSearchRequests)
                {
                    requests++;
                    results = await _client.SearchAsync(cleaned, anime, ct);
                }

                var ranked = JimakuEntryMatcher.Rank(results, query, season, episode);
                if (selectedShow is not null)
                    ranked = ranked.Where(e => MatchesSelectedShow(e, selectedShow)).ToList();
                _logger.LogInformation("Jimaku {Kind} search '{Query}': {Count} entries, {Plausible} plausible",
                    anime ? "anime" : "live action", query, results.Count, ranked.Count);
                if (ranked.Count > 0)
                    return [.. ranked.DistinctBy(e => e.Id)];
            }
        }
        return [];
    }

    /// Skips past downloads that fail and archives without this episode.
    private async Task<bool> AdvanceAsync(Session session, int start, int step, bool userInitiated,
        CancellationToken ct, bool single = false)
    {
        for (var i = start; i >= 0 && i < session.Candidates.Count; i += step)
        {
            ct.ThrowIfCancellationRequested();
            session.Index = i;
            try
            {
                if (await LoadCandidateAsync(session, i, userInitiated, ct))
                {
                    if (session.Candidates[session.Index].TrackId is null) Remember(session);
                    if (userInitiated) await RememberReleaseAsync(session.Candidates[session.Index].Name, ct);
                    return true;
                }
            }
            catch (JimakuException ex) when (ex.Kind is JimakuErrorKind.NotFound or JimakuErrorKind.BadResponse
                                                 or JimakuErrorKind.Network)
            {
                _logger.LogWarning("Skipping {Name}: {Message}", session.Candidates[i].Name, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning("Skipping {Name}: {Message}", session.Candidates[i].Name, ex.Message);
            }
            if (single) break;
            i = session.Index;
        }
        return false;
    }

    private async Task<string> DownloadCandidateAsync(Candidate candidate, CancellationToken ct)
    {
        var target = Path.Combine(_cacheDir, candidate.Entry!.Id.ToString(), SafeFileName(candidate.Name));
        if (!File.Exists(target) || new FileInfo(target).Length == 0)
            await _client.DownloadAsync(candidate.Url!, target, ct);
        return target;
    }

    private async Task<bool> LoadCandidateAsync(Session session, int index, bool userInitiated, CancellationToken ct)
    {
        var candidate = session.Candidates[index];
        if (candidate.TrackId is { } trackId && string.IsNullOrEmpty(candidate.LocalPath))
        {
            if (_synchronization is not null) await _synchronization.RememberCurrentDelayAsync(ct);
            _expectedSubtitle = "track:" + trackId;
            await _ipc.SetPropertyAsync("sid", trackId, ct);
            if (_synchronization is not null) await _synchronization.RestoreCurrentDelayAsync(ct);
            await NotifyAsync(Position(session, index), userInitiated, ct);
            return true;
        }
        if (candidate.LocalPath is null)
        {
            await NotifyAsync($"Jimaku: downloading {candidate.Name}", false, ct);
            candidate = candidate with { LocalPath = await DownloadCandidateAsync(candidate, ct) };
            session.Candidates[index] = candidate;
        }
        if (!File.Exists(candidate.LocalPath)) return false;
        var prepared = await PrepareAsync(candidate.LocalPath, ct);
        if (_synchronization is not null && _synchronization.Enabled)
        {
            await NotifyAsync("Checking subtitle timing...", false, ct);
            var synced = await _synchronization.PrepareAsync(prepared, ct);
            prepared = synced.Path;
        }
        if (_synchronization is not null) await _synchronization.RememberCurrentDelayAsync(ct);
        if (!await AddTrackAsync(prepared, candidate.Name, ct)) return false;
        if (_synchronization is not null) await _synchronization.RestoreCurrentDelayAsync(ct);
        await NotifyAsync(Position(session, index) + (_synchronization?.Enabled == true
            ? $"\n{_synchronization.Status}" : ""), userInitiated, ct);
        return true;
    }

    private static string Position(Session session, int index)
    {
        var candidate = session.Candidates[index];
        var label = candidate.Entry is { } entry ? $"{entry.Name}: {Path.GetFileName(candidate.Name)}" : candidate.Name;
        return $"[{index + 1}/{session.Candidates.Count}] {label}";
    }

    /// Writes a Japanese-only copy beside a bilingual JP/CN subtitle; anything else loads as is.
    private async Task<string> PrepareAsync(string path, CancellationToken ct)
    {
        var extension = JimakuFileRanker.ExtensionOf(path);
        if (!_settings.JimakuStripChinese || !JimakuFileRanker.IsText(path))
            return path;

        var (text, changed) = ChineseLineStripper.Strip(await File.ReadAllTextAsync(path, ct), extension);
        if (!changed) return path;

        var output = Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.jp-only.{extension}");
        await File.WriteAllTextAsync(output, text, ct);
        _logger.LogInformation("Stripped Chinese lines into {Path}", output);
        return output;
    }

    /// Re-selects a subtitle this file already has rather than adding it a second time.
    private async Task<bool> AddTrackAsync(string path, string name, CancellationToken ct)
    {
        _expectedSubtitle = "file:" + path;
        var existing = (await ReadTracksAsync(ct, waitForTracks: false))
            .FirstOrDefault(t => t.ExternalFilename == path);
        if (existing is not null)
        {
            await _ipc.SetPropertyAsync("sid", existing.Id, ct);
            return true;
        }

        var error = await _ipc.SubAddAsync(path, "select", $"Jimaku: {Path.GetFileName(name)}", "ja", ct);
        if (error is null) return true;

        _logger.LogWarning("mpv could not load {Path}: {Error}", path, error);
        return false;
    }

    /// The Japanese text tracks the file has without Jimaku: embedded ones and those mpv loaded itself.
    private async Task<(List<Candidate> Tracks, int Selected)> FileTracksAsync(CancellationToken ct)
    {
        var tracks = (await ReadTracksAsync(ct, waitForTracks: false))
            .Where(t => !JapaneseTrackDetector.IsBitmap(t) && IsJapaneseTrack(t)
                        && t.ExternalFilename?.StartsWith(_cacheDir, StringComparison.Ordinal) != true
                        && t.ExternalFilename?.StartsWith(SubtitleSyncService.CacheDirectory, StringComparison.Ordinal) != true)
            .ToList();
        return ([.. tracks.Select(t => new Candidate(t.Title ?? t.Lang ?? $"track {t.Id}", null, t.ExternalFilename, t.Id))],
            tracks.FindIndex(t => t.Selected));
    }

    private async Task<List<SubtitleTrack>> ReadTracksAsync(CancellationToken ct, bool waitForTracks)
    {
        for (var attempt = 0; ; attempt++)
        {
            var raw = await _ipc.GetPropertyRawAsync("track-list", ct);
            var loaded = raw is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0;
            if (loaded || !waitForTracks || attempt >= TrackPollAttempts)
                return raw is { } value ? JapaneseTrackDetector.ParseTrackList(value) : [];
            await Task.Delay(TrackPollInterval, ct);
        }
    }

    /// A subtitle downloaded by hand rarely carries a language tag, so an untagged external file
    /// is judged by its text.
    private bool IsJapaneseTrack(SubtitleTrack track)
    {
        if (JapaneseTrackDetector.IsJapanese(track)) return true;
        if (track.Lang is { Length: > 0 } || track.ExternalFilename is not { } file || !File.Exists(file))
            return false;

        try
        {
            using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[32 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return JapaneseTrackDetector.LooksJapanese(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read {Path}", file);
            return false;
        }
    }

    private async Task<bool> TryLoadRememberedAsync(string mediaPath, string? mediaName, CancellationToken ct)
    {
        RememberedChoice? choice;
        try
        {
            var file = RememberPath(mediaPath);
            if (!File.Exists(file)) return false;
            await using var stream = File.OpenRead(file);
            choice = await JsonSerializer.DeserializeAsync<RememberedChoice>(stream, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the remembered Jimaku choice");
            return false;
        }

        if (choice is null || choice.Entries is null || choice.Scope != SelectionScope || choice.Index < 0 || choice.Index >= choice.Candidates.Count
            || choice.Candidates[choice.Index].LocalPath is not { } local || !File.Exists(local))
            return false;

        // Old remembered lists can contain other episodes that were only ranked lower.
        var selected = choice.Candidates[choice.Index];
        var (fileTracks, _) = await FileTracksAsync(ct);
        var candidates = fileTracks.Concat(choice.Candidates.Where(c => c.TrackId is null
            && JimakuFileRanker.IsForEpisode(c.Name, choice.Episode))).ToList();
        var index = candidates.FindIndex(c => c.Url == selected.Url && c.LocalPath == selected.LocalPath
            && c.TrackId == selected.TrackId);
        if (index < 0) return false;

        var session = new Session(mediaPath)
        {
            MediaName = mediaName,
            Episode = choice.Episode,
            Entries = choice.Entries ?? [choice.Entry],
            Scope = choice.Scope,
            Candidates = candidates.Select(c => c.TrackId is null && c.Entry is null
                ? c with { Entry = choice.Entry } : c).ToList(),
            Index = index
        };
        _session = session;
        _logger.LogInformation("Reusing {Name} from an earlier viewing", choice.Candidates[choice.Index].Name);
        return await LoadCandidateAsync(session, index, userInitiated: false, ct);
    }

    private void Remember(Session session)
    {
        try
        {
            var file = RememberPath(session.MediaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(
                new RememberedChoice(session.Candidates[session.Index].Entry!, session.Episode,
                    session.Candidates, session.Index, session.Entries, session.Scope), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remember the Jimaku choice");
        }
    }

    private string RememberPath(string mediaPath)
        => Path.Combine(_cacheDir, "remembered",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(mediaPath)))[..32] + ".json");

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. Path.GetFileName(name).Select(c => invalid.Contains(c) || c is '\\' or ':' ? '_' : c)])
            .Trim();
        return safe.Length == 0 ? "subtitle" : safe;
    }

    private Task PromptAsync(string defaultText, CancellationToken ct)
        => _ipc.SendScriptMessageAsync(LuaScriptName, "jiten-jimaku-prompt", defaultText, ct);

    /// Only problems the user has to act on, and where a key press landed, show over the video;
    /// progress goes to mpv's log.
    private Task NotifyAsync(string message, bool important, CancellationToken ct)
        => important
            ? _status.ShowAsync(_ipc, AssOverride().Replace(message, "_"), NoticeDurationMs, ct)
            : _status.LogAsync(_ipc, message, ct);

    public void Dispose()
    {
        TaskHelper.CancelAndDispose(ref _fileCts);
    }

    [GeneratedRegex(@"[!-/:-@\[-`{-~]")]
    private static partial Regex AsciiPunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[{}\\]")]
    private static partial Regex AssOverride();
}
