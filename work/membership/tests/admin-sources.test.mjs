import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {PGlite} from '@electric-sql/pglite';
const db=new PGlite(); let checks=0;
const owner='a2000000-0000-4000-8000-000000000001',paid='a2000000-0000-4000-8000-000000000002',free='a2000000-0000-4000-8000-000000000003';
const sub='a2000000-0000-4000-8000-000000000004',invoice='a2000000-0000-4000-8000-000000000005';
const equal=(a,b,n)=>{assert.deepEqual(a,b,n);checks++;};
const ok=(v,n)=>{assert.ok(v,n);checks++;};
const migration=await readFile(new URL('../migrations/202610110003_admin_entitlement_sources.sql',import.meta.url),'utf8');
async function rpc(name,args=[],user=owner,role='authenticated'){
  assert.ok(['authenticated','anon','service_role'].includes(role)); await db.exec('begin');
  try {await db.exec(`set local role ${role}`);await db.query("select set_config('request.jwt.claim.sub',$1,true)",[user||'']);
    const result=await db.query(`select public.${name}(${args.map((_,i)=>'$'+(i+1)).join(',')}) as value`,args);await db.exec('commit');return result.rows[0].value;
  } catch(e){await db.exec('rollback');throw e;}
}
const rejected=async(work,code='42501')=>{await assert.rejects(work,e=>e.code===code);checks++;};
const stamp=days=>new Date(Date.now()+days*86400000).toISOString();
const listPaid=async()=> (await rpc('admin_list_members',['paid@',0,20])).members[0];
try {
  for(const file of ['tests/bootstrap.sql','migrations/202610110001_membership.sql','migrations/202610110002_billing.sql']) await db.exec(await readFile(new URL('../'+file,import.meta.url),'utf8'));
  await db.query("insert into auth.users(id,email,email_confirmed_at) values($1,'owner@example.test',now()),($2,'paid@example.test',now()),($3,'free@example.test',now())",[owner,paid,free]);
  await db.query('insert into usage_widget_private.membership_admins(user_id) values($1)',[owner]);
  // A deployed pre-migration grant, payment and audit record must survive upgrade.
  await rpc('admin_set_membership',[paid,'pro',stamp(2),'legacy grant']);
  await db.query("insert into usage_widget_private.billing_subscriptions(id,user_id,customer_key,billing_key_ciphertext,status,auto_renew,current_period_start,current_period_end,current_invoice_id) values($1,$2,'PRIVATE_CUSTOMER','PRIVATE_CIPHER','active',true,now(),now()+interval '30 days',$3)",[sub,paid,invoice]);
  await db.query("insert into usage_widget_private.billing_invoices(id,subscription_id,cycle_number,order_id,idempotency_key,status,is_initial,period_start,period_end,payment_key) values($1,$2,1,'PRIVATE_ORDER','PRIVATE_IDEMPOTENCY','paid',true,now(),now()+interval '30 days','PRIVATE_PAYMENT')",[invoice,sub]);
  const legacy=(await db.query('select old_value,new_value,reason from usage_widget_private.membership_audit')).rows;
  const payment=(await db.query('select * from usage_widget_private.billing_subscriptions')).rows;
  await db.exec(migration);
  equal((await db.query('select old_value,new_value,reason from usage_widget_private.membership_audit')).rows,legacy,'Upgrade preserves historical audit snapshots');
  equal((await db.query('select * from usage_widget_private.billing_subscriptions')).rows,payment,'Upgrade leaves existing payment and renewal untouched');
  let member=await listPaid();
  equal(member.plan,'pro','Effective paid access remains Pro');equal(member.manual.plan,'pro','Manual grant is independent');equal(member.manual.active,true,'Manual validity supplied by server');
  equal(member.billing.active,true,'Paid period valid');equal(member.billing.auto_renew,true,'Renewal state exposed separately');equal(member.billing.invoice_status,'paid','Only sanitized invoice status exposed');
  ok(Date.parse(member.expires_at)>Date.parse(member.manual.expires_at),'Effective expiry may be longer than manual expiry');
  equal(Date.parse(member.expires_at),Date.parse(member.billing.expires_at),'Paid period drives effective expiry');
  ok(!JSON.stringify(member).includes('PRIVATE_'),'Admin member list excludes payment credentials, references and order data');
  let records=await rpc('admin_get_membership_audit',[paid,10]);
  equal(records.entries[0].legacy,true,'Old combined snapshot clearly marked legacy');equal(records.entries[0].before_manual,null,'Legacy manual data is not invented');
  await rpc('admin_set_membership',[paid,'pro',stamp(4),'extend manual inside longer paid period']);
  records=await rpc('admin_get_membership_audit',[paid,10]);let audit=records.entries[0];
  equal(audit.legacy,false,'New audit records have distinct manual snapshots');
  ok(Date.parse(audit.after_manual.expires_at)>Date.parse(audit.before_manual.expires_at),'Manual change remains visible under a longer paid period');
  equal(audit.before_effective.expires_at,audit.after_effective.expires_at,'Effective entitlement is correctly unchanged');
  equal(audit.reason,'extend manual inside longer paid period','Actual change reason remains attached');
  equal(Object.hasOwn(audit,'actor_user_id'),false,'Audit UI contract excludes unnecessary identity fields');
  ok(!JSON.stringify(audit).includes('@')&&!JSON.stringify(audit).includes('PRIVATE_'),'Audit contains no email or payment secrets');
  await rpc('admin_set_membership',[paid,'free',null,'remove only manual']);
  member=await listPaid();equal(member.manual.plan,'free','Manual revocation visible');equal(member.manual.active,false,'Manual grant is inactive');
  equal(member.billing.active,true,'Manual revocation does not remove paid access');equal(member.plan,'pro','Effective Pro retained');equal(member.billing.auto_renew,true,'Manual revocation does not cancel billing');
  audit=(await rpc('admin_get_membership_audit',[paid,1])).entries[0];equal(audit.after_manual.status,'revoked','Audit records actual manual revocation');equal(audit.after_effective.plan,'pro','Audit separately records continued paid access');
  const freeMember=(await rpc('admin_list_members',['free@',0,20])).members[0];
  equal(freeMember.manual,{plan:'free',status:'free',expires_at:null,updated_at:null,active:false},'No grant has an explicit empty manual source');
  equal(freeMember.billing,{status:'none',auto_renew:false,expires_at:null,invoice_status:null,active:false},'No payment is distinct from failed payment');
  equal((await rpc('admin_get_membership_audit',[free,10])).entries,[],'No audit is a valid empty list');
  await db.query("update usage_widget_private.billing_invoices set status='refunded' where id=$1",[invoice]);
  member=await listPaid();equal(member.plan,'free','Refund after manual removal ends effective Pro');equal(member.billing.active,false,'Refund invalidates payment source');equal(member.billing.invoice_status,'refunded','Refund reason visible in admin list');
  for(const name of ['admin_list_members','admin_get_membership_audit']) {
    const args=name==='admin_list_members'?[]:[paid,10];
    await rejected(()=>rpc(name,args,free));await rejected(()=>rpc(name,args,null,'anon'));await rejected(()=>rpc(name,args,null,'service_role'));
  }
  await rejected(()=>rpc('admin_set_membership',[free,'pro',stamp(1),'not admin'],free));
  for(const limit of [0,51,null]) await rejected(()=>rpc('admin_get_membership_audit',[paid,limit]),'22023');
  await rejected(()=>rpc('admin_get_membership_audit',[null,10]),'22023');
  const beforeCount=(await db.query('select count(*)::int as n from usage_widget_private.membership_audit')).rows[0].n;
  await rejected(()=>rpc('admin_set_membership',[paid,'pro',stamp(-1),'invalid']),'22023');
  equal((await db.query('select count(*)::int as n from usage_widget_private.membership_audit')).rows[0].n,beforeCount,'Invalid edit leaves audit and grant unchanged');
  await db.exec(migration);equal((await rpc('admin_get_membership_audit',[paid,50])).entries.length,3,'Migration can be reapplied without data loss');
  await db.query("update auth.users set banned_until=now()+interval '1 day' where id=$1",[owner]);await rejected(()=>rpc('admin_get_membership_audit',[paid,10]));
  console.log(`PASS: ${checks} admin entitlement source PostgreSQL checks; no live services.`);
} finally {await db.close();}
