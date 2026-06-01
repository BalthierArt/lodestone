using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class NoteAlarmService : IDisposable
{
    private readonly Plugin plugin;
    private long lastUpdate;

    public NoteAlarmService(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= Update;
    }

    private void Update(IFramework framework)
    {
        if (!plugin.Configuration.EnableNoteAlarms)
            return;

        if (Environment.TickCount64 - lastUpdate < 10000)
            return;

        lastUpdate = Environment.TickCount64;
        var warningMinutes = plugin.Configuration.UseCustomNoteAlarmWarning
            ? Math.Clamp(plugin.Configuration.CustomNoteAlarmWarningMinutes, 1, 1440)
            : Math.Clamp(plugin.Configuration.NoteAlarmWarningMinutes, 1, 1440);
        var now = DateTime.Now;
        var changed = false;

        foreach (var note in plugin.Configuration.Notes.Where(IsAlarmCandidate))
        {
            var scheduledAt = note.ScheduledAt!.Value;
            var notifyAt = scheduledAt.AddMinutes(-warningMinutes);
            if (now < notifyAt || now > scheduledAt)
                continue;

            if (note.NotifiedWarningMinutes.Contains(warningMinutes))
                continue;

            note.NotifiedWarningMinutes.Add(warningMinutes);
            changed = true;
            Plugin.Notifications.AddNotification(new Notification
            {
                Title = "Lodestone Note",
                Content = $"{note.Text} at {scheduledAt:t}",
                Type = NotificationType.Info,
                Minimized = false
            });
        }

        if (changed)
            plugin.Configuration.Save();
    }

    private static bool IsAlarmCandidate(CalendarNote note)
    {
        return note.AlarmEnabled
               && note.HasTime
               && note.ScheduledAt.HasValue
               && note.ScheduledAt.Value >= DateTime.Now.Date;
    }
}
