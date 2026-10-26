using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MusicBrainzSearch.Discogs;
using MusicBrainzSearch.MusicBrainz;

namespace MusicBrainzSearch.Domain;

/// <summary>
/// Pipeline de recherche des œuvres électroniques liégeoises :
///  1. zones liégeoises (MusicBrainz, avec sous-zones) ;
///  2. artistes rattachés à ces zones + mentions textuelles de Liège (+ entonnoir belge optionnel) ;
///  3. extension aux groupes, pseudonymes et collaborations (relations MusicBrainz puis Discogs) ;
///  4. filtrage électronique via tags/genres des artistes et des release-groups ;
///  5. collecte des éditions (labels, n° de catalogue, codes-barres) et enrichissement Discogs ;
///  6. labels liégeois ; 7. score d'importance.
/// </summary>
public sealed partial class LiegeScanner(
    MusicBrainzClient mb,
    DiscogsClient? discogs,
    GenreMatcher genres,
    ScanOptions options,
    IScanReporter reporter)
{
    private readonly Dictionary<string, Candidate> _byMbId = new();
    private readonly Dictionary<int, Candidate> _byDiscogsId = new();
    private readonly Dictionary<string, Work> _worksByKey = new();
    private readonly Dictionary<string, List<MbReleaseGroup>> _releaseGroups = new();
    private readonly HashSet<string> _areaIds = [];
    private int _discogsLookups;

    public async Task<ScanResult> RunAsync(CancellationToken ct)
    {
        var areas = await ResolveAreasAsync(ct);
        var funnel = await FindSeedsAsync(areas, ct);
        await LookupMusicBrainzAsync(_byMbId.Values.ToList(), "Fiches MusicBrainz des artistes liégeois", ct);
        if (funnel.Count > 0) await ConfirmFunnelAsync(funnel, ct);
        await ExpandMusicBrainzRelationsAsync(ct);
        if (discogs is not null) await ExpandDiscogsAsync(ct);
        await ClassifyAsync(ct);
        if (discogs is not null && options.DiscogsGenreSample > 0) await ClassifyUntaggedWithDiscogsAsync(ct);
        await CollectMusicBrainzWorksAsync(ct);
        if (discogs is not null) await EnrichFromDiscogsAsync(ct);
        if (options.IncludeLabels) await CollectLabelWorksAsync(areas, ct);

        var works = _worksByKey.Values.ToList();
        foreach (var w in works) w.Score = Scoring.Compute(w);

        var artists = AllCandidates()
            .Where(c => c.IsElectronic || works.Any(w => w.LiegeArtists.Contains(c)))
            .OrderByDescending(c => c.Confidence).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ScanResult
        {
            Areas = areas,
            Artists = artists,
            Works = works.OrderByDescending(w => w.Score).ToList(),
        };
    }

    // --- 1. Zones ---------------------------------------------------------------

    private async Task<List<AreaInfo>> ResolveAreasAsync(CancellationToken ct)
    {
        using var phase = reporter.Begin("Zones liégeoises (sous-zones incluses)", 0);
        var areas = await new AreaResolver(mb).ResolveAsync(
            options.RootAreaIds, options.AreaDepth, a => phase.Advance(a.Name), ct);
        foreach (var a in areas) _areaIds.Add(a.Id);
        reporter.Info($"{areas.Count} zone(s) : {string.Join(", ", areas.Take(12).Select(a => a.Name))}{(areas.Count > 12 ? "…" : "")}");
        return areas;
    }

    // --- 2. Artistes de départ ----------------------------------------------------

    private async Task<List<MbArtist>> FindSeedsAsync(List<AreaInfo> areas, CancellationToken ct)
    {
        var names = areas.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var phase = reporter.Begin("Recherche des artistes par zone",
            areas.Count + names.Count + 1 + (options.BelgiumFunnel ? 1 : 0));

        // Zone principale : liste exhaustive par identifiant de zone (non plafonnée, sans homonymes).
        foreach (var area in areas)
        {
            foreach (var artist in await mb.BrowseArtistsByAreaAsync(area.Id, ct))
                AddSeed(artist);
            phase.Advance(area.Name);
        }

        // Lieu de naissance / fondation et de fin : seulement accessibles par recherche (filtrée ensuite par identifiant).
        foreach (var name in names)
        {
            var q = Quote(name);
            foreach (var artist in await mb.SearchArtistsAsync($"beginarea:{q} OR endarea:{q}", ct))
                AddSeed(artist);
            phase.Advance($"origine : {name}");
        }

        // Désambiguïsations du type "DJ techno liégeois", "Belgian producer from Liège".
        foreach (var artist in await mb.SearchArtistsAsync("comment:liege OR comment:liège OR comment:luik OR comment:liégeois", ct))
            AddSeed(artist);
        phase.Advance("mentions de Liège");

        var funnel = new List<MbArtist>();
        if (options.BelgiumFunnel)
        {
            const string tags = "tag:electronic OR tag:techno OR tag:house OR tag:\"new beat\" OR tag:ebm OR tag:trance "
                              + "OR tag:ambient OR tag:electro OR tag:idm OR tag:\"drum and bass\" OR tag:synthpop OR tag:\"synth-pop\"";
            var belgians = await mb.SearchArtistsAsync($"(country:BE OR area:Belgium OR beginarea:Belgium) AND ({tags})", ct);
            funnel = belgians.Where(a => !_byMbId.ContainsKey(a.Id)).Take(options.MaxFunnel).ToList();
            phase.Advance($"entonnoir belge : {funnel.Count} artiste(s) à vérifier");
        }

        if (options.MaxArtists is { } max && _byMbId.Count > max)
        {
            var keep = _byMbId.Values.OrderByDescending(c => c.Confidence).Take(max).Select(c => c.MbId!).ToHashSet();
            foreach (var id in _byMbId.Keys.Where(k => !keep.Contains(k)).ToList()) _byMbId.Remove(id);
        }

        reporter.Info($"{_byMbId.Count} artiste(s) rattaché(s) directement à Liège");
        foreach (var q in mb.TruncatedQueries)
            reporter.Info($"Attention, recherche tronquée par MusicBrainz : {q}");
        return funnel;
    }

    private void AddSeed(MbArtist artist)
    {
        var links = new List<LiegeLink>();
        if (artist.Area is { } area && _areaIds.Contains(area.Id))
            links.Add(new(LinkKind.Area, $"Zone : {area.Name}", "MusicBrainz"));
        if (artist.BeginArea is { } begin && _areaIds.Contains(begin.Id))
            links.Add(new(LinkKind.BeginArea, $"{BeginVerb(artist)} à {begin.Name}", "MusicBrainz"));
        if (artist.EndArea is { } end && _areaIds.Contains(end.Id))
            links.Add(new(LinkKind.EndArea, $"Fin / décès à {end.Name}", "MusicBrainz"));
        if (MentionsLiege(artist.Disambiguation))
            links.Add(new(LinkKind.TextMention, $"Description MB : « {artist.Disambiguation} »", "MusicBrainz"));
        if (links.Count == 0) return;

        var candidate = GetOrAddMb(artist);
        foreach (var l in links) candidate.AddLink(l);
    }

    private static string BeginVerb(MbArtist a) => a.Type == "Person" ? "Né·e" : "Fondé";

    /// <summary>Artistes belges tagués électro : liégeois si leur profil Discogs ou leur description cite Liège.</summary>
    private async Task ConfirmFunnelAsync(List<MbArtist> funnel, CancellationToken ct)
    {
        using var phase = reporter.Begin("Entonnoir belge : vérification des profils", funnel.Count);
        var confirmed = 0;
        foreach (var artist in funnel)
        {
            phase.Advance(artist.Name);
            if (discogs is null) continue;

            var details = await mb.GetArtistAsync(artist.Id, ct);
            var dgId = DiscogsIdOf(details);
            if (dgId is null) continue;
            var dg = await discogs.GetArtistAsync(dgId.Value, ct);
            if (dg is null || !MentionsLiege(dg.Profile)) continue;

            var c = GetOrAddMb(details);
            ApplyMbDetails(c, details);
            c.DiscogsDetails = dg;
            c.AddLink(new(LinkKind.TextMention, $"Profil Discogs : « {Excerpt(dg.Profile!)} »", "Discogs"));
            confirmed++;
        }
        reporter.Info($"Entonnoir belge : {confirmed} artiste(s) confirmé(s) liégeois via Discogs"
                      + (discogs is null ? " (Discogs désactivé : aucune vérification possible)" : ""));
    }

    // --- 3. Extension aux groupes / pseudonymes -----------------------------------

    private async Task LookupMusicBrainzAsync(List<Candidate> candidates, string title, CancellationToken ct)
    {
        using var phase = reporter.Begin(title, candidates.Count);
        foreach (var c in candidates)
        {
            phase.Advance(c.Name);
            if (c.MbId is null || c.MbDetails is not null) continue;
            ApplyMbDetails(c, await mb.GetArtistAsync(c.MbId, ct));
        }
    }

    private async Task ExpandMusicBrainzRelationsAsync(CancellationToken ct)
    {
        var added = new List<Candidate>();
        foreach (var source in _byMbId.Values.Where(c => c.IsDirectlyLiegeois).ToList())
        {
            foreach (var rel in source.MbDetails?.Relations ?? [])
            {
                if (rel.Artist is null) continue;
                LiegeLink? link = (rel.Type, rel.Direction) switch
                {
                    ("member of band", "forward") => new(LinkKind.Member, $"Membre liégeois : {source.Name}", "MusicBrainz"),
                    ("is person", _) => new(LinkKind.Alias, $"Autre nom de {source.Name}", "MusicBrainz"),
                    ("collaboration", "forward") => new(LinkKind.Collaboration, $"Collaboration de {source.Name}", "MusicBrainz"),
                    _ => null,
                };
                if (link is null) continue;

                var isNew = !_byMbId.ContainsKey(rel.Artist.Id);
                var target = GetOrAddMb(rel.Artist);
                target.AddLink(link);
                if (isNew) added.Add(target);
            }
        }
        reporter.Info($"Relations MusicBrainz : {added.Count} groupe(s) / pseudonyme(s) / collaboration(s) ajouté(s)");
        await LookupMusicBrainzAsync(added, "Fiches MusicBrainz des groupes et pseudonymes", ct);
    }

    private async Task ExpandDiscogsAsync(CancellationToken ct)
    {
        var sources = AllCandidates().Where(c => c.IsDirectlyLiegeois && c.DiscogsId is not null).ToList();
        using var phase = reporter.Begin("Discogs : pseudonymes et groupes", sources.Count);
        var added = 0;

        foreach (var source in sources)
        {
            phase.Advance(source.Name);
            var dg = source.DiscogsDetails ??= await discogs!.GetArtistAsync(source.DiscogsId!.Value, ct);
            if (dg is null) continue;
            AddDiscogsUrls(source, dg);
            if (source.Links.All(l => l.Kind != LinkKind.TextMention) && MentionsLiege(dg.Profile))
                source.AddLink(new(LinkKind.TextMention, $"Profil Discogs : « {Excerpt(dg.Profile!)} »", "Discogs"));

            var related = (dg.Aliases ?? []).Select(a => (Ref: a, Link: new LiegeLink(LinkKind.Alias, $"Pseudonyme de {source.Name}", "Discogs")))
                .Concat((dg.Groups ?? []).Select(g => (Ref: g, Link: new LiegeLink(LinkKind.Member, $"Membre liégeois : {source.Name}", "Discogs"))));

            foreach (var (dgRef, link) in related)
            {
                var target = await ResolveDiscogsArtistAsync(dgRef, ct);
                if (target is null) continue;
                if (target.Links.Count == 0) added++;
                target.AddLink(link);
            }
        }
        reporter.Info($"Discogs : {added} groupe(s) / pseudonyme(s) supplémentaire(s)");
    }

    /// <summary>Retrouve l'artiste MusicBrainz correspondant à un artiste Discogs, sinon crée un candidat "Discogs seul".</summary>
    private async Task<Candidate?> ResolveDiscogsArtistAsync(DgArtistRef dgRef, CancellationToken ct)
    {
        if (_byDiscogsId.TryGetValue(dgRef.Id, out var known)) return known;

        var mbArtist = await mb.FindArtistByUrlAsync(DiscogsClient.ArtistUrl(dgRef.Id), ct);
        if (mbArtist is not null)
        {
            var c = GetOrAddMb(mbArtist);
            if (c.MbDetails is null) ApplyMbDetails(c, await mb.GetArtistAsync(mbArtist.Id, ct));
            c.DiscogsId ??= dgRef.Id;
            _byDiscogsId[dgRef.Id] = c;
            return c;
        }

        var dg = await discogs!.GetArtistAsync(dgRef.Id, ct);
        var candidate = new Candidate { Name = DiscogsClient.CleanName(dgRef.Name), DiscogsId = dgRef.Id, DiscogsDetails = dg };
        if (dg is not null) AddDiscogsUrls(candidate, dg);
        _byDiscogsId[dgRef.Id] = candidate;
        return candidate;
    }

    // --- 4. Filtrage électronique ---------------------------------------------------

    private async Task ClassifyAsync(CancellationToken ct)
    {
        var candidates = _byMbId.Values.Where(c => c.Links.Count > 0).ToList();
        using var phase = reporter.Begin("Genres des artistes et de leurs sorties", candidates.Count);
        foreach (var c in candidates)
        {
            phase.Advance(c.Name);
            var rgs = await mb.BrowseReleaseGroupsByArtistAsync(c.MbId!, ct);
            _releaseGroups[c.MbId!] = rgs;

            var rgTags = rgs.SelectMany(rg => GenreMatcher.Merge(rg.Tags, rg.Genres)).ToList();
            c.AllTags = c.AllTags.Concat(rgTags.Select(t => t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            c.ElectronicTags = c.ElectronicTags
                .Concat(rgTags.Where(t => genres.IsElectronic(t.Name)).OrderByDescending(t => t.Count).Select(t => t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (c.IsElectronic) c.GenreSource ??= "MusicBrainz";
        }
        reporter.Info($"{candidates.Count(c => c.IsElectronic)} artiste(s) MusicBrainz identifié(s) comme électroniques "
                      + $"sur {candidates.Count} ({candidates.Count(c => c.AllTags.Count == 0)} sans aucun tag)");
    }

    /// <summary>
    /// Beaucoup d'artistes MusicBrainz n'ont aucun tag. S'ils ont une fiche Discogs, on échantillonne
    /// leurs sorties principales : l'artiste est électronique si au moins la moitié de l'échantillon l'est.
    /// </summary>
    private async Task ClassifyUntaggedWithDiscogsAsync(CancellationToken ct)
    {
        var untagged = _byMbId.Values
            .Where(c => c.Links.Count > 0 && c.AllTags.Count == 0 && c.DiscogsId is not null)
            .ToList();
        using var phase = reporter.Begin("Discogs : genres des artistes sans tag MusicBrainz", untagged.Count);
        var classified = 0;

        foreach (var c in untagged)
        {
            phase.Advance(c.Name);
            var sample = (await discogs!.GetArtistReleasesAsync(c.DiscogsId!.Value, ct))
                .Where(r => r.Role == "Main")
                .OrderByDescending(r => r.Type == "master")
                .ThenByDescending(r => r.Stats?.Community?.InCollection ?? 0)
                .Take(options.DiscogsGenreSample)
                .ToList();
            if (sample.Count == 0) continue;

            var needed = (sample.Count + 1) / 2;
            int hits = 0, misses = 0;
            var styles = new List<string>();
            foreach (var item in sample)
            {
                var all = await DiscogsGenresAsync(item, ct);
                if (all is null) { misses++; continue; }

                c.AllTags = c.AllTags.Union(all, StringComparer.OrdinalIgnoreCase).ToList();
                var electronic = ElectronicStyles(all);
                if (electronic.Count > 0) { hits++; styles.AddRange(electronic); }
                else misses++;

                // Arrêt dès que l'issue est connue, pour économiser le quota Discogs.
                if (hits >= needed || misses > sample.Count - needed) break;
            }

            if (hits >= needed)
            {
                c.ElectronicTags = styles.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(8).ToList();
                c.GenreSource = "Discogs";
                classified++;
            }
        }
        reporter.Info($"Discogs : {classified} artiste(s) sans tag MusicBrainz identifié(s) comme électroniques "
                      + $"sur {untagged.Count} examiné(s)");
    }

    private bool IsRetained(Candidate c) =>
        c.Links.Count > 0 && (c.IsElectronic || (options.IncludeUntagged && c.AllTags.Count == 0));

    // --- 5. Œuvres et éditions MusicBrainz ------------------------------------------

    private async Task CollectMusicBrainzWorksAsync(CancellationToken ct)
    {
        var retained = _byMbId.Values.Where(IsRetained).ToList();
        using var phase = reporter.Begin("Éditions (labels, catalogue, formats)", retained.Count);

        foreach (var c in retained)
        {
            phase.Advance(c.Name);
            var rgs = _releaseGroups.GetValueOrDefault(c.MbId!) ?? [];
            var releases = await mb.BrowseReleasesByArtistAsync(c.MbId!, ct);
            var releasesByRg = releases.Where(r => r.ReleaseGroup is not null)
                .GroupBy(r => r.ReleaseGroup!.Id).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var rg in rgs)
            {
                var rgElectronic = genres.Matching(rg.Tags, rg.Genres);
                // Une sortie taguée non électronique (rock, jazz…) est écartée ; une sortie sans tag hérite du genre de l'artiste.
                var rgTagged = GenreMatcher.Merge(rg.Tags, rg.Genres).Count > 0;
                if (rgElectronic.Count == 0 && rgTagged) continue;

                var work = GetOrAddWork(rg, releasesByRg.GetValueOrDefault(rg.Id) ?? [], c.Name);
                work.Genres = work.Genres.Concat(rgElectronic.Count > 0 ? rgElectronic : c.ElectronicTags.Take(3))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                work.Unclassified = work.Genres.Count == 0;
                AddArtist(work, c);
            }

            if (options.IncludeAppearances && c.IsElectronic)
            {
                var own = rgs.Select(rg => rg.Id).ToHashSet();
                var appearances = await mb.BrowseReleasesByTrackArtistAsync(c.MbId!, ct);
                foreach (var group in appearances.Where(r => r.ReleaseGroup is not null && !own.Contains(r.ReleaseGroup.Id))
                             .GroupBy(r => r.ReleaseGroup!.Id))
                {
                    var work = GetOrAddWork(group.First().ReleaseGroup!, group.ToList(), "Various Artists");
                    if (!work.SecondaryTypes.Contains("Apparition")) work.SecondaryTypes.Add("Apparition");
                    work.Genres = work.Genres.Union(c.ElectronicTags.Take(2)).ToList();
                    AddArtist(work, c);
                }
            }
        }
        reporter.Info($"{_worksByKey.Count} œuvre(s) MusicBrainz retenue(s)");
    }

    private Work GetOrAddWork(MbReleaseGroup rg, List<MbRelease> releases, string fallbackCredit)
    {
        var key = $"mb:{rg.Id}";
        if (_worksByKey.TryGetValue(key, out var existing)) return existing;

        var tags = GenreMatcher.Merge(rg.Tags, rg.Genres);
        var work = new Work
        {
            Title = rg.Title,
            ArtistCredit = Credit(releases.FirstOrDefault()?.ArtistCredit) ?? fallbackCredit,
            MbReleaseGroupId = rg.Id,
            PrimaryType = rg.PrimaryType,
            SecondaryTypes = rg.SecondaryTypes?.ToList() ?? [],
            FirstReleaseDate = rg.FirstReleaseDate is { Length: > 0 } d ? d
                : releases.Select(r => r.Date).Where(x => !string.IsNullOrEmpty(x)).Order().FirstOrDefault(),
            Rating = rg.Rating?.Value,
            RatingVotes = rg.Rating?.VotesCount ?? 0,
            TagVotes = tags.Sum(t => t.Count),
        };
        foreach (var r in releases.OrderBy(r => r.Date ?? "9999"))
        {
            work.Releases.Add(new ReleaseInfo(
                r.Title,
                r.Date,
                r.Country,
                JoinOrNull(Formats(r.Media)),
                JoinOrNull(r.LabelInfo?.Select(li => li.Label is null
                    ? $"[{li.CatalogNumber}]"
                    : string.IsNullOrWhiteSpace(li.CatalogNumber) ? li.Label.Name : $"{li.Label.Name} [{li.CatalogNumber}]")),
                string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode,
                r.Status,
                $"https://musicbrainz.org/release/{r.Id}"));
        }
        _worksByKey[key] = work;
        return work;
    }

    private static void AddArtist(Work work, Candidate c)
    {
        if (!work.LiegeArtists.Contains(c)) work.LiegeArtists.Add(c);
    }

    // --- 6. Discogs : popularité, œuvres absentes de MusicBrainz --------------------

    private async Task EnrichFromDiscogsAsync(CancellationToken ct)
    {
        var targets = AllCandidates()
            .Where(c => c.DiscogsId is not null && (c.MbId is null ? c.Links.Count > 0 : IsRetained(c)))
            .ToList();
        using var phase = reporter.Begin("Discogs : discographies et popularité", targets.Count);
        var added = 0;

        foreach (var c in targets)
        {
            phase.Advance(c.Name);
            var items = (await discogs!.GetArtistReleasesAsync(c.DiscogsId!.Value, ct))
                .Where(r => r.Role == "Main")
                .GroupBy(r => NormalizeTitle(r.Title))
                .Select(g => g.OrderByDescending(r => r.Type == "master").First())
                .ToList();

            var ownWorks = _worksByKey.Values.Where(w => w.LiegeArtists.Contains(c)).ToList();
            foreach (var item in items)
            {
                var match = ownWorks.FirstOrDefault(w => NormalizeTitle(w.Title) == NormalizeTitle(item.Title));
                if (match is not null)
                {
                    ApplyDiscogs(match, item);
                    continue;
                }

                // Œuvre absente de MusicBrainz : on vérifie le genre sur Discogs pour les artistes non classés.
                var dgGenres = c.IsElectronic ? c.ElectronicTags.Take(3).ToList() : await DiscogsElectronicStylesAsync(item, ct);
                if (dgGenres is null || dgGenres.Count == 0) continue;
                if (!c.IsElectronic)
                {
                    c.ElectronicTags = c.ElectronicTags.Union(dgGenres).Take(8).ToList();
                    c.GenreSource = "Discogs";
                }

                var work = new Work
                {
                    Title = item.Title,
                    ArtistCredit = DiscogsClient.CleanName(item.Artist ?? c.Name),
                    PrimaryType = GuessType(item.Format),
                    FirstReleaseDate = item.Year is > 0 ? item.Year.ToString() : null,
                    Genres = dgGenres,
                };
                ApplyDiscogs(work, item);
                if (item.Label is not null || item.Format is not null)
                    work.Releases.Add(new ReleaseInfo(item.Title, work.FirstReleaseDate, null, item.Format, item.Label, null, null, item.WebUrl));
                AddArtist(work, c);
                _worksByKey[$"dg:{item.Type}:{item.Id}"] = work;
                added++;
            }
        }
        reporter.Info($"Discogs : {added} œuvre(s) absente(s) de MusicBrainz ajoutée(s)");
    }

    private static void ApplyDiscogs(Work work, DgArtistRelease item)
    {
        work.DiscogsUrl ??= item.WebUrl;
        if (item.Type == "master") work.DiscogsMasterId ??= item.Id;
        if (item.Stats?.Community is { } community)
        {
            work.DiscogsHave = Math.Max(work.DiscogsHave ?? 0, community.InCollection);
            work.DiscogsWant = Math.Max(work.DiscogsWant ?? 0, community.InWantlist);
        }
        if (work.FirstReleaseDate is null && item.Year is > 0) work.FirstReleaseDate = item.Year.ToString();
    }

    private async Task<List<string>?> DiscogsElectronicStylesAsync(DgArtistRelease item, CancellationToken ct)
    {
        if (_discogsLookups >= options.DiscogsMaxLookups) return null;
        _discogsLookups++;
        var all = await DiscogsGenresAsync(item, ct);
        return all is null ? null : ElectronicStyles(all);
    }

    /// <summary>Genres + styles Discogs d'un master ou d'une release.</summary>
    private async Task<List<string>?> DiscogsGenresAsync(DgArtistRelease item, CancellationToken ct)
    {
        var detail = item.Type == "master"
            ? await discogs!.GetMasterAsync(item.Id, ct)
            : await discogs!.GetReleaseAsync(item.Id, ct);
        return detail is null ? null : (detail.Genres ?? []).Concat(detail.Styles ?? []).ToList();
    }

    /// <summary>Styles électroniques d'une liste genres+styles Discogs, ou liste vide si ce n'est pas de l'électro.</summary>
    /// <remarks>Seuls les styles électroniques sont gardés : un disque "Electronic, Rock" ne doit pas afficher "Psychedelic Rock".</remarks>
    private List<string> ElectronicStyles(List<string> all)
    {
        var isElectronic = all.Any(g => g.Equals("Electronic", StringComparison.OrdinalIgnoreCase) || genres.IsElectronic(g));
        if (!isElectronic) return [];
        return all.Where(g => !g.Equals("Electronic", StringComparison.OrdinalIgnoreCase) && genres.IsElectronic(g))
            .DefaultIfEmpty("Electronic").Take(4).ToList();
    }

    // --- 7. Labels liégeois ----------------------------------------------------------

    private async Task CollectLabelWorksAsync(List<AreaInfo> areas, CancellationToken ct)
    {
        var names = areas.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var labels = new Dictionary<string, MbLabel>();
        using (var phase = reporter.Begin("Labels liégeois", names.Count))
        {
            foreach (var name in names)
            {
                phase.Advance(name);
                foreach (var label in await mb.SearchLabelsAsync($"area:{Quote(name)}", ct))
                    if (label.Area is not null && _areaIds.Contains(label.Area.Id)) labels.TryAdd(label.Id, label);
            }
        }

        var electronicLabels = labels.Values.Where(l => genres.Matching(l.Tags, null).Count > 0).ToList();
        var others = labels.Values.Except(electronicLabels).Select(l => l.Name).Order().ToList();
        reporter.Info($"{labels.Count} label(s) liégeois, dont {electronicLabels.Count} tagué(s) électro"
                      + (others.Count > 0 ? $" — non tagués (à vérifier manuellement) : {string.Join(", ", others.Take(25))}" : ""));

        var toBrowse = options.IncludeUntagged ? labels.Values.ToList() : electronicLabels;
        using var browse = reporter.Begin("Catalogues des labels liégeois", toBrowse.Count);
        foreach (var label in toBrowse)
        {
            browse.Advance(label.Name);
            var isElectronic = electronicLabels.Contains(label);
            foreach (var group in (await mb.BrowseReleasesByLabelAsync(label.Id, ct))
                         .Where(r => r.ReleaseGroup is not null).GroupBy(r => r.ReleaseGroup!.Id))
            {
                var releases = group.ToList();
                var work = GetOrAddWork(releases[0].ReleaseGroup!, releases, label.Name);
                if (work.LiegeArtists.Count > 0) continue; // déjà rattachée à un artiste liégeois

                foreach (var credit in releases[0].ArtistCredit ?? [])
                {
                    if (credit.Artist is null) continue;
                    var c = GetOrAddMb(credit.Artist);
                    c.AddLink(new(LinkKind.Label, $"Publié sur le label liégeois {label.Name}", "MusicBrainz"));
                    if (isElectronic && c.ElectronicTags.Count == 0) c.ElectronicTags = genres.Matching(label.Tags, null);
                    AddArtist(work, c);
                }
                if (isElectronic) work.Genres = work.Genres.Union(genres.Matching(label.Tags, null)).ToList();
                work.Unclassified = work.Genres.Count == 0;
            }
        }
    }

    // --- Utilitaires -----------------------------------------------------------------

    private IEnumerable<Candidate> AllCandidates() => _byMbId.Values.Concat(_byDiscogsId.Values).Distinct();

    private Candidate GetOrAddMb(MbArtist artist)
    {
        if (_byMbId.TryGetValue(artist.Id, out var c)) return c;
        c = new Candidate
        {
            Name = artist.Name,
            MbId = artist.Id,
            Type = artist.Type,
            Disambiguation = artist.Disambiguation,
            Country = artist.Country,
        };
        c.Urls["MusicBrainz"] = $"https://musicbrainz.org/artist/{artist.Id}";
        _byMbId[artist.Id] = c;
        return c;
    }

    private void ApplyMbDetails(Candidate c, MbArtist details)
    {
        c.MbDetails = details;
        c.Type ??= details.Type;
        c.Disambiguation ??= details.Disambiguation;
        c.Country ??= details.Country;
        c.LifeSpan = details.LifeSpan is { Begin: not null } ls ? $"{ls.Begin}–{ls.End}" : null;

        var tags = GenreMatcher.Merge(details.Tags, details.Genres);
        c.AllTags = tags.Select(t => t.Name).ToList();
        c.ElectronicTags = genres.Matching(details.Tags, details.Genres).Take(8).ToList();
        if (c.ElectronicTags.Count > 0) c.GenreSource = "MusicBrainz";

        foreach (var rel in details.Relations ?? [])
        {
            if (rel.Url?.Resource is not { } url) continue;
            var kind = UrlKind(url);
            if (kind is not null) c.Urls.TryAdd(kind, url);
        }

        if (DiscogsIdOf(details) is { } dgId)
        {
            c.DiscogsId ??= dgId;
            _byDiscogsId.TryAdd(dgId, c);
        }
    }

    private static void AddDiscogsUrls(Candidate c, DgArtist dg)
    {
        c.Urls.TryAdd("Discogs", DiscogsClient.ArtistUrl(dg.Id));
        foreach (var url in dg.Urls ?? [])
            if (UrlKind(url) is { } kind) c.Urls.TryAdd(kind, url);
    }

    private static int? DiscogsIdOf(MbArtist details) =>
        (details.Relations ?? [])
        .Select(r => r.Url?.Resource)
        .Where(u => u is not null && u.Contains("discogs.com"))
        .Select(u => DiscogsClient.ParseArtistId(u!))
        .FirstOrDefault(id => id is not null);

    private static string? UrlKind(string url)
    {
        var u = url.ToLowerInvariant();
        (string Domain, string Kind)[] known =
        [
            ("discogs.com", "Discogs"), ("bandcamp.com", "Bandcamp"), ("soundcloud.com", "SoundCloud"),
            ("spotify.com", "Spotify"), ("deezer.com", "Deezer"), ("music.apple.com", "Apple Music"),
            ("youtube.com", "YouTube"), ("beatport.com", "Beatport"), ("ra.co", "Resident Advisor"),
            ("residentadvisor.net", "Resident Advisor"), ("wikidata.org", "Wikidata"),
            ("wikipedia.org", "Wikipedia"), ("allmusic.com", "AllMusic"), ("last.fm", "Last.fm"),
            ("facebook.com", "Facebook"), ("instagram.com", "Instagram"), ("mixcloud.com", "Mixcloud"),
        ];
        foreach (var (domain, kind) in known)
            if (u.Contains(domain)) return kind;
        return null;
    }

    private static bool MentionsLiege(string? text) => text is not null && LiegeRegex().IsMatch(text);

    private static string Excerpt(string text)
    {
        var m = LiegeRegex().Match(text);
        var start = Math.Max(0, m.Index - 60);
        var len = Math.Min(text.Length - start, 140);
        return (start > 0 ? "…" : "") + text.Substring(start, len).ReplaceLineEndings(" ") + (start + len < text.Length ? "…" : "");
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Credit(List<MbArtistCredit>? credits) =>
        credits is null || credits.Count == 0 ? null : string.Concat(credits.Select(c => c.Name + c.JoinPhrase));

    private static IEnumerable<string> Formats(List<MbMedium>? media) =>
        (media ?? []).GroupBy(m => m.Format ?? "?").Select(g => g.Count() > 1 ? $"{g.Count()}×{g.Key}" : g.Key);

    private static string? JoinOrNull(IEnumerable<string>? items)
    {
        var list = items?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        return list is null || list.Count == 0 ? null : string.Join(", ", list);
    }

    private static string? GuessType(string? format)
    {
        if (format is null) return null;
        var f = format.ToUpperInvariant();
        if (f.Contains("LP") || f.Contains("ALBUM")) return "Album";
        if (f.Contains("EP")) return "EP";
        if (f.Contains("SINGLE") || f.Contains("12\"") || f.Contains("7\"")) return "Single";
        return null;
    }

    public static string NormalizeTitle(string title)
    {
        var decomposed = title.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(li[eèé]ge|luik|l[üu]ttich|li[eé]geois(e|es)?|li[eé]gois)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiegeRegex();
}
