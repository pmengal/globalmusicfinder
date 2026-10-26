using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MusicBrainzSearch.Http;

namespace MusicBrainzSearch.Discogs;

// DTO Discogs (api.discogs.com), seuls les champs utiles sont mappés.

public sealed class DgArtistRef
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("active")] public bool? Active { get; init; }
}

public sealed class DgArtist
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("realname")] public string? RealName { get; init; }
    [JsonPropertyName("profile")] public string? Profile { get; init; }
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("urls")] public List<string>? Urls { get; init; }
    [JsonPropertyName("aliases")] public List<DgArtistRef>? Aliases { get; init; }
    [JsonPropertyName("groups")] public List<DgArtistRef>? Groups { get; init; }
    [JsonPropertyName("members")] public List<DgArtistRef>? Members { get; init; }
}

public sealed class DgCommunityStats
{
    [JsonPropertyName("in_collection")] public int InCollection { get; init; }
    [JsonPropertyName("in_wantlist")] public int InWantlist { get; init; }
}

public sealed class DgStats
{
    [JsonPropertyName("community")] public DgCommunityStats? Community { get; init; }
}

public sealed class DgArtistRelease
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";   // "master" ou "release"
    [JsonPropertyName("main_release")] public int? MainRelease { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("year")] public int? Year { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }        // Main, Appearance, TrackAppearance, Remix…
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("stats")] public DgStats? Stats { get; init; }

    public string WebUrl => $"https://www.discogs.com/{Type}/{Id}";
}

public sealed class DgPagination
{
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("pages")] public int Pages { get; init; }
}

public sealed class DgArtistReleases
{
    [JsonPropertyName("pagination")] public DgPagination? Pagination { get; init; }
    [JsonPropertyName("releases")] public List<DgArtistRelease> Releases { get; init; } = [];
}

public sealed class DgCommunity
{
    [JsonPropertyName("have")] public int Have { get; init; }
    [JsonPropertyName("want")] public int Want { get; init; }
}

public sealed class DgLabel
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("catno")] public string? CatNo { get; init; }
}

public sealed class DgFormat
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("descriptions")] public List<string>? Descriptions { get; init; }
}

/// <summary>Détail d'un master ou d'une release (champs communs).</summary>
public sealed class DgItem
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("year")] public int? Year { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("genres")] public List<string>? Genres { get; init; }
    [JsonPropertyName("styles")] public List<string>? Styles { get; init; }
    [JsonPropertyName("labels")] public List<DgLabel>? Labels { get; init; }
    [JsonPropertyName("formats")] public List<DgFormat>? Formats { get; init; }
    [JsonPropertyName("community")] public DgCommunity? Community { get; init; }
    [JsonPropertyName("uri")] public string? Uri { get; init; }
}

/// <summary>
/// Client Discogs. Sans token : 25 requêtes/minute ; avec token personnel : 60 requêtes/minute.
/// Un token se génère sur https://www.discogs.com/settings/developers.
/// </summary>
public sealed partial class DiscogsClient : IDisposable
{
    private readonly CachedHttpFetcher _fetcher;

    public int NetworkRequests => _fetcher.NetworkRequests;
    public int CacheHits => _fetcher.CacheHits;

    public DiscogsClient(string contact, string? token, string? cacheDir, TimeSpan cacheTtl)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.discogs.com/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MusicBrainzSearch/1.0 (+{contact})");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Discogs", $"token={token}");

        var interval = string.IsNullOrWhiteSpace(token) ? TimeSpan.FromSeconds(2.5) : TimeSpan.FromSeconds(1.05);
        _fetcher = new CachedHttpFetcher(http, interval, cacheDir, cacheTtl);
    }

    public Task<DgArtist?> GetArtistAsync(int id, CancellationToken ct) =>
        _fetcher.TryGetAsync<DgArtist>($"artists/{id}", ct);

    public Task<DgItem?> GetMasterAsync(int id, CancellationToken ct) =>
        _fetcher.TryGetAsync<DgItem>($"masters/{id}", ct);

    public Task<DgItem?> GetReleaseAsync(int id, CancellationToken ct) =>
        _fetcher.TryGetAsync<DgItem>($"releases/{id}", ct);

    public async Task<List<DgArtistRelease>> GetArtistReleasesAsync(int id, CancellationToken ct)
    {
        var all = new List<DgArtistRelease>();
        for (var page = 1; ; page++)
        {
            var result = await _fetcher.TryGetAsync<DgArtistReleases>(
                $"artists/{id}/releases?sort=year&sort_order=asc&per_page=100&page={page}", ct);
            if (result is null) break;
            all.AddRange(result.Releases);
            if (result.Pagination is null || page >= result.Pagination.Pages) break;
        }
        return all;
    }

    /// <summary>Extrait l'identifiant numérique d'une URL d'artiste Discogs.</summary>
    public static int? ParseArtistId(string url)
    {
        var m = ArtistUrlRegex().Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static string ArtistUrl(int id) => $"https://www.discogs.com/artist/{id}";

    /// <summary>Supprime le suffixe d'homonymie Discogs : "Daan (3)" → "Daan".</summary>
    public static string CleanName(string name) => DisambiguationSuffixRegex().Replace(name, "");

    [GeneratedRegex(@"discogs\.com/(?:[a-z]{2}/)?artist/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistUrlRegex();

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex DisambiguationSuffixRegex();

    public void Dispose() => _fetcher.Dispose();
}
