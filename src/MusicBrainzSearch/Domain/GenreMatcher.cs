using MusicBrainzSearch.MusicBrainz;

namespace MusicBrainzSearch.Domain;

/// <summary>
/// Décide si un ensemble de tags/genres MusicBrainz relève de la musique électronique au sens large.
/// Comparaison par sous-chaîne sur des tags normalisés ("electro" couvre electronic, electro-pop, electroacoustic…).
/// </summary>
public sealed class GenreMatcher
{
    public static readonly string[] DefaultKeywords =
    [
        "electro", "techno", "house", "trance", "ambient", "new beat", "ebm", "electronic body",
        "idm", "drum and bass", "drum & bass", "jungle", "dubstep", "synth", "industrial", "minimal",
        "downtempo", "trip hop", "trip-hop", "edm", "hardcore", "gabber", "hardstyle", "breakbeat",
        "big beat", "acid", "darkwave", "coldwave", "dark wave", "cold wave", "glitch", "chiptune",
        "dance", "eurodance", "hi-nrg", "italo", "nu disco", "garage", "footwork", "chillout",
        "lounge", "musique concrète", "acousmati", "noise", "dub techno", "bass music", "grime",
        "vaporwave", "witch house", "uk bass", "leftfield", "krautrock", "kosmische",
    ];

    // Faux positifs évidents des mots-clés ci-dessus.
    private static readonly string[] Exclusions =
    [
        "dancehall", "technical", "death metal", "black metal", "industrial metal", "nu metal",
        "hardcore punk", "melodic hardcore", "post-hardcore", "metalcore", "grindcore", "garage rock",
        "garage punk", "noise rock",
    ];

    private readonly string[] _keywords;

    public GenreMatcher(IEnumerable<string>? keywords = null)
    {
        _keywords = (keywords ?? DefaultKeywords).Select(Normalize).Where(k => k.Length > 0).ToArray();
    }

    public bool IsElectronic(string tag)
    {
        var t = Normalize(tag);
        if (Exclusions.Any(e => t.Contains(e))) return false;
        return _keywords.Any(t.Contains);
    }

    /// <summary>Tags électroniques présents, triés par nombre de votes.</summary>
    public List<string> Matching(IEnumerable<MbTag>? tags, IEnumerable<MbTag>? genres) =>
        Merge(tags, genres).Where(t => IsElectronic(t.Name)).Select(t => t.Name).ToList();

    public static List<MbTag> Merge(IEnumerable<MbTag>? tags, IEnumerable<MbTag>? genres) =>
        (genres ?? []).Concat(tags ?? [])
            .Where(t => t.Count >= 0)
            .GroupBy(t => Normalize(t.Name))
            .Select(g => g.OrderByDescending(t => t.Count).First())
            .OrderByDescending(t => t.Count)
            .ToList();

    private static string Normalize(string s) => s.Trim().ToLowerInvariant().Replace('_', ' ');
}
