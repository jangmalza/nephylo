-- Apply in a Supabase SQL editor as the project owner, or with Supabase migrations.
-- No application setting, JWT user_metadata value, or email address grants Pro/admin.
begin;

create schema if not exists usage_widget_private;
revoke all on schema usage_widget_private from public, anon, authenticated, service_role;

create table if not exists public.member_profiles (
  user_id uuid primary key references auth.users(id) on delete cascade,
  email text not null,
  created_at timestamptz not null default pg_catalog.now(),
  updated_at timestamptz not null default pg_catalog.now()
);

create table if not exists usage_widget_private.membership_admins (
  user_id uuid primary key references auth.users(id) on delete cascade,
  created_at timestamptz not null default pg_catalog.now()
);

create table if not exists usage_widget_private.entitlements (
  user_id uuid primary key references auth.users(id) on delete cascade,
  plan text not null check (plan in ('free', 'pro')),
  status text not null check (status in ('free', 'active', 'canceled', 'expired', 'revoked')),
  expires_at timestamptz,
  updated_at timestamptz not null default pg_catalog.now(),
  constraint entitlement_shape check (
    (plan = 'free' and status in ('free', 'revoked') and expires_at is null)
    or (plan = 'pro' and status in ('active', 'canceled', 'expired')
      and expires_at is not null and pg_catalog.isfinite(expires_at))
  )
);

-- Audit IDs deliberately remain after account deletion; do not store email/tokens here.
create table if not exists usage_widget_private.membership_audit (
  id bigint generated always as identity primary key,
  actor_user_id uuid not null,
  target_user_id uuid not null,
  old_value jsonb not null,
  new_value jsonb not null,
  reason text not null check (pg_catalog.char_length(pg_catalog.btrim(reason)) between 1 and 500),
  created_at timestamptz not null default pg_catalog.now()
);
create index if not exists membership_audit_target_created
  on usage_widget_private.membership_audit(target_user_id, created_at desc);
create index if not exists member_profiles_created
  on public.member_profiles(created_at desc, user_id);

alter table public.member_profiles enable row level security;
alter table usage_widget_private.membership_admins enable row level security;
alter table usage_widget_private.entitlements enable row level security;
alter table usage_widget_private.membership_audit enable row level security;
revoke all on public.member_profiles from public, anon, authenticated, service_role;
grant select on public.member_profiles to authenticated;
drop policy if exists member_profiles_read_own on public.member_profiles;
create policy member_profiles_read_own on public.member_profiles
  for select to authenticated using (user_id = (select auth.uid()));
-- Private tables intentionally have no API policies or grants, even for an admin session.
revoke all on all tables in schema usage_widget_private from public, anon, authenticated, service_role;
revoke all on all sequences in schema usage_widget_private from public, anon, authenticated, service_role;

create or replace function usage_widget_private.sync_member_profile()
returns trigger language plpgsql security definer set search_path = '' as $$
begin
  if new.email is not null and pg_catalog.btrim(new.email) <> '' then
    insert into public.member_profiles(user_id, email, created_at, updated_at)
    values (new.id, new.email, coalesce(new.created_at, pg_catalog.now()), pg_catalog.now())
    on conflict (user_id) do update
      set email = excluded.email, updated_at = excluded.updated_at;
  else
    delete from public.member_profiles where user_id = new.id;
  end if;
  return new;
end;
$$;
revoke all on function usage_widget_private.sync_member_profile() from public, anon, authenticated, service_role;
drop trigger if exists usage_widget_sync_member on auth.users;
create trigger usage_widget_sync_member after insert or update of email on auth.users
  for each row execute function usage_widget_private.sync_member_profile();
insert into public.member_profiles(user_id, email, created_at, updated_at)
select id, email, coalesce(created_at, pg_catalog.now()), pg_catalog.now()
from auth.users where email is not null and pg_catalog.btrim(email) <> ''
on conflict (user_id) do update set email = excluded.email, updated_at = excluded.updated_at;

create or replace function usage_widget_private.require_verified_member()
returns uuid language plpgsql security definer set search_path = '' as $$
declare v_user uuid := auth.uid();
begin
  if v_user is null or not exists (
    select 1 from auth.users u where u.id = v_user
      and u.email is not null and pg_catalog.btrim(u.email) <> ''
      and u.email_confirmed_at is not null
      and (u.banned_until is null or u.banned_until <= pg_catalog.now())
  ) then
    raise exception 'A verified email session is required.' using errcode = '42501';
  end if;
  return v_user;
end;
$$;
revoke all on function usage_widget_private.require_verified_member() from public, anon, authenticated, service_role;

create or replace function usage_widget_private.require_membership_admin()
returns uuid language plpgsql security definer set search_path = '' as $$
declare v_user uuid := usage_widget_private.require_verified_member();
begin
  if not exists (select 1 from usage_widget_private.membership_admins where user_id = v_user) then
    raise exception 'Membership administrator access is required.' using errcode = '42501';
  end if;
  return v_user;
end;
$$;
revoke all on function usage_widget_private.require_membership_admin() from public, anon, authenticated, service_role;

-- Internal helper: callers must first establish identity/administrator authorization.
create or replace function usage_widget_private.membership_snapshot(p_user_id uuid, p_now timestamptz)
returns jsonb language sql security definer set search_path = '' as $$
  select pg_catalog.jsonb_build_object(
    'user_id', u.id,
    'email', u.email,
    'plan', case when e.plan = 'pro' and e.status in ('active', 'canceled')
      and e.expires_at > p_now and pg_catalog.isfinite(e.expires_at)
      and u.email_confirmed_at is not null
      and (u.banned_until is null or u.banned_until <= p_now) then 'pro' else 'free' end,
    'status', case when e.plan = 'pro' and e.status in ('active', 'canceled')
      and e.expires_at <= p_now then 'expired' else coalesce(e.status, 'free') end,
    'expires_at', e.expires_at,
    'server_time', p_now,
    'is_admin', exists (select 1 from usage_widget_private.membership_admins a where a.user_id = u.id),
    'recheck_after_seconds', 300
  )
  from auth.users u left join usage_widget_private.entitlements e on e.user_id = u.id
  where u.id = p_user_id;
$$;
revoke all on function usage_widget_private.membership_snapshot(uuid, timestamptz) from public, anon, authenticated, service_role;

-- POST /rest/v1/rpc/get_my_membership with {}. No user ID parameter exists.
create or replace function public.get_my_membership()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_user uuid := usage_widget_private.require_verified_member();
begin
  return usage_widget_private.membership_snapshot(v_user, pg_catalog.clock_timestamp());
end;
$$;
revoke all on function public.get_my_membership() from public, anon, authenticated, service_role;
grant execute on function public.get_my_membership() to authenticated;

create or replace function public.admin_list_members(
  p_search text default '', p_offset integer default 0, p_limit integer default 50
)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_actor uuid := usage_widget_private.require_membership_admin();
  v_now timestamptz := pg_catalog.clock_timestamp();
  v_result jsonb;
begin
  if p_search is null or pg_catalog.char_length(p_search) > 200
    or p_offset is null or p_offset < 0 or p_offset > 10000
    or p_limit is null or p_limit < 1 or p_limit > 100 then
    raise exception 'Search or pagination values are invalid.' using errcode = '22023';
  end if;
  with candidates as (
    select p.user_id, p.email, p.created_at, u.last_sign_in_at,
      usage_widget_private.membership_snapshot(p.user_id, v_now) as membership
    from public.member_profiles p join auth.users u on u.id = p.user_id
    where p_search = '' or pg_catalog.strpos(pg_catalog.lower(p.email), pg_catalog.lower(pg_catalog.btrim(p_search))) > 0
    order by p.created_at desc, p.user_id
    offset p_offset limit p_limit + 1
  ), numbered as (
    select *, pg_catalog.row_number() over (order by created_at desc, user_id) as row_number from candidates
  )
  select pg_catalog.jsonb_build_object(
    'members', coalesce(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
      'user_id', user_id, 'email', email, 'plan', membership->>'plan',
      'status', membership->>'status', 'expires_at', membership->'expires_at',
      'created_at', created_at, 'last_sign_in_at', last_sign_in_at
    ) order by created_at desc, user_id) filter (where row_number <= p_limit), '[]'::jsonb),
    'has_more', count(*) > p_limit
  ) into v_result from numbered;
  return v_result;
end;
$$;
revoke all on function public.admin_list_members(text, integer, integer) from public, anon, authenticated, service_role;
grant execute on function public.admin_list_members(text, integer, integer) to authenticated;

-- This is a manual support grant/revocation, NOT evidence of a payment.
create or replace function public.admin_set_membership(
  p_user_id uuid, p_plan text, p_expires_at timestamptz, p_reason text
)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
  v_actor uuid := usage_widget_private.require_membership_admin();
  v_now timestamptz := pg_catalog.clock_timestamp();
  v_old jsonb;
  v_new jsonb;
begin
  if p_user_id is null or p_plan is null or p_plan not in ('free', 'pro')
    or p_reason is null or pg_catalog.char_length(pg_catalog.btrim(p_reason)) not between 1 and 500 then
    raise exception 'A user, valid plan, and 1-500 character reason are required.' using errcode = '22023';
  end if;
  if (p_plan = 'pro' and (p_expires_at is null or not pg_catalog.isfinite(p_expires_at)
      or p_expires_at <= v_now or p_expires_at > v_now + interval '366 days'))
    or (p_plan = 'free' and p_expires_at is not null) then
    raise exception 'Pro expiry must be within 366 days; Free expiry must be null.' using errcode = '22023';
  end if;
  -- Lock the auth row so two first-time grants cannot race the audit snapshot.
  perform 1 from auth.users u where u.id = p_user_id
    and u.email is not null and pg_catalog.btrim(u.email) <> ''
    and u.email_confirmed_at is not null
    and (u.banned_until is null or u.banned_until <= v_now)
    for update;
  if not found then
    raise exception 'The target must be an existing verified email member.' using errcode = '22023';
  end if;
  v_old := usage_widget_private.membership_snapshot(p_user_id, v_now)
    - array['email', 'server_time', 'is_admin', 'recheck_after_seconds'];
  insert into usage_widget_private.entitlements(user_id, plan, status, expires_at, updated_at)
  values (p_user_id, p_plan, case when p_plan = 'pro' then 'active' else 'revoked' end, p_expires_at, v_now)
  on conflict (user_id) do update set plan = excluded.plan, status = excluded.status,
    expires_at = excluded.expires_at, updated_at = excluded.updated_at;
  v_new := usage_widget_private.membership_snapshot(p_user_id, v_now);
  insert into usage_widget_private.membership_audit(actor_user_id, target_user_id, old_value, new_value, reason, created_at)
  values (v_actor, p_user_id, v_old,
    v_new - array['email', 'server_time', 'is_admin', 'recheck_after_seconds'], pg_catalog.btrim(p_reason), v_now);
  return v_new;
end;
$$;
revoke all on function public.admin_set_membership(uuid, text, timestamptz, text) from public, anon, authenticated, service_role;
grant execute on function public.admin_set_membership(uuid, text, timestamptz, text) to authenticated;

notify pgrst, 'reload schema';
commit;
