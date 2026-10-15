-- Toss card billing, prepared for connection. No secrets or provider calls run here.
begin;
create table if not exists usage_widget_private.billing_subscriptions (
  id uuid primary key default pg_catalog.gen_random_uuid(),
  user_id uuid not null unique references auth.users(id) on delete cascade,
  customer_key text not null unique,
  billing_key_ciphertext text,
  status text not null default 'pending' check (status in ('pending','active','canceled','past_due','review','ended')),
  auto_renew boolean not null default true,
  current_period_start timestamptz,
  current_period_end timestamptz,
  current_invoice_id uuid,
  anchor_day integer check (anchor_day between 1 and 31),
  created_at timestamptz not null default pg_catalog.now(),
  updated_at timestamptz not null default pg_catalog.now()
);
create table if not exists usage_widget_private.billing_checkout_intents (
  id uuid primary key default pg_catalog.gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  state_hash text not null unique check (state_hash ~ '^[a-f0-9]{64}$'),
  customer_key text not null unique check (customer_key ~ '^[A-Za-z0-9_:-]{16,100}$'),
  terms_version text not null check (pg_catalog.char_length(terms_version) between 1 and 100),
  consent_at timestamptz not null default pg_catalog.now(),
  expires_at timestamptz not null default (pg_catalog.now() + interval '30 minutes'),
  status text not null default 'pending' check (status in ('pending','completed','canceled')),
  invoice_id uuid,
  created_at timestamptz not null default pg_catalog.now()
);
create table if not exists usage_widget_private.billing_invoices (
  id uuid primary key default pg_catalog.gen_random_uuid(),
  subscription_id uuid not null references usage_widget_private.billing_subscriptions(id) on delete cascade,
  cycle_number integer not null,
  order_id text not null unique,
  idempotency_key text not null unique,
  amount integer not null default 3000 check (amount = 3000),
  currency text not null default 'KRW' check (currency = 'KRW'),
  status text not null default 'pending' check (status in ('pending','charging','unknown','paid','failed','canceled','review','refunded','partial_refund')),
  is_initial boolean not null,
  period_start timestamptz not null,
  period_end timestamptz not null check (pg_catalog.isfinite(period_end)),
  payment_key text unique,
  approved_at timestamptz,
  balance_amount integer,
  lease_token uuid,
  lease_until timestamptz,
  charge_authorized_at timestamptz,
  attempts integer not null default 0,
  next_check_at timestamptz not null default pg_catalog.now(),
  last_reason text,
  created_at timestamptz not null default pg_catalog.now(),
  updated_at timestamptz not null default pg_catalog.now(),
  unique(subscription_id,cycle_number)
);
create table if not exists usage_widget_private.billing_audit (
  id bigint generated always as identity primary key,
  subscription_id uuid,
  invoice_id uuid,
  actor_user_id uuid,
  event text not null,
  details jsonb not null default '{}',
  created_at timestamptz not null default pg_catalog.now()
);
create index if not exists billing_due_index on usage_widget_private.billing_subscriptions(current_period_end) where auto_renew;
create index if not exists billing_pending_index on usage_widget_private.billing_invoices(status,lease_until);
alter table usage_widget_private.billing_subscriptions enable row level security;
alter table usage_widget_private.billing_checkout_intents enable row level security;
alter table usage_widget_private.billing_invoices enable row level security;
alter table usage_widget_private.billing_audit enable row level security;
revoke all on all tables in schema usage_widget_private from public, anon, authenticated, service_role;
revoke all on all sequences in schema usage_widget_private from public, anon, authenticated, service_role;

create or replace function usage_widget_private.billing_next_period(p_start timestamptz,p_anchor_day integer)
returns timestamptz language plpgsql immutable set search_path = '' as $$
declare v_local timestamp := p_start at time zone 'Asia/Seoul'; v_month timestamp; v_day integer;
begin
  if p_anchor_day is null or p_anchor_day not between 1 and 31 or not pg_catalog.isfinite(p_start) then
    raise exception 'Invalid billing anchor.' using errcode='22023';
  end if;
  v_month := pg_catalog.date_trunc('month',v_local) + interval '1 month';
  v_day := least(p_anchor_day,extract(day from v_month + interval '1 month - 1 day')::integer);
  return (v_month + (v_day-1)*interval '1 day' + (v_local-pg_catalog.date_trunc('day',v_local))) at time zone 'Asia/Seoul';
end; $$;

create or replace function usage_widget_private.billing_status(p_user_id uuid)
returns jsonb language sql security definer set search_path = '' as $$
  select pg_catalog.jsonb_build_object('amount',3000,'currency','KRW','interval','month',
    'subscription',(select pg_catalog.jsonb_build_object(
      'id',s.id,'status',case when s.status='ended' then 'refunded' else s.status end,'auto_renew',s.auto_renew,'cancel_at_period_end',not s.auto_renew,
      'current_period_start',s.current_period_start,'current_period_end',s.current_period_end,
      'next_billing_at',case when s.auto_renew and s.status='active' then s.current_period_end else null end,
      'has_billing_method',s.billing_key_ciphertext is not null,
      'latest_invoice',(select pg_catalog.jsonb_build_object('id',i.id,'order_id',i.order_id,'status',i.status,
        'amount',i.amount,'currency',i.currency,'period_start',i.period_start,'period_end',i.period_end,
        'review_required',i.status in ('unknown','review','partial_refund'))
        from usage_widget_private.billing_invoices i where i.subscription_id=s.id order by i.cycle_number desc limit 1)
    ) from usage_widget_private.billing_subscriptions s where s.user_id=p_user_id));
$$;
create or replace function public.get_my_billing()
returns jsonb language plpgsql security definer set search_path = '' as $$
begin return usage_widget_private.billing_status(usage_widget_private.require_verified_member()); end; $$;
create or replace function public.billing_get_user_status(p_user_id uuid)
returns jsonb language sql security definer set search_path = '' as $$
  select usage_widget_private.billing_status(p_user_id);
$$;

create or replace function public.cancel_my_subscription()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_user uuid:=usage_widget_private.require_verified_member(); v_sub usage_widget_private.billing_subscriptions%rowtype;
begin
  select * into v_sub from usage_widget_private.billing_subscriptions where user_id=v_user for update;
  if found then
    update usage_widget_private.billing_subscriptions set auto_renew=false,status='canceled',updated_at=pg_catalog.now() where id=v_sub.id;
    update usage_widget_private.billing_invoices set status='canceled',lease_token=null,lease_until=null,updated_at=pg_catalog.now()
      where subscription_id=v_sub.id and (status='pending' or (status='charging' and charge_authorized_at is null));
    update usage_widget_private.billing_checkout_intents set status='canceled' where user_id=v_user and status='pending';
    if v_sub.auto_renew then
      insert into usage_widget_private.billing_audit(subscription_id,actor_user_id,event) values(v_sub.id,v_user,'cancel_requested');
    end if;
  end if;
  return usage_widget_private.billing_status(v_user);
end; $$;

create or replace function public.billing_begin_checkout(p_user_id uuid,p_state_hash text,p_customer_key text,p_terms_version text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_id uuid; v_expires timestamptz;
begin
  if p_state_hash is null or p_state_hash !~ '^[a-f0-9]{64}$' or p_customer_key is null or p_customer_key !~ '^[A-Za-z0-9_:-]{16,100}$'
    or p_terms_version is null or pg_catalog.char_length(pg_catalog.btrim(p_terms_version)) not between 1 and 100 then
    raise exception 'Invalid checkout data.' using errcode='22023';
  end if;
  perform 1 from auth.users where id=p_user_id and email_confirmed_at is not null and email is not null
    and (banned_until is null or banned_until<=pg_catalog.now()) for update;
  if not found then raise exception 'Verified member required.' using errcode='42501'; end if;
  if exists(select 1 from usage_widget_private.billing_subscriptions s where s.user_id=p_user_id and
      (s.current_period_end>pg_catalog.now() or exists(select 1 from usage_widget_private.billing_invoices i where i.subscription_id=s.id and i.status in ('pending','charging','unknown','review','partial_refund')))) then
    raise exception 'An existing billing period or unresolved invoice must be handled first.' using errcode='22023';
  end if;
  update usage_widget_private.billing_checkout_intents set status='canceled' where user_id=p_user_id and status='pending';
  insert into usage_widget_private.billing_checkout_intents(user_id,state_hash,customer_key,terms_version)
    values(p_user_id,p_state_hash,p_customer_key,pg_catalog.btrim(p_terms_version)) returning id,expires_at into v_id,v_expires;
  return pg_catalog.jsonb_build_object('checkout_id',v_id,'customer_key',p_customer_key,'expires_at',v_expires);
end; $$;

create or replace function public.billing_get_checkout(p_user_id uuid,p_checkout_id uuid,p_state_hash text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_intent usage_widget_private.billing_checkout_intents%rowtype;
begin
  select * into v_intent from usage_widget_private.billing_checkout_intents
    where id=p_checkout_id and user_id=p_user_id and state_hash=p_state_hash and status in ('pending','completed');
  if not found or (v_intent.status='pending' and v_intent.expires_at<=pg_catalog.now()) then
    raise exception 'Checkout expired or not found.' using errcode='22023';
  end if;
  return pg_catalog.jsonb_build_object('checkout_id',v_intent.id,'customer_key',v_intent.customer_key,
    'expires_at',v_intent.expires_at,'status',v_intent.status,'invoice_id',v_intent.invoice_id);
end; $$;

create or replace function public.billing_complete_checkout(p_user_id uuid,p_state_hash text,p_customer_key text,p_billing_key_ciphertext text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_intent usage_widget_private.billing_checkout_intents%rowtype; v_sub usage_widget_private.billing_subscriptions%rowtype;
  v_invoice usage_widget_private.billing_invoices%rowtype; v_id uuid:=pg_catalog.gen_random_uuid(); v_now timestamptz:=pg_catalog.now(); v_cycle integer;
begin
  perform 1 from auth.users where id=p_user_id and email_confirmed_at is not null and email is not null
    and (banned_until is null or banned_until<=v_now) for update;
  if not found then raise exception 'Verified member required.' using errcode='42501'; end if;
  select * into v_intent from usage_widget_private.billing_checkout_intents where user_id=p_user_id and state_hash=p_state_hash and customer_key=p_customer_key for update;
  if not found or v_intent.status='canceled' or (v_intent.status='pending' and v_intent.expires_at<=v_now) then
    raise exception 'Checkout expired or not found.' using errcode='22023';
  end if;
  if v_intent.status='completed' then
    select * into v_invoice from usage_widget_private.billing_invoices where id=v_intent.invoice_id;
    return pg_catalog.jsonb_build_object('subscription_id',v_invoice.subscription_id,'invoice_id',v_invoice.id,'order_id',v_invoice.order_id,'status',v_invoice.status);
  end if;
  if p_billing_key_ciphertext is null or pg_catalog.char_length(p_billing_key_ciphertext) not between 50 and 16384
    or p_billing_key_ciphertext !~ '^v1\.[A-Za-z0-9+/=_-]+\.[A-Za-z0-9+/=_-]+$' then
    raise exception 'Encrypted billing key required.' using errcode='22023';
  end if;
  select * into v_sub from usage_widget_private.billing_subscriptions where user_id=p_user_id for update;
  if found and (v_sub.current_period_end>v_now or exists(select 1 from usage_widget_private.billing_invoices where subscription_id=v_sub.id and status in ('pending','charging','unknown','review','partial_refund'))) then
    raise exception 'Billing is already active or unresolved.' using errcode='22023';
  end if;
  insert into usage_widget_private.billing_subscriptions(user_id,customer_key,billing_key_ciphertext,status,auto_renew)
    values(p_user_id,p_customer_key,p_billing_key_ciphertext,'pending',true)
    on conflict(user_id) do update set customer_key=excluded.customer_key,billing_key_ciphertext=excluded.billing_key_ciphertext,status='pending',auto_renew=true,updated_at=v_now
    returning * into v_sub;
  select coalesce(max(cycle_number),0)+1 into v_cycle from usage_widget_private.billing_invoices where subscription_id=v_sub.id;
  insert into usage_widget_private.billing_invoices(id,subscription_id,cycle_number,order_id,idempotency_key,is_initial,period_start,period_end)
    values(v_id,v_sub.id,v_cycle,'uw_'||pg_catalog.replace(v_id::text,'-',''),'uw:'||v_id::text,true,v_now,
      usage_widget_private.billing_next_period(v_now,extract(day from v_now at time zone 'Asia/Seoul')::integer)) returning * into v_invoice;
  update usage_widget_private.billing_checkout_intents set status='completed',invoice_id=v_id where id=v_intent.id;
  insert into usage_widget_private.billing_audit(subscription_id,invoice_id,actor_user_id,event,details)
    values(v_sub.id,v_id,p_user_id,'checkout_completed',pg_catalog.jsonb_build_object('terms_version',v_intent.terms_version,'consent_at',v_intent.consent_at));
  return pg_catalog.jsonb_build_object('subscription_id',v_sub.id,'invoice_id',v_id,'order_id',v_invoice.order_id,'status',v_invoice.status);
end; $$;

create or replace function usage_widget_private.billing_claim(p_invoice_id uuid,p_reconcile boolean)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_i usage_widget_private.billing_invoices%rowtype; v_s usage_widget_private.billing_subscriptions%rowtype;
  v_action text; v_lease uuid:=pg_catalog.gen_random_uuid(); v_now timestamptz:=pg_catalog.now();
begin
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id;
  if not found then return null; end if;
  select * into v_s from usage_widget_private.billing_subscriptions where id=v_i.subscription_id for update;
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id for update;
  if v_i.lease_until>v_now or (not p_reconcile and v_i.status in ('charging','unknown') and v_i.next_check_at>v_now) then return null; end if;
  if not p_reconcile and v_i.status not in ('pending','charging','unknown') then return null; end if;
  if v_i.status='pending' and not p_reconcile then
    if not v_s.auto_renew or v_s.status not in ('pending','active') then
      update usage_widget_private.billing_invoices set status='canceled',updated_at=v_now where id=v_i.id; return null;
    end if;
    if v_i.period_start<v_now-interval '24 hours' or not exists(select 1 from auth.users where id=v_s.user_id and email_confirmed_at is not null and (banned_until is null or banned_until<=v_now)) then
      update usage_widget_private.billing_invoices set status='review',last_reason='Overdue or account unavailable',updated_at=v_now where id=v_i.id;
      update usage_widget_private.billing_subscriptions set status='review',auto_renew=false,updated_at=v_now where id=v_s.id; return null;
    end if;
    v_action:='charge';
  else v_action:='reconcile'; end if;
  update usage_widget_private.billing_invoices set lease_token=v_lease,lease_until=v_now+interval '120 seconds',
    status=case when v_action='charge' then 'charging' else status end,attempts=attempts+1,updated_at=v_now where id=v_i.id;
  return pg_catalog.jsonb_build_object('invoice_id',v_i.id,'subscription_id',v_s.id,'user_id',v_s.user_id,
    'order_id',v_i.order_id,'idempotency_key',v_i.idempotency_key,'customer_key',v_s.customer_key,
    'billing_key_ciphertext',v_s.billing_key_ciphertext,'amount',3000,'currency','KRW','action',v_action,
    'lease_token',v_lease,'period_start',v_i.period_start,'period_end',v_i.period_end,'payment_key',v_i.payment_key);
end; $$;
create or replace function public.billing_claim_invoice(p_invoice_id uuid)
returns jsonb language sql security definer set search_path = '' as $$ select usage_widget_private.billing_claim(p_invoice_id,false); $$;
create or replace function public.billing_claim_order(p_order_id text default null,p_payment_key text default null)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_id uuid; v_job jsonb;
begin
  if (p_order_id is null and p_payment_key is null) or pg_catalog.char_length(p_order_id)>100 or pg_catalog.char_length(p_payment_key)>300 then return null; end if;
  select id into v_id from usage_widget_private.billing_invoices where
    (p_order_id is null or order_id=p_order_id) and (p_payment_key is null or payment_key=p_payment_key or (p_order_id is not null and payment_key is null));
  if v_id is null then return null; end if;
  v_job:=usage_widget_private.billing_claim(v_id,true);
  return coalesce(v_job,pg_catalog.jsonb_build_object('busy',true));
end; $$;

create or replace function public.billing_validate_charge(p_invoice_id uuid,p_lease_token uuid)
returns boolean language plpgsql security definer set search_path = '' as $$
declare v_i usage_widget_private.billing_invoices%rowtype; v_s usage_widget_private.billing_subscriptions%rowtype;
begin
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id;
  if not found then return false; end if;
  select * into v_s from usage_widget_private.billing_subscriptions where id=v_i.subscription_id for update;
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id for update;
  if v_i.lease_token is distinct from p_lease_token or v_i.lease_until<=pg_catalog.now() or v_i.status<>'charging'
    or v_i.charge_authorized_at is not null or not v_s.auto_renew or v_s.status not in ('pending','active')
    or not exists(select 1 from auth.users where id=v_s.user_id and email_confirmed_at is not null and (banned_until is null or banned_until<=pg_catalog.now())) then return false; end if;
  update usage_widget_private.billing_invoices set charge_authorized_at=pg_catalog.now() where id=p_invoice_id;
  return true;
end; $$;

create or replace function public.billing_claim_due(p_limit integer default 20)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_s usage_widget_private.billing_subscriptions%rowtype; v_i record; v_id uuid; v_cycle integer; v_job jsonb; v_jobs jsonb:='[]'; v_now timestamptz:=pg_catalog.now();
begin
  if p_limit is null or p_limit not between 1 and 20 then raise exception 'Batch limit must be 1-20.' using errcode='22023'; end if;
  for v_s in select s.* from usage_widget_private.billing_subscriptions s where s.auto_renew and s.status='active' and s.current_period_end<=v_now
    and not exists(select 1 from usage_widget_private.billing_invoices existing where existing.subscription_id=s.id and existing.period_start=s.current_period_end)
    order by s.current_period_end limit p_limit for update of s skip locked loop
    if v_s.current_period_end<v_now-interval '24 hours' then
      update usage_widget_private.billing_subscriptions set status='review',auto_renew=false,updated_at=v_now where id=v_s.id;
      insert into usage_widget_private.billing_audit(subscription_id,event,details) values(v_s.id,'overdue_review',pg_catalog.jsonb_build_object('period_end',v_s.current_period_end));
    elsif not exists(select 1 from usage_widget_private.billing_invoices where subscription_id=v_s.id and period_start=v_s.current_period_end) then
      select coalesce(max(cycle_number),0)+1 into v_cycle from usage_widget_private.billing_invoices where subscription_id=v_s.id;
      v_id:=pg_catalog.gen_random_uuid();
      insert into usage_widget_private.billing_invoices(id,subscription_id,cycle_number,order_id,idempotency_key,is_initial,period_start,period_end)
        values(v_id,v_s.id,v_cycle,'uw_'||pg_catalog.replace(v_id::text,'-',''),'uw:'||v_id::text,false,v_s.current_period_end,
          usage_widget_private.billing_next_period(v_s.current_period_end,v_s.anchor_day));
    end if;
  end loop;
  for v_i in select id from usage_widget_private.billing_invoices where status in ('pending','charging','unknown')
    and (lease_until is null or lease_until<=v_now) and next_check_at<=v_now order by updated_at,id limit p_limit loop
    v_job:=usage_widget_private.billing_claim(v_i.id,false);
    if v_job is not null then v_jobs:=v_jobs||pg_catalog.jsonb_build_array(v_job); end if;
  end loop;
  return pg_catalog.jsonb_build_object('jobs',v_jobs);
end; $$;

create or replace function public.billing_settle_invoice(p_invoice_id uuid,p_lease_token uuid,p_result jsonb)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_i usage_widget_private.billing_invoices%rowtype; v_s usage_widget_private.billing_subscriptions%rowtype;
  v_outcome text:=p_result->>'outcome'; v_p jsonb:=p_result->'payment'; v_now timestamptz:=pg_catalog.now(); v_approved timestamptz;
  v_start timestamptz; v_end timestamptz; v_anchor integer; v_new_status text; v_reason text:=p_result->>'reason';
begin
  if p_result is null or v_outcome is null or v_outcome not in ('paid','failed','unknown','refunded','partial_refund','review') or pg_catalog.char_length(coalesce(v_reason,''))>500 then
    raise exception 'Invalid settlement.' using errcode='22023'; end if;
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id;
  if not found then raise exception 'Invoice not found.' using errcode='22023'; end if;
  select * into v_s from usage_widget_private.billing_subscriptions where id=v_i.subscription_id for update;
  select * into v_i from usage_widget_private.billing_invoices where id=p_invoice_id for update;
  if p_lease_token is null or v_i.lease_token is distinct from p_lease_token or v_i.lease_until<=v_now then
    raise exception 'Invoice lease is no longer current.' using errcode='40001'; end if;
  if v_outcome in ('paid','refunded','partial_refund') then
    if v_p is null or v_p->>'orderId' is distinct from v_i.order_id or v_p->>'totalAmount' is distinct from '3000'
      or v_p->>'currency' is distinct from 'KRW' or pg_catalog.char_length(coalesce(v_p->>'paymentKey','')) not between 1 and 300
      or (v_i.payment_key is not null and v_i.payment_key is distinct from v_p->>'paymentKey') then
      raise exception 'Payment identity, amount or currency mismatch.' using errcode='22023'; end if;
    if (v_outcome='paid' and (v_p->>'status' is distinct from 'DONE' or v_p->>'balanceAmount' is distinct from '3000'))
      or (v_outcome='refunded' and (v_p->>'status' is distinct from 'CANCELED' or v_p->>'balanceAmount' is distinct from '0'))
      or (v_outcome='partial_refund' and (v_p->>'status' is distinct from 'PARTIAL_CANCELED' or coalesce(v_p->>'balanceAmount','') !~ '^[0-9]{1,4}$')) then
      raise exception 'Provider payment status mismatch.' using errcode='22023'; end if;
    if v_outcome='partial_refund' and (v_p->>'balanceAmount')::integer not between 1 and 2999 then raise exception 'Invalid partial refund balance.' using errcode='22023'; end if;
    begin v_approved:=(v_p->>'approvedAt')::timestamptz; exception when others then raise exception 'Invalid approval timestamp.' using errcode='22023'; end;
    if v_approved is null or not pg_catalog.isfinite(v_approved) or v_approved>v_now+interval '5 minutes' or v_approved<v_i.created_at-interval '5 minutes' then
      raise exception 'Approval timestamp outside invoice bounds.' using errcode='22023'; end if;
  end if;
  -- Once money was confirmed, a timeout/failure or older DONE cannot undo a refund.
  if v_i.status in ('paid','refunded','partial_refund') and v_outcome in ('unknown','failed','review') then
    v_new_status:=v_i.status;
  elsif v_i.status in ('refunded','partial_refund') and v_outcome='paid' then v_new_status:=v_i.status;
  elsif v_i.status='refunded' and v_outcome='partial_refund' then v_new_status:='refunded';
  else v_new_status:=v_outcome; end if;
  if v_new_status='unknown' and (v_i.attempts>=6 or v_i.created_at<v_now-interval '24 hours') then
    v_new_status:='review'; v_reason:='Unresolved payment requires review; no automatic recharge.';
  end if;
  v_start:=v_i.period_start; v_end:=v_i.period_end; v_anchor:=v_s.anchor_day;
  if v_new_status='paid' and v_i.status<>'paid' then
    if v_i.is_initial then v_start:=v_approved; v_anchor:=extract(day from v_approved at time zone 'Asia/Seoul')::integer;
      v_end:=usage_widget_private.billing_next_period(v_start,v_anchor); end if;
    if v_s.current_period_end is null or v_end>v_s.current_period_end then
      update usage_widget_private.billing_subscriptions set status=case when auto_renew then 'active' else 'canceled' end,
        current_invoice_id=v_i.id,current_period_start=v_start,current_period_end=v_end,anchor_day=v_anchor,updated_at=v_now where id=v_s.id;
    end if;
  elsif v_new_status='failed' and v_i.status<>'failed' then
    update usage_widget_private.billing_subscriptions set status='past_due',auto_renew=false,updated_at=v_now where id=v_s.id;
  elsif v_new_status='review' then
    update usage_widget_private.billing_subscriptions set status='review',auto_renew=false,updated_at=v_now where id=v_s.id;
  elsif v_new_status='partial_refund' then
    update usage_widget_private.billing_subscriptions set status='review',auto_renew=false,updated_at=v_now where id=v_s.id;
  elsif v_new_status='refunded' and (v_s.current_invoice_id=v_i.id or
    (v_i.is_initial and (v_s.current_period_end is null or v_s.current_period_end<=v_now) and not exists(
      select 1 from usage_widget_private.billing_invoices where subscription_id=v_s.id and cycle_number>v_i.cycle_number))) then
    update usage_widget_private.billing_subscriptions set status='ended',auto_renew=false,current_period_end=least(current_period_end,v_now),updated_at=v_now where id=v_s.id;
  end if;
  update usage_widget_private.billing_invoices set status=v_new_status,period_start=v_start,period_end=v_end,
    payment_key=case when v_outcome in ('paid','refunded','partial_refund') then coalesce(payment_key,v_p->>'paymentKey') else payment_key end,
    approved_at=coalesce(approved_at,v_approved),
    balance_amount=case when v_new_status=v_outcome and v_outcome in ('paid','refunded','partial_refund') then (v_p->>'balanceAmount')::integer else balance_amount end,
    lease_token=null,lease_until=null,last_reason=v_reason,updated_at=v_now,
    next_check_at=case when v_new_status='unknown' then v_now+interval '5 minutes' else v_now end where id=v_i.id;
  if v_new_status is distinct from v_i.status then
    insert into usage_widget_private.billing_audit(subscription_id,invoice_id,event,details)
      values(v_s.id,v_i.id,'invoice_'||v_new_status,pg_catalog.jsonb_build_object('previous_status',v_i.status,'reason',v_reason));
  end if;
  return pg_catalog.jsonb_build_object('status',v_new_status,'subscription_id',v_s.id);
end; $$;

-- Manual and paid grants coexist. The later valid end wins; manual Free never cancels billing.
create or replace function usage_widget_private.membership_snapshot(p_user_id uuid,p_now timestamptz)
returns jsonb language sql security definer set search_path = '' as $$
  with sources as (
    select e.expires_at,case when e.status='canceled' then 'canceled' else 'active' end as status
    from usage_widget_private.entitlements e where e.user_id=p_user_id and e.plan='pro' and e.status in ('active','canceled') and e.expires_at>p_now
    union all
    select s.current_period_end,case when s.auto_renew then 'active' else 'canceled' end
    from usage_widget_private.billing_subscriptions s join usage_widget_private.billing_invoices i on i.id=s.current_invoice_id
    where s.user_id=p_user_id and s.current_period_end>p_now and i.status in ('paid','partial_refund')
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

-- Revoke inherited Supabase defaults explicitly. Only three own-account RPCs are client-readable.
revoke all on function public.get_my_billing(), public.cancel_my_subscription() from public,anon,authenticated,service_role;
grant execute on function public.get_my_billing(), public.cancel_my_subscription() to authenticated;
revoke all on function public.billing_get_user_status(uuid),public.billing_begin_checkout(uuid,text,text,text),public.billing_get_checkout(uuid,uuid,text),
  public.billing_complete_checkout(uuid,text,text,text),public.billing_claim_invoice(uuid),public.billing_claim_order(text,text),
  public.billing_validate_charge(uuid,uuid),public.billing_claim_due(integer),public.billing_settle_invoice(uuid,uuid,jsonb) from public,anon,authenticated,service_role;
grant execute on function public.billing_get_user_status(uuid),public.billing_begin_checkout(uuid,text,text,text),public.billing_get_checkout(uuid,uuid,text),
  public.billing_complete_checkout(uuid,text,text,text),public.billing_claim_invoice(uuid),public.billing_claim_order(text,text),
  public.billing_validate_charge(uuid,uuid),public.billing_claim_due(integer),public.billing_settle_invoice(uuid,uuid,jsonb) to service_role;
revoke all on function usage_widget_private.billing_next_period(timestamptz,integer),usage_widget_private.billing_status(uuid),usage_widget_private.billing_claim(uuid,boolean),
  usage_widget_private.membership_snapshot(uuid,timestamptz) from public,anon,authenticated,service_role;
notify pgrst,'reload schema';
commit;
