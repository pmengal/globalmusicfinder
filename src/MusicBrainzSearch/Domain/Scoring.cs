namespace MusicBrainzSearch.Domain;

/// <summary>
/// Score d'importance d'une œuvre, pour faire remonter les "principales" œuvres.
/// Signaux : rééditions et diffusion internationale (MusicBrainz), votes et tags de la communauté,
/// possession / recherche sur Discogs, type de sortie, fiabilité du lien avec Liège.
/// </summary>
public static class Scoring
{
    public static double Compute(Work w)
    {
        var releases = w.Releases.Count;
        var countries = w.Releases.Select(r => r.Country).Where(c => c is not null).Distinct().Count();
        var labels = w.Releases.Select(r => r.Labels).Where(l => l is not null).Distinct().Count();

        double score = 0;
        score += Math.Min(Math.Max(releases - 1, 0), 10) * 3;     // rééditions
        score += Math.Min(countries, 5) * 2;                      // diffusion internationale
        score += Math.Min(labels, 5);                             // licences sur plusieurs labels
        score += (w.Rating ?? 0) * Math.Min(w.RatingVotes, 10);   // notes MusicBrainz
        score += Math.Min(w.TagVotes, 20);                        // tags communautaires
        score += Math.Log2(1 + (w.DiscogsHave ?? 0)) * 4;         // exemplaires possédés (Discogs)
        score += Math.Log2(1 + (w.DiscogsWant ?? 0)) * 3;         // exemplaires recherchés (Discogs)

        score += w.PrimaryType switch { "Album" => 6, "EP" => 4, "Single" => 2, _ => 1 };
        if (w.SecondaryTypes.Any(t => t is "Compilation" or "Live" or "DJ-mix" or "Apparition")) score -= 3;

        score *= w.Confidence switch { Confidence.Forte => 1.0, Confidence.Moyenne => 0.85, _ => 0.6 };
        if (w.Unclassified) score *= 0.5;
        return Math.Round(Math.Max(score, 0), 1);
    }
}
