using JitenMPV.Core.Cache;
using JitenMPV.Core.Config;
using JitenMPV.Core.Mpv;
using JitenMPV.Core.Rendering;

namespace JitenMPV.Core.Plugin;

public sealed class SubtitleMeasurer(PluginSettings settings, OsdState osd)
{
    private const int MeasureId = 99;
    private const float WrapTolerance = 2f;

    private sealed record MeasuredLine(
        string Text, int StartIdx, OverlayBounds? Ink, OverlayBounds? Centered);

    /// Every prefix is measured with this glyph appended and the glyph's own ink extent subtracted
    /// back out, which recovers the pen position exactly. Measuring the bare prefix instead reads
    /// its last glyph's ink right edge, which overstates the pen in fonts whose glyphs overhang
    /// their advance (Yu Gothic draws ~44px art on a 34px advance at fs48) and understates it for
    /// trailing whitespace, whose ink is trimmed entirely.
    private const string SentinelGlyph = "国";

    /// Measurement pen origin, kept off the canvas edge so outlines are not clipped out of the
    /// reported bounds.
    private const float MeasureOrigin = 64f;

    private volatile PluginSettings _settings = settings;
    private readonly BoundedCache<string, List<WordRect>> _cache = new(2000);
    private int _lastOsdVersion = -1;

    public void UpdateSettings(PluginSettings newSettings)
    {
        var old = _settings;
        _settings = newSettings;

        if (old.FontFamily != newSettings.FontFamily ||
            old.FontSize != newSettings.FontSize ||
            Math.Abs(old.BorderSize - newSettings.BorderSize) > 0.01 ||
            old.SubtitleAlignment != newSettings.SubtitleAlignment ||
            old.SubtitleMarginX != newSettings.SubtitleMarginX ||
            old.SubtitleMarginY != newSettings.SubtitleMarginY)
        {
            _cache.Clear();
        }
    }

    public async Task<List<WordRect>> MeasureAsync(
        string text, ParseCacheEntry entry,
        MpvIpcClient ipc, CancellationToken ct)
    {
        if (osd.Version != _lastOsdVersion)
        {
            _cache.Clear();
            _lastOsdVersion = osd.Version;
        }

        var cached = _cache.GetOrDefault(text);
        if (cached is not null)
            return cached;

        var rects = await MeasureInternalAsync(text, entry, ipc, ct);
        _cache.TryAdd(text, rects);
        return rects;
    }

    private async Task<List<WordRect>> MeasureInternalAsync(
        string text, ParseCacheEntry entry, MpvIpcClient ipc, CancellationToken ct)
    {
        var s = _settings;
        var styleTags = OverlayRenderer.BuildStyleTags(s);
        float resX = OverlayRenderer.ComputeResX(osd.Width, osd.Height);
        var posTags = OverlayRenderer.BuildPositionTags(resX, s);

        var lines = SplitLines(text);
        var rects = new List<WordRect>();
        int align = OverlayRenderer.ClampAlign(s.SubtitleAlignment);
        int nextId = MeasureId;
        int AllocId() => nextId++;

        // \q2 keeps a measurement that would not fit at the left edge from wrapping, which would
        // report the play-res width instead of the text's. \shad0\blur0 keeps a user's OSD shadow
        // or blur style out of the reported ink bounds.
        string MeasureTags() => $@"{{\an7\pos({MeasureOrigin:F0},{MeasureOrigin:F0})\q2{styleTags}\shad0\blur0}}";

        var fullAss = $@"{{\an{align}{posTags}{styleTags}\shad0\blur0}}{AssTagBuilder.EscapeText(text)}";
        var fullBounds = await ipc.MeasureOverlayAsync(AllocId(), fullAss, ct);
        if (fullBounds is null)
        {
            await RemoveOverlaysAsync(ipc, nextId, ct);
            return rects;
        }

        var lineInk = new OverlayBounds?[lines.Count];
        var lineCentered = new OverlayBounds?[lines.Count];
        var inkTasks = new Dictionary<int, (Task<OverlayBounds?> Ink, Task<OverlayBounds?> Centered)>();
        for (int li = 0; li < lines.Count; li++)
        {
            var (lineText, _) = lines[li];
            if (lineText.Length == 0) continue;

            var escapedLine = AssTagBuilder.EscapeText(lineText);
            inkTasks[li] = (
                ipc.MeasureOverlayAsync(AllocId(), $"{MeasureTags()}{escapedLine}", ct),
                ipc.MeasureOverlayAsync(AllocId(), $@"{{\an{align}{posTags}{styleTags}\shad0\blur0}}{escapedLine}", ct));
        }

        await Task.WhenAll(inkTasks.Values.SelectMany(t => new[] { t.Ink, t.Centered }));
        foreach (var (li, tasks) in inkTasks)
        {
            lineInk[li] = await tasks.Ink;
            lineCentered[li] = await tasks.Centered;
        }

        var visualLines = new List<MeasuredLine>();
        for (int li = 0; li < lines.Count; li++)
        {
            var (lineText, lineStartIdx) = lines[li];
            var ink = lineInk[li];
            var centered = lineCentered[li];

            if (lineText.Length == 0 || ink is null || centered is null)
            {
                visualLines.Add(new MeasuredLine(lineText, lineStartIdx, ink, centered));
                continue;
            }

            if (centered.Height <= ink.Height + WrapTolerance)
            {
                visualLines.Add(new MeasuredLine(lineText, lineStartIdx, ink, centered));
                continue;
            }

            var prefixTasks = new Dictionary<int, Task<OverlayBounds?>>();
            for (int p = 1; p <= lineText.Length; p++)
            {
                var prefix = AssTagBuilder.EscapeText(lineText[..p]);
                prefixTasks[p] = ipc.MeasureOverlayAsync(
                    AllocId(), $@"{{\an{align}{posTags}{styleTags}\shad0\blur0}}{prefix}", ct);
            }

            await Task.WhenAll(prefixTasks.Values);
            var prefixBounds = new Dictionary<int, OverlayBounds?>();
            foreach (var (p, task) in prefixTasks)
                prefixBounds[p] = await task;

            foreach (var (visualText, visualStart) in SplitWrappedLine(lineText, prefixBounds, ink.Height))
                visualLines.Add(new MeasuredLine(visualText, lineStartIdx + visualStart, null, null));
        }

        var visualMeasureTasks = new Dictionary<int, (Task<OverlayBounds?> Ink, Task<OverlayBounds?> Centered)>();
        for (int li = 0; li < visualLines.Count; li++)
        {
            var line = visualLines[li];
            if (line.Text.Length == 0 || (line.Ink is not null && line.Centered is not null)) continue;

            var escapedLine = AssTagBuilder.EscapeText(line.Text);
            visualMeasureTasks[li] = (
                ipc.MeasureOverlayAsync(AllocId(), $"{MeasureTags()}{escapedLine}", ct),
                ipc.MeasureOverlayAsync(AllocId(), $@"{{\an{align}{posTags}{styleTags}\shad0\blur0}}{escapedLine}", ct));
        }

        await Task.WhenAll(visualMeasureTasks.Values.SelectMany(t => new[] { t.Ink, t.Centered }));
        foreach (var (li, tasks) in visualMeasureTasks)
        {
            var line = visualLines[li];
            visualLines[li] = line with
            {
                Ink = await tasks.Ink,
                Centered = await tasks.Centered
            };
        }

        if (visualLines.Any(line => line.Ink is { } ink
                                 && line.Centered is { } centered
                                 && centered.Height > ink.Height + WrapTolerance))
        {
            await RemoveOverlaysAsync(ipc, nextId, ct);
            return rects;
        }

        int firstIdx = -1, lastIdx = -1;
        for (int li = 0; li < visualLines.Count; li++)
        {
            if (visualLines[li].Ink is not { Height: > 0 }) continue;
            if (firstIdx < 0) firstIdx = li;
            lastIdx = li;
        }

        if (firstIdx < 0)
        {
            await RemoveOverlaysAsync(ipc, nextId, ct);
            return rects;
        }

        // Line slots are one font line apart regardless of each line's ink, so the spacing is the
        // block height minus the last line's own ink height, spread over the slots between them.
        float lineSpacing = (float)(fullBounds.Height / Math.Max(visualLines.Count, 1));
        if (lastIdx > firstIdx)
        {
            float derived = (float)((fullBounds.Height - visualLines[lastIdx].Ink!.Height)
                                    / (lastIdx - firstIdx));
            if (derived > 1f) lineSpacing = derived;
        }

        var linePositions = new List<int>?[visualLines.Count];
        for (int li = 0; li < visualLines.Count; li++)
        {
            var line = visualLines[li];
            if (line.Text.Length == 0 || line.Ink is null || line.Centered is null) continue;

            var positions = new SortedSet<int> { 0, line.Text.Length };
            foreach (var token in entry.Tokens)
            {
                int tokenStart = Math.Max(token.Start, line.StartIdx);
                int tokenEnd = Math.Min(token.Start + token.Length, line.StartIdx + line.Text.Length);
                if (tokenStart >= tokenEnd) continue;
                positions.Add(tokenStart - line.StartIdx);
                positions.Add(tokenEnd - line.StartIdx);
            }

            if (positions.Count > 1) linePositions[li] = positions.ToList();
        }

        OverlayBounds? sentinelBounds = null;
        if (linePositions.Any(p => p is not null))
        {
            sentinelBounds = await ipc.MeasureOverlayAsync(
                AllocId(), $"{MeasureTags()}{SentinelGlyph}{SentinelGlyph}", ct);
        }

        for (int li = 0; li < visualLines.Count; li++)
        {
            if (linePositions[li] is not { } positions) continue;

            var line = visualLines[li];
            var lineTokens = entry.Tokens
                .Select((t, i) => (Index: i, Token: t))
                .Select(x =>
                {
                    int start = Math.Max(x.Token.Start, line.StartIdx);
                    int end = Math.Min(x.Token.Start + x.Token.Length, line.StartIdx + line.Text.Length);
                    return (x.Index, x.Token, Start: start, End: end);
                })
                .Where(x => x.Start < x.End)
                .ToList();

            if (lineTokens.Count == 0) continue;
            if (line.Ink is not { X1: > 0 } || line.Centered is null) continue;

            float border = (float)s.BorderSize;
            var advances = await MeasureAdvancesAsync(
                line.Text, positions, MeasureTags(), sentinelBounds, border, resX,
                ipc, AllocId, ct);

            float totalAdvance = advances[line.Text.Length] - border;
            float anchorX = OverlayRenderer.ComputePosition(
                align, s.SubtitleMarginX, s.SubtitleMarginY, resX).X;
            float penOrigin = (align % 3) switch
            {
                1 => anchorX,
                0 => anchorX - totalAdvance,
                _ => anchorX - totalAdvance / 2f
            };

            float lineY = (float)fullBounds.Y0 + (li - firstIdx) * lineSpacing;
            float lineHeight = (float)line.Ink.Height;

            foreach (var piece in lineTokens)
            {
                int localStart = piece.Start - line.StartIdx;
                int localEnd = piece.End - line.StartIdx;

                float x0 = penOrigin + advances.GetValueOrDefault(localStart, border) - border;
                float x1 = penOrigin + advances.GetValueOrDefault(localEnd, border) - border;

                rects.Add(new WordRect(piece.Index, piece.Token.WordId, piece.Token.ReadingIndex,
                    x0, lineY, Math.Max(x1 - x0, 1), lineHeight));
            }
        }

        await RemoveOverlaysAsync(ipc, nextId, ct);
        return WordRect.AssignHitRegions(rects);
    }

    private static Task RemoveOverlaysAsync(MpvIpcClient ipc, int nextId, CancellationToken ct)
        => Task.WhenAll(
            Enumerable.Range(MeasureId, nextId - MeasureId)
                .Select(id => ipc.RemoveOverlayAsync(id, ct)));

    private async Task<Dictionary<int, float>> MeasureAdvancesAsync(
        string text, IReadOnlyList<int> positions, string measureTags,
        OverlayBounds? sentinelBounds, float border, float resX,
        MpvIpcClient ipc, Func<int> allocId, CancellationToken ct)
    {
        var advances = new Dictionary<int, float> { [0] = border };
        float total = border;
        int previous = 0;
        foreach (var pos in positions.Where(p => p > 0).OrderBy(p => p))
        {
            total += await MeasureSegmentAdvanceAsync(
                text[previous..pos], measureTags, sentinelBounds, resX, ipc, allocId, ct);
            advances[pos] = total;
            previous = pos;
        }
        return advances;
    }

    /// A long prefix can reach mpv's right edge before the text itself wraps. Once that happens
    /// compute_bounds reports the edge rather than the pen position, collapsing the last tokens
    /// to zero width. Measure bounded spans and accumulate them instead; sentinel glyphs on both
    /// sides keep leading/trailing whitespace and glyph overhangs in the measured advance.
    private static async Task<float> MeasureSegmentAdvanceAsync(
        string text, string measureTags, OverlayBounds? sentinelBounds, float resX,
        MpvIpcClient ipc, Func<int> allocId, CancellationToken ct)
    {
        if (text.Length == 0) return 0;

        var bounds = await ipc.MeasureOverlayAsync(
            allocId(), $"{measureTags}{SentinelGlyph}{AssTagBuilder.EscapeText(text)}{SentinelGlyph}", ct);
        bool fits = bounds is not null
                    && bounds.X0 >= 0
                    && bounds.X1 < resX - WrapTolerance;
        if (fits)
        {
            return sentinelBounds is { } sentinel
                ? (float)(bounds!.X1 - sentinel.X1)
                : (float)(bounds!.X1 - MeasureOrigin);
        }

        int split = FindTextSplit(text);
        if (split <= 0 || split >= text.Length)
            return 0;

        return await MeasureSegmentAdvanceAsync(
                   text[..split], measureTags, sentinelBounds, resX, ipc, allocId, ct)
             + await MeasureSegmentAdvanceAsync(
                   text[split..], measureTags, sentinelBounds, resX, ipc, allocId, ct);
    }

    private static int FindTextSplit(string text)
    {
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        if (starts.Length < 2) return 0;

        int midpoint = text.Length / 2;
        return starts
            .Where(start => start > 0 && start < text.Length)
            .OrderBy(start => Math.Abs(start - midpoint))
            .FirstOrDefault();
    }

    private static List<(string Text, int StartIdx)> SplitWrappedLine(
        string text, IReadOnlyDictionary<int, OverlayBounds?> prefixBounds, double noWrapHeight)
    {
        var lines = new List<(string, int)>();
        int lineStart = 0;
        double maxHeight = noWrapHeight;

        for (int p = 1; p <= text.Length; p++)
        {
            if (prefixBounds[p] is not { } bounds
                || bounds.Height <= maxHeight + WrapTolerance
                || p - 1 <= lineStart)
                continue;

            int nextLineStart = p - 1;
            lines.Add((text[lineStart..nextLineStart], lineStart));
            lineStart = nextLineStart;
            maxHeight = bounds.Height;
        }

        if (lineStart < text.Length)
            lines.Add((text[lineStart..], lineStart));

        return lines;
    }

    private static List<(string Text, int StartIdx)> SplitLines(string text)
    {
        var lines = new List<(string, int)>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                lines.Add((text[start..i], start));
                start = i + 1;
            }
        }
        return lines;
    }
}
