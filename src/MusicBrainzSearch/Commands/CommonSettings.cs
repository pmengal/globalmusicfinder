using System.ComponentModel;
using MusicBrainzSearch.Discogs;
using MusicBrainzSearch.Domain;
using MusicBrainzSearch.MusicBrainz;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MusicBrainzSearch.Commands;

public enum Scope { Ville, Province }

public class CommonSettings : CommandSettings
{
    [CommandOption("-s|--scope <SCOPE>")]
    [Description("Périmètre géographique : [green]ville[/] (Liège et ses anciennes communes) ou [green]province[/].")]
    [DefaultValue(Scope.Ville)]
    public Scope Scope { get; init; }

    [CommandOption("-a|--area <MBID>")]
    [Description("Zone(s) MusicBrainz racine(s) à utiliser à la place du périmètre (répétable).")]
    public string[] Areas { get; init; } = [];

    [CommandOption("--depth <N>")]
    [Description("Profondeur de descente dans les sous-zones (défaut : 2 pour la ville, 3 pour la province).")]
    public int? Depth { get; init; }

    [CommandOption("--contact <EMAIL_OU_URL>")]
    [Description("Contact placé dans le User-Agent, exigé par MusicBrainz (ou variable MUSICBRAINZ_CONTACT).")]
    public string? Contact { get; init; }

    [CommandOption("--cache-dir <DIR>")]
    [Description("Dossier du cache des réponses API.")]
    public string? CacheDir { get; init; }

    [CommandOption("--cache-days <N>")]
    [Description("Durée de validité du cache en jours.")]
    [DefaultValue(30)]
    public int CacheDays { get; init; }

    [CommandOption("--no-cache")]
    [Description("Désactive le cache disque.")]
    public bool NoCache { get; init; }

    public IReadOnlyList<string> RootAreaIds => Areas.Length > 0
        ? Areas
        : [Scope == Scope.Province ? AreaResolver.LiegeProvinceId : AreaResolver.LiegeCityId];

    public int EffectiveDepth => Depth ?? (Scope == Scope.Province ? 3 : 2);

    public string EffectiveContact =>
        Contact ?? Environment.GetEnvironmentVariable("MUSICBRAINZ_CONTACT") ?? "contact-not-set";

    private string? EffectiveCacheDir(string source) => NoCache
        ? null
        : Path.Combine(
            CacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBrainzSearch", "cache"),
            source);

    public MusicBrainzClient CreateMusicBrainzClient() =>
        new(EffectiveContact, EffectiveCacheDir("musicbrainz"), TimeSpan.FromDays(CacheDays));

    public DiscogsClient CreateDiscogsClient(string? token) =>
        new(EffectiveContact, token, EffectiveCacheDir("discogs"), TimeSpan.FromDays(CacheDays));

    public void WarnIfNoContact()
    {
        if (Contact is null && Environment.GetEnvironmentVariable("MUSICBRAINZ_CONTACT") is null)
            AnsiConsole.MarkupLine("[yellow]Astuce :[/] renseignez [green]--contact[/] ou [green]MUSICBRAINZ_CONTACT[/] "
                                   + "(e-mail ou URL) : MusicBrainz demande un User-Agent identifiable.");
    }
}

/// <summary>Annulation propre sur Ctrl+C (le cache conserve tout ce qui a déjà été téléchargé).</summary>
public static class Cancellation
{
    public static CancellationTokenSource FromConsole(CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }
}
