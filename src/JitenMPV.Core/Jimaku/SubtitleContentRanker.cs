using System.Text.RegularExpressions;
using JitenMPV.Core.Subtitles;
using JitenMPV.Core.Tracking;

namespace JitenMPV.Core.Jimaku;

public static partial class SubtitleContentRanker
{
    public static int Score(string name, string? content, string? mediaName, SubtitlePreference preference)
    {
        var score = JimakuFileRanker.Score(name, null, JimakuFileRanker.ReleaseGroupOf(mediaName));
        if (Source(mediaName) is { } source && Source(name) == source) score += 12;
        if (preference == SubtitlePreference.Any) return score;

        var captions = CaptionLabel().IsMatch(name);
        var readings = 0;
        var labels = 0;
        if (content is not null)
        {
            List<SubtitleCue> cues;
            try
            {
                cues = JimakuFileRanker.ExtensionOf(name) switch
                {
                    "ass" or "ssa" => AssParser.Parse(content),
                    "vtt" => VttParser.Parse(content),
                    _ => SrtParser.Parse(content)
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                return score - 100;
            }
            if (cues.Count > 0)
            {
                labels = cues.Count(c => SpeakerLabel().IsMatch(c.Text)) * 100 / cues.Count;
                readings = cues.Count(c => ReadingAid().IsMatch(c.Text)) * 100 / cues.Count;
                if (content.Contains("<ruby", StringComparison.OrdinalIgnoreCase)) readings = Math.Max(readings, 30);
            }
        }
        var annotation = (captions ? 30 : 0) + Math.Min(50, labels) + Math.Min(50, readings);
        return score + (preference == SubtitlePreference.Verbatim ? annotation : -annotation);
    }

    private static string? Source(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var match = ReleaseSource().Match(name);
        if (!match.Success) return null;
        return match.Value.StartsWith('b') || match.Value.StartsWith('B') ? "bluray" : "web";
    }

    [GeneratedRegex(@"(?i)(?<![a-z0-9])(?:cc|sdh|closed[ ._-]?captions?)(?![a-z0-9])")]
    private static partial Regex CaptionLabel();

    [GeneratedRegex(@"^\s*(?:[（(【\[][^）)】\]\r\n]{1,20}[）)】\]]|[^\s:：\r\n]{1,15}[:：])")]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"<ruby\b|《[^》]+》|[\p{IsCJKUnifiedIdeographs}]+[（(][\p{IsHiragana}\p{IsKatakana}ー]+[）)]", RegexOptions.IgnoreCase)]
    private static partial Regex ReadingAid();

    [GeneratedRegex(@"(?i)(?<![a-z])(?:blu[ ._-]?ray|b[dr]rip|bd|web[ ._-]?(?:dl|rip))(?![a-z])")]
    private static partial Regex ReleaseSource();
}
