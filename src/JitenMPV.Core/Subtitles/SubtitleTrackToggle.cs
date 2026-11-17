using System.Text.Json;

namespace JitenMPV.Core.Subtitles;

/// Selects dialogue tracks for the bilingual subtitle toggle.
public static class SubtitleTrackToggle
{
    /// Signs/songs and forced tracks only cover what an English dub leaves untranslated, so they
    /// are never picked as the dialogue track.
    public static int? PickEnglish(JsonElement? trackList,
        IReadOnlyDictionary<int, SubtitleLanguage>? languages = null)
    {
        int? best = null;
        int bestScore = int.MinValue;
        foreach (var track in SubTracks(trackList))
        {
            var lang = Text(track, "lang");
            var title = Text(track, "title");
            var language = track.TryGetProperty("id", out var trackId)
                ? languages?.GetValueOrDefault(trackId.GetInt32()) ?? SubtitleLanguage.Unknown : SubtitleLanguage.Unknown;
            if (language == SubtitleLanguage.Japanese) continue;
            if (language != SubtitleLanguage.English && !IsLanguage(lang, "en", "eng")
                && !(lang.Length == 0 && title.Contains("english"))) continue;

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

    public static int? PickJapanese(JsonElement? trackList,
        IReadOnlyDictionary<int, SubtitleLanguage>? languages = null)
    {
        foreach (var track in SubTracks(trackList))
        {
            if (!track.TryGetProperty("id", out var id)) continue;
            var language = languages?.GetValueOrDefault(id.GetInt32()) ?? SubtitleLanguage.Unknown;
            if (language == SubtitleLanguage.English) continue;
            if ((language == SubtitleLanguage.Japanese || IsLanguage(Text(track, "lang"), "ja", "jpn"))
                && !Flag(track, "forced")) return id.GetInt32();
        }
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
