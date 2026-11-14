using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JitenMPV.Core.Config;
using JitenMPV.Core.Media;
using JitenMPV.Core.MediaInfo;
using JitenMPV.Core.Mpv;
using JitenMPV.Core.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

namespace JitenMPV.Core.Sync;

public sealed record SubtitleSyncEvidence(string MediaPath, string AudioIdentity,
    IReadOnlyList<SubtitleSyncWindow> Windows, string? Error = null);

public interface ISubtitleSyncEvidenceSource
{
    Task<SubtitleSyncEvidence> ReadAsync(MpvIpcClient ipc, PluginSettings settings, CancellationToken ct);
}

public sealed partial class SubtitleSyncEvidenceSource : ISubtitleSyncEvidenceSource
{
    public Task<SubtitleSyncEvidence> ReadAsync(MpvIpcClient ipc, PluginSettings settings, CancellationToken ct)
        => ReadAsync(ipc, settings, ct, 0);

    private async Task<SubtitleSyncEvidence> ReadAsync(MpvIpcClient ipc, PluginSettings settings, CancellationToken ct, int attempt)
    {
        var path = await ipc.GetPropertyAsync<string>("path", ct) ?? "";
        var source = await ipc.GetPropertyAsync<string>("stream-open-filename", ct) ?? path;
        var duration = await ipc.GetPropertyAsync<double?>("duration", ct) ?? 0;
        var demuxerStart = await ipc.GetPropertyAsync<double?>("demuxer-start-time", ct) ?? 0;
        var list = await ipc.GetPropertyRawAsync("track-list", ct);
        var tracks = list is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
        var audio = tracks.FirstOrDefault(t => Text(t, "type") == "audio" && Flag(t, "selected"));
        var audioIndex = Index(audio);
        var reference = tracks.Where(t => Text(t, "type") == "sub" && !Flag(t, "external") && !Flag(t, "forced")
                && !Bitmap(Text(t, "codec")) && !SignsOnly().IsMatch(Text(t, "title")))
            .OrderByDescending(t => ReferenceScore(t)).FirstOrDefault();
        var referenceIndex = Index(reference);
        var audioIdentity = audioIndex + ":" + Text(audio, "external-filename");
        if (path.Length == 0 || audioIndex is null || duration <= 0)
        {
            if (attempt < 10)
            {
                await Task.Delay(500, ct);
                return await ReadAsync(ipc, settings, ct, attempt + 1);
            }
            return new(path, audioIdentity, [], "Waiting for a complete media identity");
        }
        if (duration < 180 || !double.IsFinite(duration))
            return new(path, audioIdentity, [], "Not enough media for independent dialogue sections");
        var ffmpeg = await FfmpegLocator.ResolveAsync(settings.FfmpegPath, ct);
        var helper = FindVadHelper();
        if (ffmpeg is null || helper is null)
            return new(path, audioIdentity, [], "Speech detector or FFmpeg unavailable");
        var headers = await InputOptionsAsync(ipc, source, ct);
        var directory = Path.Combine(AppPaths.CacheDir, "sync", "capture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var windows = new List<SubtitleSyncWindow>();
        try
        {
            var length = Math.Min(90, duration / 6);
            var positions = (referenceIndex is not null ? new[] { .25, .5, .75, .37, .63 } : new[] { .25, .5, .75 }).Select(p => Math.Clamp(duration * p - length / 2, 10, duration - length - 10));
            var separateAudio = Flag(audio, "external") ? Text(audio, "external-filename") : "";
            foreach (var position in positions)
            {
                ct.ThrowIfCancellationRequested();
                if (windows.Count >= 3 && SubtitleOffsetAligner.Align(
                        windows.SelectMany(w => w.Reference).Distinct().ToList(), windows, ct).Accepted) break;
                if (await ipc.GetPropertyAsync<string>("path", ct) != path) throw new OperationCanceledException(ct);
                var pcm = Path.Combine(directory, "audio.pcm");
                var ass = Path.Combine(directory, "reference.ass");
                var start = position + demuxerStart;
                var end = start + length;
                var args = new List<string>(headers)
                {
                    "-ss", Number(start), "-seek_timestamp", "1", "-copyts", "-i", source
                };
                var audioInput = "0:" + audioIndex;
                if (separateAudio.Length > 0)
                {
                    args.AddRange(headers);
                    args.AddRange(["-ss", Number(start), "-seek_timestamp", "1", "-copyts", "-i", separateAudio]);
                    audioInput = "1:a:0";
                }
                args.AddRange(["-map", audioInput, "-to", Number(end), "-ac", "1", "-ar", "16000",
                    "-af", "asetnsamples=n=16000:p=0,ashowinfo", "-f", "s16le", pcm]);
                if (referenceIndex is not null)
                    args.AddRange(["-map", "0:" + referenceIndex, "-to", Number(end), "-f", "ass", ass]);
                var runner = new FfmpegRunner(ffmpeg.ExecutablePath, NullLogger.Instance);
                var result = await runner.RunAsync(args, TimeSpan.FromSeconds(25), ct);
                if (!result.Succeeded || !File.Exists(pcm)) continue;
                var firstFrame = FirstAudioTimestamp().Match(result.Stderr);
                if (!firstFrame.Success || !double.TryParse(firstFrame.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var firstPts)) continue;
                var speech = await DetectAsync(helper, pcm, ct);
                var expectedLength = (int)Math.Ceiling(length * 50) + 100;
                if (speech.Length < 1500 || speech.Length > expectedLength) continue;
                var actualStart = firstPts - demuxerStart;
                List<SubtitleCue> cues = [];
                if (referenceIndex is not null && File.Exists(ass))
                    cues = [.. SubtitleSyncDocument.Dialogue(await File.ReadAllTextAsync(ass, ct), ".ass")
                        .Select(c => c with { Start = c.Start - TimeSpan.FromSeconds(demuxerStart), End = c.End - TimeSpan.FromSeconds(demuxerStart) })
                        .Where(c => c.End.TotalSeconds > actualStart && c.Start.TotalSeconds < actualStart + speech.Length / 50.0)];
                windows.Add(new(actualStart, speech, cues));
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
        return new(path, audioIdentity, windows, windows.Count >= 2 ? null : "Not enough independently timed dialogue sections");
    }

    internal static async Task<List<string>> InputOptionsAsync(MpvIpcClient ipc, string source, CancellationToken ct)
    {
        var args = new List<string>();
        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return args;
        var headers = await ipc.GetPropertyRawAsync("http-header-fields", ct);
        if (headers is { ValueKind: JsonValueKind.Array } fields && fields.GetArrayLength() > 0)
            args.AddRange(["-headers", string.Join("\r\n", fields.EnumerateArray().Select(f => f.GetString())) + "\r\n"]);
        foreach (var (property, option) in new[] { ("user-agent", "-user_agent"), ("referrer", "-referer") })
            if (await ipc.GetPropertyAsync<string>(property, ct) is { Length: > 0 } value) args.AddRange([option, value]);
        return args;
    }

    private static async Task<byte[]> DetectAsync(string helper, string pcm, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        process.Start();
        try
        {
            using var output = new MemoryStream();
            var read = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await using (var input = File.OpenRead(pcm)) await input.CopyToAsync(process.StandardInput.BaseStream, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await read;
            await errors;
            return process.ExitCode == 0 ? output.ToArray() : [];
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return [];
        }
    }

    private static string? FindVadHelper()
    {
        var configured = Environment.GetEnvironmentVariable("JITEN_MPV_VAD_HELPER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var executable = Environment.ProcessPath;
        if (executable is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(executable)!, AppPaths.ExecutableName("jiten-mpv-vad"));
            if (File.Exists(sibling)) return sibling;
        }
        return null;
    }

    private static bool Bitmap(string codec) => codec.Contains("pgs", StringComparison.OrdinalIgnoreCase)
        || codec.Contains("dvd", StringComparison.OrdinalIgnoreCase) || codec.Contains("dvb", StringComparison.OrdinalIgnoreCase);
    private static int ReferenceScore(JsonElement track)
    {
        var title = Text(track, "title").ToLowerInvariant();
        return (title.Contains("full") || title.Contains("dialog") ? 20 : 0)
            + (Flag(track, "default") ? 5 : 0) + (Text(track, "lang") is "en" or "eng" ? 3 : 0);
    }
    private static int? Index(JsonElement track) => track.ValueKind == JsonValueKind.Object
        && track.TryGetProperty("ff-index", out var value) && value.TryGetInt32(out var index) && index >= 0 ? index : null;
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool Flag(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"ashowinfo[^\r\n]*?\bn:\s*0\s[^\r\n]*?\bpts_time:\s*([-+0-9.eE]+)")]
    private static partial Regex FirstAudioTimestamp();
    [GeneratedRegex(@"(?i)^(?!.*(?:full|dialog)).*(?:sign|song|forced|commentary)")]
    private static partial Regex SignsOnly();
}
