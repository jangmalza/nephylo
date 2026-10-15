import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { configureAuthRateLimits, main, parseArguments, planChanges, validatePolicy } from '../setup/configure-auth-rate-limits.mjs';

const policy = JSON.parse(await readFile(new URL('../setup/auth-rate-limit-policy.v1.json', import.meta.url), 'utf8'));
const ref = 'abcdefghijklmnopqrst';
const token = 'fake-management-token-never-print';
const hidden = 'fixture-private-provider-secret-never-print';
const env = { SUPABASE_ACCESS_TOKEN: token };
const current = { smtp_max_frequency: 30, mailer_otp_exp: 3600, mailer_otp_length: 6,
  rate_limit_otp: 30, rate_limit_verify: 30, rate_limit_email_sent: 200,
  external_google_secret: hidden, smtp_pass: hidden, uri_allow_list: hidden, jwt_exp: 3600 };
const expected = { ...policy.limits };
const patch = { smtp_max_frequency: 60, mailer_otp_exp: 600, rate_limit_otp: 10, rate_limit_verify: 20, rate_limit_email_sent: 100 };
const json = value => new Response(JSON.stringify(value), { status: 200 });
function fixture(responses = [() => json(current), () => json({ smtp_pass: hidden }), () => json({ ...current, ...expected })]) {
  const calls = [];
  const fetcher = async (url, options) => {
    calls.push({ url, ...options });
    const response = responses[calls.length - 1];
    if (!response) throw new Error('Unexpected request containing ' + token);
    return await response();
  };
  return { calls, fetcher };
}
const run = (f, options = {}) => configureAuthRateLimits({ projectRef: ref, policy, env, fetcher: f.fetcher, ...options });
function assertSafe(output) {
  for (const secret of [token, hidden, 'smtp_pass', 'external_google_secret', 'uri_allow_list']) assert.ok(!output.includes(secret), secret);
}

test('dry run makes one GET and returns only selected numeric fields', async () => {
  const f = fixture(); const result = await run(f);
  assert.equal(result.mode, 'dry-run'); assert.equal(result.status, 'planned');
  assert.deepEqual(result.patch, patch); assert.deepEqual(result.expected, expected);
  assert.equal(f.calls.length, 1); assert.equal(f.calls[0].method, 'GET');
  assert.equal(f.calls[0].url, `https://api.supabase.com/v1/projects/${ref}/config/auth`);
  assert.equal(f.calls[0].headers.authorization, `Bearer ${token}`);
  assert.equal(f.calls[0].redirect, 'error'); assert.ok(f.calls[0].signal instanceof AbortSignal);
  assert.equal(f.calls[0].body, undefined); assertSafe(JSON.stringify(result));
});
test('apply performs GET/PATCH/GET with the exact minimal patch and verifies all selected settings', async () => {
  const f = fixture(); const result = await run(f, { apply: true });
  assert.deepEqual(f.calls.map(c => c.method), ['GET', 'PATCH', 'GET']);
  assert.deepEqual(JSON.parse(f.calls[1].body), patch);
  assert.ok(!Object.hasOwn(JSON.parse(f.calls[1].body), 'mailer_otp_length'));
  assert.ok(f.calls.every(c => c.redirect === 'error'));
  assert.equal(result.status, 'applied-config-verified');
  assert.deepEqual(result.verified, expected); assert.equal(result.runtime_enforcement_tested, false);
  assertSafe(JSON.stringify(result));
});
test('all tighter current settings are retained and an empty patch is never sent', async () => {
  const tighter = { smtp_max_frequency: 120, mailer_otp_exp: 300, mailer_otp_length: 6,
    rate_limit_otp: 5, rate_limit_verify: 8, rate_limit_email_sent: 2 };
  const f = fixture([() => json({ ...current, ...tighter })]);
  const result = await run(f, { apply: true });
  assert.deepEqual(result.patch, {}); assert.deepEqual(result.verified, tighter);
  assert.equal(result.status, 'already-compliant'); assert.equal(f.calls.length, 1);
});
test('mixed settings change only the fields requiring tightening', () => {
  const result = planChanges({ ...current, smtp_max_frequency: 90, rate_limit_email_sent: 30, rate_limit_verify: 10 }, policy);
  assert.deepEqual(result.patch, { mailer_otp_exp: 600, rate_limit_otp: 10 });
  assert.equal(result.expected.rate_limit_email_sent, 30);
});
test('policy rejects missing, extra, relaxed, ambiguous and incompatible values', () => {
  const bad = [null, [], {}, { ...policy, schema_version: 2 }, { ...policy, secret: hidden },
    { ...policy, limits: { ...policy.limits, external_google_enabled: false } }];
  for (const [field, value] of [['smtp_max_frequency', 59], ['smtp_max_frequency', 86401], ['mailer_otp_exp', 601],
    ['mailer_otp_length', 8], ['rate_limit_otp', 11], ['rate_limit_verify', 21], ['rate_limit_email_sent', 101],
    ['rate_limit_otp', 0], ['rate_limit_verify', -1], ['mailer_otp_exp', '600'], ['rate_limit_otp', 1.5]])
    bad.push({ ...policy, limits: { ...policy.limits, [field]: value } });
  const missing = structuredClone(policy); delete missing.limits.rate_limit_otp; bad.push(missing);
  for (const value of bad) assert.throws(() => validatePolicy(value), /INVALID_POLICY/);
});
test('invalid policy or project ref makes no network request', async () => {
  for (const projectRef of ['', 'A'.repeat(20), '../secrets', ref + '?x=1', 'https://evil.example', ref + '\n']) {
    const f = fixture(); await assert.rejects(run(f, { projectRef }), /INVALID_ARGUMENTS/); assert.equal(f.calls.length, 0);
  }
  const f = fixture(); await assert.rejects(run(f, { policy: {} }), /INVALID_POLICY/); assert.equal(f.calls.length, 0);
});
test('token is mandatory in environment and header-unsafe values fail before network', async () => {
  for (const value of [undefined, '', 'short', token + '\n', token + ' ']) {
    const f = fixture(); await assert.rejects(run(f, { env: { SUPABASE_ACCESS_TOKEN: value } }), /SUPABASE_ACCESS_TOKEN_REQUIRED/);
    assert.equal(f.calls.length, 0);
  }
});
test('missing, zero, nonnumeric or non-six-digit live values cannot trigger a write', async () => {
  for (const overrides of [{ rate_limit_otp: 0 }, { rate_limit_verify: null }, { mailer_otp_exp: hidden },
    { smtp_max_frequency: 0 }, { rate_limit_email_sent: undefined }, { mailer_otp_length: 8 }]) {
    const f = fixture([() => json({ ...current, ...overrides })]);
    await assert.rejects(run(f, { apply: true }), /UNKNOWN_OR_INVALID_CURRENT_SETTINGS|OTP_LENGTH_REQUIRES_SEPARATE_REVIEW/);
    assert.equal(f.calls.length, 1);
  }
});
test('readback mismatch or missing fields fails instead of claiming deployment success', async () => {
  for (const verified of [{ ...expected, rate_limit_verify: 30 }, { ...expected, rate_limit_email_sent: 2 }, { ...expected, smtp_max_frequency: null }]) {
    const f = fixture([() => json(current), () => json({ secret: hidden }), () => json(verified)]);
    await assert.rejects(run(f, { apply: true }), /READBACK_MISMATCH|UNKNOWN_OR_INVALID_CURRENT_SETTINGS/);
    assert.equal(f.calls.length, 3);
  }
});
test('HTTP failures are sanitized, report their stage, and are never retried', async () => {
  for (const status of [301, 400, 401, 403, 429, 500, 503]) {
    for (const failedIndex of [0, 1, 2]) {
      const responses = [() => json(current), () => json({ secret: hidden }), () => json(expected)];
      responses[failedIndex] = () => new Response(hidden + token, { status });
      const f = fixture(responses); const output = [];
      const exit = await main(['--project-ref', ref, '--apply'], { env, fetcher: f.fetcher, write: value => output.push(value) });
      assert.equal(exit, 1); assert.equal(f.calls.length, failedIndex + 1);
      const result = JSON.parse(output[0]); assert.equal(result.http_status, status);
      assert.equal(result.stage, ['read', 'patch', 'readback'][failedIndex]);
      assert.equal(result.configuration_may_have_changed, failedIndex > 0); assertSafe(output.join('\n'));
    }
  }
});
test('transport errors and malformed response bodies never print raw details or retry', async () => {
  for (const response of [() => { throw Error(hidden + token); }, () => new Response(hidden + token),
    () => new Response('x'.repeat(1048577))]) {
    const f = fixture([response]); const output = [];
    assert.equal(await main(['--project-ref', ref], { env, fetcher: f.fetcher, write: value => output.push(value) }), 1);
    assert.equal(f.calls.length, 1); assertSafe(output.join('\n'));
  }
});
test('CLI defaults dry run, requires explicit apply, rejects ambiguous options and credentials', () => {
  assert.equal(parseArguments(['--project-ref', ref]).apply, false);
  assert.equal(parseArguments(['--project-ref', ref, '--apply']).apply, true);
  for (const args of [['--token', token], ['--apply', '--dry-run'], ['--project-ref'], ['--apply', '--apply'], ['--url', 'https://evil.example']])
    assert.throws(() => parseArguments(args), /INVALID_ARGUMENTS/);
});
test('CLI output omits credentials on success and missing policy errors', async () => {
  const f = fixture(); const output = [];
  assert.equal(await main(['--project-ref', ref, '--apply'], { env, fetcher: f.fetcher, write: value => output.push(value) }), 0);
  assertSafe(output.join('\n'));
  const badOutput = [];
  assert.equal(await main(['--project-ref', ref, '--policy', 'does-not-exist-' + hidden], { env, fetcher: f.fetcher, write: value => badOutput.push(value) }), 1);
  assertSafe(badOutput.join('\n')); assert.equal(f.calls.length, 3);
});
