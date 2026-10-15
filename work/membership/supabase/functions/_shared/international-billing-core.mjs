// Lemon Squeezy hosted checkout. The browser never supplies a plan, price, user
// ID or subscription ID. Auth binds checkout; signed metadata binds ownership;
// fresh provider invoice evidence, persisted atomically, authorizes finite Pro.
// Contracts: docs.lemonsqueezy.com/api and /help/webhooks/signing-requests
export const AMOUNT = 3000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ID = /^[1-9][0-9]{0,19}$/;
const TOKEN = /^[A-Za-z0-9_-]{43}$/;
const API = 'https://api.lemonsqueezy.com/v1';
const messages = {
  AUTH_REQUIRED: '다시 로그인해 주세요.', EMAIL_UNVERIFIED: '이메일 인증을 완료해 주세요.', FORBIDDEN: '이 요청을 처리할 권한이 없습니다.',
  BILLING_UNAVAILABLE: '해외 결제 서비스는 아직 준비 중입니다.', INVALID_REQUEST: '요청 내용을 확인해 주세요.',
  CONSENT_REQUIRED: '이용약관과 월 3,000원 정기결제에 동의해 주세요.', ALREADY_SUBSCRIBED: '기존 구독을 먼저 확인해 주세요. 중복 결제는 진행하지 않습니다.',
  BILLING_PENDING: '진행 중인 결제를 확인하고 있습니다. 잠시 후 상태를 다시 확인해 주세요.',
  PAYMENT_REVIEW: '결제 상태 확인이 필요합니다. 추가 결제 없이 관리자에게 문의해 주세요.', SERVER_UNAVAILABLE: '서버에 연결할 수 없습니다. 잠시 후 다시 확인해 주세요.',
};
class Fault extends Error { constructor(code, status = 400) { super(messages[code]); this.code = code; this.status = status; } }
const object = value => !!value && typeof value === 'object' && !Array.isArray(value);
const clean = (value, max = 12000) => typeof value === 'string' && value.length > 0 && value.length <= max && !/[\r\n\0]/.test(value) ? value : null;
const id = value => ID.test(String(value)) ? String(value) : null;
function https(value, onlyOrigin = false) {
  try { const u = new URL(value); return u.protocol === 'https:' && !u.username && !u.password && !u.port && !['localhost','127.0.0.1','[::1]'].includes(u.hostname)
    && (!onlyOrigin || (u.pathname === '/' && !u.search && !u.hash)) ? (onlyOrigin ? u.origin : u.href) : null; } catch { return null; }
}
export function readInternationalConfig(env = {}) {
  const cfg = {
    site: https(env.SITE_URL, true), supabase: https(env.SUPABASE_URL, true), anonKey: clean(env.SUPABASE_ANON_KEY), serviceKey: clean(env.SUPABASE_SERVICE_ROLE_KEY),
    key: clean(env.LEMON_API_KEY), webhookSecret: clean(env.LEMON_WEBHOOK_SECRET, 512), cronSecret: clean(env.INTERNATIONAL_BILLING_CRON_SECRET, 512),
    store: id(env.LEMON_STORE_ID), product: id(env.LEMON_PRODUCT_ID), variant: id(env.LEMON_VARIANT_ID), price: id(env.LEMON_PRICE_ID),
    checkoutOrigin: https(env.LEMON_STORE_URL, true), mode: env.LEMON_MODE,
    // Lemon's custom-pricing guide specifies the currency's smallest unit. KRW
    // has zero fractional digits, so the agreed KRW 3,000 price is exactly 3000.
    expectedSubtotal: AMOUNT,
    termsUrl: https(env.BILLING_TERMS_URL), privacyUrl: https(env.BILLING_PRIVACY_URL), refundUrl: https(env.BILLING_REFUND_URL),
    termsVersion: /^[A-Za-z0-9_.-]{1,64}$/.test(env.BILLING_TERMS_VERSION ?? '') ? env.BILLING_TERMS_VERSION : null,
  };
  if (cfg.checkoutOrigin && !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.lemonsqueezy\.com$/.test(new URL(cfg.checkoutOrigin).hostname)) cfg.checkoutOrigin = null;
  cfg.testMode = cfg.mode === 'test';
  cfg.enabled = env.INTERNATIONAL_BILLING_ENABLED === 'true' && ['test','live'].includes(cfg.mode)
    && !!(cfg.site && cfg.supabase && cfg.anonKey && cfg.serviceKey && cfg.key && cfg.webhookSecret?.length >= 32 && cfg.cronSecret?.length >= 32
      && cfg.store && cfg.product && cfg.variant && cfg.price && cfg.checkoutOrigin && cfg.expectedSubtotal
      && cfg.termsUrl && cfg.privacyUrl && cfg.refundUrl && cfg.termsVersion);
  return cfg;
}
export function safeProviderUrl(value, origin, kind) {
  const href = https(value); if (!href) return null;
  const u = new URL(href);
  return u.origin === origin && !u.hash && (kind === 'checkout' ? /^\/checkout\/custom\/[0-9a-f-]{36}$/.test(u.pathname) : /^\/billing(?:\/|$)/.test(u.pathname)) ? u.href : null;
}
async function bytesFor(message, limit) {
  if (Number(message.headers.get('content-length')) > limit) throw new Fault('INVALID_REQUEST', 413);
  const reader = message.body?.getReader(); if (!reader) return new Uint8Array();
  const chunks = []; let count = 0;
  try { for (;;) { const part = await reader.read(); if (part.done) break; count += part.value.length; if (count > limit) throw new Fault('INVALID_REQUEST', 413); chunks.push(part.value); } }
  finally { await reader.cancel().catch(() => {}); }
  const bytes = new Uint8Array(count); let offset = 0; for (const part of chunks) { bytes.set(part, offset); offset += part.length; } return bytes;
}
function parse(bytes) { try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); } catch { throw new Fault('INVALID_REQUEST'); } }
const hex = bytes => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
const hash = async (crypto, bytes) => hex(new Uint8Array(await crypto.subtle.digest('SHA-256', typeof bytes === 'string' ? new TextEncoder().encode(bytes) : bytes)));
async function validSignature(crypto, bytes, signature, secret) {
  if (!/^[a-f0-9]{64}$/i.test(signature ?? '')) return false;
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
  return await crypto.subtle.verify('HMAC', key, Uint8Array.from(signature.match(/../g), b => parseInt(b,16)), bytes);
}
function randomToken(crypto) { return btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32)))).replaceAll('+','-').replaceAll('/','_').replaceAll('=',''); }
function timestamp(value) { const result = typeof value === 'string' ? Date.parse(value) : NaN; if (!Number.isFinite(result)) throw new Fault('PAYMENT_REVIEW',409); return new Date(result).toISOString(); }
function resource(data, type, expectedId = null) {
  if (!object(data) || data.type !== type || !id(data.id) || (expectedId && data.id !== expectedId) || !object(data.attributes)) throw new Fault('PAYMENT_REVIEW',409);
  return data.attributes;
}
// Invoice objects have no period fields. A verified monthly invoice pays exactly
// one calendar cycle anchored to the provider's billing day; never use a past_due
// renews_at (that is a retry date), nor "now + month" on webhook delivery.
export function paidCycleEnd(createdAt, anchor, subscription) {
  const d = new Date(timestamp(createdAt));
  if (!Number.isInteger(anchor) || anchor < 1 || anchor > 31) throw new Fault('PAYMENT_REVIEW',409);
  const month = d.getUTCMonth()+1, year = d.getUTCFullYear();
  const end = new Date(Date.UTC(year, month, Math.min(anchor, new Date(Date.UTC(year,month+1,0)).getUTCDate()), d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds(), d.getUTCMilliseconds()));
  if (end <= d || end-d > 32*86400000) throw new Fault('PAYMENT_REVIEW',409);
  // The current active cycle endpoint may shorten, never lengthen, invoice proof.
  for (const value of [subscription.ends_at, subscription.status === 'active' ? subscription.renews_at : null]) {
    if (value) { const cap = new Date(timestamp(value)); if (cap>d && cap<end) end.setTime(cap.getTime()); }
  }
  return end.toISOString();
}

export function createInternationalBillingHandlers({ env = {}, fetch: fetcher = globalThis.fetch, crypto = globalThis.crypto } = {}) {
  const cfg = readInternationalConfig(env);
  function response(value, status=200, cors=false) {
    const headers = { 'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff','referrer-policy':'no-referrer' };
    if (cors && cfg.site) Object.assign(headers, { 'access-control-allow-origin':cfg.site,vary:'Origin','access-control-allow-headers':'authorization,apikey,content-type,x-client-info','access-control-allow-methods':'POST,OPTIONS' });
    return new Response(status===204 ? null : JSON.stringify(value), {status,headers});
  }
  const failure = (error,cors=false) => response({error:{code:error instanceof Fault ? error.code : 'SERVER_UNAVAILABLE',message:error instanceof Fault ? error.message : messages.SERVER_UNAVAILABLE}},error instanceof Fault ? error.status : 503,cors);
  async function remote(url, options={}) {
    const controller=new AbortController(), timer=setTimeout(()=>controller.abort(),15000);
    try {
      const result=await fetcher(url,{...options,redirect:'error',signal:controller.signal});
      const body=parse(await bytesFor(result,524288));
      if (!result.ok) { if ([401,403].includes(result.status) && new URL(url).pathname==='/auth/v1/user') throw new Fault('AUTH_REQUIRED',401); throw new Fault('SERVER_UNAVAILABLE',503); }
      return body;
    } finally { clearTimeout(timer); }
  }
  const rpc = (name,body={},token=cfg.serviceKey) => remote(`${cfg.supabase}/rest/v1/rpc/${name}`,{method:'POST',headers:{apikey:token===cfg.serviceKey?cfg.serviceKey:cfg.anonKey,authorization:`Bearer ${token}`,'content-type':'application/json'},body:JSON.stringify(body)});
  const provider = (path,body) => remote(API+path,{method:body?'POST':'GET',headers:{authorization:`Bearer ${cfg.key}`,accept:'application/vnd.api+json','content-type':'application/vnd.api+json'},...(body?{body:JSON.stringify(body)}:{})});
  function required() { if(!cfg.enabled) throw new Fault('BILLING_UNAVAILABLE',503); }
  async function identityFor(request) {
    const match=/^Bearer ([A-Za-z0-9._~-]{8,12000})$/.exec(request.headers.get('authorization')??'');
    if(!match||!cfg.supabase||!cfg.anonKey) throw new Fault('AUTH_REQUIRED',401);
    const user=await remote(cfg.supabase+'/auth/v1/user',{headers:{apikey:cfg.anonKey,authorization:`Bearer ${match[1]}`}});
    if(!UUID.test(user?.id??'')||!clean(user.email,254)||!user.email_confirmed_at||user.is_anonymous===true||user.deleted_at||(user.banned_until&&Date.parse(user.banned_until)>Date.now())) throw new Fault('EMAIL_UNVERIFIED',403);
    const membership=await rpc('get_my_membership',{},match[1]);
    if(membership?.user_id!==user.id) throw new Fault('FORBIDDEN',403);
    return {user,token:match[1],isAdmin:membership.is_admin===true};
  }
  function publicConfig() { return {enabled:cfg.enabled,provider:'lemonsqueezy',amount:AMOUNT,currency:'KRW',interval:'month',testMode:cfg.testMode,
    ...(cfg.enabled?{termsUrl:cfg.termsUrl,privacyUrl:cfg.privacyUrl,refundUrl:cfg.refundUrl,termsVersion:cfg.termsVersion,checkoutOrigin:cfg.checkoutOrigin}:{})}; }
  const statusFor=async identity=>({...await rpc('get_my_international_billing',{},identity.token),membership:await rpc('get_my_membership',{},identity.token)});
  function configEvidence() { return {store_id:cfg.store,product_id:cfg.product,variant_id:cfg.variant,price_id:cfg.price,test_mode:cfg.testMode,expected_subtotal:cfg.expectedSubtotal}; }
  function sameConfig(owned) { return ['store_id','product_id','variant_id','price_id','test_mode','expected_subtotal'].every(key=>owned[key]===configEvidence()[key]); }
  async function verifyCatalog() {
    const [store,product,variant,price]=await Promise.all([
      provider(`/stores/${cfg.store}`),provider(`/products/${cfg.product}`),provider(`/variants/${cfg.variant}`),provider(`/prices/${cfg.price}`)]);
    const s=resource(store.data,'stores',cfg.store),p=resource(product.data,'products',cfg.product),v=resource(variant.data,'variants',cfg.variant),r=resource(price.data,'prices',cfg.price);
    if(s.currency!=='KRW'||https(s.url,true)!==cfg.checkoutOrigin||id(p.store_id)!==cfg.store||p.test_mode!==cfg.testMode||p.status!=='published'
      ||id(v.product_id)!==cfg.product||v.test_mode!==cfg.testMode||id(r.variant_id)!==cfg.variant||r.category!=='subscription'||r.scheme!=='standard'
      ||r.renewal_interval_unit!=='month'||r.renewal_interval_quantity!==1||r.unit_price!==cfg.expectedSubtotal||r.usage_aggregation!=null
      ||r.setup_fee_enabled!==false||r.trial_interval_quantity!=null||r.trial_interval_unit!=null) throw new Fault('PAYMENT_REVIEW',409);
  }
  async function checkout(identity,body) {
    if(cfg.testMode&&!identity.isAdmin) throw new Fault('FORBIDDEN',403);
    if(body.consent!==true||body.termsVersion!==cfg.termsVersion) throw new Fault('CONSENT_REQUIRED');
    if(!['ko','en'].includes(body.locale)) throw new Fault('INVALID_REQUEST');
    await verifyCatalog();
    const token=randomToken(crypto);
    const intent=await rpc('international_begin_checkout',{p_user_id:identity.user.id,p_token_hash:await hash(crypto,token),p_config:configEvidence(),p_terms_version:cfg.termsVersion});
    if(['ALREADY_SUBSCRIBED','BILLING_PENDING'].includes(intent?.blocked)) throw new Fault(intent.blocked,409);
    if(intent?.existing) { const url=safeProviderUrl(intent.checkout_url,cfg.checkoutOrigin,'checkout'); if(!url) throw new Fault('PAYMENT_REVIEW',409); return {checkoutUrl:url,expiresAt:intent.expires_at}; }
    if(!UUID.test(intent?.checkout_id??'')||Date.parse(intent.expires_at)<=Date.now()) throw new Fault('SERVER_UNAVAILABLE',503);
    const checkout=await provider('/checkouts',{data:{type:'checkouts',attributes:{
      checkout_options:{embed:false,discount:false,skip_trial:true,subscription_preview:true,locale:body.locale,button_color:'#171717',button_text_color:'#ffffff'},
      product_options:{enabled_variants:[Number(cfg.variant)],redirect_url:new URL('/account/?international=return',cfg.site).href},
      checkout_data:{email:identity.user.email,custom:{nephylo_checkout:token},variant_quantities:[{variant_id:Number(cfg.variant),quantity:1}]},
      expires_at:intent.expires_at,test_mode:cfg.testMode,preview:true},
      relationships:{store:{data:{type:'stores',id:cfg.store}},variant:{data:{type:'variants',id:cfg.variant}}}}});
    const c=checkout?.data?.attributes;
    const url=safeProviderUrl(c?.url,cfg.checkoutOrigin,'checkout');
    if(checkout?.data?.type!=='checkouts'||!UUID.test(checkout?.data?.id??'')||!url||id(c.store_id)!==cfg.store||id(c.variant_id)!==cfg.variant||c.test_mode!==cfg.testMode
      ||c.preview?.currency!=='KRW'||c.preview?.subtotal!==cfg.expectedSubtotal||c.preview?.discount_total!==0||!Number.isFinite(Date.parse(c.expires_at))
      ||Date.parse(c.expires_at)<=Date.now()||Math.abs(Date.parse(c.expires_at)-Date.parse(intent.expires_at))>1000)
      throw new Fault('PAYMENT_REVIEW',409);
    if(await rpc('international_checkout_ready',{p_user_id:identity.user.id,p_checkout_id:intent.checkout_id,p_provider_checkout_id:checkout.data.id,p_url:url})!==true) throw new Fault('BILLING_PENDING',409);
    return {checkoutUrl:url,expiresAt:intent.expires_at};
  }
  function subscriptionEvidence(data,owned) {
    const s=resource(data,'subscriptions'); const item=s.first_subscription_item;
    if(!sameConfig(owned)||id(s.store_id)!==owned.store_id||id(s.product_id)!==owned.product_id||id(s.variant_id)!==owned.variant_id||s.test_mode!==owned.test_mode
      ||!id(s.customer_id)||!id(s.order_id)||!object(item)||id(item.price_id)!==owned.price_id||id(item.subscription_id)!==data.id||item.quantity!==1
      ||(owned.subscription_id&&owned.subscription_id!==data.id)|| (owned.customer_id&&owned.customer_id!==id(s.customer_id)) || (owned.order_id&&owned.order_id!==id(s.order_id))
      ||!['on_trial','active','paused','past_due','unpaid','cancelled','expired'].includes(s.status)) throw new Fault('PAYMENT_REVIEW',409);
    return {...configEvidence(),id:data.id,customer_id:id(s.customer_id),order_id:id(s.order_id),status:s.status,
      auto_renew:!s.cancelled&&!['cancelled','expired','paused','unpaid'].includes(s.status),created_at:timestamp(s.created_at),updated_at:timestamp(s.updated_at),
      ends_at:s.ends_at?timestamp(s.ends_at):null,renews_at:s.renews_at?timestamp(s.renews_at):null};
  }
  function invoiceEvidence(data,subscription,subEvidence,order) {
    const i=resource(data,'subscription-invoices');
    if(id(i.subscription_id)!==subEvidence.id||id(i.store_id)!==cfg.store||id(i.customer_id)!==subEvidence.customer_id||i.test_mode!==cfg.testMode||i.currency!=='KRW'
      ||i.subtotal!==cfg.expectedSubtotal||i.discount_total!==0||!Number.isInteger(i.total)||i.total<i.subtotal||!Number.isInteger(i.refunded_amount)||i.refunded_amount<0||i.refunded_amount>i.total
      ||!['initial','renewal','updated'].includes(i.billing_reason)||!['paid','refunded','partial_refund','pending','void'].includes(i.status)) throw new Fault('PAYMENT_REVIEW',409);
    let status=i.status;
    if(i.refunded===true||i.refunded_amount===i.total||(i.billing_reason==='initial'&&(order.refunded===true||order.status==='refunded'))) status='refunded';
    else if(i.refunded_amount>0||(i.billing_reason==='initial'&&order.status==='partial_refund')) status='partial_refund';
    if(status==='paid'&&(i.refunded!==false||i.refunded_amount!==0)) throw new Fault('PAYMENT_REVIEW',409);
    return {id:data.id,subscription_id:subEvidence.id,status,billing_reason:i.billing_reason,subtotal:i.subtotal,
      period_start:timestamp(i.created_at),period_end:paidCycleEnd(i.created_at,subscription.billing_anchor,subscription),updated_at:timestamp(i.updated_at)};
  }
  async function reconcile(subscriptionId,owned,{invoiceId=null,digest=null,eventName='sync'}={}) {
    if(!owned) throw new Fault('BILLING_PENDING',503);
    const document=await provider(`/subscriptions/${subscriptionId}`),s=resource(document.data,'subscriptions',subscriptionId),evidence=subscriptionEvidence(document.data,owned);
    const orderDocument=await provider(`/orders/${evidence.order_id}`),order=resource(orderDocument.data,'orders',evidence.order_id);
    if(id(order.store_id)!==cfg.store||id(order.customer_id)!==evidence.customer_id||order.test_mode!==cfg.testMode||order.currency!=='KRW'
      ||!['paid','refunded','partial_refund','pending'].includes(order.status)) throw new Fault('PAYMENT_REVIEW',409);
    const list=await provider(`/subscription-invoices?filter[subscription_id]=${subscriptionId}&page[size]=100`);
    if(!Array.isArray(list.data)||list.data.length>100) throw new Fault('PAYMENT_REVIEW',409);
    const records=new Map(list.data.map(value=>[value.id,value]));
    // Always re-read the named invoice, even if it is older than the list window.
    if(invoiceId) records.set(invoiceId,(await provider(`/subscription-invoices/${invoiceId}`)).data);
    const invoices=Array.from(records.values(),i=>invoiceEvidence(i,s,evidence,order));
    if(order.status==='pending'&&invoices.some(i=>i.billing_reason==='initial'&&i.status==='paid')) throw new Fault('BILLING_PENDING',503);
    await rpc('international_apply',{p_checkout_id:owned.id,p_subscription:evidence,p_invoices:invoices,p_event_digest:digest,p_event_name:eventName});
    return s;
  }
  async function processEvent(job) {
    let subscriptionId, invoiceId=null,owned;
    if(job.resource_type==='subscriptions') subscriptionId=job.resource_id;
    else if(job.resource_type==='subscription-invoices') { invoiceId=job.resource_id; const doc=await provider(`/subscription-invoices/${invoiceId}`); subscriptionId=id(resource(doc.data,'subscription-invoices',invoiceId).subscription_id); }
    else if(job.resource_type==='orders') {
      owned=await rpc('international_lookup',{p_order_id:job.resource_id}); subscriptionId=owned?.subscription_id;
      if(!subscriptionId&&job.token_hash) {
        owned=await rpc('international_lookup',{p_token_hash:job.token_hash});
        if(owned) {
          const related=await provider(`/subscriptions?filter[order_id]=${job.resource_id}&page[size]=2`);
          if(!Array.isArray(related.data)||related.data.length!==1||id(related.data[0]?.attributes?.order_id)!==job.resource_id) throw new Fault('BILLING_PENDING',503);
          subscriptionId=id(related.data[0].id);
        }
      }
    }
    if(!subscriptionId) throw new Fault('BILLING_PENDING',503);
    owned??=await rpc('international_lookup',{p_subscription_id:subscriptionId});
    if(!owned&&job.token_hash) owned=await rpc('international_lookup',{p_token_hash:job.token_hash});
    await reconcile(subscriptionId,owned,{invoiceId,digest:job.digest,eventName:job.event_name});
    await rpc('international_finish_event',{p_digest:job.digest});
  }
  async function sync(identity) {
    const pending=await rpc('international_pending_events',{p_user_id:identity.user.id,p_limit:5});
    let failed=false;
    // One late/unbound event must not hide a different verified paid subscription.
    for(const job of pending??[]) { try { await processEvent(job); } catch { failed=true; } }
    const owned=await rpc('international_lookup',{p_user_id:identity.user.id});
    if(owned?.subscription_id) await reconcile(owned.subscription_id,owned);
    else if(failed) throw new Fault('BILLING_PENDING',503);
    return await statusFor(identity);
  }
  async function api(request) {
    const cors=request.headers.get('origin')===cfg.site;
    try {
      if(request.headers.has('origin')&&!cors) throw new Fault('FORBIDDEN',403);
      if(request.method==='OPTIONS') return response(null,204,cors);
      if(request.method!=='POST'||!request.headers.get('content-type')?.toLowerCase().startsWith('application/json')) throw new Fault('INVALID_REQUEST');
      const body=parse(await bytesFor(request,8192)); if(!object(body)) throw new Fault('INVALID_REQUEST');
      if(body.action==='config') return response(publicConfig(),200,cors);
      const identity=await identityFor(request);
      if(body.action==='status') return response(await statusFor(identity),200,cors);
      required();
      if(body.action==='checkout') return response(await checkout(identity,body),200,cors);
      if(body.action==='sync') return response(await sync(identity),200,cors);
      if(body.action==='portal') {
        const owned=await rpc('international_lookup',{p_user_id:identity.user.id}); if(!owned?.subscription_id) throw new Fault('INVALID_REQUEST');
        const doc=await provider(`/subscriptions/${owned.subscription_id}`); subscriptionEvidence(doc.data,owned);
        const url=safeProviderUrl(doc.data.attributes.urls?.customer_portal,cfg.checkoutOrigin,'portal'); if(!url) throw new Fault('PAYMENT_REVIEW',409);
        return response({portalUrl:url},200,cors);
      }
      throw new Fault('INVALID_REQUEST');
    } catch(error) { return failure(error,cors); }
  }
  async function webhook(request) {
    try {
      required(); if(request.method!=='POST') throw new Fault('INVALID_REQUEST');
      const bytes=await bytesFor(request,262144);
      if(!await validSignature(crypto,bytes,request.headers.get('x-signature'),cfg.webhookSecret)) throw new Fault('FORBIDDEN',403);
      const body=parse(bytes),name=body?.meta?.event_name,type=body?.data?.type,resourceId=id(body?.data?.id);
      const allowed = {subscriptions:['subscription_created','subscription_updated','subscription_cancelled','subscription_resumed','subscription_expired','subscription_paused','subscription_unpaused'],
        'subscription-invoices':['subscription_payment_success','subscription_payment_recovered','subscription_payment_failed','subscription_payment_refunded'],orders:['order_created','order_refunded']};
      if(!allowed[type]?.includes(name)) return response({received:true,ignored:true});
      if(!resourceId||body?.data?.attributes?.test_mode!==cfg.testMode||('test_mode' in body.meta&&body.meta.test_mode!==cfg.testMode)) throw new Fault('PAYMENT_REVIEW',409);
      const custom=body.meta.custom_data?.nephylo_checkout;
      const job={digest:await hash(crypto,bytes),event_name:name,resource_type:type,resource_id:resourceId,token_hash:TOKEN.test(custom??'')?await hash(crypto,custom):null};
      const added=await rpc('international_enqueue_event',{p_event:job});
      if(added!==false) await processEvent(job);
      return response({received:true});
    } catch(error) { return failure(error); }
  }
  async function reconcileScheduled(request) {
    try {
      required(); if(request.method!=='POST') throw new Fault('FORBIDDEN',403);
      const bearer=request.headers.get('authorization')??'';
      if(!await validSignature(crypto,new TextEncoder().encode('nephylo-international-reconcile'),await hashHmac(bearer.slice(7)),cfg.cronSecret)||!bearer.startsWith('Bearer ')) throw new Fault('FORBIDDEN',403);
      // Bounded work fits the Edge lifetime even when the provider is slow.
      const started=Date.now(),jobs=await rpc('international_pending_events',{p_user_id:null,p_limit:1}); let processed=0,failed=0;
      for(const job of jobs??[]) { try { await processEvent(job); processed++; } catch { failed++; } }
      const due=Date.now()-started<30000?await rpc('international_due_subscriptions',{p_limit:1}):[];
      for(const owned of due??[]) { try { await reconcile(owned.subscription_id,owned); processed++; } catch { failed++; } }
      return response({processed,failed},failed?503:200);
    } catch(error) { return failure(error); }
  }
  async function hashHmac(secret) { const key=await crypto.subtle.importKey('raw',new TextEncoder().encode(secret),{name:'HMAC',hash:'SHA-256'},false,['sign']); return hex(new Uint8Array(await crypto.subtle.sign('HMAC',key,new TextEncoder().encode('nephylo-international-reconcile')))); }
  return {api,webhook,reconcile:reconcileScheduled};
}
