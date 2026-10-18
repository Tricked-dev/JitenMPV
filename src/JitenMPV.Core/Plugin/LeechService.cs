using JitenMPV.Core.Api;
using JitenMPV.Core.Api.Models;
using Microsoft.Extensions.Logging;

namespace JitenMPV.Core.Plugin;

/// The parse response carries no lapse counts, so leeches come from the card list. Only the first
/// load is awaited; later refreshes run in the background so a line never waits on the card list.
public sealed class LeechService(JitenApiClient api, ILogger logger)
{
    private const long RefreshIntervalMs = 5 * 60 * 1000;
    private const long RetryIntervalMs = 60 * 1000;

    /// Jiten's mature threshold, which a leech has to recover past to stop being one.
    private const double MatureStabilityDays = 21;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile HashSet<(int WordId, byte ReadingIndex)>? _leeches;
    private long _refreshAt;

    public HashSet<(int WordId, byte ReadingIndex)>? Current => _leeches;

    public void Invalidate() => Volatile.Write(ref _refreshAt, 0);

    public void Reset()
    {
        _leeches = null;
        Invalidate();
    }

    public async Task<HashSet<(int WordId, byte ReadingIndex)>?> GetLeechesAsync(CancellationToken ct)
    {
        if (Environment.TickCount64 >= Volatile.Read(ref _refreshAt))
        {
            if (_leeches is null)
                await RefreshAsync(wait: true, ct);
            else
                _ = TaskHelper.RunSafe(() => RefreshAsync(wait: false, CancellationToken.None), logger, "Leech refresh");
        }
        return _leeches;
    }

    /// Mirrors Jiten's LeechHelper, plus the cards page leaving out mastered and blacklisted cards.
    public static bool IsLeech(VocabularyCard card, int leechThreshold) =>
        leechThreshold > 0
        && card.Lapses >= leechThreshold
        && (card.Stability ?? 0) < MatureStabilityDays
        && card.State is not (FsrsState.Mastered or FsrsState.Blacklisted);

    private async Task RefreshAsync(bool wait, CancellationToken ct)
    {
        if (wait)
            await _gate.WaitAsync(ct);
        else if (!await _gate.WaitAsync(0, ct))
            return;

        try
        {
            if (Environment.TickCount64 < Volatile.Read(ref _refreshAt)) return;

            try
            {
                var threshold = (await api.GetStudySettingsAsync(ct))?.LeechThreshold ?? 0;
                var cards = await api.GetVocabularyCardsAsync(ct);
                _leeches = cards.Where(c => IsLeech(c, threshold))
                    .Select(c => (c.WordId, c.ReadingIndex)).ToHashSet();
                Volatile.Write(ref _refreshAt, Environment.TickCount64 + RefreshIntervalMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keeps the last list, and backs off so an unreachable API is not asked on every line.
                logger.LogWarning("Could not refresh leeches ({Error})", ex.GetType().Name);
                Volatile.Write(ref _refreshAt, Environment.TickCount64 + RetryIntervalMs);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
