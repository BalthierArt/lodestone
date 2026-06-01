using Dalamud.Configuration;
using Dalamud.Plugin;
using Lodestone.Models;

namespace Lodestone;

[Serializable]
public class Configuration : IPluginConfiguration
{
    private static readonly Vector4 DefaultCurrentDayHighlightColor = new(1f, 0.8f, 0.18f, 1f);
    private static readonly Vector4 DefaultDayColor = new(0.12f, 0.10f, 0.08f, 1f);
    private static readonly Vector4 DefaultDayHighlightColor = new(0.70f, 0.55f, 0.30f, 0.55f);
    private static readonly Vector4 DefaultDayOfWeekColor = new(0.90f, 0.84f, 0.65f, 1f);

    public int Version { get; set; } = 15;
    public string Region { get; set; } = "na";
    public bool ShowEvents { get; set; } = true;
    public bool ShowTopics { get; set; } = true;
    public bool ShowNotices { get; set; } = true;
    public bool ShowMaintenance { get; set; } = true;
    public bool ShowUpdates { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool ShowRecovery { get; set; } = true;
    public bool ShowDayImages { get; set; } = true;
    public bool ShowCalendarTextOnHoverOnly { get; set; } = true;
    public bool HoverTextClickOpensEntry { get; set; } = false;
    public bool AutoCycleDayHeroImages { get; set; } = false;
    public bool UseCustomDayImageDim { get; set; } = false;
    public float DayImageDimAmount { get; set; } = 0.28f;
    public bool ShowDtrEntry { get; set; } = true;
    public bool UseShortDtrText { get; set; } = false;
    public bool HideDtrWhenNoEntries { get; set; } = false;
    public bool DtrShowEvents { get; set; } = true;
    public bool DtrShowNotes { get; set; } = true;
    public bool DtrShowPartyEvents { get; set; } = true;
    public bool DtrShowActiveMaintenance { get; set; } = true;
    public bool DtrShowUpcomingMaintenance { get; set; } = true;
    public bool EnableNoteAlarms { get; set; } = true;
    public bool UseTwelveHourNoteTimes { get; set; } = false;
    public bool UseCustomNoteAlarmWarning { get; set; } = false;
    public int NoteAlarmWarningMinutes { get; set; } = 60;
    public int CustomNoteAlarmWarningMinutes { get; set; } = 10;
    public bool NotifyMaintenance { get; set; } = true;
    public bool OnlyCurrentAndFuture { get; set; } = false;
    public bool AutoRefreshOnStartup { get; set; } = true;
    public bool OpenCalendarOnStartup { get; set; } = false;
    public int RefreshMinutes { get; set; } = 1440;
    public int MaxEntriesToScan { get; set; } = 120;
    public int MaxPagesPerSource { get; set; } = 3;
    public int MaintenanceWarningHours { get; set; } = 24;
    public int PriorityUpdates { get; set; } = 100;
    public int PriorityTopics { get; set; } = 90;
    public int PriorityMaintenance { get; set; } = 5;
    public int PriorityRecovery { get; set; } = 70;
    public int PriorityStatus { get; set; } = 70;
    public int PriorityNotices { get; set; } = 60;
    public int PriorityEvents { get; set; } = 0;
    public List<PriorityRule> PriorityRules { get; set; } = DefaultPriorityRules();
    public float[] CalendarCurrentDayHighlightColor { get; set; } = ColorArray(DefaultCurrentDayHighlightColor);
    public float[] CalendarDayColor { get; set; } = ColorArray(DefaultDayColor);
    public float[] CalendarDayHighlightColor { get; set; } = ColorArray(DefaultDayHighlightColor);
    public float[] CalendarDayOfWeekColor { get; set; } = ColorArray(DefaultDayOfWeekColor);
    public bool UseFullDayNames { get; set; } = false;
    public bool ShowPartyEvents { get; set; } = true;
    public bool ShowSubmarineReturns { get; set; } = true;
    public bool PartySyncEnabled { get; set; } = false;
    public bool PartySyncExternalBridgeEnabled { get; set; } = false;
    public string PartySyncSupabaseUrl { get; set; } = string.Empty;
    public string PartySyncAnonKey { get; set; } = string.Empty;
    public string PartySyncKey { get; set; } = string.Empty;
    public string PartySyncDisplayName { get; set; } = string.Empty;
    public int PartySyncPollSeconds { get; set; } = 60;
    public List<PartyEvent> PartySyncBridgeEvents { get; set; } = [];
    public List<string> HiddenEntryIds { get; set; } = [];
    public List<CalendarNote> Notes { get; set; } = [];

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        if (Version < 2)
        {
            if (RefreshMinutes == 180)
                RefreshMinutes = 1440;

            ShowCalendarTextOnHoverOnly = true;
            Version = 2;
        }

        if (Version < 3)
        {
            UseShortDtrText = false;
            HideDtrWhenNoEntries = false;
            Version = 3;
            Save();
        }

        if (Version < 4)
        {
            EnableNoteAlarms = true;
            UseCustomNoteAlarmWarning = false;
            NoteAlarmWarningMinutes = 60;
            CustomNoteAlarmWarningMinutes = 10;
            Notes ??= [];
            Version = 4;
            Save();
        }

        if (Version < 5)
        {
            SetDefaultPriorities();
            Version = 5;
            Save();
        }

        if (Version < 6)
        {
            SetDefaultCalendarCustomization();
            Version = 6;
            Save();
        }

        if (Version < 7)
        {
            ShowPartyEvents = true;
            PartySyncEnabled = false;
            PartySyncSupabaseUrl ??= string.Empty;
            PartySyncAnonKey ??= string.Empty;
            PartySyncKey ??= string.Empty;
            PartySyncDisplayName ??= string.Empty;
            PartySyncPollSeconds = 60;
            Version = 7;
            Save();
        }

        if (Version < 8)
        {
            PartySyncExternalBridgeEnabled = false;
            PartySyncBridgeEvents ??= [];
            Version = 8;
            Save();
        }

        if (Version < 9)
        {
            DtrShowEvents = true;
            DtrShowNotes = true;
            DtrShowPartyEvents = true;
            DtrShowActiveMaintenance = true;
            DtrShowUpcomingMaintenance = true;
            PriorityRules = DefaultPriorityRules();
            Version = 9;
            Save();
        }

        if (Version < 10)
        {
            if (PriorityMaintenance >= PriorityEvents)
                PriorityMaintenance = Math.Max(0, PriorityEvents - 1);

            AddDefaultPriorityRuleIfMissing("maintenance_below_events");
            Version = 10;
            Save();
        }

        if (Version < 11)
        {
            HoverTextClickOpensEntry = false;
            Version = 11;
            Save();
        }

        if (Version < 12)
        {
            AutoCycleDayHeroImages = false;
            Version = 12;
            Save();
        }

        if (Version < 13)
        {
            UseCustomDayImageDim = false;
            DayImageDimAmount = 0.28f;
            PriorityEvents = 0;
            PriorityRules?.RemoveAll(rule => rule.Id.Equals("maintenance_below_events", StringComparison.OrdinalIgnoreCase)
                                             || rule.Id.Equals("recovery_below_events", StringComparison.OrdinalIgnoreCase));
            Version = 13;
            Save();
        }

        if (Version < 14)
        {
            ShowSubmarineReturns = true;
            Version = 14;
            Save();
        }

        if (Version < 15)
        {
            UseTwelveHourNoteTimes = false;
            Version = 15;
            Save();
        }

        PriorityRules ??= DefaultPriorityRules();
        foreach (var rule in PriorityRules.Where(rule => string.IsNullOrWhiteSpace(rule.Id)))
            rule.Id = Guid.NewGuid().ToString("N");
    }

    public void Save() => pluginInterface?.SavePluginConfig(this);

    public void SetDefaultPriorities()
    {
        PriorityUpdates = 100;
        PriorityTopics = 90;
        PriorityMaintenance = 5;
        PriorityRecovery = 70;
        PriorityStatus = 70;
        PriorityNotices = 60;
        PriorityEvents = 0;
        PriorityRules = DefaultPriorityRules();
    }

    public void SetDefaultCalendarCustomization()
    {
        CalendarCurrentDayHighlightColor = ColorArray(DefaultCurrentDayHighlightColor);
        CalendarDayColor = ColorArray(DefaultDayColor);
        CalendarDayHighlightColor = ColorArray(DefaultDayHighlightColor);
        CalendarDayOfWeekColor = ColorArray(DefaultDayOfWeekColor);
        UseFullDayNames = false;
        UseCustomDayImageDim = false;
        DayImageDimAmount = 0.28f;
    }

    public Vector4 CurrentDayHighlightColor() => ReadColor(CalendarCurrentDayHighlightColor, DefaultCurrentDayHighlightColor);
    public Vector4 DayColor() => ReadColor(CalendarDayColor, DefaultDayColor);
    public Vector4 DayHighlightColor() => ReadColor(CalendarDayHighlightColor, DefaultDayHighlightColor);
    public Vector4 DayOfWeekColor() => ReadColor(CalendarDayOfWeekColor, DefaultDayOfWeekColor);

    public static float[] ColorArray(Vector4 color) => [color.X, color.Y, color.Z, color.W];

    private static Vector4 ReadColor(float[]? values, Vector4 fallback)
    {
        if (values is not { Length: >= 4 })
            return fallback;

        return new Vector4(
            Math.Clamp(values[0], 0f, 1f),
            Math.Clamp(values[1], 0f, 1f),
            Math.Clamp(values[2], 0f, 1f),
            Math.Clamp(values[3], 0f, 1f));
    }

    public int GetPriority(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.Update => PriorityUpdates,
        LodestoneEntryKind.Topic => PriorityTopics,
        LodestoneEntryKind.Maintenance => PriorityMaintenance,
        LodestoneEntryKind.Recovery => PriorityRecovery,
        LodestoneEntryKind.Status => PriorityStatus,
        LodestoneEntryKind.Notice => PriorityNotices,
        LodestoneEntryKind.SpecialEvent => PriorityEvents,
        _ => 30
    };

    public IReadOnlyList<PriorityRule> GetPriorityRules()
    {
        if (PriorityRules.Count == 0)
            PriorityRules = DefaultPriorityRules();

        return PriorityRules;
    }

    private void AddDefaultPriorityRuleIfMissing(string id)
    {
        PriorityRules ??= [];
        if (PriorityRules.Any(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return;

        var defaultRule = DefaultPriorityRules().FirstOrDefault(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (defaultRule != null)
            PriorityRules.Add(defaultRule);
    }

    private static List<PriorityRule> DefaultPriorityRules()
        =>
        [
            new()
            {
                Id = "producer_live",
                Label = "Letter from the Producer LIVE",
                AnyTextContains = ["Letter from the Producer", "Producer LIVE"],
                UseAbsolutePriority = true,
                AbsolutePriority = 10_000,
                HeroAsset = "producer-live-hero.png",
                Notes = "Always wins the day image."
            },
            new()
            {
                Id = "duty_commenced",
                Label = "Duty Commenced",
                AllTextContains = ["Duty Commenced"],
                PriorityOffset = 25,
                Notes = "Shows above generic news topics."
            },
            new()
            {
                Id = "actions_taken",
                Label = "Actions Taken Against Players",
                AllTextContains = ["Actions Taken Against"],
                PriorityOffset = -25,
                Notes = "Common moderation topics should not steal the day image."
            },
            new()
            {
                Id = "eternal_bonding_restricted",
                Label = "Eternal Bonding Restrictions",
                AllTextContains = ["Eternal Bonding"],
                AnyTextContains = ["Reservation", "Restriction", "Restricted"],
                AnchorKind = LodestoneEntryKind.SpecialEvent,
                AnchorOffset = -1,
                HeroAsset = "eternal-bonding-restricted-hero.png",
                Notes = "Uses a custom header, but sits under seasonal events."
            }
        ];
}
