using System.Text.Json.Serialization;

namespace MusicBrainzSearch.MusicBrainz;

// DTO minimalistes pour l'API JSON MusicBrainz (ws/2). Seuls les champs utiles sont mappés.

public sealed record MbTag(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("count")] int Count);

public sealed record MbRating(
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("votes-count")] int VotesCount);

public sealed record MbLifeSpan(
    [property: JsonPropertyName("begin")] string? Begin,
    [property: JsonPropertyName("end")] string? End,
    [property: JsonPropertyName("ended")] bool? Ended);

public sealed record MbAreaRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type);

public sealed class MbArea
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("score")] public int? Score { get; init; }
    [JsonPropertyName("relations")] public List<MbRelation>? Relations { get; init; }
}

public sealed class MbUrl
{
    [JsonPropertyName("resource")] public string Resource { get; init; } = "";
}

public sealed class MbRelation
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
    [JsonPropertyName("target-type")] public string? TargetType { get; init; }
    [JsonPropertyName("artist")] public MbArtist? Artist { get; init; }
    [JsonPropertyName("area")] public MbAreaRef? Area { get; init; }
    [JsonPropertyName("url")] public MbUrl? Url { get; init; }
    [JsonPropertyName("begin")] public string? Begin { get; init; }
    [JsonPropertyName("end")] public string? End { get; init; }
}

public sealed class MbArtist
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("sort-name")] public string? SortName { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; init; }
    [JsonPropertyName("area")] public MbAreaRef? Area { get; init; }
    [JsonPropertyName("begin-area")] public MbAreaRef? BeginArea { get; init; }
    [JsonPropertyName("end-area")] public MbAreaRef? EndArea { get; init; }
    [JsonPropertyName("life-span")] public MbLifeSpan? LifeSpan { get; init; }
    [JsonPropertyName("tags")] public List<MbTag>? Tags { get; init; }
    [JsonPropertyName("genres")] public List<MbTag>? Genres { get; init; }
    [JsonPropertyName("rating")] public MbRating? Rating { get; init; }
    [JsonPropertyName("relations")] public List<MbRelation>? Relations { get; init; }
}

public sealed class MbLabel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("area")] public MbAreaRef? Area { get; init; }
    [JsonPropertyName("tags")] public List<MbTag>? Tags { get; init; }
}

public sealed class MbReleaseGroup
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("primary-type")] public string? PrimaryType { get; init; }
    [JsonPropertyName("secondary-types")] public List<string>? SecondaryTypes { get; init; }
    [JsonPropertyName("first-release-date")] public string? FirstReleaseDate { get; init; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; init; }
    [JsonPropertyName("tags")] public List<MbTag>? Tags { get; init; }
    [JsonPropertyName("genres")] public List<MbTag>? Genres { get; init; }
    [JsonPropertyName("rating")] public MbRating? Rating { get; init; }
}

public sealed class MbArtistCredit
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("joinphrase")] public string? JoinPhrase { get; init; }
    [JsonPropertyName("artist")] public MbArtist? Artist { get; init; }
}

public sealed class MbLabelInfo
{
    [JsonPropertyName("catalog-number")] public string? CatalogNumber { get; init; }
    [JsonPropertyName("label")] public MbLabel? Label { get; init; }
}

public sealed class MbMedium
{
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("track-count")] public int TrackCount { get; init; }
}

public sealed class MbRelease
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("barcode")] public string? Barcode { get; init; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; init; }
    [JsonPropertyName("label-info")] public List<MbLabelInfo>? LabelInfo { get; init; }
    [JsonPropertyName("media")] public List<MbMedium>? Media { get; init; }
    [JsonPropertyName("release-group")] public MbReleaseGroup? ReleaseGroup { get; init; }
    [JsonPropertyName("artist-credit")] public List<MbArtistCredit>? ArtistCredit { get; init; }
}

// Enveloppes de réponses (recherche et "browse").

public sealed class MbArtistSearch
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("artists")] public List<MbArtist> Artists { get; init; } = [];
}

public sealed class MbArtistBrowse
{
    [JsonPropertyName("artist-count")] public int Count { get; init; }
    [JsonPropertyName("artists")] public List<MbArtist> Artists { get; init; } = [];
}

public sealed class MbAreaSearch
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("areas")] public List<MbArea> Areas { get; init; } = [];
}

public sealed class MbLabelSearch
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("labels")] public List<MbLabel> Labels { get; init; } = [];
}

public sealed class MbReleaseGroupBrowse
{
    [JsonPropertyName("release-group-count")] public int Count { get; init; }
    [JsonPropertyName("release-groups")] public List<MbReleaseGroup> ReleaseGroups { get; init; } = [];
}

public sealed class MbReleaseBrowse
{
    [JsonPropertyName("release-count")] public int Count { get; init; }
    [JsonPropertyName("releases")] public List<MbRelease> Releases { get; init; } = [];
}

public sealed class MbUrlLookup
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("resource")] public string Resource { get; init; } = "";
    [JsonPropertyName("relations")] public List<MbRelation>? Relations { get; init; }
}
