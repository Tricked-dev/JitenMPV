using System.Globalization;
using System.Text.RegularExpressions;
using JitenMPV.Core.Subtitles;

namespace JitenMPV.Core.Sync;

public static partial class SubtitleSyncDocument
{
    public static List<SubtitleCue> Dialogue(string content, string extension)
    {
        var format = SubtitleParser.DetectFormat(extension, content);
        if (format == SubtitleFormat.Ass)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var styleIndex = 3;
            var textIndex = 9;
            content = string.Join('\n', lines.Where(line =>
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = line[7..].Split(',');
                    var style = Array.FindIndex(fields, f => f.Trim().Equals("Style", StringComparison.OrdinalIgnoreCase));
                    var text = Array.FindIndex(fields, f => f.Trim().Equals("Text", StringComparison.OrdinalIgnoreCase));
                    if (style >= 0) styleIndex = style;
                    if (text >= 0) textIndex = text;
                }
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) return true;
                var parts = line[9..].Split(',', Math.Max(textIndex + 1, 10));
                return parts.Length > Math.Max(styleIndex, textIndex) && !NonDialogueStyle().IsMatch(parts[styleIndex])
                    && !Drawing().IsMatch(parts[textIndex]);
            }));
        }
        var cues = format switch
        {
            SubtitleFormat.Ass => AssParser.Parse(content),
            SubtitleFormat.Vtt => VttParser.Parse(content),
            SubtitleFormat.Srt => SrtParser.Parse(content),
            _ => []
        };
        return [.. cues.Where(c => c.End > c.Start && (c.End - c.Start).TotalSeconds is >= .12 and <= 15
            && !string.IsNullOrWhiteSpace(c.Text))];
    }

    public static string Shift(string content, string extension, double seconds)
    {
        if (SubtitleParser.DetectFormat(extension, content) == SubtitleFormat.Ass)
        {
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var startIndex = 1;
            var endIndex = 2;
            var lines = new List<string>();
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = line[7..].Split(',');
                    var start = Array.FindIndex(fields, f => f.Trim().Equals("Start", StringComparison.OrdinalIgnoreCase));
                    var end = Array.FindIndex(fields, f => f.Trim().Equals("End", StringComparison.OrdinalIgnoreCase));
                    if (start >= 0) startIndex = start;
                    if (end >= 0) endIndex = end;
                }
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) { lines.Add(line); continue; }
                var fields2 = line[9..].Split(',', 10);
                if (fields2.Length < 10 || fields2.Length <= Math.Max(startIndex, endIndex)) { lines.Add(line); continue; }
                var a = AssTime(fields2[startIndex]);
                var b = AssTime(fields2[endIndex]);
                if (a is null || b is null) { lines.Add(line); continue; }
                if (b + seconds <= 0) continue;
                fields2[startIndex] = AssStamp(Math.Max(0, a.Value + seconds));
                fields2[endIndex] = AssStamp(b.Value + seconds);
                lines.Add("Dialogue:" + string.Join(',', fields2));
            }
            return string.Join(newline, lines);
        }
        return TimingLine().Replace(content, match =>
        {
            var a = TextTime(match.Groups["start"].Value);
            var b = TextTime(match.Groups["end"].Value);
            var separator = match.Groups["start"].Value.Contains(',') ? ',' : '.';
            return TextStamp(Math.Max(0, a + seconds), separator) + match.Groups["arrow"].Value
                + TextStamp(Math.Max(0, b + seconds), separator);
        });
    }

    private static double? AssTime(string value)
    {
        var fields = value.Trim().Split(':');
        return fields.Length == 3 && int.TryParse(fields[0], out var h) && int.TryParse(fields[1], out var m)
            && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? h * 3600 + m * 60 + s : null;
    }

    private static string AssStamp(double seconds)
    {
        var centiseconds = (long)Math.Round(seconds * 100);
        return $"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}";
    }

    private static double TextTime(string value)
    {
        var fields = value.Replace(',', '.').Split(':');
        return fields.Length == 3 ? int.Parse(fields[0]) * 3600 + int.Parse(fields[1]) * 60
            + double.Parse(fields[2], CultureInfo.InvariantCulture)
            : int.Parse(fields[0]) * 60 + double.Parse(fields[1], CultureInfo.InvariantCulture);
    }

    private static string TextStamp(double seconds, char separator)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}";
    }

    [GeneratedRegex(@"(?m)^(?<start>(?:\d+:)?\d{2}:\d{2}[,.]\d{3})(?<arrow>\s+-->\s+)(?<end>(?:\d+:)?\d{2}:\d{2}[,.]\d{3})")]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"(?i)(?:^|[ _-])(?:op|ed|title)(?:$|[ _-])|sign|song|karaoke|romaji|kanji|lyric")]
    private static partial Regex NonDialogueStyle();

    [GeneratedRegex(@"\\p[1-9]")]
    private static partial Regex Drawing();
}
