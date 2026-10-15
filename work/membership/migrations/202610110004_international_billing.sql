-- Lemon Squeezy is an independent entitlement source. Apply after 0001-0003.
-- Only service-role RPCs can write provider evidence; no client success URL grants Pro.
begin;
create table usage_widget_private.billing_provider_reservations (
  user_id uuid primary key references auth.users(id) on delete cascade,
  provider text not null check(provider in ('toss','lemonsqueezy')),
  checkout_id uuid not null,
  expires_at timestamptz not null check(pg_catalog.isfinite(expires_at))
);
create table usage_widget_private.international_checkouts (
  id uuid primary key default pg_catalog.gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  token_hash text not null unique check(token_hash ~ '^[a-f0-9]{64}$'),
  store_id text not null, product_id text not null, variant_id text not null, price_id text not null,
  test_mode boolean not null, expected_subtotal bigint not null check(expected_subtotal>0),
  terms_version text not null check(pg_catalog.char_length(terms_version) between 1 and 100),
  consent_at timestamptz not null default pg_catalog.now(),
  expires_at timestamptz not null default (pg_catalog.now()+interval '30 minutes'),
  status text not null default 'creating' check(status in ('creating','ready','completed','failed')),
  provider_checkout_id uuid unique, checkout_url text,
  created_at timestamptz not null default pg_catalog.now()
);
create index international_checkout_user on usage_widget_private.international_checkouts(user_id,created_at desc);
create table usage_widget_private.international_subscriptions (
  id text primary key check(id ~ '^[1-9][0-9]{0,19}$'),
  user_id uuid not null references auth.users(id) on delete cascade,
  checkout_id uuid not null unique references usage_widget_private.international_checkouts(id),
  order_id text not null unique, customer_id text not null,
  status text not null, auto_renew boolean not null,
  ends_at timestamptz, renews_at timestamptz,
  provider_updated_at timestamptz not null check(pg_catalog.isfinite(provider_updated_at)),
  created_at timestamptz not null default pg_catalog.now(), checked_at timestamptz not null default pg_catalog.now()
);
create index international_subscription_user on usage_widget_private.international_subscriptions(user_id,created_at desc);
create table usage_widget_private.international_invoices (
  id text primary key check(id ~ '^[1-9][0-9]{0,19}$'),
  subscription_id text not null references usage_widget_private.international_subscriptions(id),
  status text not null check(status in ('paid','refunded','partial_refund','pending','void')),
  billing_reason text not null check(billing_reason in ('initial','renewal','updated')),
  period_start timestamptz not null, period_end timestamptz not null,
  provider_updated_at timestamptz not null, subtotal bigint not null,
  check(pg_catalog.isfinite(period_start) and pg_catalog.isfinite(period_end) and period_end>period_start and period_end<=period_start+interval '32 days'),
  check(pg_catalog.isfinite(provider_updated_at))
);
create table usage_widget_private.international_events (
  digest text primary key check(digest ~ '^[a-f0-9]{64}$'),
  event_name text not null, subscription_id text not null,
  created_at timestamptz not null default pg_catalog.now()
);
create table usage_widget_private.international_webhook_inbox (
  digest text primary key check(digest ~ '^[a-f0-9]{64}$'),
  event_name text not null, resource_type text not null check(resource_type in ('subscriptions','subscription-invoices','orders')),
  resource_id text not null check(resource_id ~ '^[1-9][0-9]{0,19}$'), token_hash text,
  received_at timestamptz not null default pg_catalog.now(), processed_at timestamptz,
  next_attempt_at timestamptz not null default pg_catalog.now()
);
alter table usage_widget_private.billing_provider_reservations enable row level security;
alter table usage_widget_private.international_checkouts enable row level security;
alter table usage_widget_private.international_subscriptions enable row level security;
alter table usage_widget_private.international_invoices enable row level security;
alter table usage_widget_private.international_events enable row level security;
alter table usage_widget_private.international_webhook_inbox enable row level security;
revoke all on all tables in schema usage_widget_private from public,anon,authenticated,service_role;

create function usage_widget_private.international_paid_until(p_subscription_id text)
returns timestamptz language sql stable security definer set search_path='' as $$
  select case when max(i.period_end) is null then null
    when s.status='expired' then least(max(i.period_end),s.ends_at,pg_catalog.now())
    else least(max(i.period_end),s.ends_at) end
  from usage_widget_private.international_subscriptions s left join usage_widget_private.international_invoices i
    on i.subscription_id=s.id and i.status in ('paid','partial_refund') and i.billing_reason in ('initial','renewal')
  where s.id=p_subscription_id group by s.status,s.ends_at;
$$;
create function usage_widget_private.international_blocks_checkout(p_user_id uuid)
returns boolean language sql stable security definer set search_path='' as $$
  select exists(select 1 from usage_widget_private.international_subscriptions s where s.user_id=p_user_id and
    (s.status not in ('cancelled','expired') or usage_widget_private.international_paid_until(s.id)>pg_catalog.now()));
$$;
-- Both providers lock the same auth row and reserve the same row before checkout.
-- Triggers cover old Toss Edge deployments as well as the new international API.
create function usage_widget_private.guard_toss_checkout()
returns trigger language plpgsql security definer set search_path='' as $$
begin
  perform 1 from auth.users where id=new.user_id for update;
  if usage_widget_private.international_blocks_checkout(new.user_id) or exists(
    select 1 from usage_widget_private.billing_provider_reservations r where r.user_id=new.user_id
      and r.provider='lemonsqueezy' and exists(select 1 from usage_widget_private.international_checkouts c where c.id=r.checkout_id and c.status in ('creating','ready'))) then
    raise exception 'Another provider is active or checkout is pending.' using errcode='P0001',hint='ALREADY_SUBSCRIBED';
  end if;
  insert into usage_widget_private.billing_provider_reservations(user_id,provider,checkout_id,expires_at)
    values(new.user_id,'toss',new.id,new.expires_at+interval '15 minutes') on conflict(user_id)
    do update set provider=excluded.provider,checkout_id=excluded.checkout_id,expires_at=excluded.expires_at;
  return new;
end; $$;
create trigger international_guard_toss_checkout before insert on usage_widget_private.billing_checkout_intents
  for each row execute function usage_widget_private.guard_toss_checkout();

create function public.international_begin_checkout(p_user_id uuid,p_token_hash text,p_config jsonb,p_terms_version text)
returns jsonb language plpgsql security definer set search_path='' as $$
declare v_id uuid; v_expires timestamptz; v_existing usage_widget_private.international_checkouts%rowtype;
begin
  perform 1 from auth.users where id=p_user_id and email_confirmed_at is not null and email is not null
    and (banned_until is null or banned_until<=pg_catalog.now()) for update;
  if not found then raise exception 'Verified member required.' using errcode='42501'; end if;
  if p_token_hash is null or p_token_hash !~ '^[a-f0-9]{64}$' or p_terms_version is null or pg_catalog.char_length(p_terms_version) not between 1 and 100
    or coalesce(p_config->>'store_id','') !~ '^[1-9][0-9]{0,19}$' or coalesce(p_config->>'product_id','') !~ '^[1-9][0-9]{0,19}$'
    or coalesce(p_config->>'variant_id','') !~ '^[1-9][0-9]{0,19}$' or coalesce(p_config->>'price_id','') !~ '^[1-9][0-9]{0,19}$'
    or pg_catalog.jsonb_typeof(p_config->'test_mode') is distinct from 'boolean' or (p_config->>'expected_subtotal')::bigint<=0 then
    raise exception 'Invalid checkout configuration.' using errcode='22023';
  end if;
  if usage_widget_private.international_blocks_checkout(p_user_id) or exists(
    select 1 from usage_widget_private.billing_subscriptions s where s.user_id=p_user_id and
      (s.current_period_end>pg_catalog.now() or (s.auto_renew and s.status='active') or exists(select 1 from usage_widget_private.billing_invoices i
        where i.subscription_id=s.id and i.status in ('pending','charging','unknown','review','partial_refund')))) then
    return pg_catalog.jsonb_build_object('blocked','ALREADY_SUBSCRIBED');
  end if;
  if exists(select 1 from usage_widget_private.billing_provider_reservations r where r.user_id=p_user_id and
    ((r.provider='toss' and r.expires_at>pg_catalog.now()) or (r.provider='lemonsqueezy' and exists(
      select 1 from usage_widget_private.international_checkouts c where c.id=r.checkout_id and c.status in ('creating','ready'))))) then
    select * into v_existing from usage_widget_private.international_checkouts c where c.user_id=p_user_id and c.status='ready'
      and c.expires_at>pg_catalog.now() order by c.created_at desc limit 1;
    if found then return pg_catalog.jsonb_build_object('existing',true,'checkout_url',v_existing.checkout_url,'expires_at',v_existing.expires_at); end if;
    return pg_catalog.jsonb_build_object('blocked','BILLING_PENDING');
  end if;
  -- Pre-migration Toss checkouts have no reservation yet.
  if exists(select 1 from usage_widget_private.billing_checkout_intents where user_id=p_user_id and status='pending' and expires_at+interval '15 minutes'>pg_catalog.now()) then
    return pg_catalog.jsonb_build_object('blocked','BILLING_PENDING');
  end if;
  insert into usage_widget_private.international_checkouts(user_id,token_hash,store_id,product_id,variant_id,price_id,test_mode,expected_subtotal,terms_version)
    values(p_user_id,p_token_hash,p_config->>'store_id',p_config->>'product_id',p_config->>'variant_id',p_config->>'price_id',
      (p_config->>'test_mode')::boolean,(p_config->>'expected_subtotal')::bigint,p_terms_version) returning id,expires_at into v_id,v_expires;
  insert into usage_widget_private.billing_provider_reservations(user_id,provider,checkout_id,expires_at)
    values(p_user_id,'lemonsqueezy',v_id,v_expires+interval '15 minutes') on conflict(user_id)
    do update set provider=excluded.provider,checkout_id=excluded.checkout_id,expires_at=excluded.expires_at;
  return pg_catalog.jsonb_build_object('checkout_id',v_id,'expires_at',v_expires);
end; $$;

create function public.international_checkout_ready(p_user_id uuid,p_checkout_id uuid,p_provider_checkout_id uuid,p_url text)
returns boolean language plpgsql security definer set search_path='' as $$
begin
  if p_url is null or pg_catalog.char_length(p_url)>4096 or p_url !~ '^https://' then raise exception 'Invalid URL.'; end if;
  update usage_widget_private.international_checkouts set provider_checkout_id=p_provider_checkout_id,checkout_url=p_url,status='ready'
    where id=p_checkout_id and user_id=p_user_id and status='creating' and expires_at>pg_catalog.now();
  return found;
end; $$;
create function public.international_lookup(p_subscription_id text default null,p_token_hash text default null,p_user_id uuid default null,p_order_id text default null)
returns jsonb language sql security definer set search_path='' as $$
  select (pg_catalog.to_jsonb(c)-array['checkout_url','token_hash']) || pg_catalog.jsonb_build_object('subscription_id',s.id,'order_id',s.order_id,'customer_id',s.customer_id)
  from usage_widget_private.international_checkouts c left join usage_widget_private.international_subscriptions s on s.checkout_id=c.id
  where (p_subscription_id is not null and s.id=p_subscription_id) or (p_token_hash is not null and c.token_hash=p_token_hash)
    or (p_user_id is not null and s.user_id=p_user_id) or (p_order_id is not null and s.order_id=p_order_id)
  order by s.created_at desc nulls last,c.created_at desc limit 1;
$$;

-- A single transaction permanently binds ownership and upserts immutable invoice IDs.
-- A refund is terminal for that invoice; an older success never resurrects it.
create function public.international_apply(p_checkout_id uuid,p_subscription jsonb,p_invoices jsonb,p_event_digest text,p_event_name text)
returns boolean language plpgsql security definer set search_path='' as $$
declare c usage_widget_private.international_checkouts%rowtype; s usage_widget_private.international_subscriptions%rowtype;
  inv jsonb; v_user uuid; v_id text:=p_subscription->>'id'; v_updated timestamptz:=(p_subscription->>'updated_at')::timestamptz;
begin
  select user_id into v_user from usage_widget_private.international_checkouts where id=p_checkout_id;
  if v_user is null then raise exception 'Unknown checkout.' using errcode='22023'; end if;
  perform 1 from auth.users where id=v_user for update;
  select * into c from usage_widget_private.international_checkouts where id=p_checkout_id for update;
  if v_id is null or v_id !~ '^[1-9][0-9]{0,19}$' or not pg_catalog.isfinite(v_updated)
    or p_subscription->>'store_id' is distinct from c.store_id or p_subscription->>'product_id' is distinct from c.product_id
    or p_subscription->>'variant_id' is distinct from c.variant_id or p_subscription->>'price_id' is distinct from c.price_id
    or (p_subscription->>'test_mode')::boolean is distinct from c.test_mode
    or p_subscription->>'status' not in ('on_trial','active','paused','past_due','unpaid','cancelled','expired')
    or pg_catalog.jsonb_typeof(p_invoices) is distinct from 'array' or pg_catalog.jsonb_array_length(p_invoices)>101 then
    raise exception 'Provider evidence mismatch.' using errcode='22023';
  end if;
  select * into s from usage_widget_private.international_subscriptions where id=v_id for update;
  if found then
    if s.checkout_id<>c.id or s.user_id<>c.user_id or s.customer_id is distinct from p_subscription->>'customer_id' or s.order_id is distinct from p_subscription->>'order_id' then
      raise exception 'Subscription ownership mismatch.' using errcode='42501';
    end if;
  else
    if c.status not in ('creating','ready') or (p_subscription->>'created_at')::timestamptz<c.created_at-interval '1 minute'
      or (p_subscription->>'created_at')::timestamptz>c.expires_at+interval '1 minute' then
      raise exception 'Checkout was reused or expired.' using errcode='22023';
    end if;
    insert into usage_widget_private.international_subscriptions(id,user_id,checkout_id,order_id,customer_id,status,auto_renew,provider_updated_at)
      values(v_id,c.user_id,c.id,p_subscription->>'order_id',p_subscription->>'customer_id',p_subscription->>'status',false,v_updated);
    update usage_widget_private.international_checkouts set status='completed',checkout_url=null where id=c.id;
  end if;
  update usage_widget_private.international_subscriptions set status=p_subscription->>'status',auto_renew=(p_subscription->>'auto_renew')::boolean,
    ends_at=(p_subscription->>'ends_at')::timestamptz,renews_at=(p_subscription->>'renews_at')::timestamptz,
    provider_updated_at=v_updated,checked_at=pg_catalog.now() where id=v_id and provider_updated_at<=v_updated;
  for inv in select value from pg_catalog.jsonb_array_elements(p_invoices) loop
    if (inv->>'subtotal')::bigint is distinct from c.expected_subtotal or inv->>'subscription_id' is distinct from v_id
      or inv->>'status' not in ('paid','refunded','partial_refund','pending','void') then
      raise exception 'Invoice evidence mismatch.' using errcode='22023';
    end if;
    if exists(select 1 from usage_widget_private.international_invoices where id=inv->>'id' and
      (subscription_id<>v_id or period_start<>(inv->>'period_start')::timestamptz)) then raise exception 'Invoice ownership changed.' using errcode='42501'; end if;
    insert into usage_widget_private.international_invoices(id,subscription_id,status,billing_reason,period_start,period_end,provider_updated_at,subtotal)
      values(inv->>'id',v_id,inv->>'status',inv->>'billing_reason',(inv->>'period_start')::timestamptz,(inv->>'period_end')::timestamptz,
        (inv->>'updated_at')::timestamptz,(inv->>'subtotal')::bigint)
      on conflict(id) do update set status=case when international_invoices.status='refunded' then 'refunded'
          when international_invoices.status='partial_refund' and excluded.status='paid' then 'partial_refund' else excluded.status end,
        period_end=least(international_invoices.period_end,excluded.period_end),provider_updated_at=excluded.provider_updated_at
      where international_invoices.provider_updated_at<=excluded.provider_updated_at or excluded.status='refunded';
  end loop;
  if p_event_digest is not null then
    insert into usage_widget_private.international_events(digest,event_name,subscription_id) values(p_event_digest,p_event_name,v_id) on conflict(digest) do nothing;
  end if;
  return true;
end; $$;

create function public.get_my_international_billing()
returns jsonb language plpgsql security definer set search_path='' as $$
declare v_user uuid:=usage_widget_private.require_verified_member(); v_result jsonb;
begin
  select pg_catalog.jsonb_build_object('provider','lemonsqueezy','status',s.status,'auto_renew',s.auto_renew,'cancel_at_period_end',not s.auto_renew,
    'current_period_end',usage_widget_private.international_paid_until(s.id),'next_billing_at',case when s.auto_renew then s.renews_at else null end)
    into v_result from usage_widget_private.international_subscriptions s where s.user_id=v_user order by s.created_at desc limit 1;
  return pg_catalog.jsonb_build_object('subscription',v_result,'pendingCheckout',exists(select 1 from usage_widget_private.international_checkouts
    where user_id=v_user and status in ('creating','ready')));
end; $$;

create or replace function usage_widget_private.membership_snapshot(p_user_id uuid,p_now timestamptz)
returns jsonb language sql security definer set search_path='' as $$
  with sources as (
    select e.expires_at,case when e.status='canceled' then 'canceled' else 'active' end as status
      from usage_widget_private.entitlements e where e.user_id=p_user_id and e.plan='pro' and e.status in ('active','canceled') and e.expires_at>p_now
    union all select s.current_period_end,case when s.auto_renew then 'active' else 'canceled' end
      from usage_widget_private.billing_subscriptions s join usage_widget_private.billing_invoices i on i.id=s.current_invoice_id
      where s.user_id=p_user_id and s.current_period_end>p_now and i.status in ('paid','partial_refund')
    union all select usage_widget_private.international_paid_until(s.id),case when s.auto_renew then 'active' else 'canceled' end
      from usage_widget_private.international_subscriptions s where s.user_id=p_user_id and usage_widget_private.international_paid_until(s.id)>p_now
  ), best as (select * from sources order by expires_at desc limit 1)
  select pg_catalog.jsonb_build_object('user_id',u.id,'email',u.email,
    'plan',case when b.expires_at is not null and u.email_confirmed_at is not null and (u.banned_until is null or u.banned_until<=p_now) then 'pro' else 'free' end,
    'status',case when b.expires_at is not null then b.status when e.plan='pro' and e.status in ('active','canceled') and e.expires_at<=p_now then 'expired'
      when s.current_period_end<=p_now and e.status is null then 'expired' else coalesce(e.status,'free') end,
    'expires_at',coalesce(b.expires_at,e.expires_at,s.current_period_end),'server_time',p_now,
    'is_admin',exists(select 1 from usage_widget_private.membership_admins a where a.user_id=u.id),'recheck_after_seconds',300)
  from auth.users u left join usage_widget_private.entitlements e on e.user_id=u.id
    left join usage_widget_private.billing_subscriptions s on s.user_id=u.id left join best b on true where u.id=p_user_id;
$$;
-- Existing admin responses retain manual/billing fields and gain international.
alter function usage_widget_private.admin_entitlement_sources(uuid,timestamptz) rename to admin_entitlement_sources_before_international;
create function usage_widget_private.admin_entitlement_sources(p_user_id uuid,p_now timestamptz)
returns jsonb language sql stable security definer set search_path='' as $$
  select usage_widget_private.admin_entitlement_sources_before_international(p_user_id,p_now) || pg_catalog.jsonb_build_object('international',
    coalesce((select pg_catalog.jsonb_build_object('provider','lemonsqueezy','status',s.status,'auto_renew',s.auto_renew,
      'expires_at',usage_widget_private.international_paid_until(s.id),'active',coalesce(usage_widget_private.international_paid_until(s.id)>p_now,false))
      from usage_widget_private.international_subscriptions s where s.user_id=p_user_id order by s.created_at desc limit 1),
      pg_catalog.jsonb_build_object('provider','lemonsqueezy','status','none','active',false)));
$$;
create function public.international_enqueue_event(p_event jsonb)
returns boolean language plpgsql security definer set search_path='' as $$
begin
  insert into usage_widget_private.international_webhook_inbox(digest,event_name,resource_type,resource_id,token_hash)
    values(p_event->>'digest',p_event->>'event_name',p_event->>'resource_type',p_event->>'resource_id',p_event->>'token_hash')
    on conflict(digest) do nothing;
  return exists(select 1 from usage_widget_private.international_webhook_inbox where digest=p_event->>'digest' and processed_at is null);
end; $$;
create function public.international_pending_events(p_user_id uuid default null,p_limit integer default 5)
returns jsonb language plpgsql security definer set search_path='' as $$
declare result jsonb;
begin
  if p_limit is null or p_limit not between 1 and 5 then raise exception 'Invalid batch size.'; end if;
  with candidates as (select e.digest from usage_widget_private.international_webhook_inbox e
    where e.processed_at is null and e.next_attempt_at<=pg_catalog.now() and (p_user_id is null or exists(
      select 1 from usage_widget_private.international_checkouts c left join usage_widget_private.international_subscriptions s on s.checkout_id=c.id
      where c.user_id=p_user_id and (c.token_hash=e.token_hash or (e.resource_type='subscriptions' and s.id=e.resource_id) or (e.resource_type='orders' and s.order_id=e.resource_id))))
    order by e.next_attempt_at,e.received_at limit p_limit for update skip locked),
    claimed as (update usage_widget_private.international_webhook_inbox e set next_attempt_at=pg_catalog.now()+interval '5 minutes'
      from candidates c where c.digest=e.digest returning e.*)
  select coalesce(pg_catalog.jsonb_agg(pg_catalog.to_jsonb(claimed)),'[]'::jsonb) into result from claimed;
  return result;
end; $$;
create function public.international_finish_event(p_digest text)
returns void language sql security definer set search_path='' as $$
  update usage_widget_private.international_webhook_inbox set processed_at=pg_catalog.now() where digest=p_digest;
$$;
create function public.international_due_subscriptions(p_limit integer default 5)
returns jsonb language plpgsql security definer set search_path='' as $$
declare result jsonb;
begin
  if p_limit is null or p_limit not between 1 and 5 then raise exception 'Invalid batch size.'; end if;
  with candidates as (select id from usage_widget_private.international_subscriptions where checked_at<pg_catalog.now()-interval '6 hours'
    and (status<>'expired' or usage_widget_private.international_paid_until(id)>pg_catalog.now()) order by checked_at limit p_limit for update skip locked),
    claimed as (update usage_widget_private.international_subscriptions s set checked_at=pg_catalog.now()-interval '5 hours 55 minutes'
      from candidates c where c.id=s.id returning s.id)
  select coalesce(pg_catalog.jsonb_agg(public.international_lookup(p_subscription_id=>id)),'[]'::jsonb) into result from claimed;
  return result;
end; $$;
-- Operator-only recovery: first verify in the Lemon dashboard that this checkout
-- has no paid order/subscription. Never automatically release an expired URL;
-- a successful payment whose first webhook is lost would otherwise be charged twice.
create function public.international_resolve_unpaid_checkout(p_checkout_id uuid,p_reason text)
returns boolean language plpgsql security definer set search_path='' as $$
declare v_user uuid;
begin
  if p_reason is null or pg_catalog.char_length(pg_catalog.btrim(p_reason)) not between 10 and 500 then raise exception 'Document the provider verification.'; end if;
  select user_id into v_user from usage_widget_private.international_checkouts where id=p_checkout_id;
  perform 1 from auth.users where id=v_user for update;
  update usage_widget_private.international_checkouts set status='failed',checkout_url=null
    where id=p_checkout_id and status in ('creating','ready') and expires_at<pg_catalog.now();
  if not found then return false; end if;
  delete from usage_widget_private.billing_provider_reservations where user_id=v_user and checkout_id=p_checkout_id;
  insert into usage_widget_private.billing_audit(actor_user_id,event,details) values(v_user,'international_checkout_verified_unpaid',
    pg_catalog.jsonb_build_object('checkout_id',p_checkout_id,'reason',pg_catalog.btrim(p_reason)));
  return true;
end; $$;
revoke all on all functions in schema usage_widget_private from public,anon,authenticated,service_role;
revoke all on function public.international_begin_checkout(uuid,text,jsonb,text),public.international_checkout_ready(uuid,uuid,uuid,text),
  public.international_lookup(text,text,uuid,text),public.international_apply(uuid,jsonb,jsonb,text,text),public.get_my_international_billing(),
  public.international_enqueue_event(jsonb),public.international_pending_events(uuid,integer),public.international_finish_event(text),
  public.international_due_subscriptions(integer),public.international_resolve_unpaid_checkout(uuid,text) from public,anon,authenticated,service_role;
grant execute on function public.international_begin_checkout(uuid,text,jsonb,text),public.international_checkout_ready(uuid,uuid,uuid,text),
  public.international_lookup(text,text,uuid,text),public.international_apply(uuid,jsonb,jsonb,text,text),
  public.international_enqueue_event(jsonb),public.international_pending_events(uuid,integer),public.international_finish_event(text),
  public.international_due_subscriptions(integer),public.international_resolve_unpaid_checkout(uuid,text) to service_role;
grant execute on function public.get_my_international_billing() to authenticated;
create or replace function public.admin_list_members(p_search text default '', p_offset integer default 0, p_limit integer default 50)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid:=usage_widget_private.require_membership_admin(); v_now timestamptz:=pg_catalog.clock_timestamp(); v_result jsonb;
begin
  if p_search is null or pg_catalog.char_length(p_search)>200 or p_offset is null or p_offset<0 or p_offset>10000
    or p_limit is null or p_limit<1 or p_limit>100 then
    raise exception 'Search or pagination values are invalid.' using errcode='22023';
  end if;
  with candidates as (
    select p.user_id,p.email,p.created_at,u.last_sign_in_at,
      usage_widget_private.membership_snapshot(p.user_id,v_now) as membership,
      usage_widget_private.admin_entitlement_sources(p.user_id,v_now) as sources
    from public.member_profiles p join auth.users u on u.id=p.user_id
    where p_search='' or pg_catalog.strpos(pg_catalog.lower(p.email),pg_catalog.lower(pg_catalog.btrim(p_search)))>0
    order by p.created_at desc,p.user_id offset p_offset limit p_limit+1
  ), numbered as (select *,pg_catalog.row_number() over(order by created_at desc,user_id) as row_number from candidates)
  select pg_catalog.jsonb_build_object('members',coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object('user_id',user_id,'email',email,'plan',membership->>'plan','status',membership->>'status',
      'expires_at',membership->'expires_at','created_at',created_at,'last_sign_in_at',last_sign_in_at,
      'manual',sources->'manual','billing',sources->'billing','international',sources->'international') order by created_at desc,user_id)
    filter(where row_number<=p_limit),'[]'::jsonb),'has_more',count(*)>p_limit) into v_result from numbered;
  return v_result;
end; $$;

notify pgrst,'reload schema';
commit;
