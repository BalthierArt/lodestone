const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

type Actor = {
  key?: string;
  name?: string;
  world?: string;
};

type PartyEventInput = {
  id?: string | null;
  date?: string | null;
  hour?: number | null;
  minute?: number | null;
  title?: string | null;
  description?: string | null;
  iconKey?: string | null;
  creatorName?: string | null;
  creatorWorld?: string | null;
};

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  try {
    if (req.method !== "POST") {
      return json({ error: "POST required" }, 405);
    }

    const body = await req.json();
    const action = String(body.action ?? "");
    const partyKey = assertString(body.partyKey, "partyKey", 4, 128);
    const partyHash = await sha256(partyKey);

    switch (action) {
      case "list":
        return json({ events: await listEvents(partyHash, body.rangeStart, body.rangeEnd) });
      case "upsertEvent":
        return json({ event: await upsertEvent(partyHash, body.actor, body.event) });
      case "respond":
        return json({ event: await respond(partyHash, body.actor, body.eventId, body.responseStatus) });
      case "deleteEvent":
        await deleteEvent(partyHash, body.actor, body.eventId);
        return json({ ok: true });
      default:
        return json({ error: "Unknown action" }, 400);
    }
  } catch (err) {
    return json({ error: err instanceof Error ? err.message : String(err) }, 400);
  }
});

async function listEvents(partyHash: string, rangeStart: unknown, rangeEnd: unknown) {
  const start = assertDate(rangeStart, "rangeStart");
  const end = assertDate(rangeEnd, "rangeEnd");
  const events = await rest(
    `lodestone_party_events?party_hash=eq.${encodeURIComponent(partyHash)}&date=gte.${start}&date=lte.${end}&order=date.asc,hour.asc,minute.asc,created_at.asc`,
  );
  return attachResponses(partyHash, events);
}

async function upsertEvent(partyHash: string, actorInput: unknown, eventInput: unknown) {
  const actor = normalizeActor(actorInput);
  const event = normalizeEvent(eventInput, actor);
  const now = new Date().toISOString();

  if (event.id) {
    const existing = await getEvent(partyHash, event.id);
    if (!existing) {
      throw new Error("Party event not found.");
    }
    if (existing.creator_key !== actor.key) {
      throw new Error("Only the creator can edit this party event.");
    }

    const updated = await rest(
      `lodestone_party_events?id=eq.${encodeURIComponent(event.id)}&party_hash=eq.${encodeURIComponent(partyHash)}&creator_key=eq.${encodeURIComponent(actor.key)}`,
      {
        method: "PATCH",
        headers: { Prefer: "return=representation" },
        body: JSON.stringify({
          date: event.date,
          hour: event.hour,
          minute: event.minute,
          title: event.title,
          description: event.description,
          icon_key: event.icon_key,
          creator_name: actor.name,
          creator_world: actor.world,
          updated_at: now,
        }),
      },
    );
    return attachSingleResponse(partyHash, updated[0]);
  }

  const inserted = await rest("lodestone_party_events", {
    method: "POST",
    headers: { Prefer: "return=representation" },
    body: JSON.stringify({
      party_hash: partyHash,
      date: event.date,
      hour: event.hour,
      minute: event.minute,
      title: event.title,
      description: event.description,
      icon_key: event.icon_key,
      creator_key: actor.key,
      creator_name: actor.name,
      creator_world: actor.world,
      created_at: now,
      updated_at: now,
    }),
  });
  return attachSingleResponse(partyHash, inserted[0]);
}

async function respond(partyHash: string, actorInput: unknown, eventIdInput: unknown, statusInput: unknown) {
  const actor = normalizeActor(actorInput);
  const eventId = assertString(eventIdInput, "eventId", 1, 80);
  const event = await getEvent(partyHash, eventId);
  if (!event) {
    throw new Error("Party event not found.");
  }

  const status = String(statusInput ?? "");
  if (status === "remove") {
    await rest(
      `lodestone_party_event_responses?event_id=eq.${encodeURIComponent(eventId)}&party_hash=eq.${encodeURIComponent(partyHash)}&player_key=eq.${encodeURIComponent(actor.key)}`,
      { method: "DELETE" },
    );
    return attachSingleResponse(partyHash, event);
  }

  if (status !== "interested" && status !== "maybe") {
    throw new Error("Invalid response status.");
  }

  await rest("lodestone_party_event_responses?on_conflict=event_id,player_key", {
    method: "POST",
    headers: { Prefer: "resolution=merge-duplicates,return=representation" },
    body: JSON.stringify({
      event_id: eventId,
      party_hash: partyHash,
      player_key: actor.key,
      player_name: actor.name,
      player_world: actor.world,
      status,
      updated_at: new Date().toISOString(),
    }),
  });

  return attachSingleResponse(partyHash, event);
}

async function deleteEvent(partyHash: string, actorInput: unknown, eventIdInput: unknown) {
  const actor = normalizeActor(actorInput);
  const eventId = assertString(eventIdInput, "eventId", 1, 80);
  const existing = await getEvent(partyHash, eventId);
  if (!existing) {
    throw new Error("Party event not found.");
  }
  if (existing.creator_key !== actor.key) {
    throw new Error("Only the creator can delete this party event.");
  }

  await rest(
    `lodestone_party_events?id=eq.${encodeURIComponent(eventId)}&party_hash=eq.${encodeURIComponent(partyHash)}&creator_key=eq.${encodeURIComponent(actor.key)}`,
    { method: "DELETE" },
  );
}

async function getEvent(partyHash: string, id: string) {
  const rows = await rest(
    `lodestone_party_events?id=eq.${encodeURIComponent(id)}&party_hash=eq.${encodeURIComponent(partyHash)}&limit=1`,
  );
  return rows[0] ?? null;
}

async function attachSingleResponse(partyHash: string, event: Record<string, unknown>) {
  const rows = await attachResponses(partyHash, [event]);
  return rows[0];
}

async function attachResponses(partyHash: string, events: Record<string, unknown>[]) {
  if (events.length === 0) {
    return [];
  }

  const ids = events.map((event) => event.id).filter(Boolean).join(",");
  const responses = await rest(
    `lodestone_party_event_responses?party_hash=eq.${encodeURIComponent(partyHash)}&event_id=in.(${ids})&order=updated_at.asc`,
  );
  const byEvent = new Map<string, Record<string, unknown>[]>();
  for (const response of responses) {
    const key = String(response.event_id);
    byEvent.set(key, [...(byEvent.get(key) ?? []), response]);
  }

  return events.map((event) => ({
    id: event.id,
    date: event.date,
    hour: event.hour,
    minute: event.minute,
    title: event.title,
    description: event.description,
    iconKey: event.icon_key,
    creatorName: event.creator_name,
    creatorWorld: event.creator_world,
    createdAt: event.created_at,
    updatedAt: event.updated_at,
    responses: (byEvent.get(String(event.id)) ?? []).map((response) => ({
      playerKey: response.player_key,
      playerName: response.player_name,
      playerWorld: response.player_world,
      status: response.status,
      updatedAt: response.updated_at,
    })),
  }));
}

async function rest(path: string, init: RequestInit = {}) {
  const url = requiredEnv("SUPABASE_URL").replace(/\/$/, "");
  const serviceRoleKey = requiredEnv("SUPABASE_SERVICE_ROLE_KEY");
  const headers = new Headers(init.headers);
  headers.set("apikey", serviceRoleKey);
  headers.set("authorization", `Bearer ${serviceRoleKey}`);
  headers.set("content-type", "application/json");

  const response = await fetch(`${url}/rest/v1/${path}`, { ...init, headers });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Database request failed: ${text}`);
  }

  if (!text) {
    return [];
  }

  return JSON.parse(text);
}

function normalizeActor(input: unknown) {
  const actor = input as Actor;
  return {
    key: assertString(actor?.key, "actor.key", 16, 128),
    name: assertString(actor?.name, "actor.name", 1, 80),
    world: optionalString(actor?.world, 40),
  };
}

function normalizeEvent(input: unknown, actor: { name: string; world: string }) {
  const event = input as PartyEventInput;
  return {
    id: optionalString(event?.id, 80) || null,
    date: assertDate(event?.date, "event.date"),
    hour: optionalNumber(event?.hour, 0, 23),
    minute: optionalNumber(event?.minute, 0, 59),
    title: assertString(event?.title, "event.title", 1, 160),
    description: optionalString(event?.description, 600),
    icon_key: optionalString(event?.iconKey, 40) || "trial",
    creator_name: actor.name,
    creator_world: actor.world,
  };
}

function assertString(value: unknown, name: string, min: number, max: number) {
  const text = String(value ?? "").trim();
  if (text.length < min || text.length > max) {
    throw new Error(`${name} length must be between ${min} and ${max}.`);
  }
  return text;
}

function optionalString(value: unknown, max: number) {
  const text = String(value ?? "").trim();
  if (text.length > max) {
    throw new Error(`Text length must be ${max} or less.`);
  }
  return text;
}

function assertDate(value: unknown, name: string) {
  const text = String(value ?? "").trim();
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) {
    throw new Error(`${name} must be YYYY-MM-DD.`);
  }
  return text;
}

function optionalNumber(value: unknown, min: number, max: number) {
  if (value === null || value === undefined) {
    return null;
  }

  const number = Number(value);
  if (!Number.isInteger(number) || number < min || number > max) {
    throw new Error(`Number must be between ${min} and ${max}.`);
  }
  return number;
}

async function sha256(value: string) {
  const bytes = new TextEncoder().encode(value);
  const hash = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(hash)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function requiredEnv(name: string) {
  const value = Deno.env.get(name);
  if (!value) {
    throw new Error(`${name} is not configured.`);
  }
  return value;
}

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      ...corsHeaders,
      "content-type": "application/json",
    },
  });
}
