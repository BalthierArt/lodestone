namespace Lodestone.Models;

public enum LodestoneEntryKind
{
    Topic,
    Notice,
    Maintenance,
    Update,
    Status,
    Recovery,
    SpecialEvent
}

public sealed class LodestoneEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public LodestoneEntryKind Kind { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string HeroImageUrl { get; set; } = string.Empty;
    public string StartingNpc { get; set; } = string.Empty;
    public string StartingLocation { get; set; } = string.Empty;
    public List<string> Requirements { get; set; } = [];
    public List<LodestoneReward> Rewards { get; set; } = [];
    public List<string> ImageUrls { get; set; } = [];
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    public DateTime EffectiveEnd => EndsAt ?? StartsAt;
    public bool IsMultiDay => EffectiveEnd.Date > StartsAt.Date;
}

public sealed class LodestoneReward
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
}
