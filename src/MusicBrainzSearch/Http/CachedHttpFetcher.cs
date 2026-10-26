using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicBrainzSearch.Http;

/// <summary>
/// Client HTTP JSON avec limitation de débit (intervalle minimal entre requêtes),
/// relances sur 429/503 et cache disque des réponses.
/// Partagé par les clients MusicBrainz et Discogs, chacun avec ses propres règles.
/// </summary>
public sealed class CachedHttpFetcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly TimeSpan _minInterval;
    private readonly string? _cacheDir;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public int NetworkRequests { get; private set; }
    public int CacheHits { get; private set; }

    public CachedHttpFetcher(HttpClient http, TimeSpan minInterval, string? cacheDir, TimeSpan cacheTtl)
    {
        _http = http;
        _minInterval = minInterval;
        _cacheDir = cacheDir;
        _cacheTtl = cacheTtl;
        if (_cacheDir is not null) Directory.CreateDirectory(_cacheDir);
    }

    public async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var json = await GetJsonAsync(relativeUrl, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Réponse vide pour {relativeUrl}");
    }

    /// <summary>Comme <see cref="GetAsync{T}"/> mais renvoie null sur 404 (le 404 est aussi mis en cache).</summary>
    public async Task<T?> TryGetAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        var json = await GetJsonAsync(relativeUrl, ct, allowNotFound: true);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken ct) =>
        (await GetJsonAsync(url, ct, allowNotFound: false))!;

    private async Task<string?> GetJsonAsync(string url, CancellationToken ct, bool allowNotFound)
    {
        const string notFoundMarker = "\u0000404";
        var cacheFile = CachePath(url);
        if (cacheFile is not null && File.Exists(cacheFile)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < _cacheTtl)
        {
            CacheHits++;
            var cached = await File.ReadAllTextAsync(cacheFile, ct);
            if (cached != notFoundMarker) return cached;
            if (allowNotFound) return null;
            throw new HttpRequestException($"404 : {url}", null, HttpStatusCode.NotFound);
        }

        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync(ct);
            NetworkRequests++;
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
                && attempt < 6)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                await Task.Delay(wait, ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
            {
                if (cacheFile is not null) await File.WriteAllTextAsync(cacheFile, notFoundMarker, ct);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"{(int)response.StatusCode} {response.ReasonPhrase} pour {_http.BaseAddress}{url}\n{body[..Math.Min(body.Length, 300)]}",
                    null, response.StatusCode);
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            if (cacheFile is not null) await File.WriteAllTextAsync(cacheFile, json, ct);
            return json;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + _minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? CachePath(string url)
    {
        if (_cacheDir is null) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        return Path.Combine(_cacheDir, hash + ".json");
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
