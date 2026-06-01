# Lodestone Party Sync Supabase Setup

Party sync uses a Supabase Edge Function so the Dalamud plugin only needs your project URL and public anon key. Do not put the service-role key in the plugin.

1. Create a Supabase project.
2. Run `schema.sql` in the Supabase SQL editor.
3. Create an Edge Function named `lodestone-party-sync`.
4. Copy `functions/lodestone-party-sync/index.ts` into that function.
5. Add the function secret `SUPABASE_SERVICE_ROLE_KEY` with your project's service-role key.
6. Deploy the function.
7. In game, open Lodestone settings, Party Sync, then fill in:
   - Supabase project URL
   - Supabase anon key
   - Party key

The shared party key behaves like an invite link: anyone who knows it can see and update events for that key. New clients send a SHA-256 hash of the party key to the Edge Function, while the raw Party key stays client-side and is used to encrypt/decrypt display names.

Supabase stores party event rows and RSVP rows, but player display names and worlds are written as encrypted text. Players using the same Party key can still see names in game; the Supabase tables should not contain a readable list of character names.

If you used Party Sync before encrypted display names were added, old rows may still contain readable names. After deploying the updated function and schema, delete the old test rows if you do not want them retained:

```sql
delete from public.lodestone_party_events;
```

Responses are deleted automatically because they reference the event rows.

## IPC Bridge Notes

Lodestone also exposes local Dalamud IPC under `Lodestone.PartySync.*`. Other plugins can use that IPC to read cached party events, create events, update responses, import events received from their own network, and request refreshes.

IPC is not a network transport by itself. Supabase remains Lodestone's built-in sharing path. If a sync or mod-sharing plugin wants to carry Lodestone party events through its own network, it should call the IPC providers documented in the root `README.md` and relay those JSON payloads itself. In External IPC Bridge mode, Lodestone does not require a separate Party key because the other plugin's group/key controls sharing.
