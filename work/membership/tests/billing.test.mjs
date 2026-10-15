import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {PGlite} from '@electric-sql/pglite';
const db=new PGlite(); let checks=0,sequence=0;
const cipher='v1.'+'A'.repeat(16)+'.'+'B'.repeat(96);
const equal=(a,b,message)=>{assert.deepEqual(a,b,message);checks++;};
const ok=(a,message)=>{assert.ok(a,message);checks++;};
const now=async()=>new Date((await db.query('select clock_timestamp() as value')).rows[0].value).toISOString();
async function as(role,user,query,args=[]){
  assert.ok(['authenticated','anon','service_role'].includes(role));await db.exec('begin');
  try{await db.exec(`set local role ${role}`);await db.query("select set_config('request.jwt.claim.sub',$1,true)",[user||'']);
    const result=await db.query(query,args);await db.exec('commit');return result.rows[0]?.value;
  }catch(error){await db.exec('rollback');throw error;}
}
const rpc=(name,args=[],user=null,role='service_role')=>as(role,user,`select public.${name}(${args.map((_,i)=>'$'+(i+1)).join(',')}) as value`,args);
const rejected=async(action,code='22023')=>{await assert.rejects(action,e=>e.code===code);checks++;};
async function user(verified=true){const id='b1000000-0000-4000-8000-'+String(++sequence).padStart(12,'0');await db.query('insert into auth.users(id,email,email_confirmed_at) values($1,$2,case when $3 then now() else null end)',[id,`member${sequence}@example.test`,verified]);return id;}
async function checkout(id){const state=String(++sequence).padStart(64,'a');const customer='customer_'+String(sequence).padStart(20,'0');const intent=await rpc('billing_begin_checkout',[id,state,customer,'2026-10-12-v1']);return{...intent,state,customer,user:id};}
async function complete(intent){return rpc('billing_complete_checkout',[intent.user,intent.state,intent.customer,cipher]);}
async function start(id){return complete(await checkout(id));}
async function claim(invoice){return rpc('billing_claim_invoice',[invoice.invoice_id||invoice.id]);}
async function settle(job,outcome='paid',overrides={}){
  const payment={paymentKey:'payment_'+job.invoice_id,orderId:job.order_id,totalAmount:3000,currency:'KRW',status:outcome==='refunded'?'CANCELED':outcome==='partial_refund'?'PARTIAL_CANCELED':'DONE',approvedAt:await now(),balanceAmount:outcome==='refunded'?0:outcome==='partial_refund'?1500:3000,...overrides};
  return rpc('billing_settle_invoice',[job.invoice_id,job.lease_token,{outcome,payment,reason:'fixture'}]);
}
async function member(id){return rpc('get_my_membership',[],id,'authenticated');}
async function billing(id){return rpc('get_my_billing',[],id,'authenticated');}
async function cancel(id){return rpc('cancel_my_subscription',[],id,'authenticated');}
async function makePaid(id){const invoice=await start(id),job=await claim(invoice);equal(await rpc('billing_validate_charge',[job.invoice_id,job.lease_token]),true,'Charge authorization is current');await settle(job);return invoice;}
async function expireLease(id){await db.query("update usage_widget_private.billing_invoices set lease_until=now()-interval '1 second',next_check_at=now()-interval '1 second' where id=$1",[id]);}

try{
  for(const file of ['tests/bootstrap.sql','migrations/202610110001_membership.sql','migrations/202610110002_billing.sql','migrations/202610110003_admin_entitlement_sources.sql'])await db.exec(await readFile(new URL('../'+file,import.meta.url),'utf8'));
  const alice=await user(),bob=await user(),pending=await user(false);
  equal(await billing(alice),{amount:3000,currency:'KRW',interval:'month',subscription:null},'Guest billing account has no automatic subscription');
  await rejected(()=>rpc('get_my_billing',[],null,'anon'),'42501');
  await rejected(()=>billing(pending),'42501');
  for(const table of ['billing_subscriptions','billing_checkout_intents','billing_invoices','billing_audit']){
    await rejected(()=>as('authenticated',alice,`select count(*) as value from usage_widget_private.${table}`),'42501');
    await rejected(()=>as('service_role',null,`select count(*) as value from usage_widget_private.${table}`),'42501');
  }
  const svcFunctions=(await db.query("select p.proname,p.oid from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname like 'billing_%'")).rows;
  equal(svcFunctions.length,9,'Expected nine narrow service-only RPCs');
  for(const f of svcFunctions){const privileges=(await db.query("select has_function_privilege('anon',$1,'execute') as anon,has_function_privilege('authenticated',$1,'execute') as member,has_function_privilege('service_role',$1,'execute') as service",[f.oid])).rows[0];equal(privileges,{anon:false,member:false,service:true},f.proname+' rejects browser access');}
  await rejected(()=>rpc('billing_begin_checkout',[alice,'a'.repeat(64),'customer_12345678','v1'],alice,'authenticated'),'42501');
  await rejected(()=>checkout(pending),'42501');
  await rejected(()=>rpc('billing_begin_checkout',[alice,'short','customer_12345678','v1']));
  await rejected(()=>rpc('billing_begin_checkout',[alice,'a'.repeat(64),'short','v1']));
  await rejected(()=>rpc('billing_begin_checkout',[alice,'a'.repeat(64),'customer_12345678',' ']));
  const intent=await checkout(alice);
  const stored=(await db.query('select terms_version,consent_at,expires_at-created_at as lifetime from usage_widget_private.billing_checkout_intents where id=$1',[intent.checkout_id])).rows[0];
  equal(stored.terms_version,'2026-10-12-v1','Consent version recorded by server');ok(stored.consent_at,'Consent timestamp exists');
  equal((await rpc('billing_get_checkout',[alice,intent.checkout_id,intent.state])).status,'pending','Owner nonce gets checkout');
  await rejected(()=>rpc('billing_get_checkout',[bob,intent.checkout_id,intent.state]));
  await rejected(()=>rpc('billing_get_checkout',[alice,intent.checkout_id,'0'.repeat(64)]));
  await rejected(()=>rpc('billing_complete_checkout',[alice,intent.state,intent.customer,'plaintext-billing-key']));
  const invoice=await complete(intent);equal((await complete(intent)).invoice_id,invoice.invoice_id,'Complete retry uses same invoice/order');
  equal((await member(alice)).plan,'free','Registering a billing key does not grant Pro');
  await rejected(()=>checkout(alice));
  equal((await billing(bob)).subscription,null,'Other member sees no billing data');
  const publicState=JSON.stringify(await billing(alice));ok(!publicState.includes(cipher)&&!publicState.includes(intent.customer)&&!publicState.includes('payment_key'),'User status excludes billing secrets/customer/payment keys');
  let job=await claim(invoice);equal(job.action,'charge','New invoice authorizes one charge');equal(job.amount,3000,'Amount fixed by DB');equal(job.currency,'KRW','Currency fixed by DB');
  equal(await claim(invoice),null,'Active lease prevents duplicate worker');
  equal(await rpc('billing_claim_order',[invoice.order_id,'payment_'+invoice.invoice_id]),{busy:true},'Known in-flight order is busy, not an unknown webhook');
  equal(await rpc('billing_validate_charge',[job.invoice_id,job.lease_token]),true,'Current lease validates once');
  equal(await rpc('billing_validate_charge',[job.invoice_id,job.lease_token]),false,'Same lease cannot authorize a second provider POST');
  await rejected(()=>rpc('billing_settle_invoice',[job.invoice_id,'00000000-0000-4000-8000-000000000001',{outcome:'paid'}]),'40001');
  for(const mismatch of [{totalAmount:2999},{currency:'USD'},{orderId:'other'},{status:'READY'},{balanceAmount:2999},{paymentKey:''},{approvedAt:'infinity'},{approvedAt:'2000-01-01T00:00:00Z'}])await rejected(()=>settle(job,'paid',mismatch));
  equal((await member(alice)).plan,'free','Rejected payment data never grants Pro');
  equal((await settle(job)).status,'paid','Exact verified DONE settles payment');
  equal((await member(alice)).plan,'pro','Paid invoice enables Pro');
  equal((await billing(alice)).subscription.status,'active','Paid account becomes active');
  await rejected(()=>settle(job),'40001');
  equal((await db.query('select count(*)::int as value from usage_widget_private.billing_invoices where subscription_id=$1',[invoice.subscription_id])).rows[0].value,1,'Duplicate settlement does not create an invoice');
  await rejected(()=>checkout(alice));
  const paidEnd=(await member(alice)).expires_at;
  const admin=await user();await db.query('insert into usage_widget_private.membership_admins(user_id) values($1)',[admin]);
  await rpc('admin_set_membership',[alice,'free',null,'manual Free does not cancel paid'],admin,'authenticated');
  equal((await member(alice)).plan,'pro','Admin manual Free preserves independent paid subscription');
  const manualEnd=new Date(Date.now()+60*86400000).toISOString();
  await rpc('admin_set_membership',[alice,'pro',manualEnd,'manual longer grant'],admin,'authenticated');
  equal(Date.parse((await member(alice)).expires_at),Date.parse(manualEnd),'Later manual entitlement wins');
  await rpc('admin_set_membership',[alice,'free',null,'remove manual grant'],admin,'authenticated');
  equal((await member(alice)).expires_at,paidEnd,'Removing manual entitlement retains paid period');
  const canceled=await cancel(alice);equal(canceled.subscription.auto_renew,false,'Cancellation stops renewal');equal((await member(alice)).plan,'pro','Cancellation retains already-paid time');equal((await member(alice)).status,'canceled','Membership communicates cancellation');
  await rejected(()=>checkout(alice));
  equal((await rpc('billing_claim_due',[20])).jobs.length,0,'Canceled active subscription is never renewed');

  const before=await user();const beforeInvoice=await start(before);let beforeJob=await claim(beforeInvoice);await cancel(before);
  equal(await rpc('billing_validate_charge',[beforeJob.invoice_id,beforeJob.lease_token]),false,'Cancel after claim but before POST prevents charge');
  equal(await claim(beforeInvoice),null,'Canceled pending invoice cannot restart');equal((await member(before)).plan,'free','Canceled unpaid checkout grants nothing');
  const during=await user();const duringInvoice=await start(during);let duringJob=await claim(duringInvoice);await rpc('billing_validate_charge',[duringJob.invoice_id,duringJob.lease_token]);await cancel(during);await settle(duringJob);
  equal((await billing(during)).subscription.auto_renew,false,'In-flight paid result cannot reenable renewal after cancellation');equal((await member(during)).plan,'pro','In-flight confirmed charge receives paid period');

  const unknown=await user();const unknownInvoice=await start(unknown);let unknownJob=await claim(unknownInvoice);await rpc('billing_validate_charge',[unknownJob.invoice_id,unknownJob.lease_token]);
  await rpc('billing_settle_invoice',[unknownJob.invoice_id,unknownJob.lease_token,{outcome:'unknown',reason:'provider timeout'}]);
  equal(await claim(unknownInvoice),null,'Automatic unknown retry has five-minute backoff');
  unknownJob=await rpc('billing_claim_order',[unknownInvoice.order_id,null]);equal(unknownJob.action,'reconcile','Timeout can only reconcile same order');equal(unknownJob.order_id,unknownInvoice.order_id,'Timeout never creates a replacement order');
  equal(await rpc('billing_validate_charge',[unknownJob.invoice_id,unknownJob.lease_token]),false,'Reconciliation cannot charge');await rejected(()=>checkout(unknown));
  await expireLease(unknownInvoice.invoice_id);const laterJob=await claim(unknownInvoice);equal(laterJob.action,'reconcile','Expired worker lease also reconciles');
  await rejected(()=>settle(unknownJob),'40001');await settle(laterJob);equal((await member(unknown)).plan,'pro','Reconciled DONE grants paid period once');

  const lost=await user();const lostInvoice=await start(lost);const lostJob=await claim(lostInvoice);await expireLease(lostJob.invoice_id);equal((await claim(lostInvoice)).action,'reconcile','Crashed worker without settlement never retries a fresh charge');
  const failed=await user();const failedInvoice=await start(failed);let failedJob=await claim(failedInvoice);await rpc('billing_validate_charge',[failedJob.invoice_id,failedJob.lease_token]);await rpc('billing_settle_invoice',[failedJob.invoice_id,failedJob.lease_token,{outcome:'failed',reason:'card declined'}]);
  equal((await billing(failed)).subscription.status,'past_due','Definitive failure sets past_due');equal((await billing(failed)).subscription.auto_renew,false,'Definitive failure stops automatic retries');equal((await member(failed)).plan,'free','Failure cannot grant Pro');equal(await claim(failedInvoice),null,'Failed invoice is not autocharged again');
  const expired=await user();const expiredIntent=await checkout(expired);await db.query("update usage_widget_private.billing_checkout_intents set expires_at=now()-interval '1 second' where id=$1",[expiredIntent.checkout_id]);await rejected(()=>rpc('billing_get_checkout',[expired,expiredIntent.checkout_id,expiredIntent.state]));await rejected(()=>complete(expiredIntent));

  const firstRefund=await user();const firstRefundInvoice=await start(firstRefund);
  let firstRefundJob=await rpc('billing_claim_order',[firstRefundInvoice.order_id,'payment_'+firstRefundInvoice.invoice_id]);
  equal(firstRefundJob.action,'reconcile','Known order without stored payment key can reconcile the supplied provider key');
  await settle(firstRefundJob,'refunded');equal((await billing(firstRefund)).subscription.status,'refunded','First observation CANCELED ends pending checkout');
  equal((await billing(firstRefund)).subscription.auto_renew,false,'First observation refund stops renewal');equal((await member(firstRefund)).plan,'free','Refunded before DONE never grants access');
  const exhausted=await user();const exhaustedInvoice=await start(exhausted);let exhaustedJob=await claim(exhaustedInvoice);
  await db.query('update usage_widget_private.billing_invoices set attempts=6 where id=$1',[exhaustedInvoice.invoice_id]);
  await rpc('billing_settle_invoice',[exhaustedJob.invoice_id,exhaustedJob.lease_token,{outcome:'unknown',reason:'provider order remains absent'}]);
  equal((await billing(exhausted)).subscription.status,'review','Bounded reconciliation failures become review');
  equal(await claim(exhaustedInvoice),null,'Review is never automatically charged or reconciled again');await rejected(()=>checkout(exhausted));
  const backedOff=await user();const backedOffInvoice=await start(backedOff);let backedOffJob=await claim(backedOffInvoice);
  await rpc('billing_settle_invoice',[backedOffJob.invoice_id,backedOffJob.lease_token,{outcome:'unknown',reason:'not found yet'}]);
  const ready=await user();const readyInvoice=await start(ready);let fairBatch=(await rpc('billing_claim_due',[1])).jobs;
  equal(fairBatch.length,1,'A ready checkout is not starved by an older unknown invoice');equal(fairBatch[0].invoice_id,readyInvoice.invoice_id,'Backoff leaves room for the new invoice');
  await cancel(ready);

  const renewing=await user();const oldInvoice=await makePaid(renewing);
  await db.query("update usage_widget_private.billing_subscriptions set current_period_end=now()-interval '1 minute',anchor_day=31 where id=$1",[oldInvoice.subscription_id]);
  // Hold unrelated uncertain invoices out of this deterministic renewal batch.
  await db.query("update usage_widget_private.billing_invoices set lease_until=now()+interval '10 minutes' where status in ('unknown','charging')");
  let renewals=(await rpc('billing_claim_due',[20])).jobs;equal(renewals.length,1,'One due subscription creates one invoice');let renewal=renewals[0];equal(renewal.action,'charge','Due renewal is chargeable');
  equal((await rpc('billing_claim_due',[20])).jobs.length,0,'Overlapping scheduler cannot claim same cycle');
  await rpc('billing_validate_charge',[renewal.invoice_id,renewal.lease_token]);await settle(renewal);const renewedEnd=(await member(renewing)).expires_at;
  let refundOld=await rpc('billing_claim_order',[oldInvoice.order_id,null]);equal(refundOld.action,'reconcile','Paid historical invoice remains discoverable for refund');await settle(refundOld,'refunded');
  equal((await member(renewing)).expires_at,renewedEnd,'Refund of older invoice cannot revoke the current paid period');equal((await member(renewing)).plan,'pro','Old refund leaves current Pro active');
  let currentRefund=await rpc('billing_claim_order',[null,'payment_'+renewal.invoice_id]);ok(currentRefund,'Known paymentKey maps to stored invoice');await settle(currentRefund,'partial_refund');
  equal((await member(renewing)).plan,'pro','Partial refund requires review but does not revoke paid access');equal((await billing(renewing)).subscription.status,'review','Partial refund flags manual review');equal((await billing(renewing)).subscription.auto_renew,false,'Partial refund stops automatic charging');
  currentRefund=await rpc('billing_claim_order',[renewal.order_id,null]);await settle(currentRefund,'refunded');equal((await member(renewing)).plan,'free','Full refund of current invoice removes paid entitlement');equal((await billing(renewing)).subscription.status,'refunded','Public status shows refunded');
  let staleDone=await rpc('billing_claim_order',[renewal.order_id,null]);await settle(staleDone,'paid');equal((await member(renewing)).plan,'free','Older DONE event cannot undo full refund');
  equal((await db.query('select balance_amount as value from usage_widget_private.billing_invoices where id=$1',[renewal.invoice_id])).rows[0].value,0,'Older DONE does not rewrite refunded balance');
  equal(await rpc('billing_claim_order',['unknown_order',null]),null,'Unknown webhook order cannot invent a payment');equal(await rpc('billing_claim_order',[oldInvoice.order_id,'payment_'+renewal.invoice_id]),null,'Mismatched order/payment identity rejected');

  const late=await user();const lateInvoice=await makePaid(late);await db.query("update usage_widget_private.billing_subscriptions set current_period_end=now()-interval '25 hours' where id=$1",[lateInvoice.subscription_id]);
  await rpc('billing_claim_due',[20]);equal((await billing(late)).subscription.status,'review','Downtime beyond 24 hours requires review');equal((await billing(late)).subscription.auto_renew,false,'Overdue charging is stopped');
  equal((await db.query('select count(*)::int as value from usage_widget_private.billing_invoices where subscription_id=$1',[lateInvoice.subscription_id])).rows[0].value,1,'Overdue subscription does not create a catch-up charge');
  await rejected(()=>rpc('billing_claim_due',[21]));await rejected(()=>rpc('billing_claim_due',[0]));
  for(const [start,anchor,end] of [['2026-01-31T10:30:00+09:00',31,'2026-02-28T10:30:00+09:00'],['2026-02-28T10:30:00+09:00',31,'2026-03-31T10:30:00+09:00'],['2028-01-31T23:30:00+09:00',31,'2028-02-29T23:30:00+09:00'],['2026-12-31T01:00:00+09:00',31,'2027-01-31T01:00:00+09:00']]){
    const value=(await db.query('select usage_widget_private.billing_next_period($1,$2) as value',[start,anchor])).rows[0].value;equal(new Date(value).getTime(),Date.parse(end),'KST calendar month preserves anchor and clamps month end');
  }
  const dueA=await user(),dueB=await user();const paidA=await makePaid(dueA),paidB=await makePaid(dueB);
  await db.query("update usage_widget_private.billing_subscriptions set current_period_end=now()-interval '2 minutes' where id=$1",[paidA.subscription_id]);
  await db.query("update usage_widget_private.billing_subscriptions set current_period_end=now()-interval '1 minute' where id=$1",[paidB.subscription_id]);
  const jobA=(await rpc('billing_claim_due',[1])).jobs[0];equal(jobA.subscription_id,paidA.subscription_id,'Oldest subscription is claimed first');
  await rpc('billing_settle_invoice',[jobA.invoice_id,jobA.lease_token,{outcome:'unknown',reason:'oldest remains unresolved'}]);
  const jobB=(await rpc('billing_claim_due',[1])).jobs[0];equal(jobB.subscription_id,paidB.subscription_id,'Existing unresolved cycle does not starve creation for the next due subscription');
  await cancel(dueA);await cancel(dueB);
  const manual=await user();await rpc('admin_set_membership',[manual,'pro',new Date(Date.now()+45*86400000).toISOString(),'manual'],admin,'authenticated');await makePaid(manual);let manualInvoice=(await billing(manual)).subscription.latest_invoice;let manualRefund=await rpc('billing_claim_order',[manualInvoice.order_id,null]);await settle(manualRefund,'refunded');equal((await member(manual)).plan,'pro','Paid refund preserves independent manual Pro entitlement');
  const audit=(await db.query("select count(*)::int as value from usage_widget_private.billing_audit where event like 'invoice_%'")).rows[0].value;ok(audit>5,'Payment state transitions are audited');
  await db.exec(await readFile(new URL('../migrations/202610110002_billing.sql',import.meta.url),'utf8'));equal((await billing(alice)).subscription.auto_renew,false,'Reapplying billing migration preserves cancellation/data');
  console.log(`PASS: ${checks} billing PostgreSQL checks; no provider/network/payment credentials used.`);
}finally{await db.close();}
