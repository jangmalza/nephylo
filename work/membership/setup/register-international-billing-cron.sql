-- Optional setup after INTERNATIONAL-BILLING.md test verification. This worker
-- reconciles provider evidence; it never charges a card or creates a checkout.
-- Enable pg_cron + pg_net, and configure these Supabase Vault secrets:
-- usage_widget_project_url, usage_widget_publishable_key,
-- nephylo_international_billing_cron_secret (same as Edge
-- INTERNATIONAL_BILLING_CRON_SECRET). Do not paste actual secrets into this file.
begin;
do $validate$
declare project_url text; public_key text; cron_secret text;
begin
  if not exists(select 1 from pg_extension where extname='pg_cron') or
     not exists(select 1 from pg_extension where extname='pg_net') then
    raise exception 'Enable pg_cron and pg_net before registering reconciliation';
  end if;
  select decrypted_secret into project_url from vault.decrypted_secrets where name='usage_widget_project_url';
  select decrypted_secret into public_key from vault.decrypted_secrets where name='usage_widget_publishable_key';
  select decrypted_secret into cron_secret from vault.decrypted_secrets where name='nephylo_international_billing_cron_secret';
  if project_url is null or project_url !~ '^https://[a-z0-9]+[.]supabase[.]co/?$' or
    coalesce(length(public_key),0)<20 or coalesce(length(cron_secret),0)<32 then
    raise exception 'Configure all three Vault secrets before registering reconciliation';
  end if;
end $validate$;
select cron.schedule(
  'nephylo-international-billing-reconcile',
  '*/5 * * * *',
  $job$
    select net.http_post(
      url := rtrim((select decrypted_secret from vault.decrypted_secrets where name='usage_widget_project_url'),'/') || '/functions/v1/international-billing-reconcile',
      headers := jsonb_build_object(
        'Content-Type','application/json',
        'apikey',(select decrypted_secret from vault.decrypted_secrets where name='usage_widget_publishable_key'),
        'Authorization','Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name='nephylo_international_billing_cron_secret')
      ),
      body := '{}'::jsonb,
      timeout_milliseconds := 120000
    );
  $job$
);
commit;
-- Stop polling only: select cron.unschedule('nephylo-international-billing-reconcile');
