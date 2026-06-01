# Lodestone Calendar

Lodestone Calendar is a Dalamud plugin that scans the official Final Fantasy XIV Lodestone and presents events, topics, notices, maintenance, recovery, status, and update posts in an in-game calendar.

## Party Sync

Party Sync lets players share planned calendar events. A player can right-click a day, choose `Plan Party Event`, pick a Party Finder-style icon, add a title/details/time, and other players in the same shared group can mark themselves as `Interested`, `Maybe`, or remove their response.

The built-in transport uses the Supabase Edge Function in `Supabase/functions/lodestone-party-sync`. Supabase mode needs a Lodestone Party Key because that key decides who can see the shared party events.

External IPC Bridge mode does not need a Lodestone Party Key. In that mode, Lodestone keeps and exposes party event JSON locally, and another plugin is expected to relay it through that plugin's existing group/key system.

The plugin stores only party events and sign-up responses. It does not upload Lodestone scrape data or local player notes.

## IPC For Sharing Plugins

Lodestone exposes local Dalamud IPC so other plugins can integrate with party event sharing. This is meant for plugins such as sync or mod-sharing clients that may want to bridge Lodestone party event payloads through their own network later.

Important: IPC is local to the player's client. Another plugin must still call Lodestone's IPC and relay the data through its own sharing system if it wants to sync through Snowcloak, Light Sync, Mare-style groups, or another transport. When External IPC Bridge is enabled, Lodestone does not require its own separate Party Key.

### IPC Providers

`Lodestone.PartySync.ApiVersion`

Returns the IPC contract version as a string.

`Lodestone.PartySync.IsConfigured`

Returns whether Lodestone can currently create/share party events through either Supabase or External IPC Bridge.

`Lodestone.PartySync.IsSupabaseConfigured`

Returns whether Lodestone's built-in Supabase transport is configured.

`Lodestone.PartySync.IsExternalBridgeEnabled`

Returns whether Lodestone is allowing another plugin to bridge party events through IPC.

`Lodestone.PartySync.Transport`

Returns `Supabase`, `External IPC bridge`, or `Disabled`.

`Lodestone.PartySync.Status`

Returns the current party sync status text.

`Lodestone.PartySync.GetIconCatalogJson`

Returns JSON containing supported Party Finder-style icon keys and Lumina icon IDs.

`Lodestone.PartySync.GetEventsJson`

Takes `startDate` and `endDate` strings, preferably `YYYY-MM-DD`, and returns cached party events for that range.

`Lodestone.PartySync.QueueEventJson`

Takes an event JSON payload and queues a save through Lodestone.

Example:

```json
{
  "date": "2026-06-05",
  "hour": 21,
  "minute": 0,
  "title": "New Extreme Trial. Who's interested?",
  "description": "Learning party, bring snacks.",
  "iconKey": "trial"
}
```

`Lodestone.PartySync.QueueResponseJson`

Takes a response JSON payload and queues an RSVP update through Lodestone.

Example:

```json
{
  "eventId": "event-id-from-get-events",
  "status": "interested"
}
```

`status` can be `interested`, `maybe`, or `remove`.

`Lodestone.PartySync.ImportEventsJson`

Takes a single event, an array of events, or an object with `events`. This lets a sync plugin import party event data it received from its own group/key network.

Example:

```json
{
  "events": [
    {
      "id": "bridge-event-id",
      "date": "2026-06-05",
      "hour": 21,
      "minute": 0,
      "title": "New Extreme Trial. Who's interested?",
      "description": "Learning party, bring snacks.",
      "iconKey": "trial",
      "creatorName": "Player Name",
      "creatorWorld": "Cactuar",
      "responses": []
    }
  ]
}
```

`Lodestone.PartySync.QueueRefresh`

Queues a refresh of the currently visible party event range. This is a no-op in External IPC Bridge mode unless the bridge plugin imports fresh data.

### C# Consumer Sketch

```csharp
var eventsIpc = pluginInterface.GetIpcSubscriber<string, string, string>("Lodestone.PartySync.GetEventsJson");
var resultJson = eventsIpc.InvokeFunc("2026-06-01", "2026-06-30");

var createIpc = pluginInterface.GetIpcSubscriber<string, string>("Lodestone.PartySync.QueueEventJson");
var createResultJson = createIpc.InvokeFunc("""
{
  "date": "2026-06-05",
  "hour": 21,
  "minute": 0,
  "title": "New Extreme Trial. Who's interested?",
  "iconKey": "trial"
}
""");
```

All JSON responses include `ok`. Failed calls include `error`.
