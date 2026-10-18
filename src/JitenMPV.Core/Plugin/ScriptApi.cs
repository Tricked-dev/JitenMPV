using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JitenMPV.Core.Api;
using JitenMPV.Core.Api.Models;
using JitenMPV.Core.Interaction;
using JitenMPV.Core.Mpv;
using JitenMPV.Core.Tracking;
using Microsoft.Extensions.Logging;

namespace JitenMPV.Core.Plugin;

/// Lets other mpv scripts read the current match and act on the Jiten list, see "Script messages" in the README.
public sealed partial class ScriptApi(
    MpvIpcClient ipc, IMediaDeckApi api, EpisodeTracker tracker, StatusOverlay status, ILogger logger)
{
    internal const string MediaProperty = "user-data/jiten/media";
    private const int Version = 1;

    private static readonly HashSet<string> Commands =
    [
        "jiten-media-set", "jiten-media-rematch", "jiten-lookup", "jiten-deck-status", "jiten-mark-episode"
    ];

    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private int _endedVersion = -1;
    private bool _published;

    public static bool IsCommand(string message) => Commands.Contains(message);

    public void Attach(CancellationToken ct)
    {
        tracker.MatchChanged += media => _ = TaskHelper.RunSafe(() => OnMatchChangedAsync(media, ct), logger, "Publish media");
        tracker.EpisodeWatched += media => _ = TaskHelper.RunSafe(() => OnWatchedAsync(media, ct), logger, "Publish watched");
        ipc.FileEnded += reason => _ = TaskHelper.RunSafe(() => ClearAsync(ct), logger, "Clear media");
    }

    public async Task HandleAsync(string message, string[] args, CancellationToken ct)
    {
        switch (message)
        {
            case "jiten-media-set" when args.Length >= 1:
                await SetMediaAsync(args[0], args.Length >= 2 && int.TryParse(args[1], out var episode) ? episode : null, ct);
                break;
            case "jiten-media-rematch":
                await tracker.OpenPickerAsync(ct);
                break;
            case "jiten-lookup" when args.Length >= 3:
                await ReplyAsync(args[0], args[1], args[2], ct);
                break;
            case "jiten-deck-status" when args.Length >= 2:
                await SetStatusAsync(args[0], args[1], ct);
                break;
            case "jiten-mark-episode" when args.Length >= 2
                                           && int.TryParse(args[0], out var deckId)
                                           && int.TryParse(args[1], out var number):
                await tracker.MarkEpisodeAsync(deckId, number, $"Deck {deckId}");
                await RefreshAsync(deckId, ct);
                break;
            default:
                await WarnAsync($"Jiten: {message} has invalid arguments: {string.Join(' ', args)}", ct);
                break;
        }
    }

    private async Task SetMediaAsync(string reference, int? episode, CancellationToken ct)
    {
        if (await FindDeckAsync(reference, ct) is not { } deck)
            await WarnAsync($"Jiten: {reference} matches no Jiten anime", ct);
        else if (!await tracker.SetMatchAsync(deck, episode, ct))
            await WarnAsync($"Jiten: cannot match {reference}, nothing trackable is playing", ct);
    }

    private async Task ReplyAsync(string replyTo, string requestId, string reference, CancellationToken ct)
    {
        var reply = await FindDeckAsync(reference, ct) is { } deck
            ? Describe(deck)
            : new JsonObject { ["error"] = "not found" };
        reply["api"] = Version;
        await ipc.SendScriptMessageAsync(replyTo, "jiten-reply", requestId, reply.ToJsonString(), ct);
    }

    private async Task SetStatusAsync(string deck, string name, CancellationToken ct)
    {
        if (!int.TryParse(deck, out var deckId)
            || !Enum.TryParse<DeckStatus>(name, ignoreCase: true, out var value)
            || !Enum.IsDefined(value))
        {
            await WarnAsync($"Jiten: invalid deck status {deck} {name}", ct);
            return;
        }

        var changed = await api.SetDeckStatusAsync(deckId, value, ct);
        await status.LogAsync(ipc, $"Jiten: deck {deckId} {(changed ? "set to" : "was already")} {Name(value)}", ct);
        await RefreshAsync(deckId, ct);
    }

    /// Jiten's link index answers with deck ids only, which can include manga sharing the number.
    private async Task<MediaDeck?> FindDeckAsync(string reference, CancellationToken ct)
    {
        var parts = reference.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var id) || id <= 0) return null;

        IReadOnlyList<int> ids = parts[0].ToLowerInvariant() switch
        {
            "deck" => [id],
            "anilist" => await api.FindDecksByLinkAsync(MediaDeckLink.AniList, parts[1], ct),
            "mal" => await api.FindDecksByLinkAsync(MediaDeckLink.Mal, parts[1], ct),
            _ => []
        };

        var decks = new List<MediaDeck>();
        foreach (var deckId in ids.Distinct())
        {
            if (await api.GetDeckAsync(deckId, ct) is { MediaType: 1, ParentDeckId: null } deck)
                decks.Add(deck);
        }
        if (decks.Count > 1)
            await WarnAsync($"Jiten: {reference} links to {decks.Count} decks, not choosing one", ct);
        return decks.Count == 1 ? decks[0] : null;
    }

    private async Task OnMatchChangedAsync(TrackedMedia media, CancellationToken ct)
    {
        var node = await DescribeAsync(media, ct);
        await PublishAsync(media, node, ct);

        if (media.Match is { Kind: DeckMatchKind.Matched })
            await BroadcastAsync("jiten-media-resolved", node, ct);
        else if (media.Match is { Kind: DeckMatchKind.Ambiguous } match)
            await BroadcastAsync("jiten-match-ambiguous", new JsonObject
            {
                ["api"] = Version,
                ["file_title"] = media.Identity.Title,
                ["episode"] = media.Identity.Episode,
                ["candidates"] = new JsonArray([.. match.Candidates.Select(c =>
                {
                    var candidate = Describe(c.Deck);
                    candidate["score"] = Math.Round(c.Score, 3);
                    return (JsonNode)candidate;
                })])
            }, ct);
    }

    /// Sent after the end of a file too, so it goes out even when the property is already gone.
    private async Task OnWatchedAsync(TrackedMedia media, CancellationToken ct)
    {
        var node = await DescribeAsync(media, ct);
        await BroadcastAsync("jiten-episode-watched", node, ct);
        await PublishAsync(media, node, ct);
    }

    private async Task RefreshAsync(int deckId, CancellationToken ct)
    {
        if (tracker.Current is { Match.Deck: { } deck } media && deck.DeckId == deckId)
            await PublishAsync(media, await DescribeAsync(media, ct), ct);
    }

    private async Task<JsonObject> DescribeAsync(TrackedMedia media, CancellationToken ct)
    {
        var match = media.Match;
        var node = new JsonObject
        {
            ["api"] = Version,
            ["state"] = match?.Kind switch
            {
                null => "resolving",
                DeckMatchKind.Matched => "resolved",
                DeckMatchKind.Ambiguous => "ambiguous",
                _ => "unmatched"
            },
            ["source"] = media.Source.ToString().ToLowerInvariant()
        };
        if (match is not { Kind: DeckMatchKind.Matched, Deck: { } matched }) return node;

        // The search result behind a match carries no links or status; the detail page does.
        var deck = matched;
        DeckEpisodes? episodes = null;
        try
        {
            if (match.IsMovie)
            {
                deck = await api.GetDeckAsync(matched.DeckId, ct) ?? matched;
            }
            else
            {
                episodes = await api.GetEpisodesAsync(matched.DeckId, ct);
                deck = episodes.Deck;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read deck {DeckId} for scripts", matched.DeckId);
        }

        foreach (var (key, value) in Describe(deck))
            node[key] = value?.DeepClone();
        if (media.Identity.Season is { } season) node["season"] = season;
        if (match.Episode is { } number)
        {
            node["episode"] = number;
            if (episodes?.Find(number) is { } episode)
            {
                node["episode_deck_id"] = episode.DeckId;
                node["episode_status"] = Name(episode.Status);
                node["watched"] = episode.Status == DeckStatus.Completed;
            }
        }
        else if (match.IsMovie)
        {
            node["watched"] = deck.Status == (int)DeckStatus.Completed;
        }
        return node;
    }

    private static JsonObject Describe(MediaDeck deck)
    {
        var title = new JsonObject();
        if (deck.OriginalTitle is { } original) title["original"] = original;
        if (deck.RomajiTitle is { } romaji) title["romaji"] = romaji;
        if (deck.EnglishTitle is { } english) title["english"] = english;

        var links = new JsonObject();
        if (deck.LinkId(MediaDeckLink.AniList) is { } anilist) links["anilist"] = anilist;
        if (deck.LinkId(MediaDeckLink.Mal) is { } mal) links["mal"] = mal;
        if (TmdbReference(deck) is { } tmdb) links["tmdb"] = tmdb;

        return new JsonObject
        {
            ["deck_id"] = deck.DeckId,
            ["title"] = title,
            ["episode_count"] = deck.ChildrenDeckCount,
            ["links"] = links,
            ["status"] = Name(deck.Status is { } s && Enum.IsDefined(typeof(DeckStatus), s) ? (DeckStatus)s : DeckStatus.None)
        };
    }

    private static string Name(DeckStatus status) => status.ToString().ToLowerInvariant();

    private async Task PublishAsync(TrackedMedia media, JsonObject node, CancellationToken ct)
    {
        await _publishGate.WaitAsync(ct);
        try
        {
            if (!tracker.IsCurrent(media) || Volatile.Read(ref _endedVersion) == media.Version) return;
            await ipc.SetPropertyAsync(MediaProperty, node, ct);
            _published = true;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task ClearAsync(CancellationToken ct)
    {
        Volatile.Write(ref _endedVersion, tracker.Current?.Version ?? -1);
        await _publishGate.WaitAsync(ct);
        try
        {
            if (!_published) return;
            _published = false;
            await ipc.ClearUserDataAsync(MediaProperty, ct);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private Task WarnAsync(string message, CancellationToken ct)
    {
        logger.LogWarning("{Message}", message);
        return ipc.SendScriptMessageAsync("jiten_mpv", "jiten-log", "warn", message, ct);
    }

    private Task BroadcastAsync(string message, JsonObject node, CancellationToken ct)
        => ipc.BroadcastScriptMessageAsync(message, node.ToJsonString(), ct);

    /// "tv:55925" or "movie:758866", as Jimaku writes it: a TMDB id alone does not say which.
    private static string? TmdbReference(MediaDeck deck)
        => deck.Links?.Where(l => l.LinkType == MediaDeckLink.Tmdb)
            .Select(l => TmdbUrl().Match(l.Url ?? ""))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value}:{m.Groups[2].Value}")
            .FirstOrDefault();

    [GeneratedRegex(@"themoviedb\.org/(tv|movie)/(\d+)")]
    private static partial Regex TmdbUrl();
}
