namespace Lodestone.Models;

public sealed class PriorityRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Priority rule";
    public bool Enabled { get; set; } = true;
    public LodestoneEntryKind? Kind { get; set; }
    public List<string> AnyTextContains { get; set; } = [];
    public List<string> AllTextContains { get; set; } = [];
    public int PriorityOffset { get; set; }
    public bool UseAbsolutePriority { get; set; }
    public int AbsolutePriority { get; set; }
    public LodestoneEntryKind? AnchorKind { get; set; }
    public int AnchorOffset { get; set; }
    public string HeroAsset { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public bool Matches(LodestoneEntry entry)
    {
        if (!Enabled)
            return false;

        if (Kind.HasValue && entry.Kind != Kind.Value)
            return false;

        var text = $"{entry.Title} {entry.Summary}";
        if (AllTextContains.Any(term => !text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (AnyTextContains.Count > 0 && !AnyTextContains.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }
}
