import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const DEFAULT_POLICY = new URL('./auth-rate-limit-policy.v1.json', import.meta.url);
const FIELDS = Object.freeze(['smtp_max_frequency', 'mailer_otp_exp', 'mailer_otp_length',
  'rate_limit_otp', 'rate_limit_verify', 'rate_limit_email_sent']);
const CAPS = Object.freeze({ mailer_otp_exp: 600, rate_limit_otp: 10, rate_limit_verify: 20, rate_limit_email_sent: 100 });
const UNITS = Object.freeze({ smtp_max_frequency: 'seconds between messages to the same user',
  mailer_otp_exp: 'seconds', mailer_otp_length: 'digits, preserved without patching',
  rate_limit_otp: 'requests per 5 minutes per IP per endpoint bucket; burst 30',
  rate_limit_verify: 'requests per 5 minutes per IP; burst 30',
  rate_limit_email_sent: 'Auth emails per hour for the project' });

class SafeFailure extends Error {
  constructor(code, stage = 'validation', httpStatus) {
    super(code); this.code = code; this.stage = stage;
    if (Number.isInteger(httpStatus)) this.httpStatus = httpStatus;
  }
}
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const integer = value => Number.isSafeInteger(value) && value > 0;
function exactKeys(value, keys) {
  return object(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}
export function validatePolicy(value) {
  if (!exactKeys(value, ['schema_version', 'limits']) || value.schema_version !== 1 || !exactKeys(value.limits, FIELDS))
    throw new SafeFailure('INVALID_POLICY');
  const limits = value.limits;
  if (!FIELDS.every(key => integer(limits[key])) || limits.smtp_max_frequency < 60 || limits.smtp_max_frequency > 86400
    || limits.mailer_otp_length !== 6 || Object.entries(CAPS).some(([key, cap]) => limits[key] > cap))
    throw new SafeFailure('INVALID_POLICY');
  return Object.fromEntries(FIELDS.map(key => [key, limits[key]]));
}
function selectedConfig(value, stage) {
  if (!object(value) || !FIELDS.every(key => Object.hasOwn(value, key) && integer(value[key])))
    throw new SafeFailure('UNKNOWN_OR_INVALID_CURRENT_SETTINGS', stage);
  // Changing OTP length can break the existing six-digit clients; never shorten it.
  if (value.mailer_otp_length !== 6) throw new SafeFailure('OTP_LENGTH_REQUIRES_SEPARATE_REVIEW', stage);
  return Object.fromEntries(FIELDS.map(key => [key, value[key]]));
}
export function planChanges(current, policy) {
  const limits = validatePolicy(policy), before = selectedConfig(current, 'read');
  const expected = { ...before }, patch = {};
  for (const field of FIELDS) {
    if (field === 'mailer_otp_length') continue;
    expected[field] = field === 'smtp_max_frequency' ? Math.max(before[field], limits[field]) : Math.min(before[field], limits[field]);
    if (expected[field] !== before[field]) patch[field] = expected[field];
  }
  return { before, expected, patch };
}
async function discard(response) {
  try { await response.body?.cancel(); } catch { /* Never expose response or transport details. */ }
}
async function boundedJson(response, stage) {
  const reader = response.body?.getReader();
  if (!reader) throw new SafeFailure('INVALID_API_RESPONSE', stage);
  const chunks = []; let count = 0;
  try {
    for (;;) {
      const item = await reader.read(); if (item.done) break;
      count += item.value.length;
      if (count > 1048576) throw new SafeFailure('INVALID_API_RESPONSE', stage);
      chunks.push(item.value);
    }
    const bytes = new Uint8Array(count); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch (error) {
    if (error instanceof SafeFailure) throw error;
    throw new SafeFailure('INVALID_API_RESPONSE', stage);
  } finally { try { await reader.cancel(); } catch { /* No raw errors. */ } }
}

// Native Auth limits also apply to direct public Auth requests. This is not a proxy limiter.
export async function configureAuthRateLimits({ projectRef, policy, apply = false, env = process.env, fetcher = globalThis.fetch }) {
  if (typeof projectRef !== 'string' || !/^[a-z]{20}$/.test(projectRef) || typeof apply !== 'boolean')
    throw new SafeFailure('INVALID_ARGUMENTS');
  validatePolicy(policy);
  const token = env.SUPABASE_ACCESS_TOKEN;
  if (typeof token !== 'string' || token.length < 16 || token.length > 8192 || !/^[\x21-\x7e]+$/.test(token))
    throw new SafeFailure('SUPABASE_ACCESS_TOKEN_REQUIRED');
  const url = `https://api.supabase.com/v1/projects/${projectRef}/config/auth`;
  async function request(method, stage, patch) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetcher(url, { method, redirect: 'error', signal: controller.signal,
        headers: { authorization: `Bearer ${token}`, accept: 'application/json', ...(patch ? { 'content-type': 'application/json' } : {}) },
        ...(patch ? { body: JSON.stringify(patch) } : {}) });
      if (response.status !== 200) { await discard(response); throw new SafeFailure('MANAGEMENT_API_HTTP_ERROR', stage, response.status); }
      if (method === 'PATCH') { await discard(response); return; }
      return await boundedJson(response, stage);
    } catch (error) {
      if (error instanceof SafeFailure) throw error;
      throw new SafeFailure('MANAGEMENT_API_REQUEST_FAILED', stage);
    } finally { clearTimeout(timer); }
  }
  const { before, expected, patch } = planChanges(await request('GET', 'read'), policy);
  const result = { schema_version: 1, project_ref: projectRef, mode: apply ? 'apply' : 'dry-run',
    status: 'planned', units: UNITS, before, patch, expected, runtime_enforcement_tested: false };
  if (!apply) return result;
  if (Object.keys(patch).length === 0) return { ...result, status: 'already-compliant', verified: before };
  await request('PATCH', 'patch', patch);
  const verified = selectedConfig(await request('GET', 'readback'), 'readback');
  if (FIELDS.some(field => verified[field] !== expected[field])) throw new SafeFailure('READBACK_MISMATCH', 'readback');
  return { ...result, status: 'applied-config-verified', verified };
}

export function parseArguments(args) {
  const result = { apply: false, policyPath: DEFAULT_POLICY }; const seen = new Set();
  for (let i = 0; i < args.length; i++) {
    const flag = args[i];
    if (!['--project-ref', '--policy', '--apply', '--dry-run'].includes(flag) || seen.has(flag)) throw new SafeFailure('INVALID_ARGUMENTS');
    seen.add(flag);
    if (flag === '--apply') result.apply = true;
    else if (flag === '--dry-run') result.apply = false;
    else {
      const value = args[++i];
      if (!value || value.startsWith('--')) throw new SafeFailure('INVALID_ARGUMENTS');
      if (flag === '--project-ref') result.projectRef = value;
      else result.policyPath = value;
    }
  }
  if (seen.has('--apply') && seen.has('--dry-run')) throw new SafeFailure('INVALID_ARGUMENTS');
  return result;
}
export async function main(args, { env = process.env, fetcher = globalThis.fetch, write = line => process.stdout.write(line + '\n') } = {}) {
  try {
    const { policyPath, ...options } = parseArguments(args);
    let policy;
    try {
      const raw = await readFile(policyPath, 'utf8');
      if (Buffer.byteLength(raw) > 16384) throw new Error();
      policy = JSON.parse(raw);
    } catch { throw new SafeFailure('POLICY_FILE_UNREADABLE_OR_INVALID'); }
    write(JSON.stringify(await configureAuthRateLimits({ ...options, policy, env, fetcher }), null, 2));
    return 0;
  } catch (error) {
    const safe = error instanceof SafeFailure ? error : new SafeFailure('UNEXPECTED_FAILURE');
    write(JSON.stringify({ status: 'failed', code: safe.code, stage: safe.stage,
      ...(safe.httpStatus ? { http_status: safe.httpStatus } : {}),
      configuration_may_have_changed: ['patch', 'readback'].includes(safe.stage) }));
    return 1;
  }
}
if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url)
  process.exitCode = await main(process.argv.slice(2));
