using System.Text.Json;
using JitenMPV.Core.Config;
using JitenMPV.Core.MediaInfo;
using JitenMPV.Core.Tracking;

namespace JitenMPV.Core.Jimaku;

public sealed class SubtitleReleasePreferences(string path)
{
    private readonly Lock _lock = new();
    private Dictionary<string, string>? _entries;
    public static string DefaultPath => Path.Combine(AppPaths.ConfigDir, "subtitle-releases.json");

    public string? Get(MediaIdentity? identity)
    {
        var key = identity is null ? null : MediaMap.KeyFor(identity);
        lock (_lock) return key is null ? null : Load().GetValueOrDefault(key);
    }

    public void Set(MediaIdentity identity, string group)
    {
        var key = MediaMap.KeyFor(identity);
        if (key is null) return;
        lock (_lock)
        {
            if (Load().GetValueOrDefault(key) == group) return;
            Load()[key] = group;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_entries is not null) return _entries;
        try { _entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) : null; }
        catch (JsonException) { }
        return _entries ??= [];
    }
}
