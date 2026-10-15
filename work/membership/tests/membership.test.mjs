import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { PGlite } from '@electric-sql/pglite';

const db = new PGlite();
const migration = await readFile(new URL('../migrations/202610110001_membership.sql', import.meta.url), 'utf8');
const alice = '10000000-0000-4000-8000-000000000001';
const bob = '10000000-0000-4000-8000-000000000002';
const owner = '10000000-0000-4000-8000-000000000003';
const pending = '10000000-0000-4000-8000-000000000004';
const missing = '10000000-0000-4000-8000-000000000099';
let checks = 0;
function equal(actual, expected, message) { assert.deepEqual(actual, expected, message); checks++; }
function ok(value, message) { assert.ok(value, message); checks++; }
async function roleQuery(role, user, query, values = []) {
  assert.ok(['anon', 'authenticated', 'service_role'].includes(role));
  await db.exec('begin');
  try {
    await db.exec(`set local role ${role}`);
    await db.query("select set_config('request.jwt.claim.sub', $1, true)", [user ?? '']);
    await db.query("select set_config('request.jwt.claims', $1, true)", [JSON.stringify({ sub: user, role, user_metadata: { plan: 'pro', is_admin: true }, app_metadata: { is_admin: true } })]);
    const result = await db.query(query, values);
    await db.exec('commit');
    return result;
  } catch (error) {
    await db.exec('rollback');
    throw error;
  }
}
async function member(user) {
  return (await roleQuery('authenticated', user, 'select public.get_my_membership() as value')).rows[0].value;
}
async function denied(user, sql, values = [], code = '42501', role = 'authenticated') {
  await assert.rejects(roleQuery(role, user, sql, values), error => error.code === code, sql);
  checks++;
}
async function setPlan(user, plan, expires, reason = 'manual test grant') {
  return (await roleQuery('authenticated', owner,
    'select public.admin_set_membership($1, $2, $3, $4) as value', [user, plan, expires, reason])).rows[0].value;
}
async function list(search = '', offset = 0, limit = 50) {
  return (await roleQuery('authenticated', owner,
    'select public.admin_list_members($1, $2, $3) as value', [search, offset, limit])).rows[0].value;
}

try {
  await db.exec(await readFile(new URL('./bootstrap.sql', import.meta.url), 'utf8'));
  await db.exec(migration);
  await db.query(`insert into auth.users(id,email,email_confirmed_at,last_sign_in_at) values
    ($1,'alice@example.test',now(),now()), ($2,'bob@example.test',now(),null),
    ($3,'owner@example.test',now(),now()), ($4,'pending@example.test',null,null)`, [alice, bob, owner, pending]);
  const value = await member(alice);
  equal(Object.keys(value).sort(), ['user_id', 'email', 'plan', 'status', 'expires_at', 'server_time', 'is_admin', 'recheck_after_seconds'].sort(), 'RPC returns the documented single object');
  equal(value.user_id, alice, 'Identity is auth.uid');
  equal(value.email, 'alice@example.test', 'Email comes from auth.users');
  equal(value.plan, 'free', 'A new verified user is Free');
  equal(value.status, 'free', 'No entitlement is Free');
  equal(value.expires_at, null, 'Free does not have a paid expiry');
  equal(value.is_admin, false, 'JWT metadata does not confer admin');
  equal(value.recheck_after_seconds, 300, 'Entitlement refresh contract');
  ok(Number.isFinite(Date.parse(value.server_time)), 'Server returns an ISO timestamp');

  await denied(null, 'select public.get_my_membership()', [], '42501', 'anon');
  await denied(null, 'select public.get_my_membership()');
  await denied(missing, 'select public.get_my_membership()');
  await denied(pending, 'select public.get_my_membership()');
  await denied(null, 'select public.get_my_membership()', [], '42501', 'service_role');
  equal((await roleQuery('authenticated', alice, 'select email from public.member_profiles')).rows, [{ email: 'alice@example.test' }], 'RLS exposes only own profile');
  equal((await roleQuery('authenticated', alice, 'select email from public.member_profiles where user_id=$1', [bob])).rows.length, 0, 'Other profile is hidden');
  await denied(null, 'select * from public.member_profiles', [], '42501', 'anon');
  await denied(alice, "update public.member_profiles set email='other@example.test'");
  await denied(alice, 'delete from public.member_profiles');
  await denied(alice, "insert into public.member_profiles(user_id,email) values($1,'attack@example.test')", [missing]);
  for (const table of ['membership_admins', 'entitlements', 'membership_audit']) {
    await denied(alice, `select * from usage_widget_private.${table}`);
  }
  await denied(alice, 'select usage_widget_private.membership_snapshot($1,now())', [bob]);
  await denied(alice, 'select public.admin_list_members()');
  await denied(alice, 'select public.admin_set_membership($1,$2,$3,$4)', [alice, 'pro', new Date(Date.now() + 86400000).toISOString(), 'self promotion']);
  await db.query("update auth.users set raw_user_meta_data='{" + '"plan":"pro","is_admin":true' + "}' where id=$1", [alice]);
  equal((await member(alice)).plan, 'free', 'User-controlled stored metadata does not grant Pro');
  equal((await member(alice)).is_admin, false, 'Stored metadata does not grant admin');

  await db.query('insert into usage_widget_private.membership_admins(user_id) values($1)', [owner]);
  equal((await member(owner)).is_admin, true, 'Owner SQL bootstrap enables admin');
  equal((await member(owner)).plan, 'free', 'Administrator membership does not imply paid plan');
  await denied(owner, 'insert into usage_widget_private.membership_admins(user_id) values($1)', [alice]);
  const expiry = new Date(Date.now() + 30 * 86400000).toISOString();
  const granted = await setPlan(alice, 'pro', expiry, '  support: 30 day access  ');
  equal(granted.plan, 'pro', 'Authorized administrator grants Pro');
  equal(granted.status, 'active', 'Granted plan is active');
  equal(Date.parse(granted.expires_at), Date.parse(expiry), 'Expiry is retained');
  equal((await member(alice)).plan, 'pro', 'User immediately sees server grant');
  const audit = (await db.query('select * from usage_widget_private.membership_audit')).rows[0];
  equal(audit.actor_user_id, owner, 'Audit records actual actor');
  equal(audit.target_user_id, alice, 'Audit records target');
  equal(audit.reason, 'support: 30 day access', 'Reason is normalized');
  equal(audit.old_value.plan, 'free', 'Audit captures old plan');
  equal(audit.new_value.plan, 'pro', 'Audit captures new plan');
  ok(!('email' in audit.new_value) && !('email' in audit.old_value), 'Audit does not duplicate email');

  const all = await list();
  equal(all.members.length, 4, 'Administrator can list members');
  equal(all.has_more, false, 'Pagination end is correct');
  equal(Object.keys(all.members[0]).sort(), ['user_id', 'email', 'plan', 'status', 'expires_at', 'created_at', 'last_sign_in_at'].sort(), 'Admin list shape');
  equal((await list('ALICE')).members[0].user_id, alice, 'Search is case insensitive');
  equal((await list('%')).members.length, 0, 'SQL wildcard is a literal search string');
  equal((await list("' OR true --")).members.length, 0, 'Search is not interpolated SQL');
  equal((await list('', 0, 2)).has_more, true, 'Extra row detects next page');
  equal((await list('', 2, 2)).has_more, false, 'Last page end');
  equal((await list('', 4, 2)).members, [], 'Empty page is an array');
  const firstPage = (await list('', 0, 2)).members.map(x => x.user_id);
  const lastPage = (await list('', 2, 2)).members.map(x => x.user_id);
  equal(new Set([...firstPage, ...lastPage]).size, 4, 'Stable order does not overlap pages');
  for (const args of [[null, 0, 50], ['x'.repeat(201), 0, 50], ['', -1, 50], ['', 10001, 50], ['', 0, 0], ['', 0, 101], ['', 0, null]]) {
    await denied(owner, 'select public.admin_list_members($1,$2,$3)', args, '22023');
  }
  const tomorrow = new Date(Date.now() + 86400000).toISOString();
  for (const args of [
    [alice, 'premium', tomorrow, 'reason'], [alice, null, tomorrow, 'reason'], [null, 'pro', tomorrow, 'reason'],
    [alice, 'pro', null, 'reason'], [alice, 'pro', 'infinity', 'reason'],
    [alice, 'pro', '2000-01-01T00:00:00Z', 'reason'],
    [alice, 'pro', new Date(Date.now() + 367 * 86400000).toISOString(), 'reason'],
    [alice, 'free', tomorrow, 'reason'], [alice, 'pro', tomorrow, ' '],
    [alice, 'pro', tomorrow, null], [alice, 'pro', tomorrow, 'x'.repeat(501)],
    [pending, 'pro', tomorrow, 'reason'], [missing, 'pro', tomorrow, 'reason']
  ]) await denied(owner, 'select public.admin_set_membership($1,$2,$3,$4)', args, '22023');
  equal((await db.query('select count(*)::int as n from usage_widget_private.membership_audit')).rows[0].n, 1, 'Rejected actions do not create an audit or mutate grants');
  equal((await member(alice)).plan, 'pro', 'Invalid actions preserve prior entitlement');

  await db.query("update usage_widget_private.entitlements set status='canceled' where user_id=$1", [alice]);
  equal((await member(alice)).plan, 'pro', 'Canceled subscription remains Pro until expiry');
  equal((await member(alice)).status, 'canceled', 'Cancellation is reported');
  await db.query("update usage_widget_private.entitlements set expires_at=now()-interval '1 second' where user_id=$1", [alice]);
  equal((await member(alice)).plan, 'free', 'Past expiration loses Pro immediately without a scheduled job');
  equal((await member(alice)).status, 'expired', 'Past cancellation becomes effective expired');
  equal((await list('alice')).members[0].status, 'expired', 'Admin list uses same expiry rules');
  await db.query("update usage_widget_private.entitlements set status='expired', expires_at=now()+interval '1 day' where user_id=$1", [alice]);
  equal((await member(alice)).plan, 'free', 'Explicit expired status cannot grant Pro even with future expiry');
  const revoked = await setPlan(alice, 'free', null, 'support: revoke');
  equal(revoked.plan, 'free', 'Admin revokes Pro');
  equal(revoked.status, 'revoked', 'Revocation is explicit');
  equal(revoked.expires_at, null, 'Revoked Free has no paid expiry');
  equal((await db.query('select count(*)::int as n from usage_widget_private.membership_audit')).rows[0].n, 2, 'Grant and revoke are both audited');

  await db.query("update auth.users set email='changed@example.test' where id=$1", [bob]);
  equal((await member(bob)).email, 'changed@example.test', 'RPC follows current auth email');
  equal((await roleQuery('authenticated', bob, 'select email from public.member_profiles')).rows[0].email, 'changed@example.test', 'Profile trigger mirrors email changes');
  await db.query("update auth.users set banned_until=now()+interval '1 day' where id=$1", [bob]);
  await denied(bob, 'select public.get_my_membership()');
  await denied(owner, 'select public.admin_set_membership($1,$2,$3,$4)', [bob, 'pro', tomorrow, 'reason'], '22023');
  await db.query("update auth.users set banned_until=now()+interval '1 day' where id=$1", [owner]);
  await denied(owner, 'select public.admin_list_members()');
  await db.query('update auth.users set banned_until=null where id=$1', [owner]);
  await db.query('delete from usage_widget_private.membership_admins where user_id=$1', [owner]);
  await denied(owner, 'select public.admin_list_members()');
  equal((await member(owner)).is_admin, false, 'Admin revocation applies without issuing a new token');

  const funcs = (await db.query(`select n.nspname, p.proname, p.prosecdef, p.proconfig
    from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='usage_widget_private' or (n.nspname='public' and p.proname in ('get_my_membership','admin_list_members','admin_set_membership'))`)).rows;
  equal(funcs.length, 7, 'Only expected membership functions exist');
  ok(funcs.every(f => f.prosecdef && f.proconfig.includes('search_path=""')), 'Every definer function fixes search_path');
  const grants = (await db.query(`select p.proname, has_function_privilege('anon',p.oid,'execute') as anon_exec,
    has_function_privilege('authenticated',p.oid,'execute') as auth_exec
    from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='usage_widget_private' or (n.nspname='public' and p.proname in ('get_my_membership','admin_list_members','admin_set_membership'))`)).rows;
  ok(grants.every(g => !g.anon_exec), 'No anon function privilege survives inherited PUBLIC defaults');
  equal(grants.filter(g => g.auth_exec).map(g => g.proname).sort(), ['admin_list_members', 'admin_set_membership', 'get_my_membership'], 'Authenticated can call only public guarded RPCs');
  await db.exec(migration);
  equal((await member(alice)).status, 'revoked', 'Reapplying migration preserves entitlement');
  equal((await db.query('select count(*)::int as n from usage_widget_private.membership_audit')).rows[0].n, 2, 'Reapplying migration preserves audit');
  await db.query('delete from auth.users where id=$1', [alice]);
  equal((await db.query('select count(*)::int as n from public.member_profiles where user_id=$1', [alice])).rows[0].n, 0, 'Deleting auth user removes profile');
  equal((await db.query('select count(*)::int as n from usage_widget_private.entitlements where user_id=$1', [alice])).rows[0].n, 0, 'Deleting auth user removes entitlement');
  equal((await db.query('select count(*)::int as n from usage_widget_private.membership_audit where target_user_id=$1', [alice])).rows[0].n, 2, 'Audit remains attributable after account removal');
  await denied(alice, 'select public.get_my_membership()');
  console.log(`PASS: ${checks} membership PostgreSQL/RLS/RPC checks; in-memory PGlite; no Supabase project or email used.`);
} finally {
  await db.close();
}
