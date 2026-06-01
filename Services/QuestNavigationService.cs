using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lodestone.Models;
using Lumina.Excel.Sheets;

namespace Lodestone.Services;

public sealed class QuestNavigationService : IDisposable
{
    private readonly Plugin plugin;
    private PendingNavigation? pendingNavigation;
    private long lastLifestreamAvailabilityCheck;
    private long lastVNavAvailabilityCheck;
    private long lastNavigationActivityCheck;
    private bool lifestreamAvailableCache;
    private bool vnavAvailableCache;
    private bool navigationActivityCache;

    public QuestNavigationService(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public string Status { get; private set; } = "Navigation idle.";
    public bool HasPendingNavigation => pendingNavigation != null;
    public bool HasNavigationActivity
    {
        get
        {
            if (pendingNavigation != null)
                return true;

            var now = Environment.TickCount64;
            if (now - lastNavigationActivityCheck < 500)
                return navigationActivityCache;

            navigationActivityCache = VNavPathIsRunning() || VNavSimpleMoveInProgress();
            lastNavigationActivityCheck = now;
            return navigationActivityCache;
        }
    }

    public bool IsLifestreamAvailable()
    {
        var now = Environment.TickCount64;
        if (now - lastLifestreamAvailabilityCheck < 1_000)
            return lifestreamAvailableCache;

        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            lifestreamAvailableCache = true;
        }
        catch
        {
            lifestreamAvailableCache = false;
        }

        lastLifestreamAvailabilityCheck = now;
        return lifestreamAvailableCache;
    }

    public bool IsVNavAvailable()
    {
        var now = Environment.TickCount64;
        if (now - lastVNavAvailabilityCheck < 1_000)
            return vnavAvailableCache;

        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
            vnavAvailableCache = true;
        }
        catch
        {
            vnavAvailableCache = false;
        }

        lastVNavAvailabilityCheck = now;
        return vnavAvailableCache;
    }

    public bool TeleportToQuest(GameEscapeQuest quest, bool useVNav)
    {
        if (string.IsNullOrWhiteSpace(quest.ClosestAetheryte) && string.IsNullOrWhiteSpace(quest.Zone))
        {
            Status = "No quest location was parsed.";
            return false;
        }

        var destination = !string.IsNullOrWhiteSpace(quest.ClosestAetheryte) ? quest.ClosestAetheryte : quest.Zone;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction($"tp {destination}");
            Status = $"Teleport requested: {destination}.";
            if (useVNav && quest.MapX.HasValue && quest.MapY.HasValue && !string.IsNullOrWhiteSpace(quest.Zone))
            {
                pendingNavigation = new PendingNavigation
                {
                    Zone = quest.Zone,
                    MapX = quest.MapX.Value,
                    MapY = quest.MapY.Value,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(4),
                    NextAttemptAt = DateTime.UtcNow.AddSeconds(2)
                };
                Status = $"Teleport requested. Waiting to navigate in {quest.Zone}...";
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to call Lifestream IPC.");
            Status = "Could not call Lifestream teleport.";
            return false;
        }
    }

    public void PanicStop()
    {
        pendingNavigation = null;
        var stopped = TryStopVNav();
        navigationActivityCache = false;
        lastNavigationActivityCheck = 0;
        Status = stopped
            ? "Navigation stopped."
            : "Navigation stopped locally. Could not call vnavmesh stop.";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingNavigation == null)
            return;

        if (DateTime.UtcNow > pendingNavigation.ExpiresAt)
        {
            Status = "Navigation timed out after teleport.";
            pendingNavigation = null;
            return;
        }

        if (!TryGetTerritoryForZone(pendingNavigation.Zone, out var territory))
        {
            Status = $"Waiting for territory data: {pendingNavigation.Zone}.";
            return;
        }

        if (Plugin.ClientState.TerritoryType != territory.RowId)
        {
            pendingNavigation.TerritoryReadyAt = null;
            Status = $"Waiting for teleport to {pendingNavigation.Zone}...";
            return;
        }

        if (ClientIsBusy() || LifestreamIsBusy())
        {
            pendingNavigation.TerritoryReadyAt = null;
            pendingNavigation.NextAttemptAt = DateTime.UtcNow.AddSeconds(1);
            Status = "Waiting for loading and teleport busy state to clear...";
            return;
        }

        var now = DateTime.UtcNow;
        pendingNavigation.TerritoryReadyAt ??= now;
        if (now - pendingNavigation.TerritoryReadyAt.Value < TimeSpan.FromSeconds(2))
        {
            Status = "Waiting for zone to settle...";
            return;
        }

        if (now < pendingNavigation.NextAttemptAt)
            return;

        if (!VNavReadyToMove())
        {
            pendingNavigation.NextAttemptAt = now.AddSeconds(1);
            Status = "Waiting for vnavmesh to be ready...";
            return;
        }

        if (!TryMapToWorld(territory, pendingNavigation.MapX, pendingNavigation.MapY, out var destination))
        {
            Status = "Could not map quest coordinates to world position.";
            return;
        }

        Status = "Starting vnavmesh movement...";
        if (TryMoveWithVNav(destination))
        {
            Status = "Movement handed to vnavmesh.";
            pendingNavigation = null;
            return;
        }

        pendingNavigation.Attempts++;
        pendingNavigation.NextAttemptAt = now.AddSeconds(Math.Min(5, 1 + pendingNavigation.Attempts));
        Status = $"vnavmesh did not start moving. Retrying ({pendingNavigation.Attempts}/5)...";
        if (pendingNavigation.Attempts >= 5)
        {
            Plugin.Log.Warning("Unable to start vnavmesh movement after teleport to {Zone}.", pendingNavigation.Zone);
            Status = "Unable to start vnavmesh movement after teleport.";
            pendingNavigation = null;
        }
    }

    private static bool ClientIsBusy()
    {
        try
        {
            return !Plugin.PlayerState.IsLoaded
                   || Plugin.Condition[ConditionFlag.BetweenAreas]
                   || Plugin.Condition[ConditionFlag.Casting];
        }
        catch
        {
            return true;
        }
    }

    private static bool LifestreamIsBusy()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static bool VNavReadyToMove()
    {
        try
        {
            if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
                return false;

            try
            {
                var buildProgress = Plugin.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress").InvokeFunc();
                if (buildProgress > 0f && buildProgress < 1f)
                    return false;
            }
            catch
            {
            }

            try
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc())
                    return false;
            }
            catch
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool VNavPathIsRunning()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static bool VNavSimpleMoveInProgress()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStopVNav()
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to call vnavmesh stop IPC.");
            return false;
        }
    }

    private static bool TryMoveWithVNav(Vector3 approximateDestination)
    {
        try
        {
            var destination = approximateDestination;
            try
            {
                var floor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor")
                    .InvokeFunc(approximateDestination, true, 8f);
                if (floor.HasValue)
                    destination = floor.Value;
            }
            catch
            {
            }

            return Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                .InvokeFunc(destination, false, 1.5f);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to call vnavmesh IPC.");
            return false;
        }
    }

    private static bool TryGetTerritoryForZone(string zone, out TerritoryType territory)
    {
        var cleanedZone = CleanZoneName(zone);
        foreach (var row in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            if (placeName.Equals(cleanedZone, StringComparison.OrdinalIgnoreCase)
                || placeName.Contains(cleanedZone, StringComparison.OrdinalIgnoreCase)
                || cleanedZone.Contains(placeName, StringComparison.OrdinalIgnoreCase))
            {
                territory = row;
                return true;
            }
        }

        territory = default;
        return false;
    }

    private static bool TryMapToWorld(TerritoryType territory, float mapX, float mapY, out Vector3 world)
    {
        world = default;
        if (!territory.Map.IsValid)
            return false;

        var map = territory.Map.Value;
        if (map.SizeFactor <= 0)
            return false;

        var payload = new MapLinkPayload(territory.RowId, map.RowId, mapX, mapY, 0f);
        world = new Vector3(payload.RawX, 0f, payload.RawY);
        return true;
    }

    private static string CleanZoneName(string zone)
        => zone.Replace("Ul'dah", "Ul'dah", StringComparison.OrdinalIgnoreCase).Trim();

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private sealed class PendingNavigation
    {
        public string Zone { get; init; } = string.Empty;
        public float MapX { get; init; }
        public float MapY { get; init; }
        public DateTime ExpiresAt { get; init; }
        public DateTime NextAttemptAt { get; set; }
        public DateTime? TerritoryReadyAt { get; set; }
        public int Attempts { get; set; }
    }
}
