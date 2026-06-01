namespace Lodestone.Models;

public enum PartyEventResponseStatus
{
    Interested,
    Maybe
}

public sealed class PartyEvent
{
    public string Id { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public int? Hour { get; set; }
    public int? Minute { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconKey { get; set; } = PartyEventIcons.DefaultKey;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorWorld { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PartyEventResponse> Responses { get; set; } = [];

    public bool HasTime => Hour.HasValue && Minute.HasValue;

    public DateTime? ScheduledAt => HasTime
        ? Date.Date.AddHours(Hour!.Value).AddMinutes(Minute!.Value)
        : null;
}

public sealed class PartyEventResponse
{
    public string PlayerKey { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string PlayerWorld { get; set; } = string.Empty;
    public PartyEventResponseStatus Status { get; set; } = PartyEventResponseStatus.Interested;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record PartyEventIconDefinition(string Key, string Label, uint IconId);

public static class PartyEventIcons
{
    public const string DefaultKey = "trial";

    public static readonly PartyEventIconDefinition[] All =
    [
        new("trial", "Trials", 61804),
        new("raid", "Raids", 61802),
        new("ultimate", "Ultimate Raids", 61832),
        new("chaotic", "Chaotic Alliance Raid", 61850),
        new("dungeon", "Dungeons", 61801),
        new("deep_dungeon", "Deep Dungeons", 61824),
        new("hunt", "The Hunt", 61819),
        new("gathering", "Gathering", 61815),
        new("fishing", "Fishing", 61756),
        new("treasure", "Treasure Hunt", 61808),
        new("pvp", "PvP", 61806),
        new("gold_saucer", "Gold Saucer", 61820),
        new("variant", "V&C Dungeon Finder", 61846),
        new("field_operation", "Eureka / Field Operation", 61833),
        new("occult", "Occult Crescent", 61851),
        new("other", "Other", 61821),
    ];

    public static PartyEventIconDefinition Get(string? key)
        => All.FirstOrDefault(icon => icon.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
