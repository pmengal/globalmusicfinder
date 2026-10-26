using MusicBrainzSearch.Domain;
using Spectre.Console;

namespace MusicBrainzSearch.Output;

public static class ConsoleRenderer
{
    public static void Render(ScanResult result, int top, bool showArtists)
    {
        AnsiConsole.Write(new Rule("[bold yellow]Musique électronique liégeoise[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[grey]{result.Works.Count} œuvre(s) · {result.Artists.Count} artiste(s) · {result.Areas.Count} zone(s)[/]");
        AnsiConsole.WriteLine();

        if (showArtists) RenderArtists(result.Artists);
        RenderWorks(result.Works.Take(top).ToList(), result.Works.Count);
    }

    private static void RenderArtists(List<Candidate> artists)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Artistes[/]")
            .AddColumn("Artiste").AddColumn("Lien avec Liège").AddColumn("Confiance")
            .AddColumn("Genres").AddColumn("Source");

        foreach (var a in artists)
        {
            var name = a.Urls.TryGetValue("MusicBrainz", out var url) || a.Urls.TryGetValue("Discogs", out url)
                ? $"[link={url}]{Markup.Escape(a.Name)}[/]"
                : Markup.Escape(a.Name);
            if (a.Disambiguation is { Length: > 0 } d) name += $"\n[grey]{Markup.Escape(d)}[/]";

            table.AddRow(
                new Markup(name),
                new Markup(Markup.Escape(Exporters.LiegeLinks(a))),
                new Markup(ConfidenceMarkup(a.Confidence)),
                new Markup($"[grey]{Markup.Escape(string.Join(", ", a.ElectronicTags.Take(4)))}"
                           + (a.GenreSource == "Discogs" ? " [italic](Discogs)[/]" : "") + "[/]"),
                new Markup(a.Source));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderWorks(List<Work> works, int total)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]Principales œuvres[/] [grey]({works.Count} sur {total})[/]")
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Année")
            .AddColumn("Artiste")
            .AddColumn("Titre")
            .AddColumn("Type")
            .AddColumn("Labels [[catalogue]]")
            .AddColumn(new TableColumn("Score").RightAligned())
            .AddColumn("Retrouver");

        foreach (var (w, i) in works.Select((w, i) => (w, i)))
        {
            var title = Markup.Escape(w.Title);
            if (w.Genres.Count > 0) title += $"\n[grey]{Markup.Escape(string.Join(", ", w.Genres.Take(3)))}[/]";
            if (w.Unclassified) title += "\n[yellow](genre non confirmé)[/]";

            var artist = Markup.Escape(w.ArtistCredit);
            var liegeois = w.LiegeArtists.Where(a => !string.Equals(a.Name, w.ArtistCredit, StringComparison.OrdinalIgnoreCase)).ToList();
            if (liegeois.Count > 0)
                artist += $"\n[grey]via {Markup.Escape(string.Join(", ", liegeois.Select(a => a.Name)))}[/]";

            var links = new List<string>();
            if (w.MusicBrainzUrl is not null) links.Add($"[link={w.MusicBrainzUrl}]MusicBrainz[/]");
            links.Add(w.DiscogsUrl is not null
                ? $"[link={w.DiscogsUrl}]Discogs[/]"
                : $"[link={Exporters.DiscogsSearchUrl(w)}]Discogs ?[/]");
            links.Add($"[link={Exporters.YouTubeSearchUrl(w)}]YouTube ?[/]");
            if (w.DiscogsHave is not null) links.Add($"[grey]{w.DiscogsHave} possédé(s) / {w.DiscogsWant} recherché(s)[/]");

            table.AddRow(
                new Markup($"{i + 1}"),
                new Markup(w.Year ?? "[grey]?[/]"),
                new Markup(artist),
                new Markup(title),
                new Markup(Markup.Escape(w.TypeLabel)),
                new Markup(Markup.Escape(string.Join("\n", w.LabelsWithCatNo.Take(3)))),
                new Markup($"{w.Score:0.#}\n{ConfidenceMarkup(w.Confidence)}"),
                new Markup(string.Join("\n", links)));
        }
        AnsiConsole.Write(table);
    }

    private static string ConfidenceMarkup(Confidence c) => c switch
    {
        Confidence.Forte => "[green]forte[/]",
        Confidence.Moyenne => "[yellow]moyenne[/]",
        _ => "[red]faible[/]",
    };
}
