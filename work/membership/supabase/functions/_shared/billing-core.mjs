// Browser input and webhook payloads never authorize an entitlement. Only a
// verified Toss payment for a server-created, leased invoice reaches settlement.
// Provider contracts: https://docs.tosspayments.com/reference
// https://supabase.com/docs/guides/functions/auth
export const PRICE = 3000;
export const CURRENCY = 'KRW';
const TOSS_ORIGIN = 'https://api.tosspayments.com';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ORDER = /^[A-Za-z0-9_-]{6,64}$/;
const STATE = /^[A-Za-z0-9_-]{43}$/;
// Only definitive card/billing rejections documented for billing approvals are
// eligible for a new attempt, and only after Toss confirms the order is absent.
// https://docs.tosspayments.com/reference/error-codes#카드-자동결제-승인
const DECLINED = new Set(['INVALID_STOPPED_CARD', 'INVALID_REJECT_CARD', 'INVALID_CARD_LOST_OR_STOLEN',
  'INVALID_CARD_EXPIRATION', 'INVALID_CARD_NUMBER', 'INVALID_BILL_KEY_REQUEST', 'NOT_SUPPORTED_CARD_TYPE',
  'NOT_REGISTERED_CARD_COMPANY', 'REJECT_CARD_PAYMENT', 'REJECT_ACCOUNT_PAYMENT', 'REJECT_CARD_COMPANY', 'EXCEED_MAX_AUTH_COUNT']);
const MESSAGES = {
  AUTH_REQUIRED: '이메일 인증으로 다시 로그인해 주세요.',
  EMAIL_UNVERIFIED: '이메일 인증을 완료한 계정으로 로그인해 주세요.',
  FORBIDDEN: '이 요청을 처리할 권한이 없습니다.',
  BILLING_UNAVAILABLE: '결제 서비스는 아직 준비 중입니다. 현재 무료 기능을 이용할 수 있습니다.',
  ALREADY_SUBSCRIBED: '다른 결제 수단의 구독이나 진행 중인 결제를 먼저 확인해 주세요.',
  INVALID_REQUEST: '요청 내용을 확인한 뒤 다시 시도해 주세요.',
  CONSENT_REQUIRED: '현재 이용약관과 월 3,000원 자동 갱신 결제에 동의해 주세요.',
  CHECKOUT_EXPIRED: '결제 연결이 만료되었거나 계정이 다릅니다. 다시 로그인하고 결제를 시작해 주세요.',
  BILLING_PENDING: '결제 결과를 확인하고 있습니다. 새 결제를 시작하지 말고 상태를 다시 확인해 주세요.',
  PAYMENT_REVIEW: '결제 상태를 확인 중입니다. 추가 결제 없이 관리자에게 문의해 주세요.',
  PAYMENT_FAILED: '결제가 완료되지 않았습니다. 결제 내역을 확인해 주세요.',
  SERVER_UNAVAILABLE: '서버에 연결할 수 없습니다. 잠시 후 다시 확인해 주세요.',
};
class Fault extends Error {
  constructor(code, status = 400) { super(MESSAGES[code] ?? MESSAGES.SERVER_UNAVAILABLE); this.code = code; this.status = status; }
}
class RemoteFailure extends Error {
  constructor(status = 0, code = '') { super('Remote request failed'); this.status = status; this.code = code; }
}
function text(value, max = 300) { return typeof value === 'string' && value.length > 0 && value.length <= max && !/[\r\n\0]/.test(value) ? value : null; }
function origin(value) {
  try {
    const u = new URL(value);
    if (u.protocol !== 'https:' || u.username || u.password || u.search || u.hash || u.pathname !== '/' || ['localhost', '127.0.0.1', '[::1]'].includes(u.hostname)) return null;
    return u.origin;
  } catch { return null; }
}
function policyUrl(value) {
  try { const u = new URL(value); return u.protocol === 'https:' && !u.username && !u.password && !['localhost', '127.0.0.1', '[::1]'].includes(u.hostname) ? u.href : null; } catch { return null; }
}
function base64(bytes) { let value = ''; for (const b of bytes) value += String.fromCharCode(b); return btoa(value); }
function decode64(value) { return Uint8Array.from(atob(value), c => c.charCodeAt(0)); }
function randomState(crypto) { return base64(crypto.getRandomValues(new Uint8Array(32))).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', ''); }
async function digest(crypto, value) { return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))), n => n.toString(16).padStart(2, '0')).join(''); }
async function secretEqual(crypto, first, second) {
  if (!text(first, 512) || !text(second, 512)) return false;
  const a = await digest(crypto, first), b = await digest(crypto, second); let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}
export function readBillingConfig(env = {}) {
  let encryptionKey;
  try { encryptionKey = decode64(env.BILLING_ENCRYPTION_KEY ?? ''); } catch { encryptionKey = new Uint8Array(); }
  const client = /^(test|live)_ck_[A-Za-z0-9_-]{8,}$/.exec(env.TOSS_CLIENT_KEY ?? '');
  const secret = /^(test|live)_sk_[A-Za-z0-9_-]{8,}$/.exec(env.TOSS_SECRET_KEY ?? '');
  const cfg = {
    site: origin(env.SITE_URL), supabase: origin(env.SUPABASE_URL), anonKey: text(env.SUPABASE_ANON_KEY, 12000),
    serviceKey: text(env.SUPABASE_SERVICE_ROLE_KEY, 12000), clientKey: client ? env.TOSS_CLIENT_KEY : null,
    secretKey: secret ? env.TOSS_SECRET_KEY : null, mid: /^[A-Za-z0-9_-]{1,14}$/.test(env.TOSS_MID ?? '') ? env.TOSS_MID : null,
    encryptionKey, cronSecret: text(env.BILLING_CRON_SECRET, 512),
    termsUrl: policyUrl(env.BILLING_TERMS_URL), privacyUrl: policyUrl(env.BILLING_PRIVACY_URL), refundUrl: policyUrl(env.BILLING_REFUND_URL),
    termsVersion: /^[A-Za-z0-9_.-]{1,64}$/.test(env.BILLING_TERMS_VERSION ?? '') ? env.BILLING_TERMS_VERSION : null,
  };
  cfg.enabled = env.BILLING_ENABLED === 'true' && !!(cfg.site && cfg.supabase && cfg.anonKey && cfg.serviceKey && cfg.mid && client && secret && client[1] === secret[1] && encryptionKey.length === 32 && cfg.termsUrl && cfg.privacyUrl && cfg.refundUrl && cfg.termsVersion && cfg.cronSecret?.length >= 32);
  return cfg;
}
export async function sealBillingKey(value, userId, rawKey, crypto = globalThis.crypto) {
  if (!text(value, 200) || !UUID.test(userId) || rawKey.length !== 32) throw new Fault('PAYMENT_REVIEW', 409);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const key = await crypto.subtle.importKey('raw', rawKey, 'AES-GCM', false, ['encrypt']);
  const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(`UsageWidget.Billing.v1|${userId}`) }, key, new TextEncoder().encode(value));
  return `v1.${base64(iv)}.${base64(new Uint8Array(cipher))}`;
}
export async function unsealBillingKey(value, userId, rawKey, crypto = globalThis.crypto) {
  try {
    if (!text(value, 16384) || !UUID.test(userId) || rawKey.length !== 32) throw new Error();
    const parts = value.split('.'); if (parts.length !== 3 || parts[0] !== 'v1') throw new Error();
    const iv = decode64(parts[1]), cipher = decode64(parts[2]); if (iv.length !== 12 || cipher.length < 17 || cipher.length > 1024) throw new Error();
    const key = await crypto.subtle.importKey('raw', rawKey, 'AES-GCM', false, ['decrypt']);
    const clear = await crypto.subtle.decrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(`UsageWidget.Billing.v1|${userId}`) }, key, cipher);
    const result = new TextDecoder('utf-8', { fatal: true }).decode(clear); if (!text(result, 200)) throw new Error(); return result;
  } catch { throw new Fault('PAYMENT_REVIEW', 409); }
}
async function readJson(response, limit) {
  if (Number(response.headers.get('content-length')) > limit) throw new RemoteFailure();
  const reader = response.body?.getReader(); if (!reader) return {};
  let length = 0; const chunks = [];
  try {
    while (true) { const item = await reader.read(); if (item.done) break; length += item.value.length; if (length > limit) throw new RemoteFailure(); chunks.push(item.value); }
  } finally { await reader.cancel().catch(() => {}); }
  if (length === 0) return {};
  const bytes = new Uint8Array(length); let offset = 0; for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); } catch { throw new RemoteFailure(); }
}
function plainObject(value) { return !!value && typeof value === 'object' && !Array.isArray(value); }
function safePayment(payment) {
  return { paymentKey: payment.paymentKey, orderId: payment.orderId, totalAmount: payment.totalAmount, currency: payment.currency,
    status: payment.status, approvedAt: payment.approvedAt ?? null, balanceAmount: payment.balanceAmount, type: payment.type, mId: payment.mId };
}
function invoiceState(state, subscription) {
  if (state !== 'pending') return state;
  const latest = subscription?.latest_invoice?.status;
  if (latest === 'paid') return 'active';
  if (['failed', 'canceled'].includes(latest)) return 'failed';
  if (latest === 'refunded') return 'refunded';
  if (['review', 'partial_refund'].includes(latest)) return 'review';
  return state;
}
function settledState(result) {
  const states = { paid: 'active', refunded: 'refunded', partial_refund: 'review', review: 'review', failed: 'failed', canceled: 'failed', unknown: 'pending' };
  if (!states[result?.status]) throw new Fault('SERVER_UNAVAILABLE', 503);
  return states[result.status];
}
export function validatePayment(payment, job, mid) {
  // Payment has no documented customerKey field. The Billing issuance response
  // binds it to our invoice; reject a conflicting field if a provider adds one.
  return plainObject(payment) && payment.orderId === job.order_id && payment.totalAmount === PRICE && payment.currency === CURRENCY &&
    payment.type === 'BILLING' && payment.mId === mid && !!text(payment.paymentKey, 200) &&
    (!job.payment_key || payment.paymentKey === job.payment_key) && (!('customerKey' in payment) || payment.customerKey === job.customer_key) &&
    Number.isInteger(payment.balanceAmount) && payment.balanceAmount >= 0 && payment.balanceAmount <= PRICE;
}

export function createBillingHandlers({ env = {}, fetch: fetcher = globalThis.fetch, crypto = globalThis.crypto } = {}) {
  const cfg = readBillingConfig(env);
  function response(value, status = 200, cors = false) {
    const headers = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff', 'referrer-policy': 'no-referrer' };
    if (cors && cfg.site) Object.assign(headers, { 'access-control-allow-origin': cfg.site, 'vary': 'Origin', 'access-control-allow-headers': 'authorization,apikey,content-type,x-client-info', 'access-control-allow-methods': 'POST,OPTIONS', 'access-control-max-age': '600' });
    return new Response(status === 204 ? null : JSON.stringify(value), { status, headers });
  }
  function failure(error, cors = false) {
    if (error instanceof Fault) return response({ error: { code: error.code, message: error.message } }, error.status, cors);
    return response({ error: { code: 'SERVER_UNAVAILABLE', message: MESSAGES.SERVER_UNAVAILABLE } }, 503, cors);
  }
  async function remote(url, options = {}, timeout = 15000) {
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), timeout);
    try {
      const result = await fetcher(url, { ...options, redirect: 'error', signal: controller.signal });
      const data = await readJson(result, 262144);
      if (!result.ok) throw new RemoteFailure(result.status, /^[A-Z0-9_]{1,80}$/.test(data?.code ?? '') ? data.code : '');
      return data;
    } catch (error) { if (error instanceof RemoteFailure) throw error; throw new RemoteFailure(); }
    finally { clearTimeout(timer); }
  }
  async function rpc(name, body, bearer = cfg.serviceKey) {
    return await remote(`${cfg.supabase}/rest/v1/rpc/${name}`, { method: 'POST', headers: { apikey: bearer === cfg.serviceKey ? cfg.serviceKey : cfg.anonKey, authorization: `Bearer ${bearer}`, 'content-type': 'application/json' }, body: JSON.stringify(body) });
  }
  async function toss(path, body, idempotencyKey, method = 'POST') {
    const headers = { authorization: `Basic ${btoa(`${cfg.secretKey}:`)}`, 'content-type': 'application/json' };
    if (method === 'POST') headers['Idempotency-Key'] = idempotencyKey;
    return await remote(TOSS_ORIGIN + path, { method, headers, ...(body ? { body: JSON.stringify(body) } : {}) }, method === 'POST' && /^\/v1\/billing\/[^/]+$/.test(path) ? 65000 : 15000);
  }
  async function userFor(request) {
    const match = /^Bearer ([A-Za-z0-9._~-]{8,12000})$/.exec(request.headers.get('authorization') ?? '');
    if (!match || !cfg.supabase || !cfg.anonKey) throw new Fault('AUTH_REQUIRED', 401);
    let user;
    try { user = await remote(`${cfg.supabase}/auth/v1/user`, { headers: { apikey: cfg.anonKey, authorization: `Bearer ${match[1]}` } }); }
    catch (error) { if ([401, 403].includes(error.status)) throw new Fault('AUTH_REQUIRED', 401); throw error; }
    if (!UUID.test(user?.id ?? '') || !text(user.email, 254) || !user.email_confirmed_at || user.is_anonymous === true || user.deleted_at || (user.banned_until && Date.parse(user.banned_until) > Date.now())) throw new Fault('EMAIL_UNVERIFIED', 403);
    let membership;
    try { membership = await rpc('get_my_membership', {}, match[1]); }
    catch (error) { if ([401, 403].includes(error.status)) throw new Fault('FORBIDDEN', 403); throw error; }
    if (!plainObject(membership) || membership.user_id !== user.id) throw new Fault('FORBIDDEN', 403);
    return { user, token: match[1], membership };
  }
  async function bodyFor(request, limit = 32768) {
    if (request.method !== 'POST' || !request.headers.get('content-type')?.toLowerCase().startsWith('application/json')) throw new Fault('INVALID_REQUEST', 400);
    try { const data = await readJson(request, limit); if (!plainObject(data)) throw new Error(); return data; } catch { throw new Fault('INVALID_REQUEST', 400); }
  }
  function required() { if (!cfg.enabled) throw new Fault('BILLING_UNAVAILABLE', 503); }
  function publicConfig() {
    return { enabled: cfg.enabled, amount: PRICE, currency: CURRENCY, interval: 'month', ...(cfg.enabled ? { clientKey: cfg.clientKey, termsUrl: cfg.termsUrl, privacyUrl: cfg.privacyUrl, refundUrl: cfg.refundUrl, termsVersion: cfg.termsVersion } : {}) };
  }
  async function statusFor(identity) {
    const billing = await rpc('get_my_billing', {}, identity.token);
    const membership = await rpc('get_my_membership', {}, identity.token);
    return { subscription: billing.subscription, membership };
  }
  async function settle(job, outcome, payment = null, reason = '') {
    return await rpc('billing_settle_invoice', { p_invoice_id: job.invoice_id, p_lease_token: job.lease_token,
      p_result: { outcome, ...(payment ? { payment: safePayment(payment) } : {}), reason } });
  }
  async function reconcile(job, decline = null) {
    let payment;
    try { payment = await toss(`/v1/payments/orders/${encodeURIComponent(job.order_id)}`, null, null, 'GET'); }
    catch (error) {
      if (decline && error.status === 404 && error.code === 'NOT_FOUND_PAYMENT')
        return settledState(await settle(job, 'failed', null, `PROVIDER_DECLINED_${decline}`));
      await settle(job, 'unknown', null, 'PROVIDER_QUERY_UNAVAILABLE'); return 'pending';
    }
    if (!validatePayment(payment, job, cfg.mid)) { await settle(job, 'review', null, 'PROVIDER_PAYMENT_MISMATCH'); return 'review'; }
    if (payment.status === 'DONE' && payment.balanceAmount === PRICE && Number.isFinite(Date.parse(payment.approvedAt))) return settledState(await settle(job, 'paid', payment));
    if (payment.status === 'CANCELED' && payment.balanceAmount === 0) return settledState(await settle(job, 'refunded', payment));
    if (payment.status === 'PARTIAL_CANCELED' && payment.balanceAmount > 0 && payment.balanceAmount < PRICE) return settledState(await settle(job, 'partial_refund', payment));
    if (['ABORTED', 'EXPIRED'].includes(payment.status)) return settledState(await settle(job, 'failed', payment, 'PROVIDER_PAYMENT_FAILED'));
    await settle(job, 'unknown', null, 'PROVIDER_PAYMENT_PENDING'); return 'pending';
  }
  async function processJob(job) {
    if (!job) return 'pending';
    if (!UUID.test(job.invoice_id ?? '') || !UUID.test(job.user_id ?? '') || !UUID.test(job.lease_token ?? '') || !ORDER.test(job.order_id ?? '') ||
      job.amount !== PRICE || job.currency !== CURRENCY || !text(job.customer_key, 50) || !/^[A-Za-z0-9:_-]{8,100}$/.test(job.idempotency_key ?? '')) throw new Fault('PAYMENT_REVIEW', 409);
    if (job.action === 'reconcile') return await reconcile(job);
    if (job.action !== 'charge') throw new Fault('PAYMENT_REVIEW', 409);
    let key;
    try { key = await unsealBillingKey(job.billing_key_ciphertext, job.user_id, cfg.encryptionKey, crypto); }
    catch { await settle(job, 'review', null, 'BILLING_KEY_UNAVAILABLE'); return 'review'; }
    const permitted = await rpc('billing_validate_charge', { p_invoice_id: job.invoice_id, p_lease_token: job.lease_token });
    if (permitted !== true) {
      // Cancellation can atomically invalidate this lease before dispatch. Its
      // committed state wins; never force another invoice or charge to recover.
      try { await settle(job, 'failed', null, 'CHARGE_NOT_ALLOWED'); } catch { return 'pending'; }
      return 'failed';
    }
    let payment;
    try { payment = await toss(`/v1/billing/${encodeURIComponent(key)}`, { amount: PRICE, customerKey: job.customer_key, orderId: job.order_id, orderName: 'Nephylo Pro 월 구독' }, job.idempotency_key); }
    catch (error) { return await reconcile(job, [400, 403].includes(error.status) && DECLINED.has(error.code) ? error.code : null); }
    if (!validatePayment(payment, job, cfg.mid)) { await settle(job, 'review', null, 'PROVIDER_PAYMENT_MISMATCH'); return 'review'; }
    if (payment.status === 'DONE' && payment.balanceAmount === PRICE && Number.isFinite(Date.parse(payment.approvedAt))) return settledState(await settle(job, 'paid', payment));
    return await reconcile(job);
  }
  async function checkout(identity, body) {
    if (body.consent !== true || body.termsVersion !== cfg.termsVersion) throw new Fault('CONSENT_REQUIRED', 400);
    const state = randomState(crypto), customerKey = crypto.randomUUID();
    let data;
    try { data = await rpc('billing_begin_checkout', { p_user_id: identity.user.id, p_state_hash: await digest(crypto, state), p_customer_key: customerKey, p_terms_version: cfg.termsVersion }); }
    catch (error) { if (error instanceof RemoteFailure && error.code === 'P0001') throw new Fault('ALREADY_SUBSCRIBED', 409); throw error; }
    if (!UUID.test(data?.checkout_id ?? '') || data.customer_key !== customerKey) throw new Fault('SERVER_UNAVAILABLE', 503);
    const callback = flag => { const u = new URL('/account/', cfg.site); u.searchParams.set('billing', flag); u.searchParams.set('checkoutId', data.checkout_id); u.searchParams.set('state', state); return u.href; };
    return { ...publicConfig(), checkoutId: data.checkout_id, state, customerKey, successUrl: callback('success'), failUrl: callback('fail'), expiresAt: data.expires_at };
  }
  async function confirm(identity, body) {
    if (!UUID.test(body.checkoutId ?? '') || !STATE.test(body.state ?? '') || !text(body.customerKey, 50) || !text(body.authKey, 300)) throw new Fault('INVALID_REQUEST', 400);
    const hash = await digest(crypto, body.state);
    let pending;
    try { pending = await rpc('billing_get_checkout', { p_user_id: identity.user.id, p_checkout_id: body.checkoutId, p_state_hash: hash }); }
    catch (error) { if ([400, 403, 404].includes(error.status)) throw new Fault('CHECKOUT_EXPIRED', 409); throw error; }
    if (!pending || pending.customer_key !== body.customerKey || (!pending.invoice_id && !(Date.parse(pending.expires_at) > Date.now()))) throw new Fault('CHECKOUT_EXPIRED', 409);
    let invoiceId = pending.invoice_id;
    if (!invoiceId) {
      let billing;
      try { billing = await toss('/v1/billing/authorizations/issue', { authKey: body.authKey, customerKey: pending.customer_key }, body.checkoutId); }
      catch (error) { throw new Fault(error.status >= 400 && error.status < 500 ? 'PAYMENT_FAILED' : 'BILLING_PENDING', error.status >= 400 && error.status < 500 ? 409 : 503); }
      if (billing.customerKey !== pending.customer_key || billing.mId !== cfg.mid || !text(billing.billingKey, 200)) throw new Fault('PAYMENT_REVIEW', 409);
      const sealed = await sealBillingKey(billing.billingKey, identity.user.id, cfg.encryptionKey, crypto);
      const created = await rpc('billing_complete_checkout', { p_user_id: identity.user.id, p_state_hash: hash, p_customer_key: pending.customer_key, p_billing_key_ciphertext: sealed });
      invoiceId = created?.invoice_id;
    }
    if (!UUID.test(invoiceId ?? '')) throw new Fault('PAYMENT_REVIEW', 409);
    const job = await rpc('billing_claim_invoice', { p_invoice_id: invoiceId });
    const state = job ? await processJob(job) : 'pending';
    const current = await statusFor(identity);
    return { status: invoiceState(state, current.subscription), ...current };
  }
  async function api(request) {
    const requestOrigin = request.headers.get('origin');
    if (requestOrigin && requestOrigin !== cfg.site) return failure(new Fault('FORBIDDEN', 403));
    if (request.method === 'OPTIONS') return response(null, 204, true);
    try {
      const body = await bodyFor(request);
      if (body.action === 'config') return response(publicConfig(), 200, true);
      if (!['status', 'checkout', 'confirm', 'cancel', 'sync'].includes(body.action)) throw new Fault('INVALID_REQUEST', 400);
      const identity = await userFor(request);
      if (body.action === 'status') return response({ ...publicConfig(), ...await statusFor(identity) }, 200, true);
      if (body.action === 'cancel') { await rpc('cancel_my_subscription', {}, identity.token); return response(await statusFor(identity), 200, true); }
      required();
      if (body.action === 'checkout') return response(await checkout(identity, body), 200, true);
      if (body.action === 'confirm') { const result = await confirm(identity, body); return response(result, result.status === 'pending' ? 202 : 200, true); }
      const billing = await rpc('get_my_billing', {}, identity.token);
      const invoice = billing.subscription?.latest_invoice;
      const job = invoice?.id ? await rpc('billing_claim_order', { p_order_id: invoice.order_id, p_payment_key: null }) : null;
      const state = job && !job.busy ? await processJob({ ...job, action: 'reconcile' }) : invoice ? 'pending' : 'none';
      const current = await statusFor(identity);
      const result = invoiceState(state, current.subscription);
      return response({ status: result, ...current }, result === 'pending' ? 202 : 200, true);
    } catch (error) { return failure(error, true); }
  }
  async function webhook(request) {
    try {
      required(); const body = await bodyFor(request, 65536);
      if (!['PAYMENT_STATUS_CHANGED', 'CANCEL_STATUS_CHANGED'].includes(body.eventType) || !plainObject(body.data)) return response({ received: true });
      const order = ORDER.test(body.data.orderId ?? '') ? body.data.orderId : null;
      const payment = text(body.data.paymentKey, 200);
      if (!order && !payment) return response({ received: true });
      const job = await rpc('billing_claim_order', { p_order_id: order, p_payment_key: payment });
      if (!job) return response({ received: true });
      if (job.busy) return response({ received: false }, 503);
      // Ignore all webhook amounts/status/metadata; the signal can only reconcile.
      const state = await processJob({ ...job, action: 'reconcile' });
      if (state === 'pending') return response({ received: false }, 503);
      return response({ received: true });
    } catch (error) { return failure(error); }
  }
  async function renew(request) {
    try {
      required();
      if (!(await secretEqual(crypto, request.headers.get('x-billing-cron-secret'), cfg.cronSecret))) throw new Fault('FORBIDDEN', 403);
      await bodyFor(request);
      // Bounded batches fit the edge runtime, including a provider's 65s charge.
      const claimed = await rpc('billing_claim_due', { p_limit: 1 });
      const jobs = Array.isArray(claimed?.jobs) ? claimed.jobs : [];
      if (jobs.length > 1) throw new Fault('SERVER_UNAVAILABLE', 503);
      let processed = 0;
      for (const job of jobs) { await processJob(job); processed++; }
      return response({ processed });
    } catch (error) { return failure(error); }
  }
  return { api, webhook, renew };
}
