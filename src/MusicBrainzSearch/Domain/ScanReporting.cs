namespace MusicBrainzSearch.Domain;

/// <summary>Découple le scanner de l'affichage (barres de progression Spectre.Console).</summary>
public interface IScanReporter
{
    IScanPhase Begin(string title, int total);
    void Info(string message);
}

public interface IScanPhase : IDisposable
{
    void Advance(string? detail = null);
    void AddToTotal(int count);
}

public sealed record ScanOptions
{
    public required IReadOnlyList<string> RootAreaIds { get; init; }
    public int AreaDepth { get; init; } = 2;

    /// <summary>Entonnoir large : artistes belges tagués électro, confirmés liégeois par leur profil.</summary>
    public bool BelgiumFunnel { get; init; }
    public int MaxFunnel { get; init; } = 400;

    public bool IncludeUntagged { get; init; }
    public bool IncludeAppearances { get; init; }
    public bool IncludeLabels { get; init; } = true;

    /// <summary>Limite le nombre d'artistes de départ (utile pour un premier essai).</summary>
    public int? MaxArtists { get; init; }

    /// <summary>Nombre max de fiches master/release Discogs consultées pour classer les œuvres hors MusicBrainz.</summary>
    public int DiscogsMaxLookups { get; init; } = 300;

    /// <summary>Nombre de sorties Discogs échantillonnées pour classer un artiste sans tag MusicBrainz (0 = désactivé).</summary>
    public int DiscogsGenreSample { get; init; } = 3;
}
