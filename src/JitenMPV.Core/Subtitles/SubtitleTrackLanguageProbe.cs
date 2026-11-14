using System.Text.Json;
using System.Text.RegularExpressions;
using JitenMPV.Core.Jimaku;
using JitenMPV.Core.Config;
using JitenMPV.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using JitenMPV.Core.Mpv;

namespace JitenMPV.Core.Subtitles;

public enum SubtitleLanguage { Unknown, English, Japanese }

public sealed partial class SubtitleTrackLanguageProbe
{
    private string? _path;
    private readonly Dictionary<int, SubtitleLanguage> _languages = [];
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "to", "of", "for", "with", "from", "in", "on",
        "i", "you", "he", "she", "we", "they", "it", "me", "my", "your", "our", "their",
        "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did",
        "don't", "doesn't", "didn't", "can't", "won't", "it's", "i'm", "you're", "that's",
        "this", "that", "these", "those", "what", "why", "when", "where", "how", "who",
        "not", "no", "yes", "now", "just", "so", "if", "then", "will", "would", "should",
        "can", "could", "must", "know", "want", "go", "get", "come", "here", "there"
    };

    public static SubtitleLanguage Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SubtitleLanguage.Unknown;
        var letters = text.Count(char.IsLetter);
        var kana = text.Count(c => c is >= '\u3040' and <= '\u30ff');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (kana >= 3 && kana * 10 >= letters && latin * 2 < letters) return SubtitleLanguage.Japanese;
        if (letters < 12 || latin * 100 < letters * 85) return SubtitleLanguage.Unknown;
        var words = EnglishToken().Matches(text).Select(m => m.Value.Replace('’', '\'')).ToList();
        var common = words.Where(EnglishWords.Contains).ToList();
        return words.Count >= 5 && common.Count >= 3 && common.Count * 2 >= words.Count
            && common.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2
            ? SubtitleLanguage.English : SubtitleLanguage.Unknown;
    }

    public async Task<IReadOnlyDictionary<int, SubtitleLanguage>> InspectAsync(
        MpvIpcClient ipc, JsonElement? trackList, string? path, CancellationToken ct)
    {
        if (_path != path)
        {
            _path = path;
            _languages.Clear();
        }
        var tracks = trackList is { } list ? JapaneseTrackDetector.ParseTrackList(list) : [];
        var selected = tracks.FirstOrDefault(t => t.Selected);
        var selectedRaw = await ipc.GetPropertyRawAsync("sid", ct);
        var selectedId = selectedRaw is { ValueKind: JsonValueKind.Number } sid ? sid.GetInt32() : selected?.Id;
        if (selectedId is { } current)
            Remember(current, await ReadLinesAsync(ipc, false, ct));

        foreach (var track in tracks.Where(t => t.ExternalFilename is not null && !_languages.ContainsKey(t.Id)))
        {
            try
            {
                if (JimakuFileRanker.IsText(track.ExternalFilename!) && File.Exists(track.ExternalFilename))
                    Remember(track.Id, string.Join('\n', SubtitleParser.ParseFile(track.ExternalFilename!)
                        .Take(64).Select(c => c.Text)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                or OverflowException or ArgumentOutOfRangeException)
            { }
        }
        if (SubtitleTrackToggle.PickEnglish(trackList, _languages) is not null)
            return _languages;

        var secondary = await ipc.GetPropertyRawAsync("secondary-sid", ct);
        if (secondary is { ValueKind: JsonValueKind.Number } secondaryId)
        {
            Remember(secondaryId.GetInt32(), await ReadLinesAsync(ipc, true, ct));
            return _languages;
        }
        var visible = await ipc.GetPropertyRawAsync("secondary-sub-visibility", ct);
        if (visible is null) return _languages;
        var wasVisible = visible.Value.ValueKind == JsonValueKind.True;
        foreach (var candidate in tracks.Where(t => t.ExternalFilename is null && !t.Forced
            && !JapaneseTrackDetector.IsBitmap(t) && !_languages.ContainsKey(t.Id)
            && !SignsOnly().IsMatch(t.Title ?? "")).Take(3))
        {
            try
            {
                await ipc.SetPropertyAsync("secondary-sub-visibility", false, ct);
                await ipc.SetPropertyAsync("secondary-sid", candidate.Id, ct);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (await ipc.GetPropertyAsync<string>("path", ct) != path) return _languages;
                    Remember(candidate.Id, await ReadLinesAsync(ipc, true, ct));
                    if (_languages.ContainsKey(candidate.Id)) break;
                    await Task.Delay(50, ct);
                }
                if (!_languages.ContainsKey(candidate.Id))
                    Remember(candidate.Id, await ReadBufferedTrackAsync(ipc, candidate, tracks, path, ct));
                if (!_languages.ContainsKey(candidate.Id))
                    Remember(candidate.Id, await ReadSourceTrackAsync(ipc, candidate, trackList, path, ct));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var sameFile = await ipc.GetPropertyAsync<string>("path", cleanup.Token) == path;
                    var active = await ipc.GetPropertyRawAsync("secondary-sid", cleanup.Token);
                    if (sameFile && active is { ValueKind: JsonValueKind.Number } activeId
                        && activeId.GetInt32() == candidate.Id)
                        await ipc.SetPropertyAsync("secondary-sid", "no", cleanup.Token);
                    var currentVisibility = await ipc.GetPropertyRawAsync("secondary-sub-visibility", cleanup.Token);
                    if (currentVisibility is { ValueKind: JsonValueKind.False })
                        await ipc.SetPropertyAsync("secondary-sub-visibility", wasVisible, cleanup.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
            if (SubtitleTrackToggle.PickEnglish(trackList, _languages) is not null) break;
        }
        return _languages;
    }

    private static async Task<string?> ReadBufferedTrackAsync(MpvIpcClient ipc, SubtitleTrack candidate,
        IReadOnlyList<SubtitleTrack> tracks, string? path, CancellationToken ct)
    {
        var state = await ipc.GetPropertyRawAsync("demuxer-cache-state", ct);
        var position = await ipc.GetPropertyAsync<double?>("time-pos", ct);
        if (position is null || state is not { ValueKind: JsonValueKind.Object } cache
            || !cache.TryGetProperty("seekable-ranges", out var ranges)) return null;
        foreach (var range in ranges.EnumerateArray())
        {
            var start = Math.Max(range.GetProperty("start").GetDouble(), position.Value - 10);
            var end = Math.Min(range.GetProperty("end").GetDouble(), position.Value + 10);
            if (end - start < 2) continue;
            var ffmpeg = await FfmpegLocator.ResolveAsync(null, ct);
            if (ffmpeg is null) return null;
            var directory = Path.Combine(AppPaths.CacheDir, "subtitle-probe");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mkv");
            try
            {
                if (await ipc.GetPropertyAsync<string>("path", ct) != path) return null;
                await ipc.DumpCacheAsync(start, end, file, ct);
                var embedded = tracks.Where(t => t.ExternalFilename is null && (t.Selected || t.Id == candidate.Id))
                    .OrderBy(t => t.Id).ToList();
                var index = embedded.FindIndex(t => t.Id == candidate.Id);
                if (index < 0) return null;
                var runner = new FfmpegRunner(ffmpeg.ExecutablePath, NullLogger.Instance);
                var (result, bytes) = await runner.RunCaptureStdoutAsync(
                    ["-i", file, "-map", $"0:s:{index}", "-f", "ass", "-"], TimeSpan.FromSeconds(3), ct);
                return result.Succeeded ? DialogueText(Encoding.UTF8.GetString(bytes)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                or OverflowException or ArgumentOutOfRangeException)
            { return null; }
            finally
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        return null;
    }

    private static async Task<string?> ReadSourceTrackAsync(MpvIpcClient ipc, SubtitleTrack candidate,
        JsonElement? trackList, string? path, CancellationToken ct)
    {
        if (trackList is not { ValueKind: JsonValueKind.Array } list) return null;
        int? index = null;
        foreach (var track in list.EnumerateArray())
            if (track.TryGetProperty("type", out var type) && type.GetString() == "sub"
                && track.TryGetProperty("id", out var id) && id.GetInt32() == candidate.Id
                && track.TryGetProperty("ff-index", out var ff)) index = ff.GetInt32();
        if (index is null) return null;
        var source = await ipc.GetPropertyAsync<string>("stream-open-filename", ct) ?? path;
        var position = await ipc.GetPropertyAsync<double?>("time-pos", ct);
        var ffmpeg = await FfmpegLocator.ResolveAsync(null, ct);
        if (source is null || position is null || ffmpeg is null) return null;
        var args = new List<string>();
        var headers = await ipc.GetPropertyRawAsync("http-header-fields", ct);
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && headers is { ValueKind: JsonValueKind.Array } fields && fields.GetArrayLength() > 0)
            args.AddRange(["-headers", string.Join("\r\n", fields.EnumerateArray().Select(f => f.GetString())) + "\r\n"]);
        foreach (var (property, option) in new[] { ("user-agent", "-user_agent"), ("referrer", "-referer") })
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && await ipc.GetPropertyAsync<string>(property, ct) is { Length: > 0 } value)
                args.AddRange([option, value]);
        args.AddRange(["-ss", Math.Max(0, position.Value - 5).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", source, "-t", "30", "-map", $"0:{index}", "-f", "ass", "-"]);
        var runner = new FfmpegRunner(ffmpeg.ExecutablePath, NullLogger.Instance);
        var (result, bytes) = await runner.RunCaptureStdoutAsync(args, TimeSpan.FromSeconds(8), ct);
        if (await ipc.GetPropertyAsync<string>("path", ct) != path) return null;
        return result.Succeeded ? DialogueText(Encoding.UTF8.GetString(bytes)) : null;
    }

    private static string DialogueText(string ass)
    {
        var filtered = new List<string>();
        var styleIndex = 3;
        foreach (var line in ass.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line[7..].Split(',');
                var found = Array.FindIndex(fields, f => f.Trim().Equals("Style", StringComparison.OrdinalIgnoreCase));
                if (found >= 0) styleIndex = found;
            }
            if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line[9..].Split(',', 10);
                if (fields.Length <= styleIndex || NonDialogueStyle().IsMatch(fields[styleIndex])
                    || DrawingTag().IsMatch(line)) continue;
            }
            filtered.Add(line);
        }
        return string.Join('\n', AssParser.Parse(string.Join('\n', filtered))
            .Where(c => c.Start >= TimeSpan.Zero && c.Start < TimeSpan.FromSeconds(45) && c.End > c.Start)
            .Take(64).Select(c => c.Text));
    }

    private void Remember(int id, string? text)
    {
        var language = Detect(text);
        if (language != SubtitleLanguage.Unknown) _languages[id] = language;
    }

    private static async Task<string?> ReadLinesAsync(MpvIpcClient ipc, bool secondary, CancellationToken ct)
    {
        var lines = await ipc.GetPropertyRawAsync(secondary ? "secondary-sub-lines" : "sub-lines", ct);
        if (lines is { ValueKind: JsonValueKind.Array } list)
            return string.Join('\n', list.EnumerateArray().Take(64)
                .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("text", out _))
                .Select(x => x.GetProperty("text").GetString()));
        return await ipc.GetPropertyAsync<string>(secondary ? "secondary-sub-text" : "sub-text", ct);
    }

    [GeneratedRegex(@"(?i)(?:^|[ _-])(?:op|ed)(?:$|[ _-])|sign|song|karaoke|romaji|kanji|lyric|(?:^|[ _-])title(?:$|[ _-])")]
    private static partial Regex NonDialogueStyle();

    [GeneratedRegex(@"\\p[1-9]")]
    private static partial Regex DrawingTag();

    [GeneratedRegex(@"[A-Za-z]+(?:['’][A-Za-z]+)?")]
    private static partial Regex EnglishToken();

    [GeneratedRegex(@"(?i)^(?!.*(?:full|dialog)).*(?:sign|song|forced)")]
    private static partial Regex SignsOnly();
}
