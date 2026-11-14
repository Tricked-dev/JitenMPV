using System.Numerics;
using JitenMPV.Core.Subtitles;

namespace JitenMPV.Core.Sync;

public sealed record SubtitleSyncWindow(double Start, byte[] Speech, IReadOnlyList<SubtitleCue> Reference)
{
    public double End => Start + Speech.Length / 50.0;
}

public sealed record SubtitleSyncPeak(double Offset, double Correlation, double Margin, double Start);

public sealed record SubtitleSyncDecision(bool Accepted, double Offset, string Method, string Reason,
    IReadOnlyList<SubtitleSyncPeak> Peaks);

public static class SubtitleOffsetAligner
{
    private const int Rate = 50;

    public static SubtitleSyncDecision Align(IReadOnlyList<SubtitleCue> candidate,
        IReadOnlyList<SubtitleSyncWindow> windows, CancellationToken ct = default)
    {
        if (candidate.Count < 12) return Reject("Not enough dialogue");
        var valid = windows.Where(w => w.Speech.Length >= 1500 && Transitions(w.Speech) >= 6).ToList();
        if (valid.Count < 2) return Reject("Collecting more dialogue");
        var calibration = new List<SubtitleSyncPeak>();
        foreach (var window in valid.Where(w => w.Reference.Count >= 6))
        {
            ct.ThrowIfCancellationRequested();
            var peak = Search(window.Reference, window, false, 2, ct);
            if (Good(peak, .25, .02) && Math.Abs(peak.Offset) < 1.98) calibration.Add(peak);
        }
        var calibrated = Consensus(calibration, 2, .4);
        if (calibrated is { } referenceOffset)
        {
            var matches = valid.Where(w => w.Reference.Count >= 6)
                .Select(w => Search(candidate, w, true, 120, ct)).ToList();
            var strong = matches.Where(p => Good(p, .45, .025) && Math.Abs(p.Offset) < 119.98).ToList();
            if (Consensus(strong, 2, .4) is { } candidateOffset)
            {
                if (strong.Any(p => Math.Abs(p.Offset - candidateOffset) > .6))
                    return new(false, 0, "reference", "Timing changes across dialogue sections", strong);
                return new(true, Math.Round(candidateOffset + referenceOffset, 3), "reference",
                    "Validated against embedded dialogue and audio", strong);
            }
        }
        var audioPeaks = valid.Select(w => Search(candidate, w, false, 120, ct))
            .Where(p => Good(p, .35, .035) && Math.Abs(p.Offset) < 119.98).ToList();
        if (Consensus(audioPeaks, 3, .4) is { } offset && audioPeaks.All(p => Math.Abs(p.Offset - offset) <= .6))
            return new(true, Math.Round(offset, 3), "audio", "Validated across three speech sections", audioPeaks);
        return new(false, 0, "none", "Sync uncertain; kept original timing", audioPeaks);
    }

    private static SubtitleSyncDecision Reject(string reason) => new(false, 0, "none", reason, []);

    private static bool Good(SubtitleSyncPeak peak, double correlation, double margin)
        => peak.Correlation >= correlation && peak.Margin >= margin;

    private static double? Consensus(IReadOnlyList<SubtitleSyncPeak> peaks, int minimum, double tolerance)
    {
        var best = peaks.Select(p => peaks.Where(q => Math.Abs(q.Offset - p.Offset) <= tolerance).ToList())
            .OrderByDescending(group => group.Count).ThenByDescending(group => group.Sum(p => p.Correlation))
            .FirstOrDefault();
        if (best is null || best.Count < minimum) return null;
        var offsets = best.Select(p => p.Offset).Order().ToArray();
        return offsets.Length % 2 == 0 ? (offsets[offsets.Length / 2 - 1] + offsets[offsets.Length / 2]) / 2
            : offsets[offsets.Length / 2];
    }

    public static SubtitleSyncPeak Search(IReadOnlyList<SubtitleCue> candidate, SubtitleSyncWindow window,
        bool useReference, double maximumOffset, CancellationToken ct = default)
    {
        var n = window.Speech.Length;
        if (n == 0) return new(0, -1, 0, window.Start);
        var padding = (int)Math.Ceiling(maximumOffset * Rate) + 64;
        var input = Mask(candidate, window.Start - padding / (double)Rate, n + padding * 2);
        var reference = useReference ? Mask(window.Reference, window.Start, n) : Bits(window.Speech);
        var positives = reference.Sum(word => (double)BitOperations.PopCount(word));
        if (positives < n * .05 || positives > n * .95) return new(0, -1, 0, window.Start);
        var limit = (int)Math.Round(maximumOffset * Rate);
        var scores = new double[limit * 2 + 1];
        var best = 0;
        for (var shift = -limit; shift <= limit; shift++)
        {
            if ((shift & 127) == 0) ct.ThrowIfCancellationRequested();
            var sourceStart = padding - shift;
            double candidateCount = 0, overlap = 0;
            for (var word = 0; word < reference.Length; word++)
            {
                var bits = Read(input, sourceStart + word * 64);
                if (word == reference.Length - 1 && n % 64 != 0) bits &= (1UL << (n % 64)) - 1;
                candidateCount += BitOperations.PopCount(bits);
                overlap += BitOperations.PopCount(bits & reference[word]);
            }
            var denominator = Math.Sqrt(candidateCount * (n - candidateCount) * positives * (n - positives));
            var score = denominator > 0 ? (n * overlap - candidateCount * positives) / denominator : -1;
            var index = shift + limit;
            scores[index] = score;
            if (score > scores[best] || index == 0) best = index;
        }
        var competing = -1.0;
        for (var i = 0; i < scores.Length; i++)
            if (Math.Abs(i - best) > Rate / 2) competing = Math.Max(competing, scores[i]);
        return new((best - limit) / (double)Rate, scores[best], scores[best] - competing, window.Start);
    }

    private static int Transitions(byte[] samples)
    {
        var count = 0;
        for (var i = 1; i < samples.Length; i++) if (samples[i] != samples[i - 1]) count++;
        return count;
    }

    private static ulong Read(ulong[] bits, int start)
    {
        var word = start / 64;
        var shift = start % 64;
        var value = word < bits.Length ? bits[word] >> shift : 0;
        if (shift != 0 && word + 1 < bits.Length) value |= bits[word + 1] << (64 - shift);
        return value;
    }

    private static ulong[] Bits(byte[] samples)
    {
        var bits = new ulong[(samples.Length + 63) / 64];
        for (var i = 0; i < samples.Length; i++) if (samples[i] != 0) bits[i / 64] |= 1UL << (i % 64);
        return bits;
    }

    private static ulong[] Mask(IReadOnlyList<SubtitleCue> cues, double start, int count)
    {
        var bits = new ulong[(count + 63) / 64];
        foreach (var cue in cues)
        {
            var a = (int)Math.Clamp(Math.Ceiling((cue.Start.TotalSeconds - start) * Rate - 1e-7), 0, count);
            var b = (int)Math.Clamp(Math.Ceiling((cue.End.TotalSeconds - start) * Rate - 1e-7), 0, count);
            for (var i = a; i < b; i++) bits[i / 64] |= 1UL << (i % 64);
        }
        return bits;
    }
}
