using MusicBrainzSearch.Discogs;
using MusicBrainzSearch.MusicBrainz;

namespace MusicBrainzSearch.Domain;

/// <summary>Nature du lien entre un artiste et Liège.</summary>
public enum LinkKind
{
    Area,             // zone principale MusicBrainz = Liège (ou sous-zone)
    BeginArea,        // né·e / fondé à Liège
    EndArea,          // décédé·e / dissous à Liège
    TextMention,      // "Liège" cité dans la désambiguïsation MB ou le profil Discogs
    Member,           // groupe dont un membre est liégeois
    Alias,            // pseudonyme / autre nom d'un artiste liégeois
    Collaboration,    // projet collaboratif impliquant un liégeois
    Label,            // seulement publié sur un label liégeois
}

public sealed record LiegeLink(LinkKind Kind, string Description, string Source)
{
    /// <summary>Lien suffisamment fiable pour propager la "liégeoisité" aux groupes et pseudonymes.</summary>
    public bool IsDirect => Kind is LinkKind.Area or LinkKind.BeginArea or LinkKind.TextMention;

    public Confidence Confidence => Kind switch
    {
        LinkKind.Area or LinkKind.BeginArea or LinkKind.Member or LinkKind.Alias => Confidence.Forte,
        LinkKind.TextMention or LinkKind.Collaboration => Confidence.Moyenne,
        _ => Confidence.Faible,
    };
}

public enum Confidence { Faible = 0, Moyenne = 1, Forte = 2 }

/// <summary>Artiste candidat (MusicBrainz et/ou Discogs).</summary>
public sealed class Candidate
{
    public required string Name { get; set; }
    public string? MbId { get; set; }
    public int? DiscogsId { get; set; }
    public string? Type { get; set; }
    public string? Disambiguation { get; set; }
    public string? Country { get; set; }
    public string? LifeSpan { get; set; }

    public List<LiegeLink> Links { get; } = [];
    public List<string> ElectronicTags { get; set; } = [];
    public List<string> AllTags { get; set; } = [];
    /// <summary>Origine des genres électroniques : "MusicBrainz" ou "Discogs".</summary>
    public string? GenreSource { get; set; }
    public SortedDictionary<string, string> Urls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MbArtist? MbDetails { get; set; }
    public DgArtist? DiscogsDetails { get; set; }

    public bool IsElectronic => ElectronicTags.Count > 0;
    public bool IsDirectlyLiegeois => Links.Any(l => l.IsDirect);
    public Confidence Confidence => Links.Count == 0 ? Confidence.Faible : Links.Max(l => l.Confidence);
    public string Key => MbId is not null ? $"mb:{MbId}" : $"dg:{DiscogsId}";
    public string Source => (MbId, DiscogsId) switch
    {
        (not null, not null) => "MB+Discogs",
        (not null, null) => "MusicBrainz",
        _ => "Discogs",
    };

    public void AddLink(LiegeLink link)
    {
        if (!Links.Any(l => l.Kind == link.Kind && l.Description == link.Description)) Links.Add(link);
    }
}

public sealed record ReleaseInfo(
    string Title,
    string? Date,
    string? Country,
    string? Formats,
    string? Labels,
    string? Barcode,
    string? Status,
    string Url);

/// <summary>Une œuvre = un release-group MusicBrainz ou un master Discogs.</summary>
public sealed class Work
{
    public required string Title { get; init; }
    public required string ArtistCredit { get; set; }
    public string? MbReleaseGroupId { get; init; }
    public int? DiscogsMasterId { get; set; }
    public string? PrimaryType { get; init; }
    public List<string> SecondaryTypes { get; init; } = [];
    public string? FirstReleaseDate { get; set; }
    public List<string> Genres { get; set; } = [];
    public double? Rating { get; init; }
    public int RatingVotes { get; init; }
    public int TagVotes { get; init; }
    public int? DiscogsHave { get; set; }
    public int? DiscogsWant { get; set; }
    public string? DiscogsUrl { get; set; }
    public bool Unclassified { get; set; }

    public List<ReleaseInfo> Releases { get; } = [];
    public List<Candidate> LiegeArtists { get; } = [];

    public double Score { get; set; }

    public string? Year => FirstReleaseDate is { Length: >= 4 } d ? d[..4] : null;
    public string? MusicBrainzUrl => MbReleaseGroupId is null ? null : $"https://musicbrainz.org/release-group/{MbReleaseGroupId}";
    public Confidence Confidence => LiegeArtists.Count == 0 ? Confidence.Faible : LiegeArtists.Max(a => a.Confidence);

    public string TypeLabel => SecondaryTypes.Count == 0
        ? PrimaryType ?? "?"
        : $"{PrimaryType ?? "?"} ({string.Join(", ", SecondaryTypes)})";

    public IEnumerable<string> LabelsWithCatNo => Releases
        .Where(r => r.Labels is not null)
        .Select(r => r.Labels!)
        .Distinct();

    public string SearchQuery => $"{ArtistCredit} {Title}";
}

public sealed class ScanResult
{
    public required List<AreaInfo> Areas { get; init; }
    public required List<Candidate> Artists { get; init; }
    public required List<Work> Works { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
}
