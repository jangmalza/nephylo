import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { webcrypto, createHmac } from 'node:crypto';
import { PGlite } from '@electric-sql/pglite';
import { createInternationalBillingHandlers, readInternationalConfig, paidCycleEnd, safeProviderUrl } from '../supabase/functions/_shared/international-billing-core.mjs';

// Real Edge handlers, real Postgres migrations and real role grants; only the
// Auth and Lemon HTTP servers are fakes. No payment keys, network or live data.
const db=new PGlite(), site='https://nephylo.example', supabase='https://member.supabase.co', store='https://nephylo-test.lemonsqueezy.com';
const ids=Array.from({length:8},(_,i)=>`ca220718-12f7-4197-86a4-d7271d2b820${i}`);
let identity=ids[0], checks=0, createCalls=0, brokenProvider=false, unsafePortal=false, catalogPrice=3000, lastCheckout, sqlFailure;
const subscriptions=new Map(), invoices=new Map(), orders=new Map(), checkouts=new Map();
const env={INTERNATIONAL_BILLING_ENABLED:'true',LEMON_MODE:'live',SITE_URL:site,SUPABASE_URL:supabase,SUPABASE_ANON_KEY:'public-key',SUPABASE_SERVICE_ROLE_KEY:'service-key',
  LEMON_API_KEY:'fake-provider-key',LEMON_WEBHOOK_SECRET:'fake-webhook-secret-32-characters-or-more',LEMON_STORE_ID:'1',LEMON_PRODUCT_ID:'2',LEMON_VARIANT_ID:'3',LEMON_PRICE_ID:'4',LEMON_STORE_URL:store,
  INTERNATIONAL_BILLING_CRON_SECRET:'fake-cron-secret-32-characters-or-more',BILLING_TERMS_URL:site+'/terms/',BILLING_PRIVACY_URL:site+'/privacy/',BILLING_REFUND_URL:site+'/refund/',BILLING_TERMS_VERSION:'v1'};
const json=(value,status=200)=>new Response(JSON.stringify(value),{status,headers:{'content-type':'application/json'}});
const check=(value,message)=>{assert.ok(value,message);checks++;};
async function rpc(name,body={},user=identity,service=false) {
  const entries=Object.entries(body); assert.match(name,/^[a-z_]+$/); for(const [key] of entries)assert.match(key,/^p_[a-z_]+$/);
  await db.exec('begin');
  try {
    await db.exec(`set local role ${service?'service_role':'authenticated'}`);
    await db.query("select set_config('request.jwt.claim.sub',$1,true)",[service?'':user]);
    const result=await db.query(`select public.${name}(${entries.map(([k],i)=>`${k} => $${i+1}`).join(',')}) as value`,entries.map(([,v])=>v));
    await db.exec('commit'); return result.rows[0].value;
  }catch(e){await db.exec('rollback');throw e;}
}
const fetch=async(url,options={})=>{
  const u=new URL(url),headers=new Headers(options.headers),body=options.body?JSON.parse(options.body):null;
  assert.equal(options.redirect,'error');
  if(u.origin===supabase){
    if(u.pathname==='/auth/v1/user'){
      if(headers.get('authorization')==='Bearer invalid-token')return json({},401);
      return json({id:identity,email:`member${ids.indexOf(identity)}@example.test`,email_confirmed_at:new Date().toISOString()});
    }
    try{return json(await rpc(u.pathname.split('/').pop(),body,identity,headers.get('authorization')==='Bearer service-key'));}
    catch(e){sqlFailure={message:e.message,detail:e.detail};return json({code:e.code},400);}
  }
  assert.equal(u.origin,'https://api.lemonsqueezy.com');assert.equal(headers.get('authorization'),'Bearer fake-provider-key');
  if(brokenProvider)throw Error('provider unavailable');
  const result=(type,id,attributes)=>json({data:{type,id,attributes}}), path=u.pathname;
  if(path==='/v1/stores/1')return result('stores','1',{currency:'KRW',url:store});
  if(path==='/v1/products/2')return result('products','2',{store_id:1,status:'published',test_mode:false});
  if(path==='/v1/variants/3')return result('variants','3',{product_id:2,test_mode:false});
  if(path==='/v1/prices/4')return result('prices','4',{variant_id:3,category:'subscription',scheme:'standard',renewal_interval_unit:'month',renewal_interval_quantity:1,unit_price:catalogPrice,usage_aggregation:null,setup_fee_enabled:false,trial_interval_quantity:null,trial_interval_unit:null});
  if(path==='/v1/checkouts'){
    createCalls++; const a=body.data.attributes;check(!('custom_price' in a),'client cannot override price');
    check(a.product_options.enabled_variants.length===1&&a.product_options.enabled_variants[0]===3,'checkout allows only approved variant');
    check(a.checkout_options.discount===false&&a.checkout_options.skip_trial===true,'discount and trial do not create unpaid access');
    check(a.product_options.redirect_url===site+'/account/?international=return','return URL is fixed server-side');
    const checkoutId=webcrypto.randomUUID(), checkoutUrl=`${store}/checkout/custom/${checkoutId}?signature=test`;
    lastCheckout={id:checkoutId,...a};checkouts.set(identity,lastCheckout);
    return result('checkouts',checkoutId,{store_id:1,variant_id:3,test_mode:false,url:checkoutUrl,expires_at:a.expires_at,preview:{currency:'KRW',subtotal:3000,discount_total:0}});
  }
  if(path==='/v1/subscriptions')return json({data:[...subscriptions.values()].filter(s=>String(s.attributes.order_id)===u.searchParams.get('filter[order_id]'))});
  if(path.startsWith('/v1/subscriptions/')){const s=structuredClone(subscriptions.get(path.split('/').pop()));if(!s)return json({},404);if(unsafePortal)s.attributes.urls.customer_portal='https://evil.example/billing';return json({data:s});}
  if(path==='/v1/subscription-invoices')return json({data:[...invoices.values()].filter(i=>String(i.attributes.subscription_id)===u.searchParams.get('filter[subscription_id]')).sort((a,b)=>b.attributes.created_at.localeCompare(a.attributes.created_at))});
  if(path.startsWith('/v1/subscription-invoices/'))return json({data:invoices.get(path.split('/').pop())});
  if(path.startsWith('/v1/orders/'))return json({data:orders.get(path.split('/').pop())});
  throw Error('unexpected fake provider request '+path);
};
const handlers=createInternationalBillingHandlers({env,fetch,crypto:webcrypto});
async function api(body,handler=handlers,authorization='Bearer verified-user-token',origin=site){
  const response=await handler.api(new Request(supabase+'/functions/v1/international-billing-api',{method:'POST',headers:{'content-type':'application/json',origin,authorization},body:JSON.stringify(body)}));
  return {status:response.status,body:await response.json()};
}
const start=()=>api({action:'checkout',consent:true,termsVersion:'v1',locale:'en',amount:1,variantId:'evil',userId:ids[7]});
function providerSubscription(number,{paid=true}={}) {
  const subId=String(number),orderId=String(number+100),invoiceId=String(number+200),created=new Date(Date.now()-1000).toISOString(),anchor=new Date(created).getUTCDate();
  const end=paidCycleEnd(created,anchor,{status:'active'});
  const attrs={store_id:1,customer_id:number+300,order_id:Number(orderId),product_id:2,variant_id:3,status:'active',cancelled:false,test_mode:false,
    first_subscription_item:{price_id:4,subscription_id:number,quantity:1},billing_anchor:anchor,renews_at:end,ends_at:null,created_at:created,updated_at:created,
    urls:{customer_portal:store+'/billing?signature=private-customer-link'}};
  subscriptions.set(subId,{type:'subscriptions',id:subId,attributes:attrs});
  orders.set(orderId,{type:'orders',id:orderId,attributes:{store_id:1,customer_id:number+300,test_mode:false,currency:'KRW',status:'paid',refunded:false}});
  invoices.set(invoiceId,{type:'subscription-invoices',id:invoiceId,attributes:{store_id:1,subscription_id:number,customer_id:number+300,test_mode:false,currency:'KRW',subtotal:3000,discount_total:0,total:3300,refunded_amount:0,refunded:false,status:paid?'paid':'pending',billing_reason:'initial',created_at:created,updated_at:created}});
  return {subId,orderId,invoiceId,end,attrs};
}
async function event(name,doc,custom=checkouts.get(identity)?.checkout_data.custom,signature=true){
  const body=JSON.stringify({meta:{event_name:name,custom_data:custom},data:doc});
  const sig=signature?createHmac('sha256',env.LEMON_WEBHOOK_SECRET).update(body).digest('hex'):'0'.repeat(64);
  const r=await handlers.webhook(new Request(supabase+'/functions/v1/international-billing-webhook',{method:'POST',headers:{'content-type':'application/json','x-signature':sig},body}));
  return {status:r.status,body:await r.json()};
}
async function cron(secret=env.INTERNATIONAL_BILLING_CRON_SECRET){const r=await handlers.reconcile(new Request(supabase+'/functions/v1/international-billing-reconcile',{method:'POST',headers:{authorization:'Bearer '+secret}}));return{status:r.status,body:await r.json()};}
try {
  for(const path of ['tests/bootstrap.sql',...['0001_membership','0002_billing','0003_admin_entitlement_sources','0004_international_billing'].map(x=>'migrations/20261011'+x+'.sql')])await db.exec(await readFile(new URL('../'+path,import.meta.url),'utf8'));
  for(const user of ids)await db.query('insert into auth.users(id,email,email_confirmed_at) values($1,$2,now())',[user,`member${ids.indexOf(user)}@example.test`]);
  await db.query('insert into usage_widget_private.membership_admins(user_id) values($1)',[ids[7]]);
  check(!readInternationalConfig({}).enabled,'checkout defaults disabled');
  check(!readInternationalConfig({...env,LEMON_STORE_URL:'https://lemonsqueezy.com.evil.example'}).enabled,'store host is exact allowlist');
  check(!safeProviderUrl(store+'/checkout/custom/anything',store,'checkout'),'invalid checkout path rejected');
  check(!safeProviderUrl('https://user@nephylo-test.lemonsqueezy.com/billing',store,'portal'),'credentialed provider URL rejected');
  check(paidCycleEnd('2025-01-31T12:00:00Z',31,{status:'active'})==='2025-02-28T12:00:00.000Z','January anchor clamps February');
  check(paidCycleEnd('2025-02-28T12:00:00Z',31,{status:'past_due',renews_at:'2025-04-10T12:00:00Z'})==='2025-03-31T12:00:00.000Z','February renewal preserves31 anchor and ignores retry date');
  let r=await api({action:'config'});check(r.body.enabled&&r.body.amount===3000&&!JSON.stringify(r).includes('fake-provider'),'public config has fixed price and no secrets');
  r=await api({action:'status'},handlers,'Bearer invalid-token');check(r.status===401,'invalid Auth token rejected');
  r=await api({action:'config'},handlers,'Bearer verified-user-token','https://evil.example');check(r.status===403,'cross-origin browser rejected');
  r=await api({action:'checkout',consent:true,termsVersion:'v1',locale:'en'},createInternationalBillingHandlers({env:{...env,LEMON_MODE:'test'},fetch,crypto:webcrypto}));check(r.status===403,'testcheckout restricted to verified admins');
  r=await api({action:'checkout',consent:false,termsVersion:'v1',locale:'en'});check(r.status===400,'explicit current consent required');
  catalogPrice=2999;r=await start();check(r.status===409&&createCalls===0,'incorrect catalog amount blocks before checkout');catalogPrice=3000;
  r=await start();check(r.status===200,r.body.error?.message+' '+JSON.stringify(sqlFailure));const firstUrl=r.body.checkoutUrl;
  check((await api({action:'status'})).body.membership.plan==='free','checkout redirect never grants Pro');
  r=await start();check(r.body.checkoutUrl===firstUrl&&createCalls===1,'doubleclick reuses one server checkout');
  await assert.rejects(()=>rpc('billing_begin_checkout',{p_user_id:identity,p_state_hash:'a'.repeat(64),p_customer_key:webcrypto.randomUUID(),p_terms_version:'v1'},identity,true));checks++;
  const first=providerSubscription(10,{paid:false});
  r=await event('subscription_created',subscriptions.get('10'),undefined,false);check(r.status===403,'invalid raw HMAC rejected');
  check((await db.query('select count(*) from usage_widget_private.international_webhook_inbox')).rows[0].count===0,'unsigned event is never persisted');
  r=await event('subscription_created',subscriptions.get('10'));check(r.status===200,JSON.stringify(r)+' '+JSON.stringify(sqlFailure));
  check((await api({action:'status'})).body.membership.plan==='free','active subscription with unpaid invoice has no Pro');
  invoices.get(first.invoiceId).attributes.status='paid';invoices.get(first.invoiceId).attributes.updated_at=new Date().toISOString();
  r=await event('subscription_payment_success',invoices.get(first.invoiceId));check(r.status===200,JSON.stringify(r)+' '+JSON.stringify(sqlFailure));
  r=await api({action:'status'});check(r.body.membership.plan==='pro'&&new Date(r.body.subscription.current_period_end).toISOString()===first.end,'paid authoritative invoice grants one finite term');
  const expiration=r.body.membership.expires_at;
  await event('subscription_payment_success',invoices.get(first.invoiceId));check((await api({action:'status'})).body.membership.expires_at===expiration,'replayed paid event does not add another month');
  first.attrs.renews_at=new Date(Date.now()+75*86400000).toISOString();first.attrs.updated_at=new Date().toISOString();
  await event('subscription_updated',subscriptions.get('10'));check((await api({action:'status'})).body.membership.expires_at===expiration,'status-only future cycle does not extend old paid invoice');
  r=await api({action:'portal'});check(r.status===200&&r.body.portalUrl.startsWith(store+'/billing'),'own signed customer portal returned');
  unsafePortal=true;r=await api({action:'portal'});check(r.status===409,'foreign portal URL rejected');unsafePortal=false;
  identity=ids[1];r=await api({action:'portal',subscriptionId:'10'});check(r.status===400,'client cannot claim another member portal');identity=ids[0];
  first.attrs.status='cancelled';first.attrs.cancelled=true;first.attrs.ends_at=first.end;first.attrs.updated_at=new Date().toISOString();
  await event('subscription_cancelled',subscriptions.get('10'));check((await api({action:'status'})).body.membership.plan==='pro','cancellation keeps paid term');
  invoices.get(first.invoiceId).attributes.status='refunded';invoices.get(first.invoiceId).attributes.refunded=true;invoices.get(first.invoiceId).attributes.refunded_amount=3300;invoices.get(first.invoiceId).attributes.updated_at=new Date().toISOString();
  await event('subscription_payment_refunded',invoices.get(first.invoiceId));r=await api({action:'status'});check(r.body.membership.plan==='free'&&r.body.subscription.current_period_end===null,'full refund plus future cancellation date cannot grant Pro via NULL least');
  invoices.get(first.invoiceId).attributes.status='paid';invoices.get(first.invoiceId).attributes.refunded=false;invoices.get(first.invoiceId).attributes.refunded_amount=0;
  await api({action:'sync'});check((await api({action:'status'})).body.membership.plan==='free','stale paid provider response cannot resurrect refunded invoice');
  await db.query("insert into usage_widget_private.entitlements(user_id,plan,status,expires_at) values($1,'pro','active',now()+interval '10 days')",[identity]);
  await api({action:'sync'});check((await api({action:'status'})).body.membership.plan==='pro','international refund preserves independent manual grant');
  identity=ids[1];r=await start();check(r.status===200,'another member can checkout independently');const second=providerSubscription(20);
  brokenProvider=true;r=await event('subscription_created',subscriptions.get('20'));check(r.status===503,'provider outage asks webhook retry after durable enqueue');brokenProvider=false;
  check((await db.query('select count(*) from usage_widget_private.international_webhook_inbox where processed_at is null')).rows[0].count>0,'first event retained durably');
  check((await cron('wrong-secret')).status===403,'scheduler secret required');
  r=await cron();check(r.status===200,r.body.failed+' '+JSON.stringify(sqlFailure));check((await api({action:'status'})).body.membership.plan==='pro','scheduler recovers first callback provider outage');
  identity=ids[7];const members=await rpc('admin_list_members',{});check(members.members.some(m=>m.user_id===ids[1]&&m.international?.active),'admin sees international source separately');
  identity=ids[2];r=await start();check(r.status===200,'third checkout starts');
  await db.query("update usage_widget_private.international_checkouts set expires_at=now()-interval '1 hour' where user_id=$1",[identity]);
  await db.query("update usage_widget_private.billing_provider_reservations set expires_at=now()-interval '1 hour' where user_id=$1",[identity]);
  const callCount=createCalls;r=await start();check(r.status===409&&r.body.error.code==='BILLING_PENDING'&&createCalls===callCount,'expired URL with missing first webhook does not permit a second charge');
  await assert.rejects(()=>rpc('billing_begin_checkout',{p_user_id:identity,p_state_hash:'b'.repeat(64),p_customer_key:webcrypto.randomUUID(),p_terms_version:'v1'},identity,true));checks++;
  identity=ids[3];await rpc('billing_begin_checkout',{p_user_id:identity,p_state_hash:'c'.repeat(64),p_customer_key:webcrypto.randomUUID(),p_terms_version:'v1'},identity,true);
  r=await start();check(r.status===409&&createCalls===callCount,'pending Toss checkout blocks international checkout');
  identity=ids[4];await start();const fourth=providerSubscription(40);r=await event('order_created',orders.get(fourth.orderId));check(r.status===200,JSON.stringify(r)+' '+JSON.stringify(sqlFailure));
  check((await api({action:'status'})).body.membership.plan==='pro','signed order custom data recovers missing subscription-created event');
  identity=ids[5];await start();const fifth=providerSubscription(50);fifth.attrs.variant_id=99;r=await event('subscription_created',subscriptions.get('50'));check(r.status===409&&(await api({action:'status'})).body.membership.plan==='free','wrong provider variant cannot grant Pro');
  await assert.rejects(()=>rpc('international_lookup',{p_subscription_id:'10'}));checks++;
  await assert.rejects(()=>rpc('international_apply',{p_checkout_id:webcrypto.randomUUID(),p_subscription:{},p_invoices:[],p_event_digest:null,p_event_name:'forge'}));checks++;
  await db.exec('set role authenticated');await assert.rejects(()=>db.query('select * from usage_widget_private.international_subscriptions'));await db.exec('reset role');checks++;
  console.log(`PASS: ${checks} international billing Edge + Postgres checks`);
}catch(e){console.error(e.message,sqlFailure??'');process.exitCode=1;}finally{await db.close();}
