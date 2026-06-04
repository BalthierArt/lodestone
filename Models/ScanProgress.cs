namespace Lodestone.Models;

public sealed class ScanProgress
{
    public static readonly ScanProgress Idle = new()
    {
        Status = "Idle.",
        Operation = string.Empty,
        Source = string.Empty
    };

    public bool IsActive { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = "Idle.";
    public int Completed { get; set; }
    public int Total { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public float Percent => Total <= 0 ? 0f : Math.Clamp((float)Completed / Total, 0f, 1f);
}

public sealed record QuestLookupProgress(string Status, float Percent, bool Indeterminate = false);
