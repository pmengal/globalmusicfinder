using MusicBrainzSearch.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MusicBrainzSearch.Commands;

/// <summary>Affiche les zones MusicBrainz considérées comme liégeoises pour le périmètre choisi.</summary>
public sealed class AreasCommand : AsyncCommand<CommonSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings settings, CancellationToken cancellationToken)
    {
        using var cts = Cancellation.FromConsole(cancellationToken);
        settings.WarnIfNoContact();
        using var mb = settings.CreateMusicBrainzClient();

        var areas = await AnsiConsole.Status().StartAsync("Résolution des zones…", ctx =>
            new AreaResolver(mb).ResolveAsync(settings.RootAreaIds, settings.EffectiveDepth,
                a => ctx.Status($"Zone : {Markup.Escape(a.Name)}"), cts.Token));

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Zone").AddColumn("Type").AddColumn(new TableColumn("Niveau").RightAligned()).AddColumn("MBID");
        foreach (var a in areas)
            table.AddRow(Markup.Escape(a.Name), a.Type ?? "", a.Depth.ToString(), $"[link=https://musicbrainz.org/area/{a.Id}]{a.Id}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{areas.Count} zone(s) — requêtes réseau : {mb.NetworkRequests}, cache : {mb.CacheHits}[/]");
        return 0;
    }
}
