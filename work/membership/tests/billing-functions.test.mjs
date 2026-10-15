import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { createBillingHandlers, readBillingConfig, sealBillingKey, unsealBillingKey, validatePayment } from '../supabase/functions/_shared/billing-core.mjs';

const UID = 'ab220718-12f7-4197-86a4-d7271d2b8201';
const OTHER = 'bb220718-12f7-4197-86a4-d7271d2b8201';
const CHECKOUT = 'ab220718-12f7-4197-86a4-d7271d2b8202';
const INVOICE = 'ab220718-12f7-4197-86a4-d7271d2b8203';
const LEASE = 'ab220718-12f7-4197-86a4-d7271d2b8204';
const ORDER = 'uw_test_server_order_20261011';
const TOKEN = 'user-access-token';
const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });
let checks = 0;
function check(value, description) { assert.ok(value, description); checks++; }
const baseEnv = {
  BILLING_ENABLED: 'true', SITE_URL: 'https://widget.example', SUPABASE_URL: 'https://test.supabase.co',
  SUPABASE_ANON_KEY: 'public-anon-key', SUPABASE_SERVICE_ROLE_KEY: 'server-service-secret',
  TOSS_CLIENT_KEY: 'test_ck_abcdefgh12345678', TOSS_SECRET_KEY: 'test_sk_serversecret12345678', TOSS_MID: 'testmid',
  BILLING_ENCRYPTION_KEY: Buffer.alloc(32, 22).toString('base64'), BILLING_CRON_SECRET: 'scheduler-secret-12345678901234567890123456',
  BILLING_TERMS_URL: 'https://widget.example/terms/', BILLING_PRIVACY_URL: 'https://widget.example/privacy/',
  BILLING_REFUND_URL: 'https://widget.example/refund/', BILLING_TERMS_VERSION: '2026-10-11',
};
function fixture(overrides = {}) {
  const calls = [], settlements = [];
  const state = { plan: 'free', subscription: null, pending: null, job: null, providerStatus: 'DONE', rejectCharge: false,
    timeoutCharge: false, queryMissing: false, paymentMismatch: false, badBillingCustomer: false, unverified: false, banned: false, ...overrides };
  const env = { ...baseEnv, ...overrides.env };
  function member() { return { user_id: UID, email: 'member@example.com', plan: state.plan, status: state.plan === 'pro' ? 'active' : 'free', is_admin: false }; }
  function payment() {
    return { orderId: ORDER, paymentKey: 'provider-payment-key', totalAmount: state.paymentMismatch ? 1 : 3000,
      currency: 'KRW', type: 'BILLING', mId: 'testmid', status: state.providerStatus, approvedAt: new Date().toISOString(),
      balanceAmount: state.providerStatus === 'CANCELED' ? 0 : state.providerStatus === 'PARTIAL_CANCELED' ? 1500 : 3000 };
  }
  const fetch = async (url, options = {}) => {
    const u = new URL(url), body = options.body ? JSON.parse(options.body) : null;
    const headers = new Headers(options.headers); calls.push({ url: u.href, path: u.pathname, method: options.method ?? 'GET', body, headers });
    assert.equal(options.redirect, 'error', 'provider redirects are never followed');
    assert.ok(options.signal instanceof AbortSignal, 'network request is bounded');
    assert.ok(['https://test.supabase.co', 'https://api.tosspayments.com'].includes(u.origin), 'only fixed configured/provider origins');
    if (u.origin === 'https://test.supabase.co') {
      if (u.pathname === '/auth/v1/user') {
        if (headers.get('authorization') !== `Bearer ${TOKEN}`) return json({}, 401);
        return json({ id: UID, email: 'member@example.com', email_confirmed_at: state.unverified ? null : '2026-10-11T00:00:00Z', ...(state.banned ? { banned_until: '2099-01-01T00:00:00Z' } : {}) });
      }
      const name = u.pathname.split('/').pop();
      if (['get_my_membership', 'get_my_billing', 'cancel_my_subscription'].includes(name)) assert.equal(headers.get('authorization'), `Bearer ${TOKEN}`);
      else assert.equal(headers.get('authorization'), 'Bearer server-service-secret');
      if (name === 'get_my_membership') return json(member());
      if (name === 'get_my_billing') return json({ amount: 3000, currency: 'KRW', interval: 'month', subscription: state.subscription });
      if (name === 'cancel_my_subscription') { if (state.subscription) { state.subscription.auto_renew = false; state.subscription.cancel_at_period_end = true; } return json({ subscription: state.subscription }); }
      if (name === 'billing_begin_checkout') {
        assert.equal(body.p_user_id, UID); assert.match(body.p_state_hash, /^[0-9a-f]{64}$/); assert.equal(body.p_terms_version, env.BILLING_TERMS_VERSION);
        state.pending = { checkout_id: CHECKOUT, customer_key: body.p_customer_key, state_hash: body.p_state_hash, expires_at: new Date(Date.now() + 1800000).toISOString(), status: 'pending', invoice_id: null };
        return json(state.pending);
      }
      if (name === 'billing_get_checkout') {
        if (!state.pending || body.p_user_id !== UID || body.p_checkout_id !== CHECKOUT || body.p_state_hash !== state.pending.state_hash) return json({}, 403);
        return json(state.pending);
      }
      if (name === 'billing_complete_checkout') {
        assert.equal(body.p_user_id, UID); assert.equal(body.p_state_hash, state.pending.state_hash); assert.equal(body.p_customer_key, state.pending.customer_key);
        state.pending.invoice_id = INVOICE;
        state.subscription = { id: CHECKOUT, status: 'pending', auto_renew: true, latest_invoice: { id: INVOICE, order_id: ORDER, status: 'pending' } };
        state.job = { invoice_id: INVOICE, subscription_id: CHECKOUT, user_id: UID, order_id: ORDER, idempotency_key: `uw:${INVOICE}`,
          customer_key: state.pending.customer_key, billing_key_ciphertext: body.p_billing_key_ciphertext, amount: 3000, currency: 'KRW',
          action: 'charge', lease_token: LEASE, payment_key: null };
        return json({ invoice_id: INVOICE });
      }
      if (name === 'billing_claim_invoice') {
        if (state.subscription?.latest_invoice.status === 'paid') return json(null);
        const job = { ...state.job }; if (['unknown', 'charging'].includes(state.subscription.latest_invoice.status)) job.action = 'reconcile';
        state.subscription.latest_invoice.status = 'charging'; return json(job);
      }
      if (name === 'billing_claim_order') {
        if (!state.job || (body.p_order_id && body.p_order_id !== ORDER) || (body.p_payment_key && body.p_payment_key !== 'provider-payment-key')) return json(null);
        if (state.leaseBusy) return json({ busy: true });
        return json({ ...state.job, action: 'reconcile' });
      }
      if (name === 'billing_claim_due') { assert.equal(body.p_limit, 1); return json({ jobs: state.job ? [state.job] : [] }); }
      if (name === 'billing_validate_charge') return json(!state.rejectCharge);
      if (name === 'billing_settle_invoice') {
        assert.equal(body.p_invoice_id, INVOICE); assert.equal(body.p_lease_token, LEASE); settlements.push(body.p_result);
        const outcome = body.p_result.outcome;
        if (outcome === 'paid') { state.plan = 'pro'; state.subscription.status = 'active'; state.subscription.latest_invoice.status = 'paid'; state.job.payment_key = 'provider-payment-key'; }
        else { state.subscription.latest_invoice.status = outcome; if (['refunded', 'partial_refund'].includes(outcome)) state.plan = 'free'; }
        return json({ status: outcome, subscription_id: CHECKOUT });
      }
      throw Error(`Unexpected RPC ${name}`);
    }
    assert.equal(headers.get('authorization'), `Basic ${btoa(`${env.TOSS_SECRET_KEY}:`)}`);
    if (u.pathname === '/v1/billing/authorizations/issue') {
      assert.equal(headers.get('idempotency-key'), CHECKOUT);
      return json({ billingKey: 'sensitive-billing-key', customerKey: state.badBillingCustomer ? 'other-customer' : state.pending.customer_key, mId: 'testmid' });
    }
    if (u.pathname === '/v1/billing/sensitive-billing-key') {
      assert.equal(body.amount, 3000); assert.equal(body.orderId, ORDER); assert.equal(body.customerKey, state.pending.customer_key);
      assert.equal(headers.get('idempotency-key'), `uw:${INVOICE}`);
      if (state.rejectCode) return json({ code: state.rejectCode, message: 'provider private rejection details' }, 403);
      if (state.timeoutCharge) throw Error('Simulated timeout; secret must not leak');
      return json(payment());
    }
    if (u.pathname === `/v1/payments/orders/${ORDER}`) return state.queryMissing ? json({ code: 'NOT_FOUND_PAYMENT', message: 'private-provider-text' }, 404) : json(payment());
    throw Error(`Unexpected provider path ${u.pathname}`);
  };
  const handlers = createBillingHandlers({ env, fetch, crypto: webcrypto });
  async function api(body, extraHeaders = {}) {
    const result = await handlers.api(new Request('https://test.supabase.co/functions/v1/billing-api', { method: 'POST', headers: { origin: env.SITE_URL, 'content-type': 'application/json', authorization: `Bearer ${TOKEN}`, ...extraHeaders }, body: JSON.stringify(body) }));
    return { status: result.status, data: await result.json(), headers: result.headers };
  }
  async function start() { const created = await api({ action: 'checkout', consent: true, termsVersion: env.BILLING_TERMS_VERSION, amount: 1, userId: OTHER }); assert.equal(created.status, 200); return created.data; }
  async function confirm(created) { return await api({ action: 'confirm', checkoutId: created.checkoutId, state: created.state, customerKey: created.customerKey, authKey: 'provider-auth-key', amount: 1, userId: OTHER }); }
  async function webhook(body) { const result = await handlers.webhook(new Request('https://test.supabase.co/functions/v1/billing-webhook', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })); return { status: result.status, data: await result.json() }; }
  return { api, start, confirm, webhook, handlers, calls, settlements, state, env };
}

check(!readBillingConfig({}).enabled, 'billing is disabled by default');
check(readBillingConfig(baseEnv).enabled, 'matching complete configuration enables billing');
for (const changed of [{ BILLING_ENABLED: 'false' }, { TOSS_SECRET_KEY: 'live_sk_abcdefgh12345678' }, { BILLING_ENCRYPTION_KEY: 'not-key' }, { SITE_URL: 'http://unsafe.example' }, { BILLING_TERMS_URL: '' }, { BILLING_PRIVACY_URL: '' }, { BILLING_REFUND_URL: '' }, { BILLING_TERMS_VERSION: '' }, { BILLING_CRON_SECRET: 'short' }, { SUPABASE_URL: 'https://user:pass@unsafe.example' }])
  check(!readBillingConfig({ ...baseEnv, ...changed }).enabled, 'incomplete or mixed configuration remains disabled');

const encrypted = await sealBillingKey('sensitive-billing-key', UID, Buffer.alloc(32, 22), webcrypto);
check(!encrypted.includes('sensitive-billing-key') && encrypted.startsWith('v1.'), 'billing key is AES-GCM sealed');
check(await unsealBillingKey(encrypted, UID, Buffer.alloc(32, 22), webcrypto) === 'sensitive-billing-key', 'sealed key decrypts for same owner');
await assert.rejects(() => unsealBillingKey(encrypted, OTHER, Buffer.alloc(32, 22), webcrypto)); checks++;
await assert.rejects(() => unsealBillingKey(encrypted.slice(0, -3) + 'AAA', UID, Buffer.alloc(32, 22), webcrypto)); checks++;

let f = fixture({ env: { BILLING_ENABLED: 'false' } });
let result = await f.api({ action: 'config' });
check(result.status === 200 && result.data.enabled === false && f.calls.length === 0, 'unconfigured config does not call providers');
check(!JSON.stringify(result.data).includes('secret') && !result.data.clientKey, 'disabled config exposes no keys');
result = await f.api({ action: 'checkout', consent: true, termsVersion: '2026-10-11' });
check(result.status === 503 && !f.calls.some(c => c.url.includes('tosspayments')), 'disabled billing never charges');
f = fixture();
result = await f.api({ action: 'checkout' }, { origin: 'https://evil.example' });
check(result.status === 403 && f.calls.length === 0 && !result.headers.get('access-control-allow-origin'), 'cross-origin requests fail before auth');
result = await f.api({ action: 'checkout' }, { authorization: '' });
check(result.status === 401 && f.calls.length === 0, 'missing auth token never reaches server');
f = fixture({ unverified: true }); result = await f.api({ action: 'checkout' });
check(result.status === 403 && f.calls.length === 1, 'unverified email cannot create checkout');
f = fixture({ banned: true }); result = await f.api({ action: 'checkout' });
check(result.status === 403, 'banned account cannot create checkout');
f = fixture(); result = await f.api({ action: 'checkout', consent: false, termsVersion: f.env.BILLING_TERMS_VERSION });
check(result.status === 400 && result.data.error.code === 'CONSENT_REQUIRED', 'explicit current consent is required');
result = await f.api({ action: 'checkout', consent: true, termsVersion: 'outdated' });
check(result.status === 400, 'outdated terms cannot authorize payment');
let created = await f.start();
check(created.amount === 3000 && created.currency === 'KRW' && new URL(created.successUrl).origin === baseEnv.SITE_URL && new URL(created.successUrl).pathname === '/account/', 'server price and fixed callback ignore client values');
check(created.state.length === 43 && created.customerKey !== UID && created.customerKey !== OTHER, 'nonce and customer ID are random');
check(f.state.pending.state_hash !== created.state && f.state.pending.state_hash.length === 64, 'only nonce digest is sent to database');
result = await f.api({ action: 'confirm', checkoutId: created.checkoutId, state: created.state.replace(/^./, created.state[0] === 'A' ? 'B' : 'A'), customerKey: created.customerKey, authKey: 'auth-key' });
check(result.status === 409 && !f.calls.some(c => c.url.includes('tosspayments')), 'bad nonce rejected before billing key issuance');
result = await f.confirm(created);
check(result.status === 200 && result.data.status === 'active' && result.data.membership.plan === 'pro', 'verified paid payment activates membership');
check(f.settlements[0].outcome === 'paid' && !JSON.stringify(f.settlements).includes('sensitive-billing-key'), 'settlement stores minimal verified payment without billing key');
check(!JSON.stringify(result.data).includes('sensitive-billing-key') && !JSON.stringify(result.data).includes('serversecret'), 'client response never exposes payment secrets');
const charged = f.calls.filter(c => c.path === '/v1/billing/sensitive-billing-key').length;
await f.confirm(created);
check(f.calls.filter(c => c.path === '/v1/billing/sensitive-billing-key').length === charged, 'confirm replay cannot create second charge');

f = fixture({ timeoutCharge: true }); created = await f.start(); result = await f.confirm(created);
check(result.data.status === 'active' && f.calls.some(c => c.method === 'GET' && c.path.includes('/payments/orders/')), 'ambiguous charge timeout reconciles known order before settlement');
f = fixture({ timeoutCharge: true, queryMissing: true }); created = await f.start(); result = await f.confirm(created);
check(result.status === 202 && result.data.status === 'pending' && f.state.plan === 'free', 'unknown provider result remains pending without Pro');
await f.confirm(created);
check(f.calls.filter(c => c.path === '/v1/billing/sensitive-billing-key').length === 1, 'unknown invoice is reconciled, never recharged');
f.state.queryMissing = false;
result = await f.api({ action: 'sync', orderId: 'attacker-order' });
check(result.data.status === 'active' && f.calls.filter(c => c.path === '/v1/billing/sensitive-billing-key').length === 1, 'sync finds existing server invoice and never charges');
f = fixture({ plan: 'pro', timeoutCharge: true, queryMissing: true }); created = await f.start(); result = await f.confirm(created);
check(result.status === 202 && result.data.status === 'pending', 'manual Pro membership cannot pretend unresolved payment completed');
f = fixture(); result = await f.api({ action: 'sync' });
check(result.status === 200 && result.data.status === 'none', 'sync without subscription does not claim payment is pending');

f = fixture({ paymentMismatch: true }); created = await f.start(); result = await f.confirm(created);
check(result.data.status === 'review' && f.state.plan === 'free' && f.settlements.at(-1).outcome === 'review', 'wrong provider amount never grants Pro');
f = fixture({ badBillingCustomer: true }); created = await f.start(); result = await f.confirm(created);
check(result.status === 409 && !f.calls.some(c => c.path === '/v1/billing/sensitive-billing-key'), 'different customer billing key never charged');
f = fixture({ rejectCharge: true }); created = await f.start(); result = await f.confirm(created);
check(result.data.status === 'failed' && !f.calls.some(c => c.path === '/v1/billing/sensitive-billing-key'), 'cancellation or ban before dispatch prevents renewal charge');
f = fixture({ rejectCode: 'REJECT_CARD_PAYMENT', queryMissing: true }); created = await f.start(); result = await f.confirm(created);
check(result.data.status === 'failed' && f.state.plan === 'free' && f.settlements.at(-1).outcome === 'failed', 'definitive card decline plus missing order permits a later explicit retry');
f = fixture({ rejectCode: 'DUPLICATED_ORDER_ID', queryMissing: true }); created = await f.start(); result = await f.confirm(created);
check(result.status === 202 && result.data.status === 'pending', 'duplicate or unknown provider errors never authorize a fresh payment');
f = fixture({ rejectCode: 'REJECT_CARD_PAYMENT' }); created = await f.start(); result = await f.confirm(created);
check(result.data.status === 'active', 'verified existing successful order overrides rejection response');

f = fixture();
result = await f.webhook({ eventType: 'PAYMENT_STATUS_CHANGED', data: { orderId: 'uw_unknown_order', paymentKey: 'fake', status: 'DONE', totalAmount: 3000 } });
check(result.status === 200 && f.calls.length === 1 && !f.calls.some(c => c.url.includes('tosspayments')), 'unknown spoofed webhook cannot touch provider or entitlement');
created = await f.start(); await f.confirm(created); f.state.providerStatus = 'CANCELED';
result = await f.webhook({ eventType: 'PAYMENT_STATUS_CHANGED', data: { orderId: ORDER, paymentKey: 'provider-payment-key', status: 'DONE', totalAmount: 3000 } });
check(result.status === 200 && f.state.plan === 'free' && f.settlements.at(-1).outcome === 'refunded', 'webhook trusts provider re-query instead of forged DONE status');
check(f.calls.filter(c => c.path === '/v1/billing/sensitive-billing-key').length === 1, 'webhooks never charge');
f.state.leaseBusy = true;
result = await f.webhook({ eventType: 'PAYMENT_STATUS_CHANGED', data: { orderId: ORDER, paymentKey: 'provider-payment-key' } });
check(result.status === 503, 'busy known invoice requests webhook retry instead of losing event');
f.state.leaseBusy = false;
f.state.providerStatus = 'PARTIAL_CANCELED';
await f.webhook({ eventType: 'CANCEL_STATUS_CHANGED', data: { paymentKey: 'provider-payment-key' } });
check(f.settlements.at(-1).outcome === 'partial_refund', 'payment-key-only webhook reconciles partial refunds for review');
await f.api({ action: 'cancel' });
check(f.state.subscription.cancel_at_period_end && !f.state.subscription.auto_renew, 'cancel stops future renewal');

f = fixture();
let renewal = await f.handlers.renew(new Request('https://test.supabase.co/functions/v1/billing-renew', { method: 'POST', headers: { 'content-type': 'application/json', 'x-billing-cron-secret': 'wrong' }, body: '{}' }));
check(renewal.status === 403 && f.calls.length === 0, 'invalid scheduler secret cannot claim work');
renewal = await f.handlers.renew(new Request('https://test.supabase.co/functions/v1/billing-renew', { method: 'POST', headers: { 'content-type': 'application/json', 'x-billing-cron-secret': baseEnv.BILLING_CRON_SECRET }, body: '{}' }));
check(renewal.status === 200 && (await renewal.json()).processed === 0, 'authorized worker claims bounded server jobs');

const job = { order_id: ORDER, customer_key: 'server-customer', payment_key: 'server-payment-key' };
const paid = { orderId: ORDER, paymentKey: 'server-payment-key', totalAmount: 3000, currency: 'KRW', type: 'BILLING', mId: 'testmid', balanceAmount: 3000 };
check(validatePayment(paid, job, 'testmid'), 'documented Payment without customerKey is valid');
for (const changed of [{ type: 'NORMAL' }, { currency: 'USD' }, { mId: 'other' }, { paymentKey: 'other' }, { orderId: 'other-order' }, { customerKey: 'other-customer' }, { balanceAmount: -1 }])
  check(!validatePayment({ ...paid, ...changed }, job, 'testmid'), 'mismatched provider identity/amount details rejected');
console.log(`PASS: ${checks} billing function checks (fake HTTP only; no provider calls)`);
