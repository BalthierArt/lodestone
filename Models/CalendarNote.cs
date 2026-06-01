namespace Lodestone.Models;

public sealed class CalendarNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Today;
    public string Text { get; set; } = string.Empty;
    public int? Hour { get; set; }
    public int? Minute { get; set; }
    public bool AlarmEnabled { get; set; } = true;
    public List<int> NotifiedWarningMinutes { get; set; } = [];

    public bool HasTime => Hour.HasValue && Minute.HasValue;

    public DateTime? ScheduledAt => HasTime
        ? Date.Date.AddHours(Hour!.Value).AddMinutes(Minute!.Value)
        : null;
}
