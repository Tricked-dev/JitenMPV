using System.Text.Json;

namespace JitenMPV.Core.Subtitles;

public readonly record struct SubtitleTrackSwitch(int Sid, double? Delay);

/// Flips the main subtitle track between English and the track it was switched from. mpv has one
/// sub-delay for all tracks, so each track's delay is remembered here and restored with it.
public sealed class SubtitleTrackToggle
{
    private string? _path;
    private int? _returnSid;
    private readonly Dictionary<int, double> _delays = [];

    /// Null when English is already selected and there is nothing to switch back to.
    public SubtitleTrackSwitch? Next(string? path, int? selected, double delay, int english, int? japanese)
    {
        if (path != _path)
        {
            _path = path;
            _returnSid = null;
            _delays.Clear();
        }

        if (selected is { } current)
            _delays[current] = delay;

        int? target;
        if (selected == english)
        {
            target = _returnSid ?? japanese;
        }
        else
        {
            _returnSid = selected;
            target = english;
        }

        if (target is not { } sid) return null;

        // A user who never touches sub-delay keeps whatever mpv.conf set it to.
        double? next = _delays.Values.Any(d => d != 0) ? _delays.GetValueOrDefault(sid) : null;
        return new SubtitleTrackSwitch(sid, next);
    }

    /// Signs/songs and forced tracks only cover what an English dub leaves untranslated, so they
    /// are never picked as the dialogue track.
    public static int? PickEnglish(JsonElement? trackList)
    {
        int? best = null;
        int bestScore = int.MinValue;
        foreach (var track in SubTracks(trackList))
        {
            var lang = Text(track, "lang");
            var title = Text(track, "title");
            if (!IsLanguage(lang, "en", "eng") && !(lang.Length == 0 && title.Contains("english")))
                continue;

            bool dialogue = title.Contains("full") || title.Contains("dialog");
            if (Flag(track, "forced")
                || (!dialogue && (title.Contains("sign") || title.Contains("song"))))
                continue;

            int score = 0;
            if (Flag(track, "default")) score += 4;
            if (dialogue) score += 2;
            if (Flag(track, "hearing-impaired") || title.Contains("sdh")) score -= 1;
            if (title.Contains("dubtitle") || title.Contains("commentary")) score -= 8;

            if (score > bestScore && track.TryGetProperty("id", out var id))
            {
                best = id.GetInt32();
                bestScore = score;
            }
        }
        return best;
    }

    public static int? PickJapanese(JsonElement? trackList)
    {
        foreach (var track in SubTracks(trackList))
            if (IsLanguage(Text(track, "lang"), "ja", "jpn") && !Flag(track, "forced")
                && track.TryGetProperty("id", out var id))
                return id.GetInt32();
        return null;
    }

    private static IEnumerable<JsonElement> SubTracks(JsonElement? trackList)
    {
        if (trackList is not { ValueKind: JsonValueKind.Array } list) yield break;
        foreach (var track in list.EnumerateArray())
            if (Text(track, "type") == "sub")
                yield return track;
    }

    private static bool IsLanguage(string lang, string twoLetter, string threeLetter)
        => lang == twoLetter || lang == threeLetter
           || lang.StartsWith(twoLetter + "-") || lang.StartsWith(threeLetter + "-");

    private static string Text(JsonElement track, string name)
        => track.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.ToLowerInvariant()
            : "";

    private static bool Flag(JsonElement track, string name)
        => track.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
