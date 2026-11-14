using JitenMPV.Core.Api.Models;

namespace JitenMPV.Core.Tracking;

public sealed record SubtitleSuggestion(string Name, string Url);

public enum SubtitlePreference { Dialogue, Verbatim, Any }

public sealed record SubtitleShowChoice(MediaDeck Deck, int? Episode, SubtitlePreference Preference);

public sealed record EpisodePickerData(
    string FileTitle,
    int? Episode,
    int? CurrentDeckId,
    IReadOnlyList<DeckCandidate> Candidates,
    Func<string, CancellationToken, Task<IReadOnlyList<DeckCandidate>>> Search,
    SubtitlePreference SubtitlePreference = SubtitlePreference.Dialogue,
    Func<MediaDeck, int?, CancellationToken, Task<IReadOnlyList<SubtitleSuggestion>>>? FindSubtitles = null,
    Func<CancellationToken, Task<string>>? SyncSubtitles = null,
    Func<CancellationToken, Task<string>>? UndoSubtitleSync = null,
    string? SyncStatus = null);

/// <param name="Episode">The deck's episode this file is; null keeps the detected number.</param>
public sealed record EpisodePickerResult(MediaDeck? Deck, int? Episode, bool IgnoreFolder,
    SubtitlePreference SubtitlePreference = SubtitlePreference.Dialogue, string? SubtitleUrl = null);

public interface IEpisodePickerPresenter
{
    /// Returns null when the user closes the picker without choosing.
    Task<EpisodePickerResult?> ShowAsync(EpisodePickerData data, CancellationToken ct);
}
