import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { webcrypto } from 'node:crypto';
import { PGlite } from '@electric-sql/pglite';
import { createBillingHandlers } from '../supabase/functions/_shared/billing-core.mjs';

// The real migrations run in isolated Postgres WASM. Only Auth HTTP and Toss HTTP
// are fakes; Edge -> RPC parameters, privileges, leases, and settlement are real.
const db = new PGlite();
const site = 'https://widget.example', supabase = 'https://test.supabase.co';
const ids = ['cb220718-12f7-4197-86a4-d7271d2b8201', 'cb220718-12f7-4197-86a4-d7271d2b8202', 'cb220718-12f7-4197-86a4-d7271d2b8203', 'cb220718-12f7-4197-86a4-d7271d2b8204'];
let identity = ids[0], checks = 0, providerStatus = 'DONE', timeoutCharge = false, missingOrder = false, cancelAtDispatch = false, eventDuringCharge = true, rejectCode = null;
const orders = new Map(), customers = new Map(), charges = [], providerCalls = [];
function check(value, message) { assert.ok(value, message); checks++; }
const env = { BILLING_ENABLED: 'true', SITE_URL: site, SUPABASE_URL: supabase, SUPABASE_ANON_KEY: 'public-key', SUPABASE_SERVICE_ROLE_KEY: 'service-key',
  TOSS_CLIENT_KEY: 'test_ck_abcdefgh12345678', TOSS_SECRET_KEY: 'test_sk_abcdefgh12345678', TOSS_MID: 'testmid',
  BILLING_ENCRYPTION_KEY: Buffer.alloc(32, 44).toString('base64'), BILLING_CRON_SECRET: 'cron-secret-12345678901234567890123456',
  BILLING_TERMS_URL: site + '/terms/', BILLING_PRIVACY_URL: site + '/privacy/', BILLING_REFUND_URL: site + '/refund/', BILLING_TERMS_VERSION: 'v1' };
const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });
async function rpc(name, body, user = identity, service = false) {
  const entries = Object.entries(body); assert.match(name, /^[a-z_]+$/); for (const [key] of entries) assert.match(key, /^p_[a-z_]+$/);
  await db.exec('begin');
  try {
    await db.exec(`set local role ${service ? 'service_role' : 'authenticated'}`);
    await db.query("select set_config('request.jwt.claim.sub',$1,true)", [service ? '' : user]);
    const result = await db.query(`select public.${name}(${entries.map(([key], i) => `${key} => $${i + 1}`).join(',')}) as value`, entries.map(([, value]) => value));
    await db.exec('commit'); return result.rows[0].value;
  } catch (error) { await db.exec('rollback'); throw error; }
}
const fetch = async (url, options = {}) => {
  const u = new URL(url), headers = new Headers(options.headers), body = options.body ? JSON.parse(options.body) : {};
  assert.equal(options.redirect, 'error');
  if (u.origin === supabase) {
    if (u.pathname === '/auth/v1/user') return json({ id: identity, email: `${identity}@example.test`, email_confirmed_at: new Date().toISOString() });
    const name = u.pathname.split('/').pop();
    if (name === 'billing_validate_charge' && cancelAtDispatch) { cancelAtDispatch = false; await rpc('cancel_my_subscription', {}); }
    try { return json(await rpc(name, body, identity, headers.get('authorization') === 'Bearer service-key')); }
    catch (error) { return json({ code: error.code, message: 'private SQL error' }, error.code === '42501' ? 403 : 400); }
  }
  assert.equal(u.origin, 'https://api.tosspayments.com'); providerCalls.push(u.pathname);
  if (u.pathname === '/v1/billing/authorizations/issue') {
    const key = 'billing-' + body.customerKey; customers.set(key, body.customerKey);
    return json({ billingKey: key, customerKey: body.customerKey, mId: 'testmid' });
  }
  if (u.pathname.startsWith('/v1/billing/')) {
    const key = decodeURIComponent(u.pathname.slice('/v1/billing/'.length));
    assert.equal(customers.get(key), body.customerKey); assert.equal(body.amount, 3000); assert.match(headers.get('idempotency-key'), /^uw:[a-f0-9-]{36}$/);
    if (rejectCode) return json({ code: rejectCode }, 403);
    charges.push(body.orderId);
    const payment = { orderId: body.orderId, paymentKey: 'pay_' + body.orderId, totalAmount: 3000, currency: 'KRW', type: 'BILLING', mId: 'testmid',
      status: 'DONE', approvedAt: new Date().toISOString(), balanceAmount: 3000 };
    orders.set(body.orderId, payment);
    if (eventDuringCharge) {
      eventDuringCharge = false;
      const event = await handlers.webhook(new Request(supabase + '/functions/v1/billing-webhook', { method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ eventType: 'PAYMENT_STATUS_CHANGED', data: { orderId: body.orderId, paymentKey: payment.paymentKey, status: 'CANCELED' } }) }));
      check(event.status === 503, 'real known invoice with in-flight lease and unset payment key requests webhook retry');
    }
    if (timeoutCharge) throw new Error('network interruption');
    return json(payment);
  }
  if (u.pathname.startsWith('/v1/payments/orders/')) {
    const order = decodeURIComponent(u.pathname.slice('/v1/payments/orders/'.length));
    if (missingOrder || !orders.has(order)) return json({ code: 'NOT_FOUND_PAYMENT' }, 404);
    return json({ ...orders.get(order), status: providerStatus, balanceAmount: providerStatus === 'CANCELED' ? 0 : providerStatus === 'PARTIAL_CANCELED' ? 1500 : 3000 });
  }
  throw Error('Unknown provider request');
};
const handlers = createBillingHandlers({ env, fetch, crypto: webcrypto });
async function api(body) {
  const response = await handlers.api(new Request(supabase + '/functions/v1/billing-api', { method: 'POST', headers: { 'content-type': 'application/json', origin: site, authorization: 'Bearer verified-user-token' }, body: JSON.stringify(body) }));
  return { status: response.status, body: await response.json() };
}
async function start() { const result = await api({ action: 'checkout', consent: true, termsVersion: 'v1' }); assert.equal(result.status, 200, JSON.stringify(result.body)); return result.body; }
async function confirm(checkout) { return await api({ action: 'confirm', checkoutId: checkout.checkoutId, state: checkout.state, customerKey: checkout.customerKey, authKey: 'verified-auth-key' }); }
try {
  for (const file of ['tests/bootstrap.sql', 'migrations/202610110001_membership.sql', 'migrations/202610110002_billing.sql', 'migrations/202610110003_admin_entitlement_sources.sql', 'migrations/202610110004_international_billing.sql']) await db.exec(await readFile(new URL('../' + file, import.meta.url), 'utf8'));
  for (const id of ids) await db.query('insert into auth.users(id,email,email_confirmed_at) values($1,$2,now())', [id, `${id}@example.test`]);
  let result = await api({ action: 'status' }); check(result.status === 200 && result.body.subscription === null, 'real member RPC initially has no billing subscription');
  let checkout = await start(); result = await confirm(checkout);
  check(result.status === 200 && result.body.status === 'active' && result.body.membership.plan === 'pro', 'full Edge/Auth/Postgres/Toss fake path activates Pro');
  check(charges.length === 1, 'first subscription issues one provider payment');
  result = await confirm(checkout); check(result.body.status === 'active' && charges.length === 1, 'real SQL deduplicates callback replay');
  const storage = (await db.query('select billing_key_ciphertext from usage_widget_private.billing_subscriptions where user_id=$1', [identity])).rows[0];
  check(storage.billing_key_ciphertext.startsWith('v1.') && !storage.billing_key_ciphertext.includes('billing-'), 'real DB contains sealed billing key only');
  const consent = (await db.query('select terms_version,consent_at from usage_widget_private.billing_checkout_intents where user_id=$1', [identity])).rows[0];
  check(consent.terms_version === 'v1' && consent.consent_at, 'real DB records agreed version and timestamp');
  result = await api({ action: 'cancel' }); check(result.body.subscription.cancel_at_period_end && result.body.membership.plan === 'pro', 'cancel keeps paid entitlement through period end');
  providerStatus = 'CANCELED';
  let event = await handlers.webhook(new Request(supabase + '/functions/v1/billing-webhook', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ eventType: 'PAYMENT_STATUS_CHANGED', data: { orderId: charges[0], paymentKey: 'pay_' + charges[0], status: 'DONE' } }) }));
  check(event.status === 200 && (await api({ action: 'status' })).body.membership.plan === 'free', 'provider-verified refund removes real paid entitlement despite forged payload status');
  providerStatus = 'DONE'; result = await api({ action: 'sync' });
  check(result.body.status === 'refunded' && result.body.membership.plan === 'free', 'stale provider DONE cannot falsely report reactivation after refund');
  identity = ids[1]; providerStatus = 'DONE'; timeoutCharge = true; missingOrder = true;
  checkout = await start(); result = await confirm(checkout);
  check(result.status === 202 && result.body.status === 'pending' && result.body.membership.plan === 'free', 'unknown charge remains pending in actual SQL');
  result = await confirm(checkout); check(result.status === 202 && charges.length === 2, 'unknown real invoice is never recharged');
  missingOrder = false; result = await api({ action: 'sync' });
  check(result.body.status === 'active' && result.body.membership.plan === 'pro' && charges.length === 2, 'sync reconciles original order into one paid period');
  identity = ids[2]; timeoutCharge = false; cancelAtDispatch = true;
  checkout = await start(); result = await confirm(checkout);
  check(charges.length === 2 && result.body.status === 'failed' && result.body.membership.plan === 'free', 'real cancellation/lease race prevents dispatch and returns canceled invoice result');
  check(!JSON.stringify(result).includes('service-key') && !JSON.stringify(result).includes('billing_key'), 'actual API status never leaks server credentials');
  identity = ids[3]; rejectCode = 'REJECT_CARD_PAYMENT'; missingOrder = true;
  checkout = await start(); result = await confirm(checkout);
  check(result.body.status === 'failed' && result.body.subscription.status === 'past_due', 'definitive declined absent payment becomes a recoverable failed subscription');
  rejectCode = null; missingOrder = false;
  checkout = await start(); result = await confirm(checkout);
  check(result.body.status === 'active' && result.body.membership.plan === 'pro' && charges.length === 3, 'new explicit card registration after definitive decline can complete subscription');
  console.log(`PASS: ${checks} billing Edge + real Postgres integration checks (fake provider HTTP only)`);
} finally { await db.close(); }
