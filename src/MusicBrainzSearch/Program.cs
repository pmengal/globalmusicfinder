using System.Text;
using MusicBrainzSearch.Commands;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

var app = new CommandApp<ScanCommand>();
app.Configure(config =>
{
    config.SetApplicationName("mbsearch");

    config.AddCommand<ScanCommand>("scan")
        .WithDescription("Recherche les œuvres de musique électronique dont au moins un auteur est liégeois.")
        .WithExample("scan", "--top", "30", "-o", "liege.md", "-o", "liege.csv")
        .WithExample("scan", "--scope", "province", "--discogs-token", "XXXX", "--belgium")
        .WithExample("scan", "--max-artists", "40", "--show-artists");

    config.AddCommand<AreasCommand>("areas")
        .WithDescription("Affiche les zones MusicBrainz considérées comme liégeoises.")
        .WithExample("areas", "--scope", "province");
});

return await app.RunAsync(args);
