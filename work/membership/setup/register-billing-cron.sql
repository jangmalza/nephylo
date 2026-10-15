-- Optional deployment step. Do not run until BILLING.md test setup is complete.
-- Enable pg_cron and pg_net, and set these three secrets in Supabase Vault:
-- usage_widget_project_url, usage_widget_publishable_key,
-- usage_widget_billing_cron_secret (same value as Edge BILLING_CRON_SECRET).
-- This script never contains the actual credentials and is separate from migrations.
begin;
do $validate$
declare project_url text; public_key text; cron_secret text;
begin
  if not exists (select 1 from pg_extension where extname = 'pg_cron') or
     not exists (select 1 from pg_extension where extname = 'pg_net') then
    raise exception 'Enable pg_cron and pg_net before registering billing renewal';
  end if;
  select decrypted_secret into project_url from vault.decrypted_secrets where name = 'usage_widget_project_url';
  select decrypted_secret into public_key from vault.decrypted_secrets where name = 'usage_widget_publishable_key';
  select decrypted_secret into cron_secret from vault.decrypted_secrets where name = 'usage_widget_billing_cron_secret';
  if project_url is null or project_url !~ '^https://[a-z0-9]+[.]supabase[.]co/?$' or
     coalesce(length(public_key),0) < 20 or coalesce(length(cron_secret),0) < 32 then
    raise exception 'Set the three required Vault secrets before registering billing renewal';
  end if;
end $validate$;

select cron.schedule(
  'usage-widget-billing-renew',
  '* * * * *',
  $job$
    select net.http_post(
      url := rtrim((select decrypted_secret from vault.decrypted_secrets where name = 'usage_widget_project_url'), '/') || '/functions/v1/billing-renew',
      headers := jsonb_build_object(
        'Content-Type', 'application/json',
        'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'usage_widget_publishable_key'),
        'x-billing-cron-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'usage_widget_billing_cron_secret')
      ),
      body := '{}'::jsonb,
      timeout_milliseconds := 120000
    );
  $job$
);
commit;

-- To stop future scheduler calls (does not cancel subscriptions or refund):
-- select cron.unschedule('usage-widget-billing-renew');
