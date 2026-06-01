namespace Lodestone.Models;

public sealed class GameEscapeQuest
{
    public string Query { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Acquisition { get; set; } = string.Empty;
    public string QuestGiver { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public string LocationDetail { get; set; } = string.Empty;
    public string ClosestAetheryte { get; set; } = string.Empty;
    public float? MapX { get; set; }
    public float? MapY { get; set; }
    public List<string> Requirements { get; set; } = [];
    public List<string> Rewards { get; set; } = [];
    public List<string> Objectives { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    public bool HasLocation => !string.IsNullOrWhiteSpace(ClosestAetheryte)
                               || (!string.IsNullOrWhiteSpace(Zone) && MapX.HasValue && MapY.HasValue);
}
