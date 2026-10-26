using MusicBrainzSearch.MusicBrainz;

namespace MusicBrainzSearch.Domain;

public sealed record AreaInfo(string Id, string Name, string? Type, int Depth);

/// <summary>
/// Construit l'ensemble des zones "liégeoises" : une ou plusieurs zones racines
/// plus leurs subdivisions (relations "part of" dans MusicBrainz : anciennes communes, quartiers…).
/// </summary>
public sealed class AreaResolver(MusicBrainzClient client)
{
    public const string LiegeCityId = "7f24b77b-abe7-42d9-b79c-ee09bbf5c83c";
    public const string LiegeProvinceId = "8ddd31f9-214c-4853-a955-57687a13e643";

    public async Task<List<AreaInfo>> ResolveAsync(
        IEnumerable<string> rootIds, int maxDepth, Action<AreaInfo>? onArea, CancellationToken ct)
    {
        var result = new Dictionary<string, AreaInfo>();
        var queue = new Queue<(string Id, int Depth)>(rootIds.Select(id => (id, 0)));

        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (result.ContainsKey(id)) continue;

            var area = await client.GetAreaAsync(id, ct);
            var info = new AreaInfo(area.Id, area.Name, area.Type, depth);
            result[id] = info;
            onArea?.Invoke(info);

            if (depth >= maxDepth) continue;

            // Sur la zone parente, une relation "part of" en direction "forward" pointe vers une sous-zone
            // ("backward" pointe vers la zone englobante, qu'on ne veut pas remonter).
            foreach (var rel in area.Relations ?? [])
            {
                if (rel is { Type: "part of", Direction: "forward", Area: not null }
                    && !result.ContainsKey(rel.Area.Id))
                {
                    queue.Enqueue((rel.Area.Id, depth + 1));
                }
            }
        }

        return result.Values.OrderBy(a => a.Depth).ThenBy(a => a.Name).ToList();
    }
}
