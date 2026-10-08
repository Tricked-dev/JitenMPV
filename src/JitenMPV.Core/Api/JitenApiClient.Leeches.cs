using System.Text.Json.Serialization;
using JitenMPV.Core.Api.Models;
using Microsoft.Extensions.Logging;

namespace JitenMPV.Core.Api;

public sealed partial class JitenApiClient
{
    public bool HighlightLeeches { get; set; }
    private readonly SemaphoreSlim _leechGate = new(1, 1);
    private HashSet<(int WordId, byte ReadingIndex)> _leeches = [];
    private DateTimeOffset _leechRefreshAt = DateTimeOffset.MinValue;

    public void InvalidateLeeches() => _leechRefreshAt = DateTimeOffset.MinValue;

    public async Task ApplyLeechStatusAsync(IEnumerable<ReaderWord> words, CancellationToken ct)
    {
        if (!HighlightLeeches)
        {
            foreach (var word in words) word.IsLeech = false;
            return;
        }

        await _leechGate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow >= _leechRefreshAt)
            {
                try
                {
                    // Personal theme rule: underline only suspended cards with 3+ lapses.
                    // Reader parsing omits lapses, so cache them from the read-only cards API.
                    var cards = await GetAsync<List<LeechCard>>(
                        "/api/user/vocabulary/cards", "LeechCards", ct);
                    if (cards is null)
                        throw new InvalidOperationException("Missing card data");
                    _leeches = cards.Where(card => card.IsSuspendedLeech)
                        .Select(card => (card.WordId, card.ReadingIndex)).ToHashSet();
                    _leechRefreshAt = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep subtitles usable and retain the last successful index on a transient
                    // failure. Back off so unavailable card data doesn't cause per-line requests.
                    _logger.LogWarning("Leech highlighting could not refresh ({Error})", ex.GetType().Name);
                    _leechRefreshAt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            foreach (var word in words)
                word.IsLeech = _leeches.Contains((word.WordId, word.ReadingIndex));
        }
        finally { _leechGate.Release(); }
    }

    private sealed class LeechCard
    {
        [JsonPropertyName("wordId")]
        public int WordId { get; set; }
        [JsonPropertyName("readingIndex")]
        public byte ReadingIndex { get; set; }
        [JsonPropertyName("lapses")]
        public int Lapses { get; set; }
        [JsonPropertyName("state")]
        public FsrsState State { get; set; }

        public bool IsSuspendedLeech => State == FsrsState.Suspended && Lapses >= 3;
    }
}
