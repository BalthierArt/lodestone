create extension if not exists pgcrypto;

create table if not exists public.lodestone_party_events (
  id uuid primary key default gen_random_uuid(),
  party_hash text not null,
  date date not null,
  hour integer null check (hour between 0 and 23),
  minute integer null check (minute between 0 and 59),
  title text not null check (char_length(title) between 1 and 160),
  description text not null default '' check (char_length(description) <= 600),
  icon_key text not null default 'trial' check (char_length(icon_key) <= 40),
  creator_key text not null,
  creator_name text not null check (char_length(creator_name) between 1 and 80),
  creator_world text not null default '' check (char_length(creator_world) <= 40),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.lodestone_party_event_responses (
  event_id uuid not null references public.lodestone_party_events(id) on delete cascade,
  party_hash text not null,
  player_key text not null,
  player_name text not null check (char_length(player_name) between 1 and 80),
  player_world text not null default '' check (char_length(player_world) <= 40),
  status text not null check (status in ('interested', 'maybe')),
  updated_at timestamptz not null default now(),
  primary key (event_id, player_key)
);

create index if not exists idx_lodestone_party_events_party_date
  on public.lodestone_party_events(party_hash, date);

create index if not exists idx_lodestone_party_responses_party_event
  on public.lodestone_party_event_responses(party_hash, event_id);

alter table public.lodestone_party_events enable row level security;
alter table public.lodestone_party_event_responses enable row level security;

-- Intentionally no anon RLS policies: the Edge Function uses the service-role key.
-- Optional cleanup, run manually or as a scheduled job:
-- delete from public.lodestone_party_events where date < current_date - interval '180 days';
