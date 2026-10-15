import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRetiredBillingHandler } from '../supabase/functions/_shared/free-distribution.mjs';
const original = globalThis.fetch;
let calls = 0, checks = 0;
globalThis.fetch = () => { calls++; throw new Error('Retired billing must never call authentication, storage or a payment provider'); };
try {
  const handler = createRetiredBillingHandler({ SITE_URL: 'https://nephylo.com', BILLING_ENABLED: 'true', INTERNATIONAL_BILLING_ENABLED: 'true' });
  for (const action of ['config', 'checkout', 'confirm', 'renew', 'sync', 'portal']) {
    const result = await handler(new Request('https://fixture.invalid/billing', { method: 'POST', headers: { origin: 'https://nephylo.com', 'content-type': 'application/json' }, body: JSON.stringify({ action }) }));
    assert.equal(result.status, 410); assert.equal((await result.json()).enabled, false); checks += 2;
  }
  assert.equal((await handler(new Request('https://fixture.invalid', { method: 'OPTIONS', headers: { origin: 'https://nephylo.com' } }))).status, 204); checks++;
  assert.equal((await handler(new Request('https://fixture.invalid', { headers: { origin: 'https://untrusted.invalid' } }))).status, 403); checks++;
  assert.equal(calls, 0); checks++;
  for (const entry of ['billing-api', 'billing-renew', 'international-billing-api', 'international-billing-reconcile']) {
    const source = await readFile(new URL(`../supabase/functions/${entry}/index.ts`, import.meta.url), 'utf8');
    assert.match(source, /Deno\.serve\(createRetiredBillingHandler\(Deno\.env\.toObject\(\)\)\)/);
    assert.doesNotMatch(source, /create(?:International)?BillingHandlers/); checks += 2;
  }
  console.log(`Free distribution: ${checks} checks passed. No auth/database/provider calls.`);
} finally { globalThis.fetch = original; }
