-- Owner-only setup: run in the Supabase SQL editor, never ship as a public RPC.
-- First log into the app/member page with your own verified email.
-- Replace the placeholder below with that account's UUID from Authentication > Users.
do $$
declare
  v_user_text text := 'REPLACE_WITH_VERIFIED_OWNER_USER_UUID';
  v_user uuid;
begin
  if v_user_text = 'REPLACE_WITH_VERIFIED_OWNER_USER_UUID' then
    raise exception 'Replace the owner UUID before running this setup.';
  end if;
  v_user := v_user_text::uuid;
  if not exists (select 1 from auth.users where id = v_user
    and email is not null and email_confirmed_at is not null
    and (banned_until is null or banned_until <= now())) then
    raise exception 'The owner must have a verified email and must not be banned.';
  end if;
  insert into usage_widget_private.membership_admins(user_id)
  values (v_user) on conflict (user_id) do nothing;
end;
$$;
