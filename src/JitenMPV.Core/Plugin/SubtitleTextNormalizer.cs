using JitenMPV.Core.Config;
using JitenMPV.Core.Subtitles;
using JitenMPV.Core.Text;

namespace JitenMPV.Core.Plugin;

/// Live sub-text and file cues both pass through here: the parse cache and the mining timeline
/// match on exact text, so the two sources must agree.
public static class SubtitleTextNormalizer
{
    public static string? Apply(string? text, PluginSettings? settings)
        => text is null || settings?.ConvertHalfWidthKatakana == false
            ? text
            : HalfWidthKatakana.ToFullWidth(text);

    public static List<SubtitleCue> ApplyAll(List<SubtitleCue> cues, PluginSettings? settings)
    {
        if (settings?.ConvertHalfWidthKatakana == false) return cues;

        for (var i = 0; i < cues.Count; i++)
        {
            var text = HalfWidthKatakana.ToFullWidth(cues[i].Text);
            if (!ReferenceEquals(text, cues[i].Text))
                cues[i] = cues[i] with { Text = text };
        }
        return cues;
    }
}
