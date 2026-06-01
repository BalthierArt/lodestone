using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Ipc;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class PartySyncIpcService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Plugin plugin;
    private readonly List<ICallGateProvider> providers = [];

    public PartySyncIpcService(Plugin plugin)
    {
        this.plugin = plugin;
        Register();
    }

    private void Register()
    {
        var version = Plugin.PluginInterface.GetIpcProvider<string>("Lodestone.PartySync.ApiVersion");
        version.RegisterFunc(() => "1");
        providers.Add(version);

        var configured = Plugin.PluginInterface.GetIpcProvider<bool>("Lodestone.PartySync.IsConfigured");
        configured.RegisterFunc(() => plugin.PartySyncService.CanCreateEvents);
        providers.Add(configured);

        var supabaseConfigured = Plugin.PluginInterface.GetIpcProvider<bool>("Lodestone.PartySync.IsSupabaseConfigured");
        supabaseConfigured.RegisterFunc(() => plugin.PartySyncService.IsConfigured);
        providers.Add(supabaseConfigured);

        var bridgeEnabled = Plugin.PluginInterface.GetIpcProvider<bool>("Lodestone.PartySync.IsExternalBridgeEnabled");
        bridgeEnabled.RegisterFunc(() => plugin.PartySyncService.IsExternalBridgeEnabled);
        providers.Add(bridgeEnabled);

        var transport = Plugin.PluginInterface.GetIpcProvider<string>("Lodestone.PartySync.Transport");
        transport.RegisterFunc(() => plugin.PartySyncService.TransportLabel);
        providers.Add(transport);

        var status = Plugin.PluginInterface.GetIpcProvider<string>("Lodestone.PartySync.Status");
        status.RegisterFunc(() => plugin.PartySyncService.Status);
        providers.Add(status);

        var icons = Plugin.PluginInterface.GetIpcProvider<string>("Lodestone.PartySync.GetIconCatalogJson");
        icons.RegisterFunc(GetIconCatalogJson);
        providers.Add(icons);

        var events = Plugin.PluginInterface.GetIpcProvider<string, string, string>("Lodestone.PartySync.GetEventsJson");
        events.RegisterFunc(GetEventsJson);
        providers.Add(events);

        var createEvent = Plugin.PluginInterface.GetIpcProvider<string, string>("Lodestone.PartySync.QueueEventJson");
        createEvent.RegisterFunc(QueueEventJson);
        providers.Add(createEvent);

        var respond = Plugin.PluginInterface.GetIpcProvider<string, string>("Lodestone.PartySync.QueueResponseJson");
        respond.RegisterFunc(QueueResponseJson);
        providers.Add(respond);

        var importEvents = Plugin.PluginInterface.GetIpcProvider<string, string>("Lodestone.PartySync.ImportEventsJson");
        importEvents.RegisterFunc(ImportEventsJson);
        providers.Add(importEvents);

        var refresh = Plugin.PluginInterface.GetIpcProvider<string>("Lodestone.PartySync.QueueRefresh");
        refresh.RegisterFunc(QueueRefresh);
        providers.Add(refresh);
    }

    private static string GetIconCatalogJson()
    {
        var icons = PartyEventIcons.All.Select(icon => new IconDto(icon.Key, icon.Label, icon.IconId)).ToArray();
        return JsonSerializer.Serialize(new { ok = true, icons }, JsonOptions);
    }

    private string GetEventsJson(string startDate, string endDate)
    {
        if (!DateTime.TryParse(startDate, out var start) || !DateTime.TryParse(endDate, out var end))
            return Error("Dates must be parseable, preferably YYYY-MM-DD.");

        var events = plugin.PartySyncService.Events
            .Where(e => e.Date.Date >= start.Date && e.Date.Date <= end.Date)
            .Select(ToDto)
            .ToArray();

        return JsonSerializer.Serialize(new { ok = true, events }, JsonOptions);
    }

    private string QueueEventJson(string json)
    {
        if (!plugin.PartySyncService.CanCreateEvents)
            return Error("Party sync is not configured.");

        PartyEventWriteDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PartyEventWriteDto>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error($"Invalid JSON: {ex.Message}");
        }

        if (dto == null)
            return Error("No event payload was provided.");

        if (string.IsNullOrWhiteSpace(dto.Title))
            return Error("title is required.");

        if (!DateTime.TryParse(dto.Date, out var date))
            return Error("date is required and must be parseable, preferably YYYY-MM-DD.");

        var partyEvent = new PartyEvent
        {
            Id = dto.Id ?? string.Empty,
            Date = date.Date,
            Hour = dto.Hour,
            Minute = dto.Minute,
            Title = dto.Title.Trim(),
            Description = dto.Description?.Trim() ?? string.Empty,
            IconKey = PartyEventIcons.Get(dto.IconKey).Key
        };

        _ = plugin.PartySyncService.SaveEventAsync(partyEvent);
        return JsonSerializer.Serialize(new { ok = true, accepted = true }, JsonOptions);
    }

    private string QueueResponseJson(string json)
    {
        if (!plugin.PartySyncService.CanCreateEvents)
            return Error("Party sync is not configured.");

        PartyEventResponseWriteDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PartyEventResponseWriteDto>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error($"Invalid JSON: {ex.Message}");
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.EventId))
            return Error("eventId is required.");

        var partyEvent = plugin.PartySyncService.Events.FirstOrDefault(e => e.Id.Equals(dto.EventId, StringComparison.OrdinalIgnoreCase));
        if (partyEvent == null)
            return Error("Event is not in Lodestone's current party sync cache. QueueRefresh first, then retry.");

        PartyEventResponseStatus? status = dto.Status?.Equals("remove", StringComparison.OrdinalIgnoreCase) == true
            ? null
            : dto.Status?.Equals("maybe", StringComparison.OrdinalIgnoreCase) == true
                ? PartyEventResponseStatus.Maybe
                : PartyEventResponseStatus.Interested;

        _ = plugin.PartySyncService.RespondAsync(partyEvent, status);
        return JsonSerializer.Serialize(new { ok = true, accepted = true }, JsonOptions);
    }

    private string ImportEventsJson(string json)
    {
        if (!plugin.PartySyncService.IsExternalBridgeEnabled)
            return Error("External IPC bridge is not enabled in Lodestone settings.");

        PartyEventImportEnvelope? envelope = null;
        try
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                var events = JsonSerializer.Deserialize<PartyEventDto[]>(json, JsonOptions);
                envelope = new PartyEventImportEnvelope(events, null);
            }
            else
            {
                envelope = JsonSerializer.Deserialize<PartyEventImportEnvelope>(json, JsonOptions);
                if (envelope?.Events == null && envelope?.Event == null)
                {
                    var single = JsonSerializer.Deserialize<PartyEventDto>(json, JsonOptions);
                    envelope = new PartyEventImportEnvelope(null, single);
                }
            }
        }
        catch (Exception ex)
        {
            return Error($"Invalid JSON: {ex.Message}");
        }

        var incoming = (envelope?.Events ?? [])
            .Concat(envelope?.Event == null ? [] : [envelope.Event])
            .Where(e => e != null)
            .Select(ToModel)
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .ToArray();

        if (incoming.Length == 0)
            return Error("No valid events were provided.");

        plugin.PartySyncService.ImportBridgeEvents(incoming);
        return JsonSerializer.Serialize(new { ok = true, imported = incoming.Length }, JsonOptions);
    }

    private string QueueRefresh()
    {
        if (plugin.PartySyncService.IsConfigured)
            _ = plugin.PartySyncService.RefreshVisibleRangeAsync(force: true);

        return JsonSerializer.Serialize(new { ok = true, accepted = true }, JsonOptions);
    }

    private static PartyEventDto ToDto(PartyEvent partyEvent)
        => new(
            partyEvent.Id,
            partyEvent.Date.ToString("yyyy-MM-dd"),
            partyEvent.Hour,
            partyEvent.Minute,
            partyEvent.Title,
            partyEvent.Description,
            PartyEventIcons.Get(partyEvent.IconKey).Key,
            PartyEventIcons.Get(partyEvent.IconKey).IconId,
            partyEvent.CreatorName,
            partyEvent.CreatorWorld,
            partyEvent.CreatedAt,
            partyEvent.UpdatedAt,
            partyEvent.Responses.Select(response => new PartyResponseDto(
                response.PlayerKey,
                response.PlayerName,
                response.PlayerWorld,
                response.Status == PartyEventResponseStatus.Maybe ? "maybe" : "interested",
                response.UpdatedAt)).ToArray());

    private static PartyEvent ToModel(PartyEventDto dto)
    {
        _ = DateTime.TryParse(dto.Date, out var date);
        var icon = PartyEventIcons.Get(dto.IconKey);
        return new PartyEvent
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? $"bridge-{Guid.NewGuid():N}" : dto.Id,
            Date = date == default ? DateTime.Today : date.Date,
            Hour = dto.Hour,
            Minute = dto.Minute,
            Title = dto.Title,
            Description = dto.Description,
            IconKey = icon.Key,
            CreatorName = dto.CreatorName,
            CreatorWorld = dto.CreatorWorld,
            CreatedAt = dto.CreatedAt == default ? DateTime.UtcNow : dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt == default ? DateTime.UtcNow : dto.UpdatedAt,
            Responses = dto.Responses.Select(response => new PartyEventResponse
            {
                PlayerKey = response.PlayerKey,
                PlayerName = response.PlayerName,
                PlayerWorld = response.PlayerWorld,
                Status = response.Status.Equals("maybe", StringComparison.OrdinalIgnoreCase)
                    ? PartyEventResponseStatus.Maybe
                    : PartyEventResponseStatus.Interested,
                UpdatedAt = response.UpdatedAt
            }).ToList()
        };
    }

    private static string Error(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, JsonOptions);

    public void Dispose()
    {
        foreach (var provider in providers)
        {
            provider.UnregisterFunc();
            provider.UnregisterAction();
        }

        providers.Clear();
    }

    private sealed record IconDto(string Key, string Label, uint IconId);
    private sealed record PartyEventWriteDto(string? Id, string? Date, int? Hour, int? Minute, string? Title, string? Description, string? IconKey);
    private sealed record PartyEventResponseWriteDto(string? EventId, string? Status);
    private sealed record PartyEventImportEnvelope(PartyEventDto[]? Events, PartyEventDto? Event);
    private sealed record PartyEventDto(
        string Id,
        string Date,
        int? Hour,
        int? Minute,
        string Title,
        string Description,
        string IconKey,
        uint IconId,
        string CreatorName,
        string CreatorWorld,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        PartyResponseDto[] Responses);

    private sealed record PartyResponseDto(string PlayerKey, string PlayerName, string PlayerWorld, string Status, DateTime UpdatedAt);
}
