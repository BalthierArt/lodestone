using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class PartySyncActor
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
}

public interface IPartySyncTransport : IDisposable
{
    string Name { get; }
    bool IsConfigured { get; }
    bool PollsAutomatically { get; }
    string Status { get; }
    Task<IReadOnlyList<PartyEvent>> ListAsync(DateTime start, DateTime end, bool force);
    Task<PartyEvent?> SaveEventAsync(PartyEvent partyEvent, PartySyncActor actor);
    Task<PartyEvent?> RespondAsync(PartyEvent partyEvent, PartySyncActor actor, PartyEventResponseStatus? status);
    Task<bool> DeleteEventAsync(PartyEvent partyEvent, PartySyncActor actor);
    void ImportEvents(IEnumerable<PartyEvent> partyEvents);
}

public sealed class SupabasePartySyncTransport : IPartySyncTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Plugin plugin;
    private readonly HttpClient httpClient = new();

    public SupabasePartySyncTransport(Plugin plugin)
    {
        this.plugin = plugin;
        httpClient.Timeout = TimeSpan.FromSeconds(20);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LodestoneDalamudPlugin/0.1 PartySync");
    }

    public string Name => "Supabase";
    public bool PollsAutomatically => true;
    public string Status { get; private set; } = "Supabase party sync disabled.";
    public bool IsConfigured => plugin.Configuration.PartySyncEnabled
                                && !string.IsNullOrWhiteSpace(plugin.Configuration.PartySyncSupabaseUrl)
                                && !string.IsNullOrWhiteSpace(plugin.Configuration.PartySyncAnonKey)
                                && !string.IsNullOrWhiteSpace(plugin.Configuration.PartySyncKey);

    private string PartyKey => plugin.Configuration.PartySyncKey.Trim();

    public async Task<IReadOnlyList<PartyEvent>> ListAsync(DateTime start, DateTime end, bool force)
    {
        var response = await SendAsync<ListResponse>(new SyncEnvelope
        {
            Action = "list",
            PartyHash = PartySyncCrypto.PartyHash(PartyKey),
            RangeStart = start.ToString("yyyy-MM-dd"),
            RangeEnd = end.ToString("yyyy-MM-dd")
        });

        var events = response.Events.Select(ToModel).ToArray();
        Status = $"{events.Length} party event{(events.Length == 1 ? string.Empty : "s")} synced.";
        return events;
    }

    public async Task<PartyEvent?> SaveEventAsync(PartyEvent partyEvent, PartySyncActor actor)
    {
        var response = await SendAsync<EventResponse>(new SyncEnvelope
        {
            Action = "upsertEvent",
            PartyHash = PartySyncCrypto.PartyHash(PartyKey),
            Actor = PartySyncActorDto.From(actor, PartyKey),
            Event = FromModel(partyEvent, actor)
        });

        Status = "Party event saved.";
        return ToModel(response.Event);
    }

    public async Task<PartyEvent?> RespondAsync(PartyEvent partyEvent, PartySyncActor actor, PartyEventResponseStatus? status)
    {
        var response = await SendAsync<EventResponse>(new SyncEnvelope
        {
            Action = "respond",
            PartyHash = PartySyncCrypto.PartyHash(PartyKey),
            Actor = PartySyncActorDto.From(actor, PartyKey),
            EventId = partyEvent.Id,
            ResponseStatus = status.HasValue ? ToWireStatus(status.Value) : "remove"
        });

        Status = status.HasValue ? "Party response updated." : "Party response removed.";
        return ToModel(response.Event);
    }

    public async Task<bool> DeleteEventAsync(PartyEvent partyEvent, PartySyncActor actor)
    {
        await SendAsync<OkResponse>(new SyncEnvelope
        {
            Action = "deleteEvent",
            PartyHash = PartySyncCrypto.PartyHash(PartyKey),
            Actor = PartySyncActorDto.From(actor, PartyKey),
            EventId = partyEvent.Id
        });

        Status = "Party event deleted.";
        return true;
    }

    public void ImportEvents(IEnumerable<PartyEvent> partyEvents)
    {
    }

    private async Task<T> SendAsync<T>(SyncEnvelope envelope)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, FunctionUrl())
        {
            Content = JsonContent.Create(envelope, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("apikey", plugin.Configuration.PartySyncAnonKey.Trim());
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {plugin.Configuration.PartySyncAnonKey.Trim()}");

        using var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Supabase function returned {(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new InvalidDataException("Supabase function returned an empty response.");
    }

    private string FunctionUrl()
    {
        var baseUrl = plugin.Configuration.PartySyncSupabaseUrl.Trim().TrimEnd('/');
        return $"{baseUrl}/functions/v1/lodestone-party-sync";
    }

    private PartyEvent ToModel(PartyEventDto dto)
    {
        _ = DateTime.TryParse(dto.Date, out var date);
        _ = DateTime.TryParse(dto.CreatedAt, out var createdAt);
        _ = DateTime.TryParse(dto.UpdatedAt, out var updatedAt);
        var partyKey = PartyKey;

        return new PartyEvent
        {
            Id = dto.Id ?? string.Empty,
            Date = date == default ? DateTime.Today : date.Date,
            Hour = dto.Hour,
            Minute = dto.Minute,
            Title = dto.Title ?? string.Empty,
            Description = dto.Description ?? string.Empty,
            IconKey = string.IsNullOrWhiteSpace(dto.IconKey) ? PartyEventIcons.DefaultKey : dto.IconKey!,
            CreatorName = PartySyncCrypto.DecryptDisplayValue(dto.CreatorName, partyKey, "creator-name"),
            CreatorWorld = PartySyncCrypto.DecryptDisplayValue(dto.CreatorWorld, partyKey, "creator-world"),
            CreatedAt = createdAt == default ? DateTime.UtcNow : createdAt,
            UpdatedAt = updatedAt == default ? DateTime.UtcNow : updatedAt,
            Responses = dto.Responses.Select(ToModel).ToList()
        };
    }

    private PartyEventResponse ToModel(PartyResponseDto dto)
    {
        _ = DateTime.TryParse(dto.UpdatedAt, out var updatedAt);
        var partyKey = PartyKey;
        return new PartyEventResponse
        {
            PlayerKey = dto.PlayerKey ?? string.Empty,
            PlayerName = PartySyncCrypto.DecryptDisplayValue(dto.PlayerName, partyKey, "player-name"),
            PlayerWorld = PartySyncCrypto.DecryptDisplayValue(dto.PlayerWorld, partyKey, "player-world"),
            Status = FromWireStatus(dto.Status),
            UpdatedAt = updatedAt == default ? DateTime.UtcNow : updatedAt
        };
    }

    private PartyEventDto FromModel(PartyEvent model, PartySyncActor actor)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(model.Id) ? null : model.Id,
            Date = model.Date.ToString("yyyy-MM-dd"),
            Hour = model.Hour,
            Minute = model.Minute,
            Title = model.Title,
            Description = model.Description,
            IconKey = PartyEventIcons.Get(model.IconKey).Key,
            CreatorName = PartySyncCrypto.EncryptDisplayValue(actor.Name, PartyKey, "creator-name"),
            CreatorWorld = PartySyncCrypto.EncryptDisplayValue(actor.World, PartyKey, "creator-world")
        };

    private static PartyEventResponseStatus FromWireStatus(string? status)
        => status?.Equals("maybe", StringComparison.OrdinalIgnoreCase) == true
            ? PartyEventResponseStatus.Maybe
            : PartyEventResponseStatus.Interested;

    private static string ToWireStatus(PartyEventResponseStatus status)
        => status == PartyEventResponseStatus.Maybe ? "maybe" : "interested";

    public void Dispose() => httpClient.Dispose();

    private sealed class SyncEnvelope
    {
        public string Action { get; set; } = string.Empty;
        public string PartyHash { get; set; } = string.Empty;
        public string? RangeStart { get; set; }
        public string? RangeEnd { get; set; }
        public string? EventId { get; set; }
        public string? ResponseStatus { get; set; }
        public PartySyncActorDto? Actor { get; set; }
        public PartyEventDto? Event { get; set; }
    }

    private sealed class PartySyncActorDto
    {
        public string Key { get; set; } = string.Empty;
        public string NameEncrypted { get; set; } = string.Empty;
        public string WorldEncrypted { get; set; } = string.Empty;

        public static PartySyncActorDto From(PartySyncActor actor, string partyKey)
            => new()
            {
                Key = actor.Key,
                NameEncrypted = PartySyncCrypto.EncryptDisplayValue(actor.Name, partyKey, "player-name"),
                WorldEncrypted = PartySyncCrypto.EncryptDisplayValue(actor.World, partyKey, "player-world")
            };
    }

    private sealed class ListResponse
    {
        public List<PartyEventDto> Events { get; set; } = [];
    }

    private sealed class EventResponse
    {
        public PartyEventDto Event { get; set; } = new();
    }

    private sealed class OkResponse
    {
        public bool Ok { get; set; }
    }

    private sealed class PartyEventDto
    {
        public string? Id { get; set; }
        public string? Date { get; set; }
        public int? Hour { get; set; }
        public int? Minute { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? IconKey { get; set; }
        public string? CreatorName { get; set; }
        public string? CreatorWorld { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public List<PartyResponseDto> Responses { get; set; } = [];
    }

    private sealed class PartyResponseDto
    {
        public string? PlayerKey { get; set; }
        public string? PlayerName { get; set; }
        public string? PlayerWorld { get; set; }
        public string? Status { get; set; }
        public string? UpdatedAt { get; set; }
    }
}

public sealed class ExternalBridgePartySyncTransport : IPartySyncTransport
{
    private readonly Plugin plugin;

    public ExternalBridgePartySyncTransport(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public string Name => "External IPC bridge";
    public bool IsConfigured => plugin.Configuration.PartySyncExternalBridgeEnabled;
    public bool PollsAutomatically => false;
    public string Status { get; private set; } = "External IPC bridge disabled.";

    public Task<IReadOnlyList<PartyEvent>> ListAsync(DateTime start, DateTime end, bool force)
    {
        var events = plugin.Configuration.PartySyncBridgeEvents
            .Where(e => e.Date.Date >= start.Date && e.Date.Date <= end.Date)
            .OrderBy(e => e.ScheduledAt ?? e.Date)
            .ThenBy(e => e.Title)
            .ToArray();

        Status = $"External IPC bridge enabled. {events.Length} local bridge event{(events.Length == 1 ? string.Empty : "s")} cached.";
        return Task.FromResult<IReadOnlyList<PartyEvent>>(events);
    }

    public Task<PartyEvent?> SaveEventAsync(PartyEvent partyEvent, PartySyncActor actor)
    {
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(partyEvent.Id))
        {
            partyEvent.Id = $"bridge-{Guid.NewGuid():N}";
            partyEvent.CreatedAt = now;
        }

        partyEvent.Date = partyEvent.Date.Date;
        partyEvent.Title = partyEvent.Title.Trim();
        partyEvent.Description = partyEvent.Description.Trim();
        partyEvent.IconKey = PartyEventIcons.Get(partyEvent.IconKey).Key;
        partyEvent.CreatorName = string.IsNullOrWhiteSpace(partyEvent.CreatorName) ? actor.Name : partyEvent.CreatorName;
        partyEvent.CreatorWorld = string.IsNullOrWhiteSpace(partyEvent.CreatorWorld) ? actor.World : partyEvent.CreatorWorld;
        partyEvent.UpdatedAt = now;
        partyEvent.Responses ??= [];
        Upsert(partyEvent);
        Status = "Party event saved for external IPC bridge.";
        return Task.FromResult<PartyEvent?>(partyEvent);
    }

    public Task<PartyEvent?> RespondAsync(PartyEvent partyEvent, PartySyncActor actor, PartyEventResponseStatus? status)
    {
        partyEvent.Responses.RemoveAll(r => r.PlayerKey.Equals(actor.Key, StringComparison.OrdinalIgnoreCase));
        if (status.HasValue)
        {
            partyEvent.Responses.Add(new PartyEventResponse
            {
                PlayerKey = actor.Key,
                PlayerName = actor.Name,
                PlayerWorld = actor.World,
                Status = status.Value,
                UpdatedAt = DateTime.UtcNow
            });
        }

        partyEvent.UpdatedAt = DateTime.UtcNow;
        Upsert(partyEvent);
        Status = status.HasValue ? "Bridge response updated." : "Bridge response removed.";
        return Task.FromResult<PartyEvent?>(partyEvent);
    }

    public Task<bool> DeleteEventAsync(PartyEvent partyEvent, PartySyncActor actor)
    {
        if (!partyEvent.CreatorName.Equals(actor.Name, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(partyEvent.CreatorWorld) && !partyEvent.CreatorWorld.Equals(actor.World, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Only the creator can delete shared party events.";
            return Task.FromResult(false);
        }

        plugin.Configuration.PartySyncBridgeEvents.RemoveAll(e => e.Id.Equals(partyEvent.Id, StringComparison.OrdinalIgnoreCase));
        plugin.Configuration.Save();
        Status = "Bridge party event deleted.";
        return Task.FromResult(true);
    }

    public void ImportEvents(IEnumerable<PartyEvent> partyEvents)
    {
        foreach (var incoming in partyEvents)
        {
            if (string.IsNullOrWhiteSpace(incoming.Id))
                incoming.Id = $"bridge-{Guid.NewGuid():N}";

            incoming.IconKey = PartyEventIcons.Get(incoming.IconKey).Key;
            incoming.Date = incoming.Date.Date;
            incoming.Responses ??= [];
            Upsert(incoming);
        }

        Status = $"Imported bridge events. {plugin.Configuration.PartySyncBridgeEvents.Count} local bridge event{(plugin.Configuration.PartySyncBridgeEvents.Count == 1 ? string.Empty : "s")} cached.";
    }

    private void Upsert(PartyEvent partyEvent)
    {
        plugin.Configuration.PartySyncBridgeEvents.RemoveAll(e => e.Id.Equals(partyEvent.Id, StringComparison.OrdinalIgnoreCase));
        plugin.Configuration.PartySyncBridgeEvents.Add(partyEvent);
        plugin.Configuration.PartySyncBridgeEvents = plugin.Configuration.PartySyncBridgeEvents
            .OrderBy(e => e.ScheduledAt ?? e.Date)
            .ThenBy(e => e.Title)
            .ToList();
        plugin.Configuration.Save();
    }

    public void Dispose()
    {
    }
}
