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

    public bool TrySetMapFlag(GameEscapeQuest quest)
    {
        if (!TryResolveQuestMap(quest, out var territory, out var payload, out var message))
        {
            Status = message;
            return false;
        }

        try
        {
            if (!Plugin.GameGui.OpenMapWithMapLink(payload))
            {
                Status = $"Could not set the map flag for {FormatTerritory(territory)}.";
                return false;
            }

            Status = $"Map flag set: {quest.Zone} (X:{quest.MapX:0.0}, Y:{quest.MapY:0.0}). Paste <flag> in chat.";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to open map flag for quest {Quest}.", quest.Title);
            Status = "Could not set the map flag.";
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

        if (!TryGetCurrentTerritory(out var currentTerritory) || !TerritoryMatchesZone(currentTerritory, pendingNavigation.Zone))
        {
            pendingNavigation.TerritoryReadyAt = null;
            Status = $"Waiting for teleport to {pendingNavigation.Zone}... Current: {CurrentTerritoryText()}";
            return;
        }

        territory = currentTerritory;

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

        if (!pendingNavigation.FlagSet)
        {
            if (!TrySetMapFlag(pendingNavigation.Zone, pendingNavigation.MapX, pendingNavigation.MapY, out var flagMessage))
            {
                pendingNavigation.Attempts++;
                pendingNavigation.NextAttemptAt = now.AddSeconds(Math.Min(5, 1 + pendingNavigation.Attempts));
                Status = $"{flagMessage} Retrying ({pendingNavigation.Attempts}/5)...";
                if (pendingNavigation.Attempts >= 5)
                {
                    pendingNavigation = null;
                    Status = "Could not set quest flag for vnavmesh.";
                }

                return;
            }

            pendingNavigation.FlagSet = true;
            pendingNavigation.FlagReadyAt = now.AddMilliseconds(750);
            pendingNavigation.NextAttemptAt = now.AddMilliseconds(750);
            pendingNavigation.Attempts = 0;
            Status = "Quest flag set. Waiting for vnavmesh to read it...";
            return;
        }

        if (pendingNavigation.FlagReadyAt.HasValue && now < pendingNavigation.FlagReadyAt.Value)
        {
            Status = "Quest flag set. Waiting for vnavmesh to read it...";
            return;
        }

        if (!TryGetVNavFlagPoint(out var destination))
        {
            pendingNavigation.Attempts++;
            pendingNavigation.NextAttemptAt = now.AddSeconds(Math.Min(5, 1 + pendingNavigation.Attempts));
            Status = $"vnavmesh could not read the quest flag yet. Retrying ({pendingNavigation.Attempts}/5)...";
            if (pendingNavigation.Attempts >= 5)
            {
                Plugin.Log.Warning("Unable to resolve vnavmesh flag point after teleport to {Zone}.", pendingNavigation.Zone);
                Status = "Unable to resolve quest flag with vnavmesh.";
                pendingNavigation = null;
            }

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

    private static bool TryGetVNavFlagPoint(out Vector3 destination)
    {
        destination = default;
        try
        {
            var flagPoint = Plugin.PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint").InvokeFunc();
            if (!flagPoint.HasValue)
                return false;

            destination = flagPoint.Value;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to read vnavmesh flag point.");
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

    private static bool TryGetCurrentTerritory(out TerritoryType territory)
    {
        var currentTerritoryId = Plugin.ClientState.TerritoryType;
        foreach (var row in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            if (row.RowId == currentTerritoryId)
            {
                territory = row;
                return true;
            }
        }

        territory = default;
        return false;
    }

    private static bool TryGetTerritoryForZone(string zone, out TerritoryType territory)
    {
        var cleanedZone = CleanZoneName(zone);
        var matches = Plugin.DataManager.GetExcelSheet<TerritoryType>()
            .Where(row => row.Map.IsValid && TerritoryMatchesZone(row, cleanedZone))
            .ToList();

        if (TryGetCurrentTerritory(out var currentTerritory) && matches.Any(row => row.RowId == currentTerritory.RowId))
        {
            territory = currentTerritory;
            return true;
        }

        var best = matches
            .OrderBy(row => TerritorySortScore(row, cleanedZone))
            .FirstOrDefault();

        if (!best.Equals(default(TerritoryType)))
        {
            territory = best;
            return true;
        }

        territory = default;
        return false;
    }

    private bool TrySetMapFlag(string zone, float mapX, float mapY, out string message)
    {
        if (!TryResolveQuestMap(zone, mapX, mapY, out var territory, out var payload, out message))
            return false;

        try
        {
            if (!Plugin.GameGui.OpenMapWithMapLink(payload))
            {
                message = $"Could not set the map flag for {FormatTerritory(territory)}.";
                return false;
            }

            message = $"Map flag set: {zone} (X:{mapX:0.0}, Y:{mapY:0.0}).";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to open map flag for {Zone}.", zone);
            message = "Could not set the map flag.";
            return false;
        }
    }

    private static bool TryResolveQuestMap(GameEscapeQuest quest, out TerritoryType territory, out MapLinkPayload payload, out string message)
    {
        if (!quest.MapX.HasValue || !quest.MapY.HasValue || string.IsNullOrWhiteSpace(quest.Zone))
        {
            territory = default;
            payload = default!;
            message = "No quest map coordinates were parsed.";
            return false;
        }

        return TryResolveQuestMap(quest.Zone, quest.MapX.Value, quest.MapY.Value, out territory, out payload, out message);
    }

    private static bool TryResolveQuestMap(string zone, float mapX, float mapY, out TerritoryType territory, out MapLinkPayload payload, out string message)
    {
        territory = default;
        payload = default!;
        if (!TryGetTerritoryForZone(zone, out territory))
        {
            message = $"Could not match quest zone: {zone}.";
            return false;
        }

        if (!territory.Map.IsValid)
        {
            message = $"No map is available for {zone}.";
            return false;
        }

        payload = new MapLinkPayload(territory.RowId, territory.Map.Value.RowId, mapX, mapY);
        message = string.Empty;
        return true;
    }

    private static int TerritorySortScore(TerritoryType territory, string cleanedZone)
    {
        var placeName = CleanZoneName(TerritoryPlaceName(territory));
        var score = placeName.Equals(cleanedZone, StringComparison.OrdinalIgnoreCase) ? 0 : 500;
        score += territory.TerritoryIntendedUse.RowId switch
        {
            0 => 0,
            1 => 10,
            47 => 20,
            49 => 20,
            _ => 1000 + (int)Math.Min(territory.TerritoryIntendedUse.RowId, 999)
        };
        score += (int)Math.Min(territory.RowId, 999);
        return score;
    }

    private static bool TerritoryMatchesZone(TerritoryType territory, string zone)
    {
        var cleanedZone = CleanZoneName(zone);
        var placeName = CleanZoneName(TerritoryPlaceName(territory));
        return !string.IsNullOrWhiteSpace(placeName)
               && (placeName.Equals(cleanedZone, StringComparison.OrdinalIgnoreCase)
                   || placeName.Contains(cleanedZone, StringComparison.OrdinalIgnoreCase)
                   || cleanedZone.Contains(placeName, StringComparison.OrdinalIgnoreCase));
    }

    private static string TerritoryPlaceName(TerritoryType territory)
        => territory.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;

    private static string FormatTerritory(TerritoryType territory)
        => $"{TerritoryPlaceName(territory)} #{territory.RowId} map #{(territory.Map.IsValid ? territory.Map.Value.RowId : 0)}";

    private static string CurrentTerritoryText()
        => TryGetCurrentTerritory(out var territory)
            ? FormatTerritory(territory)
            : $"#{Plugin.ClientState.TerritoryType}";

    private static string CleanZoneName(string zone)
        => zone.Replace('’', '\'').Trim();

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
        public DateTime? FlagReadyAt { get; set; }
        public bool FlagSet { get; set; }
        public int Attempts { get; set; }
    }
}
