using System.Security.Cryptography;
using System.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class PartySyncService : IDisposable
{
    private readonly Plugin plugin;
    private readonly object eventLock = new();
    private readonly SupabasePartySyncTransport supabaseTransport;
    private readonly ExternalBridgePartySyncTransport externalBridgeTransport;
    private List<PartyEvent> events = [];
    private bool refreshInProgress;
    private long lastPoll;
    private DateTime rangeStart = DateTime.Today.AddMonths(-1);
    private DateTime rangeEnd = DateTime.Today.AddMonths(3);

    public PartySyncService(Plugin plugin)
    {
        this.plugin = plugin;
        supabaseTransport = new SupabasePartySyncTransport(plugin);
        externalBridgeTransport = new ExternalBridgePartySyncTransport(plugin);
        LoadExternalBridgeEvents();
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public string Status { get; private set; } = "Party sync disabled.";
    public bool IsConfigured => supabaseTransport.IsConfigured;
    public bool IsExternalBridgeEnabled => externalBridgeTransport.IsConfigured;
    public bool CanCreateEvents => ActiveTransport() != null;
    public string TransportLabel => ActiveTransport()?.Name ?? "Disabled";
    public int EventsVersion { get; private set; }

    public IReadOnlyList<PartyEvent> Events
    {
        get
        {
            lock (eventLock)
                return events.ToArray();
        }
    }

    public IEnumerable<PartyEvent> GetEventsForDay(DateTime day)
    {
        lock (eventLock)
        {
            return events
                .Where(e => e.Date.Date == day.Date)
                .OrderBy(e => e.ScheduledAt ?? e.Date)
                .ThenBy(e => e.Title)
                .ToArray();
        }
    }

    public void SetVisibleRange(DateTime start, DateTime end)
    {
        rangeStart = start.Date;
        rangeEnd = end.Date;
    }

    public Task RefreshVisibleRangeAsync(bool force = false)
        => RefreshAsync(rangeStart, rangeEnd, force);

    public async Task<bool> RefreshAsync(DateTime start, DateTime end, bool force)
    {
        var transport = ActiveTransport();
        if (transport == null)
        {
            Status = plugin.Configuration.PartySyncEnabled
                ? "Party sync needs Supabase URL, anon key, and party key."
                : "Party sync disabled.";
            return false;
        }

        if (refreshInProgress)
            return false;

        if (transport.PollsAutomatically && !force && Environment.TickCount64 - lastPoll < Math.Clamp(plugin.Configuration.PartySyncPollSeconds, 15, 600) * 1000L)
            return false;

        refreshInProgress = true;
        Status = $"Refreshing party events via {transport.Name}...";
        try
        {
            var loaded = await transport.ListAsync(start, end, force);
            lock (eventLock)
            {
                events = loaded.OrderBy(e => e.ScheduledAt ?? e.Date).ThenBy(e => e.Title).ToList();
                EventsVersion++;
            }

            if (transport.PollsAutomatically)
                lastPoll = Environment.TickCount64;

            Status = transport.Status;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Party sync refresh failed.");
            Status = "Party sync refresh failed.";
            return false;
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    public async Task<bool> SaveEventAsync(PartyEvent partyEvent)
    {
        var transport = ActiveTransport();
        if (transport == null)
        {
            Notify("Party sync is not configured.", NotificationType.Warning);
            return false;
        }

        var actor = CurrentActor();
        if (string.IsNullOrWhiteSpace(actor.Name))
        {
            Notify("Set a party sync display name or log in on a character first.", NotificationType.Warning);
            return false;
        }

        try
        {
            var saved = await transport.SaveEventAsync(partyEvent, actor);
            if (saved == null)
                return false;

            CopySavedFields(partyEvent, saved);
            UpsertLocal(saved);
            Status = transport.Status;
            Notify(transport == externalBridgeTransport ? "Party event saved for external IPC bridge." : "Party event saved.", NotificationType.Success);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to save party event.");
            Status = "Party event save failed.";
            Notify("Party event save failed.", NotificationType.Error);
            return false;
        }
    }

    public async Task<bool> RespondAsync(PartyEvent partyEvent, PartyEventResponseStatus? status)
    {
        var transport = ActiveTransport();
        if (transport == null)
            return false;

        var actor = CurrentActor();
        if (string.IsNullOrWhiteSpace(actor.Name))
        {
            Notify("Set a party sync display name or log in on a character first.", NotificationType.Warning);
            return false;
        }

        try
        {
            var updated = await transport.RespondAsync(partyEvent, actor, status);
            if (updated == null)
                return false;

            UpsertLocal(updated);
            Status = transport.Status;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to update party response.");
            Status = "Party response failed.";
            Notify("Party response failed.", NotificationType.Error);
            return false;
        }
    }

    public async Task<bool> DeleteEventAsync(PartyEvent partyEvent)
    {
        var transport = ActiveTransport();
        if (transport == null)
            return false;

        try
        {
            var deleted = await transport.DeleteEventAsync(partyEvent, CurrentActor());
            Status = transport.Status;
            if (!deleted)
            {
                Notify(Status, NotificationType.Warning);
                return false;
            }

            lock (eventLock)
            {
                events.RemoveAll(e => e.Id.Equals(partyEvent.Id, StringComparison.OrdinalIgnoreCase));
                EventsVersion++;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to delete party event.");
            Status = "Party event delete failed.";
            Notify("Party event delete failed. Only the creator can delete shared party events.", NotificationType.Warning);
            return false;
        }
    }

    public PartySyncActor CurrentActor()
    {
        var name = plugin.Configuration.PartySyncDisplayName.Trim();
        var world = string.Empty;
        var contentId = 0UL;

        try
        {
            if (Plugin.PlayerState.IsLoaded)
            {
                name = string.IsNullOrWhiteSpace(name)
                    ? Plugin.PlayerState.CharacterName.ToString()
                    : name;
                world = Plugin.PlayerState.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
                contentId = Plugin.PlayerState.ContentId;
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(name))
            name = plugin.Configuration.PartySyncDisplayName.Trim();

        var keyMaterial = contentId != 0
            ? $"{contentId}:{world}"
            : $"{name}:{world}:{plugin.Configuration.PartySyncKey}";

        return new PartySyncActor
        {
            Key = Hash(keyMaterial),
            Name = name.Trim(),
            World = world.Trim()
        };
    }

    public bool IsCreator(PartyEvent partyEvent)
    {
        var actor = CurrentActor();
        return !string.IsNullOrWhiteSpace(actor.Name)
               && partyEvent.CreatorName.Equals(actor.Name, StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrWhiteSpace(partyEvent.CreatorWorld)
                   || partyEvent.CreatorWorld.Equals(actor.World, StringComparison.OrdinalIgnoreCase));
    }

    public PartyEventResponse? CurrentResponse(PartyEvent partyEvent)
    {
        var actor = CurrentActor();
        return partyEvent.Responses.FirstOrDefault(r => r.PlayerKey.Equals(actor.Key, StringComparison.OrdinalIgnoreCase));
    }

    public void ImportBridgeEvents(IEnumerable<PartyEvent> incomingEvents)
    {
        if (!IsExternalBridgeEnabled)
            return;

        externalBridgeTransport.ImportEvents(incomingEvents);
        LoadExternalBridgeEvents();
        Status = externalBridgeTransport.Status;
    }

    private IPartySyncTransport? ActiveTransport()
    {
        if (supabaseTransport.IsConfigured)
            return supabaseTransport;

        if (externalBridgeTransport.IsConfigured)
            return externalBridgeTransport;

        return null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var transport = ActiveTransport();
        if (transport is not { PollsAutomatically: true })
            return;

        if (Environment.TickCount64 - lastPoll < Math.Clamp(plugin.Configuration.PartySyncPollSeconds, 15, 600) * 1000L)
            return;

        _ = RefreshAsync(rangeStart, rangeEnd, force: false);
    }

    private void UpsertLocal(PartyEvent partyEvent)
    {
        lock (eventLock)
        {
            events.RemoveAll(e => e.Id.Equals(partyEvent.Id, StringComparison.OrdinalIgnoreCase));
            events.Add(partyEvent);
            events = events
                .OrderBy(e => e.ScheduledAt ?? e.Date)
                .ThenBy(e => e.Title)
                .ToList();
            EventsVersion++;
        }
    }

    private void LoadExternalBridgeEvents()
    {
        if (!externalBridgeTransport.IsConfigured)
            return;

        lock (eventLock)
        {
            events = plugin.Configuration.PartySyncBridgeEvents
                .Where(e => e.Date != default && !string.IsNullOrWhiteSpace(e.Title))
                .OrderBy(e => e.ScheduledAt ?? e.Date)
                .ThenBy(e => e.Title)
                .ToList();
            EventsVersion++;
        }

        Status = $"External IPC bridge enabled. {events.Count} local bridge event{(events.Count == 1 ? string.Empty : "s")} cached.";
    }

    private static void CopySavedFields(PartyEvent target, PartyEvent source)
    {
        target.Id = source.Id;
        target.CreatorName = source.CreatorName;
        target.CreatorWorld = source.CreatorWorld;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.Responses = source.Responses;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void Notify(string content, NotificationType type)
    {
        Plugin.Notifications.AddNotification(new Notification
        {
            Title = "Lodestone Party Sync",
            Content = content,
            Type = type,
            Minimized = false
        });
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        supabaseTransport.Dispose();
        externalBridgeTransport.Dispose();
    }
}
