using MusicBrainzSearch.Http;

namespace MusicBrainzSearch.MusicBrainz;

/// <summary>
/// Client MusicBrainz ws/2 respectant les règles d'usage :
/// User-Agent identifiable et 1 requête/seconde maximum.
/// </summary>
public sealed class MusicBrainzClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaxSearchResults = 500;
    private static readonly string[] ArtistTypes = ["person", "group", "orchestra", "choir", "character", "other"];
    private const string Includes = "inc=labels+media+release-groups+artist-credits";

    private readonly CachedHttpFetcher _fetcher;

    public int NetworkRequests => _fetcher.NetworkRequests;
    public int CacheHits => _fetcher.CacheHits;

    /// <summary>Recherches restées tronquées malgré le découpage (résultats potentiellement manquants).</summary>
    public List<string> TruncatedQueries { get; } = [];

    public MusicBrainzClient(string contact, string? cacheDir, TimeSpan cacheTtl)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MusicBrainzSearch/1.0 ( {contact} )");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _fetcher = new CachedHttpFetcher(http, TimeSpan.FromMilliseconds(1100), cacheDir, cacheTtl);
    }

    // --- Requêtes unitaires -------------------------------------------------

    public Task<MbArea> GetAreaAsync(string id, CancellationToken ct) =>
        Get<MbArea>($"area/{id}?inc=area-rels", ct);

    public Task<MbArtist> GetArtistAsync(string id, CancellationToken ct) =>
        Get<MbArtist>($"artist/{id}?inc=artist-rels+url-rels+tags+genres+ratings", ct);

    /// <summary>Retrouve l'artiste MusicBrainz relié à une URL externe (ex. page artiste Discogs).</summary>
    public async Task<MbArtist?> FindArtistByUrlAsync(string resource, CancellationToken ct)
    {
        var url = await _fetcher.TryGetAsync<MbUrlLookup>(
            $"url?resource={Uri.EscapeDataString(resource)}&inc=artist-rels&fmt=json", ct);
        return url?.Relations?.FirstOrDefault(r => r.Artist is not null)?.Artist;
    }

    // --- Requêtes paginées --------------------------------------------------

    /// <summary>
    /// Recherche Lucene d'artistes. MusicBrainz plafonne une recherche à 500 résultats :
    /// au-delà, la requête est découpée par type d'artiste.
    /// </summary>
    public async Task<List<MbArtist>> SearchArtistsAsync(string luceneQuery, CancellationToken ct)
    {
        var (artists, total) = await SearchArtistsCappedAsync(luceneQuery, ct);
        if (total <= MaxSearchResults) return artists;

        var all = new Dictionary<string, MbArtist>();
        var typeList = string.Join(" OR ", ArtistTypes);
        string[] partitions =
        [
            .. ArtistTypes.Select(t => $"({luceneQuery}) AND type:{t}"),
            $"({luceneQuery}) AND NOT type:({typeList})",
        ];
        foreach (var partition in partitions)
        {
            var (part, partTotal) = await SearchArtistsCappedAsync(partition, ct);
            if (partTotal > MaxSearchResults) TruncatedQueries.Add($"{partition} ({partTotal} résultats, {MaxSearchResults} lus)");
            foreach (var a in part) all.TryAdd(a.Id, a);
        }
        return all.Values.ToList();
    }

    public Task<List<MbLabel>> SearchLabelsAsync(string luceneQuery, CancellationToken ct) =>
        Paged<MbLabelSearch, MbLabel>(
            off => $"label?query={Uri.EscapeDataString(luceneQuery)}&limit={PageSize}&offset={off}",
            p => (p.Labels, p.Count), ct, MaxSearchResults);

    /// <summary>Artistes dont la zone principale est exactement cette zone (pas de plafond de 500).</summary>
    public Task<List<MbArtist>> BrowseArtistsByAreaAsync(string areaId, CancellationToken ct) =>
        Paged<MbArtistBrowse, MbArtist>(
            off => $"artist?area={areaId}&inc=tags+genres&limit={PageSize}&offset={off}",
            p => (p.Artists, p.Count), ct);

    private async Task<(List<MbArtist> Artists, int Total)> SearchArtistsCappedAsync(string luceneQuery, CancellationToken ct)
    {
        var total = 0;
        var artists = await Paged<MbArtistSearch, MbArtist>(
            off => $"artist?query={Uri.EscapeDataString(luceneQuery)}&limit={PageSize}&offset={off}",
            p => { total = p.Count; return (p.Artists, p.Count); }, ct, MaxSearchResults);
        return (artists, total);
    }

    public Task<List<MbReleaseGroup>> BrowseReleaseGroupsByArtistAsync(string artistId, CancellationToken ct) =>
        Paged<MbReleaseGroupBrowse, MbReleaseGroup>(
            off => $"release-group?artist={artistId}&inc=tags+genres+ratings&limit={PageSize}&offset={off}",
            p => (p.ReleaseGroups, p.Count), ct);

    public Task<List<MbRelease>> BrowseReleasesByArtistAsync(string artistId, CancellationToken ct) =>
        Paged<MbReleaseBrowse, MbRelease>(
            off => $"release?artist={artistId}&{Includes}&limit={PageSize}&offset={off}",
            p => (p.Releases, p.Count), ct);

    /// <summary>Releases où l'artiste n'apparaît que sur des pistes (compilations, splits…).</summary>
    public Task<List<MbRelease>> BrowseReleasesByTrackArtistAsync(string artistId, CancellationToken ct) =>
        Paged<MbReleaseBrowse, MbRelease>(
            off => $"release?track_artist={artistId}&{Includes}&limit={PageSize}&offset={off}",
            p => (p.Releases, p.Count), ct);

    public Task<List<MbRelease>> BrowseReleasesByLabelAsync(string labelId, CancellationToken ct) =>
        Paged<MbReleaseBrowse, MbRelease>(
            off => $"release?label={labelId}&{Includes}&limit={PageSize}&offset={off}",
            p => (p.Releases, p.Count), ct);

    // --- Infrastructure -----------------------------------------------------

    private Task<T> Get<T>(string relativeUrl, CancellationToken ct) =>
        _fetcher.GetAsync<T>(relativeUrl + (relativeUrl.Contains('?') ? "&" : "?") + "fmt=json", ct);

    private async Task<List<TItem>> Paged<TPage, TItem>(
        Func<int, string> urlForOffset,
        Func<TPage, (List<TItem> Items, int Total)> extract,
        CancellationToken ct,
        int maxResults = int.MaxValue)
    {
        var all = new List<TItem>();
        for (var offset = 0; ; offset += PageSize)
        {
            var (items, total) = extract(await Get<TPage>(urlForOffset(offset), ct));
            all.AddRange(items);
            if (items.Count == 0 || offset + PageSize >= Math.Min(total, maxResults)) break;
        }
        return all;
    }

    public void Dispose() => _fetcher.Dispose();
}
