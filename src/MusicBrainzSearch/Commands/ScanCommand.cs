using System.ComponentModel;
using MusicBrainzSearch.Discogs;
using MusicBrainzSearch.Domain;
using MusicBrainzSearch.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MusicBrainzSearch.Commands;

public sealed class ScanSettings : CommonSettings
{
    [CommandOption("--discogs-token <TOKEN>")]
    [Description("Token personnel Discogs (ou variable DISCOGS_TOKEN). Active le recoupement Discogs à 60 req/min.")]
    public string? DiscogsToken { get; init; }

    [CommandOption("--discogs")]
    [Description("Active Discogs même sans token (limité à 25 req/min, donc lent).")]
    public bool ForceDiscogs { get; init; }

    [CommandOption("--no-discogs")]
    [Description("Désactive Discogs même si un token est disponible.")]
    public bool NoDiscogs { get; init; }

    [CommandOption("--discogs-max-lookups <N>")]
    [Description("Nombre max de fiches Discogs consultées pour classer les œuvres absentes de MusicBrainz.")]
    [DefaultValue(300)]
    public int DiscogsMaxLookups { get; init; }

    [CommandOption("--discogs-genre-sample <N>")]
    [Description("Sorties Discogs examinées pour classer un artiste sans genre sur MusicBrainz (0 = désactivé).")]
    [DefaultValue(3)]
    public int DiscogsGenreSample { get; init; }

    [CommandOption("-b|--belgium")]
    [Description("Entonnoir belge : examine aussi les artistes belges tagués électro et garde ceux dont le profil Discogs cite Liège.")]
    public bool BelgiumFunnel { get; init; }

    [CommandOption("--max-funnel <N>")]
    [Description("Nombre max d'artistes belges examinés par l'entonnoir.")]
    [DefaultValue(400)]
    public int MaxFunnel { get; init; }

    [CommandOption("-u|--include-untagged")]
    [Description("Garde les artistes/œuvres sans aucun tag de genre (marqués « genre non confirmé »).")]
    public bool IncludeUntagged { get; init; }

    [CommandOption("--appearances")]
    [Description("Inclut les compilations où un artiste liégeois n'apparaît que sur une piste.")]
    public bool IncludeAppearances { get; init; }

    [CommandOption("--no-labels")]
    [Description("N'explore pas les catalogues des labels liégeois.")]
    public bool NoLabels { get; init; }

    [CommandOption("--max-artists <N>")]
    [Description("Limite le nombre d'artistes de départ (pour un essai rapide).")]
    public int? MaxArtists { get; init; }

    [CommandOption("-t|--top <N>")]
    [Description("Nombre d'œuvres affichées dans la console.")]
    [DefaultValue(50)]
    public int Top { get; init; }

    [CommandOption("--show-artists")]
    [Description("Affiche aussi le tableau des artistes retenus.")]
    public bool ShowArtists { get; init; }

    [CommandOption("-o|--output <FICHIER>")]
    [Description("Export(s) : .json, .csv ou .md (répétable). Contient toutes les œuvres, pas seulement le top.")]
    public string[] Outputs { get; init; } = [];

    [CommandOption("--genres <LISTE>")]
    [Description("Mots-clés de genre séparés par des virgules, en remplacement de la liste par défaut.")]
    public string? Genres { get; init; }

    public string? EffectiveDiscogsToken => DiscogsToken ?? Environment.GetEnvironmentVariable("DISCOGS_TOKEN");
    public bool UseDiscogs => !NoDiscogs && (ForceDiscogs || !string.IsNullOrWhiteSpace(EffectiveDiscogsToken));
}

public sealed class ScanCommand : AsyncCommand<ScanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ScanSettings settings, CancellationToken cancellationToken)
    {
        using var cts = Cancellation.FromConsole(cancellationToken);
        settings.WarnIfNoContact();
        if (!settings.UseDiscogs)
            AnsiConsole.MarkupLine("[yellow]Discogs désactivé[/] [grey](pas de token) : pseudonymes et groupes limités aux relations MusicBrainz. "
                                   + "Voir --discogs-token / DISCOGS_TOKEN.[/]");
        if (settings.BelgiumFunnel && !settings.UseDiscogs)
            AnsiConsole.MarkupLine("[yellow]L'entonnoir belge (--belgium) nécessite Discogs pour confirmer l'origine liégeoise.[/]");

        using var mb = settings.CreateMusicBrainzClient();
        using var discogs = settings.UseDiscogs ? settings.CreateDiscogsClient(settings.EffectiveDiscogsToken) : null;
        var genres = new GenreMatcher(settings.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var options = new ScanOptions
        {
            RootAreaIds = settings.RootAreaIds,
            AreaDepth = settings.EffectiveDepth,
            BelgiumFunnel = settings.BelgiumFunnel,
            MaxFunnel = settings.MaxFunnel,
            IncludeUntagged = settings.IncludeUntagged,
            IncludeAppearances = settings.IncludeAppearances,
            IncludeLabels = !settings.NoLabels,
            MaxArtists = settings.MaxArtists,
            DiscogsMaxLookups = settings.DiscogsMaxLookups,
            DiscogsGenreSample = settings.DiscogsGenreSample,
        };

        ScanResult? result = null;
        IReadOnlyList<string> messages = [];
        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(),
                         new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var reporter = new SpectreScanReporter(ctx);
                    messages = reporter.Messages;
                    result = await new LiegeScanner(mb, discogs, genres, options, reporter).RunAsync(cts.Token);
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Analyse interrompue.[/] [grey]Les réponses déjà téléchargées sont en cache : relancer reprendra rapidement.[/]");
            return 130;
        }

        AnsiConsole.WriteLine();
        foreach (var m in messages) AnsiConsole.MarkupLine($"[grey]•[/] {Markup.Escape(m)}");
        AnsiConsole.MarkupLine($"[grey]• Requêtes MusicBrainz : {mb.NetworkRequests} réseau, {mb.CacheHits} en cache"
                               + (discogs is null ? "" : $" — Discogs : {discogs.NetworkRequests} réseau, {discogs.CacheHits} en cache") + "[/]");
        AnsiConsole.WriteLine();

        ConsoleRenderer.Render(result!, settings.Top, settings.ShowArtists);

        foreach (var output in settings.Outputs)
        {
            await Exporters.ExportAsync(result!, output, cts.Token);
            AnsiConsole.MarkupLine($"[green]Exporté :[/] {Markup.Escape(Path.GetFullPath(output))}");
        }
        return 0;
    }
}
