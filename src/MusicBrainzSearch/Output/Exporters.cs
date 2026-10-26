using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MusicBrainzSearch.Domain;

namespace MusicBrainzSearch.Output;

public static class Exporters
{
    public static string DiscogsSearchUrl(Work w) =>
        $"https://www.discogs.com/search/?q={Uri.EscapeDataString(w.SearchQuery)}&type=all";

    public static string YouTubeSearchUrl(Work w) =>
        $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(w.SearchQuery)}";

    public static string LiegeLinks(Candidate c) => string.Join(" ; ", c.Links.Select(l => l.Description));

    public static async Task ExportAsync(ScanResult result, string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);

        var content = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => ToJson(result),
            ".csv" => ToCsv(result),
            ".md" or ".markdown" => ToMarkdown(result),
            var ext => throw new ArgumentException($"Format d'export non géré : {ext} (utiliser .json, .csv ou .md)"),
        };
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), ct);
    }

    private static string ToJson(ScanResult r)
    {
        var payload = new
        {
            generatedAt = r.GeneratedAt,
            areas = r.Areas.Select(a => new { a.Id, a.Name, a.Type }),
            artists = r.Artists.Select(a => new
            {
                a.Name, a.Type, a.Disambiguation, a.LifeSpan, a.Source,
                confidence = a.Confidence.ToString(),
                musicBrainzId = a.MbId, discogsId = a.DiscogsId,
                liegeLinks = a.Links.Select(l => new { kind = l.Kind.ToString(), l.Description, l.Source }),
                electronicTags = a.ElectronicTags,
                genreSource = a.GenreSource,
                urls = a.Urls,
            }),
            works = r.Works.Select((w, i) => new
            {
                rank = i + 1, w.Score, w.Title, artist = w.ArtistCredit, year = w.Year, w.FirstReleaseDate,
                type = w.TypeLabel, w.Genres, confidence = w.Confidence.ToString(), unclassified = w.Unclassified,
                liegeArtists = w.LiegeArtists.Select(a => new { a.Name, links = LiegeLinks(a) }),
                musicBrainz = w.MusicBrainzUrl, discogs = w.DiscogsUrl,
                discogsHave = w.DiscogsHave, discogsWant = w.DiscogsWant,
                discogsSearch = DiscogsSearchUrl(w), youTubeSearch = YouTubeSearchUrl(w),
                releases = w.Releases,
            }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string ToCsv(ScanResult r)
    {
        var sb = new StringBuilder();
        string[] header =
        [
            "Rang", "Score", "Année", "Artiste", "Titre", "Type", "Genres", "Artistes liégeois", "Lien avec Liège",
            "Confiance", "Labels [catalogue]", "Formats", "Pays", "Codes-barres", "Éditions",
            "Discogs possédé", "Discogs recherché", "MusicBrainz", "Discogs", "Recherche Discogs", "Recherche YouTube",
        ];
        sb.AppendLine(string.Join(';', header));
        foreach (var (w, i) in r.Works.Select((w, i) => (w, i)))
        {
            string[] row =
            [
                (i + 1).ToString(), w.Score.ToString(CultureInfo.InvariantCulture), w.Year ?? "", w.ArtistCredit, w.Title,
                w.TypeLabel, string.Join(", ", w.Genres), string.Join(", ", w.LiegeArtists.Select(a => a.Name)),
                string.Join(" | ", w.LiegeArtists.Select(LiegeLinks)), w.Confidence.ToString(),
                string.Join(" | ", w.LabelsWithCatNo),
                string.Join(" | ", w.Releases.Select(x => x.Formats).Where(x => x is not null).Distinct()),
                string.Join(", ", w.Releases.Select(x => x.Country).Where(x => x is not null).Distinct()),
                string.Join(", ", w.Releases.Select(x => x.Barcode).Where(x => x is not null).Distinct()),
                w.Releases.Count.ToString(), w.DiscogsHave?.ToString() ?? "", w.DiscogsWant?.ToString() ?? "",
                w.MusicBrainzUrl ?? "", w.DiscogsUrl ?? "", DiscogsSearchUrl(w), YouTubeSearchUrl(w),
            ];
            sb.AppendLine(string.Join(';', row.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string v) =>
        v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static string ToMarkdown(ScanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Musique électronique liégeoise");
        sb.AppendLine();
        sb.AppendLine($"Généré le {r.GeneratedAt:dd/MM/yyyy HH:mm} à partir de MusicBrainz{(r.Artists.Any(a => a.DiscogsId is not null) ? " et Discogs" : "")}. "
                      + $"{r.Works.Count} œuvres, {r.Artists.Count} artistes, {r.Areas.Count} zones.");
        sb.AppendLine();
        sb.AppendLine("## Œuvres (par score d'importance)");
        sb.AppendLine();
        sb.AppendLine("| # | Année | Artiste | Titre | Type | Labels [catalogue] | Lien liégeois | Liens |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (w, i) in r.Works.Select((w, i) => (w, i)))
        {
            var links = new List<string>();
            if (w.MusicBrainzUrl is not null) links.Add($"[MB]({w.MusicBrainzUrl})");
            links.Add(w.DiscogsUrl is not null ? $"[Discogs]({w.DiscogsUrl})" : $"[Discogs?]({DiscogsSearchUrl(w)})");
            links.Add($"[YouTube?]({YouTubeSearchUrl(w)})");
            sb.AppendLine($"| {i + 1} | {w.Year} | {Md(w.ArtistCredit)} | {Md(w.Title)} | {w.TypeLabel} | "
                          + $"{Md(string.Join(" ; ", w.LabelsWithCatNo.Take(3)))} | "
                          + $"{Md(string.Join(" ; ", w.LiegeArtists.Select(a => $"{a.Name} ({LiegeLinks(a)})")))} | {string.Join(" ", links)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Artistes");
        sb.AppendLine();
        foreach (var a in r.Artists)
        {
            sb.AppendLine($"### {a.Name}{(a.Disambiguation is { Length: > 0 } d ? $" ({d})" : "")}");
            sb.AppendLine();
            sb.AppendLine($"- Lien avec Liège ({a.Confidence}) : {LiegeLinks(a)}");
            if (a.ElectronicTags.Count > 0) sb.AppendLine($"- Genres : {string.Join(", ", a.ElectronicTags)}{(a.GenreSource == "Discogs" ? " (d'après Discogs)" : "")}");
            if (a.Urls.Count > 0) sb.AppendLine($"- Liens : {string.Join(" · ", a.Urls.Select(u => $"[{u.Key}]({u.Value})"))}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Md(string s) => s.Replace("|", "\\|");
}
