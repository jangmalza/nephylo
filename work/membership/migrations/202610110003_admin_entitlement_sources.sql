-- Apply after 0001 + 0002. Existing entitlements/payments/audit rows stay intact.
begin;

create or replace function usage_widget_private.admin_entitlement_sources(p_user_id uuid, p_now timestamptz)
returns jsonb language sql stable security definer set search_path = '' as $$
  select pg_catalog.jsonb_build_object(
    'manual', pg_catalog.jsonb_build_object(
      'plan', coalesce(e.plan,'free'), 'status', coalesce(e.status,'free'),
      'expires_at', e.expires_at, 'updated_at', e.updated_at,
      'active', coalesce(e.plan='pro' and e.status in ('active','canceled') and e.expires_at>p_now,false)),
    'billing', pg_catalog.jsonb_build_object(
      'status', coalesce(s.status,'none'), 'auto_renew', coalesce(s.auto_renew,false),
      'expires_at', s.current_period_end, 'invoice_status', i.status,
      'active', coalesce(s.current_period_end>p_now and i.status in ('paid','partial_refund'),false)))
  from auth.users u left join usage_widget_private.entitlements e on e.user_id=u.id
    left join usage_widget_private.billing_subscriptions s on s.user_id=u.id
    left join usage_widget_private.billing_invoices i on i.id=s.current_invoice_id
  where u.id=p_user_id;
$$;
revoke all on function usage_widget_private.admin_entitlement_sources(uuid,timestamptz) from public,anon,authenticated,service_role;

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
      'manual',sources->'manual','billing',sources->'billing') order by created_at desc,user_id)
    filter(where row_number<=p_limit),'[]'::jsonb),'has_more',count(*)>p_limit) into v_result from numbered;
  return v_result;
end; $$;

create or replace function public.admin_set_membership(p_user_id uuid,p_plan text,p_expires_at timestamptz,p_reason text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid:=usage_widget_private.require_membership_admin(); v_now timestamptz:=pg_catalog.clock_timestamp();
  v_old jsonb; v_new jsonb; v_result jsonb;
begin
  if p_user_id is null or p_plan is null or p_plan not in ('free','pro') or p_reason is null
    or pg_catalog.char_length(pg_catalog.btrim(p_reason)) not between 1 and 500 then
    raise exception 'A user, valid plan, and 1-500 character reason are required.' using errcode='22023';
  end if;
  if (p_plan='pro' and (p_expires_at is null or not pg_catalog.isfinite(p_expires_at) or p_expires_at<=v_now or p_expires_at>v_now+interval '366 days'))
    or (p_plan='free' and p_expires_at is not null) then
    raise exception 'Pro expiry must be within 366 days; Free expiry must be null.' using errcode='22023';
  end if;
  perform 1 from auth.users u where u.id=p_user_id and u.email is not null and pg_catalog.btrim(u.email)<>''
    and u.email_confirmed_at is not null and (u.banned_until is null or u.banned_until<=v_now) for update;
  if not found then raise exception 'The target must be an existing verified email member.' using errcode='22023'; end if;
  v_old:=pg_catalog.jsonb_build_object('audit_version',2,
    'manual',usage_widget_private.admin_entitlement_sources(p_user_id,v_now)->'manual',
    'effective',usage_widget_private.membership_snapshot(p_user_id,v_now)-array['email','server_time','is_admin','recheck_after_seconds']);
  insert into usage_widget_private.entitlements(user_id,plan,status,expires_at,updated_at)
    values(p_user_id,p_plan,case when p_plan='pro' then 'active' else 'revoked' end,p_expires_at,v_now)
    on conflict(user_id) do update set plan=excluded.plan,status=excluded.status,expires_at=excluded.expires_at,updated_at=excluded.updated_at;
  v_result:=usage_widget_private.membership_snapshot(p_user_id,v_now);
  v_new:=pg_catalog.jsonb_build_object('audit_version',2,
    'manual',usage_widget_private.admin_entitlement_sources(p_user_id,v_now)->'manual',
    'effective',v_result-array['email','server_time','is_admin','recheck_after_seconds']);
  insert into usage_widget_private.membership_audit(actor_user_id,target_user_id,old_value,new_value,reason,created_at)
    values(v_actor,p_user_id,v_old,v_new,pg_catalog.btrim(p_reason),v_now);
  return v_result;
end; $$;

-- A bounded, administrator-only read. Legacy audit records are explicitly marked,
-- since their combined entitlement snapshots cannot reconstruct a manual grant.
create or replace function public.admin_get_membership_audit(p_user_id uuid,p_limit integer default 10)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid:=usage_widget_private.require_membership_admin(); v_result jsonb;
begin
  if p_user_id is null or p_limit is null or p_limit<1 or p_limit>50 then
    raise exception 'A user and a limit from 1 to 50 are required.' using errcode='22023';
  end if;
  select coalesce(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('id',a.id,'created_at',a.created_at,'reason',a.reason,
    'legacy',not coalesce(a.old_value->>'audit_version'='2' and a.new_value->>'audit_version'='2',false),
    'before_manual',a.old_value->'manual','after_manual',a.new_value->'manual',
    'before_effective',coalesce(a.old_value->'effective',a.old_value),
    'after_effective',coalesce(a.new_value->'effective',a.new_value)) order by a.created_at desc,a.id desc),'[]'::jsonb)
  into v_result from (select * from usage_widget_private.membership_audit where target_user_id=p_user_id
    order by created_at desc,id desc limit p_limit) a;
  return pg_catalog.jsonb_build_object('entries',v_result);
end; $$;

revoke all on function public.admin_list_members(text,integer,integer),public.admin_set_membership(uuid,text,timestamptz,text),
  public.admin_get_membership_audit(uuid,integer) from public,anon,authenticated,service_role;
grant execute on function public.admin_list_members(text,integer,integer),public.admin_set_membership(uuid,text,timestamptz,text),
  public.admin_get_membership_audit(uuid,integer) to authenticated;
notify pgrst,'reload schema';
commit;
