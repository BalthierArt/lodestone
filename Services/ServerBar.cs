using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class ServerBar : IDisposable
{
    private readonly Plugin plugin;
    private readonly IDtrBarEntry? entry;
    private readonly HashSet<string> notifiedMaintenance = [];
    private long lastUpdate;

    public ServerBar(Plugin plugin)
    {
        this.plugin = plugin;
        entry = Plugin.DtrBar.Get("Lodestone");
        if (entry == null)
            return;

        entry.Text = "Lodestone";
        entry.Shown = plugin.Configuration.ShowDtrEntry;
        entry.Tooltip = new SeStringBuilder()
            .AddText("Open Lodestone Calendar")
            .BuiltString;
        entry.OnClick += OnClick;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        if (entry == null)
            return;

        Plugin.Framework.Update -= Update;
        entry.OnClick -= OnClick;
        entry.Remove();
    }

    public void Refresh()
    {
        lastUpdate = 0;
    }

    private void Update(IFramework framework)
    {
        if (entry == null)
            return;

        if (!plugin.Configuration.ShowDtrEntry)
        {
            entry.Shown = false;
            return;
        }

        if (Environment.TickCount64 - lastUpdate < 5000)
            return;
        lastUpdate = Environment.TickCount64;

        var currentEntries = plugin.Configuration.DtrShowEvents
            ? plugin.CalendarWindow.GetServerBarEntries(DateTime.Now).ToArray()
            : [];
        var currentNotes = plugin.Configuration.DtrShowNotes
            ? plugin.Configuration.Notes
                .Where(note => note.Date.Date == DateTime.Now.Date)
                .OrderBy(note => note.ScheduledAt ?? note.Date)
                .ThenBy(note => note.Text)
                .ToArray()
            : [];
        var currentPartyEvents = plugin.Configuration.DtrShowPartyEvents && plugin.Configuration.ShowPartyEvents
            ? plugin.PartySyncService.GetEventsForDay(DateTime.Now).ToArray()
            : [];
        var allMaintenanceWarnings = plugin.CalendarWindow.GetMaintenanceWarnings().ToArray();
        var maintenanceWarnings = allMaintenanceWarnings
            .Where(ShouldShowMaintenanceInDtr)
            .ToArray();

        if (plugin.Configuration.HideDtrWhenNoEntries && currentEntries.Length == 0 && currentNotes.Length == 0 && currentPartyEvents.Length == 0 && maintenanceWarnings.Length == 0)
        {
            entry.Shown = false;
            return;
        }

        entry.Shown = true;
        UpdateBarString(currentEntries, currentNotes, currentPartyEvents, maintenanceWarnings);
        NotifyMaintenance(allMaintenanceWarnings);
    }

    private bool ShouldShowMaintenanceInDtr(LodestoneEntry entry)
    {
        var active = entry.StartsAt <= DateTime.Now && entry.EffectiveEnd >= DateTime.Now;
        return active ? plugin.Configuration.DtrShowActiveMaintenance : plugin.Configuration.DtrShowUpcomingMaintenance;
    }

    private void UpdateBarString(IReadOnlyList<LodestoneEntry> currentEntries, IReadOnlyList<CalendarNote> currentNotes, IReadOnlyList<PartyEvent> currentPartyEvents, IReadOnlyList<LodestoneEntry> maintenanceWarnings)
    {
        var calendarCount = currentEntries.Count + currentNotes.Count + currentPartyEvents.Count;
        var activeMaintenance = maintenanceWarnings.Any(entry => entry.StartsAt <= DateTime.Now && entry.EffectiveEnd >= DateTime.Now);
        var text = plugin.Configuration.UseShortDtrText
            ? $"{(char)SeIconChar.Clock} {calendarCount}"
            : "No calendar events";

        if (calendarCount > 0)
        {
            text = plugin.Configuration.UseShortDtrText
                ? $"{(char)SeIconChar.Clock} {calendarCount}"
                : $"Calendar events: {calendarCount}";
        }
        else if (maintenanceWarnings.Count > 0)
        {
            text = plugin.Configuration.UseShortDtrText
                ? $"{(char)SeIconChar.Clock} !"
                : activeMaintenance ? "Maintenance active" : "Maintenance soon";
        }

        entry!.Text = text;
        entry.Tooltip = BuildTooltip(currentEntries, currentNotes, currentPartyEvents, maintenanceWarnings);
    }

    private SeString? BuildTooltip(IReadOnlyList<LodestoneEntry> currentEntries, IReadOnlyList<CalendarNote> currentNotes, IReadOnlyList<PartyEvent> currentPartyEvents, IReadOnlyList<LodestoneEntry> maintenanceWarnings)
    {
        var tooltip = new SeStringBuilder();

        if (currentEntries.Count + currentNotes.Count + currentPartyEvents.Count > 0)
        {
            foreach (var item in currentEntries.Take(8))
            {
                tooltip.AddText($"{KindLabel(item.Kind)}: {item.Title}\n");
                tooltip.AddUiForeground(FormatRange(item), 58);
                tooltip.AddText("\n");
            }

            foreach (var note in currentNotes.Take(8))
            {
                tooltip.AddText($"Note: {NoteLabel(note)}\n");
            }

            foreach (var partyEvent in currentPartyEvents.Take(8))
            {
                tooltip.AddText($"Party: {PartyEventLabel(partyEvent)}\n");
            }
        }
        else
        {
            tooltip.AddText("No events, notes, or party events are active today.\n");
        }

        var upcomingMaintenance = maintenanceWarnings
            .Where(warning => !currentEntries.Any(e => e.Id == warning.Id))
            .Take(4)
            .ToArray();

        if (upcomingMaintenance.Length > 0)
        {
            tooltip.AddText("\n");
            tooltip.AddUiForeground("Maintenance warnings", 17);
            tooltip.AddText("\n");
            foreach (var item in upcomingMaintenance)
            {
                tooltip.AddText($"{item.Title}\n");
                tooltip.AddUiForeground(FormatRange(item), 58);
                tooltip.AddText("\n");
            }
        }

        tooltip.AddText("\n");
        tooltip.AddUiForeground("Click to open the Lodestone Calendar.", 58);
        return tooltip.BuiltString;
    }

    private void NotifyMaintenance(IReadOnlyList<LodestoneEntry> maintenanceWarnings)
    {
        if (!plugin.Configuration.NotifyMaintenance || maintenanceWarnings.Count == 0)
            return;

        var primary = maintenanceWarnings[0];
        if (!notifiedMaintenance.Contains(primary.Id))
        {
            var now = DateTime.Now;
            var active = primary.StartsAt <= now && primary.EffectiveEnd >= now;
            notifiedMaintenance.Add(primary.Id);
            Plugin.Notifications.AddNotification(new Notification
            {
                Content = active
                    ? $"Maintenance is active until {primary.EffectiveEnd:t} local: {primary.Title}"
                    : $"Upcoming maintenance {FormatRelative(primary.StartsAt - now)} at {primary.StartsAt:t} local: {primary.Title}",
                Title = "Lodestone",
                Type = NotificationType.Warning,
                Minimized = false
            });
        }
    }

    private void OnClick(DtrInteractionEvent data)
    {
        plugin.CalendarWindow.IsOpen = true;
    }

    private static string FormatRange(LodestoneEntry entry)
    {
        var local = entry.EffectiveEnd.Date > entry.StartsAt.Date
            ? $"{entry.StartsAt:D} - {entry.EffectiveEnd:D}"
            : $"{entry.StartsAt:g} - {entry.EffectiveEnd:g}";

        return string.IsNullOrWhiteSpace(entry.SourceTimeText)
            ? local
            : $"{local} local\nSource: {entry.SourceTimeText}";
    }

    private string NoteLabel(CalendarNote note)
        => note.ScheduledAt is { } scheduledAt
            ? $"{FormatNoteTime(scheduledAt)} {note.Text}"
            : note.Text;

    private string FormatNoteTime(DateTime scheduledAt)
        => plugin.Configuration.UseTwelveHourNoteTimes
            ? scheduledAt.ToString("h:mm tt")
            : scheduledAt.ToString("HH:mm");

    private static string PartyEventLabel(PartyEvent partyEvent)
        => partyEvent.ScheduledAt is { } scheduledAt
            ? $"{scheduledAt:t} {partyEvent.Title}"
            : partyEvent.Title;

    private static string KindLabel(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => "Event",
        LodestoneEntryKind.Maintenance => "Maintenance",
        LodestoneEntryKind.Notice => "Notice",
        LodestoneEntryKind.Update => "Update",
        LodestoneEntryKind.Status => "Status",
        LodestoneEntryKind.Recovery => "Recovery",
        _ => "Topic"
    };

    private static string FormatRelative(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return "now";
        if (span.TotalHours < 1)
            return $"in {(int)Math.Ceiling(span.TotalMinutes)}m";
        if (span.TotalDays < 1)
            return $"in {(int)Math.Ceiling(span.TotalHours)}h";
        return $"in {(int)Math.Ceiling(span.TotalDays)}d";
    }
}
