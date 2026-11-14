using System.Security.Cryptography;
using System.Text;
using JitenMPV.Core.Config;
using JitenMPV.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace JitenMPV.Core.Sync;

public sealed record PreparedSubtitle(string Path, string Original, SubtitleSyncDecision Decision);

public sealed class SubtitleSyncService(MpvIpcClient ipc, PluginSettings settings, ILogger logger,
    ISubtitleSyncEvidenceSource? evidenceSource = null, string? cacheDirectory = null) : IDisposable
{
    private readonly ISubtitleSyncEvidenceSource _source = evidenceSource ?? new SubtitleSyncEvidenceSource();
    private readonly string _cache = cacheDirectory ?? CacheDirectory;
    private readonly Lock _lock = new();
    private PluginSettings _settings = settings;
    private CancellationTokenSource? _fileCts;
    private Task<SubtitleSyncEvidence>? _evidence;
    private readonly Dictionary<string, PreparedSubtitle> _prepared = [];
    private readonly Dictionary<string, string> _originals = [];
    private readonly Dictionary<string, double> _manualDelays = [];
    private string _status = "Synchronization has not run yet";

    public static string CacheDirectory => Path.Combine(AppPaths.CacheDir, "sync");
    public string Status { get { lock (_lock) return _status; } }
    public bool Enabled => _settings.SubtitleAutoSync;

    public void UpdateSettings(PluginSettings next) => _settings = next;

    public void OnFileChanged(CancellationToken lifetime)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        lock (_lock)
        {
            var previous = _fileCts;
            _fileCts = next;
            _evidence = null;
            _prepared.Clear();
            _manualDelays.Clear();
            _status = "Preparing dialogue timing";
            TaskHelper.CancelAndDispose(ref previous);
        }
        if (!Enabled) return;
        _ = TaskHelper.RunSafe(async () =>
        {
            await Task.Delay(1200, next.Token);
            await EvidenceAsync(next.Token);
        }, logger, "Prepare subtitle sync");
    }

    private Task<SubtitleSyncEvidence> EvidenceAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_evidence is { IsCompletedSuccessfully: true } && _evidence.Result.Error is not null) _evidence = null;
            return _evidence ??= CaptureAsync(_fileCts?.Token ?? ct);
        }
    }

    private async Task<SubtitleSyncEvidence> CaptureAsync(CancellationToken ct)
    {
        try { return await _source.ReadAsync(ipc, _settings, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Subtitle sync could not obtain dialogue timing ({Kind})", ex.GetType().Name);
            return new("", "", [], "Could not read dialogue timing");
        }
    }

    public async Task<PreparedSubtitle> PrepareAsync(string file, CancellationToken ct, bool force = false)
    {
        if (!Enabled && !force) return new(file, file, new(false, 0, "none", "Automatic synchronization disabled", []));
        CancellationToken fileToken;
        lock (_lock) fileToken = _fileCts?.Token ?? ct;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, fileToken);
        ct = attempt.Token;
        var path = await ipc.GetPropertyAsync<string>("path", ct);
        var evidence = await EvidenceAsync(ct).WaitAsync(ct);
        if ((evidence.Windows.Count > 0 && path != evidence.MediaPath) || path != await ipc.GetPropertyAsync<string>("path", ct))
            throw new OperationCanceledException(ct);
        var original = Original(file);
        var content = await File.ReadAllTextAsync(original, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var key = original + ":" + hash;
        lock (_lock)
            if (_prepared.TryGetValue(key, out var remembered)) return remembered;
        var cues = SubtitleSyncDocument.Dialogue(content, Path.GetExtension(original));
        var decision = evidence.Error is not null
            ? new SubtitleSyncDecision(false, 0, "none", evidence.Error, [])
            : await Task.Run(() => SubtitleOffsetAligner.Align(cues, evidence.Windows, ct), ct);
        ct.ThrowIfCancellationRequested();
        var output = original;
        if (decision.Accepted)
        {
            var fingerprint = hash + ":" + evidence.MediaPath + ":" + evidence.AudioIdentity + ":"
                + string.Join(',', evidence.Windows.Select(w => w.Start)) + ":" + decision.Offset;
            var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            Directory.CreateDirectory(_cache);
            output = Path.Combine(_cache, name + Path.GetExtension(original));
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, SubtitleSyncDocument.Shift(content,
                    Path.GetExtension(original), decision.Offset), ct);
                File.Move(temporary, output, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        var result = new PreparedSubtitle(output, original, decision);
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            _prepared[key] = result;
            _originals[output] = original;
            _status = decision.Accepted ? $"Synced {decision.Offset:+0.00;-0.00;0.00}s ({decision.Method})" : decision.Reason;
        }
        return result;
    }

    public string Original(string file)
    {
        lock (_lock) return _originals.GetValueOrDefault(file, file);
    }

    public async Task RememberCurrentDelayAsync(CancellationToken ct)
    {
        var key = await SelectionKeyAsync(ct);
        if (key is null) return;
        var delay = await ipc.GetPropertyAsync<double?>("sub-delay", ct) ?? 0;
        lock (_lock) _manualDelays[key] = delay;
    }

    public async Task RestoreCurrentDelayAsync(CancellationToken ct)
    {
        var key = await SelectionKeyAsync(ct);
        if (key is null) return;
        double delay;
        lock (_lock) delay = _manualDelays.GetValueOrDefault(key);
        await ipc.SetPropertyAsync("sub-delay", delay, ct);
    }

    private async Task<string?> SelectionKeyAsync(CancellationToken ct)
    {
        var external = await ipc.GetPropertyAsync<string>("current-tracks/sub/external-filename", ct);
        if (!string.IsNullOrEmpty(external)) return "file:" + Original(external);
        var sid = await ipc.GetPropertyRawAsync("sid", ct);
        return sid is { ValueKind: System.Text.Json.JsonValueKind.Number } selected ? "track:" + selected.GetInt32() : null;
    }

    public async Task<string> SyncCurrentAsync(CancellationToken ct)
    {
        using var attempt = LinkCurrentFile(ct);
        ct = attempt.Token;
        var file = await ipc.GetPropertyAsync<string>("current-tracks/sub/external-filename", ct);
        if (string.IsNullOrEmpty(file)) return "Select an external subtitle to synchronize";
        lock (_lock) _prepared.Clear();
        var language = await ipc.GetPropertyAsync<string>("current-tracks/sub/lang", ct) ?? "";
        var result = await PrepareAsync(file, ct, true);
        if (!result.Decision.Accepted) return Status;
        if (await ipc.GetPropertyAsync<string>("current-tracks/sub/external-filename", ct) != file)
            return "Subtitle selection changed; timing unchanged";
        await RememberCurrentDelayAsync(ct);
        var error = await ipc.SubAddAsync(result.Path, "select", "Synced subtitles", language, ct);
        if (error is null) await RestoreCurrentDelayAsync(ct);
        return error is null ? Status : "MPV could not load the synchronized subtitles";
    }

    public async Task<string> UndoCurrentAsync(CancellationToken ct)
    {
        using var attempt = LinkCurrentFile(ct);
        ct = attempt.Token;
        var file = await ipc.GetPropertyAsync<string>("current-tracks/sub/external-filename", ct);
        if (string.IsNullOrEmpty(file) || Original(file) == file) return "Current subtitles use original timing";
        var language = await ipc.GetPropertyAsync<string>("current-tracks/sub/lang", ct) ?? "";
        await RememberCurrentDelayAsync(ct);
        var error = await ipc.SubAddAsync(Original(file), "select", "Original subtitle timing", language, ct);
        if (error is null) await RestoreCurrentDelayAsync(ct);
        return error is null ? "Restored original subtitle timing" : "MPV could not restore original timing";
    }

    private CancellationTokenSource LinkCurrentFile(CancellationToken ct)
    {
        lock (_lock) return CancellationTokenSource.CreateLinkedTokenSource(ct, _fileCts?.Token ?? ct);
    }

    public void Dispose()
    {
        lock (_lock) TaskHelper.CancelAndDispose(ref _fileCts);
    }
}
