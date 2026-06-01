# Lodestone Party Sync IPC

This document is for plugin authors who want to bridge Lodestone Calendar party events through another sync or sharing plugin.

The player-facing README is here:

[README.md](README.md)

## Overview

Lodestone exposes local Dalamud IPC so other plugins can integrate with party event sharing. This is intended for sync or mod-sharing clients that may want to relay Lodestone party event payloads through their own network.

Important: IPC is local to the player's client. Another plugin must still call Lodestone's IPC and relay the data through its own sharing system if it wants to sync through Snowcloak, Light Sync, Mare-style groups, or another transport.

When External IPC Bridge is enabled, Lodestone does not require its own separate Party Key. The bridge plugin can use its own existing group/key system.

## IPC Providers

`Lodestone.PartySync.ApiVersion`

Returns the IPC contract version as a string.

`Lodestone.PartySync.IsConfigured`

Returns whether Lodestone can currently create or share party events through either Supabase or External IPC Bridge.

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

## C# Consumer Sketch

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
