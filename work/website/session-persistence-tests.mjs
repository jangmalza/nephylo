import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const code=await readFile(new URL('./dist/account/membership-api.js',import.meta.url),'utf8');
const {MembershipApi}=await import('data:text/javascript;base64,'+Buffer.from(code).toString('base64'));
const config={SupabaseUrl:'https://session-test.supabase.co',PublishableKey:'sb_publishable_testonlypublic123',EmailEnabled:true,GoogleEnabled:true};
const id='a0000000-0000-0000-0000-000000000001';
const email='tester@example.com';
const user={id,email,email_confirmed_at:'2026-10-11T00:00:00Z'};
const session={access_token:'access-session-fixture',refresh_token:'refresh-session-fixture',expires_in:3600,user,provider_token:'never-persist-provider-token',provider_refresh_token:'never-persist-provider-refresh-token'};
const member={user_id:id,email,plan:'free',status:'free',expires_at:null,server_time:'2026-10-12T09:00:00Z',is_admin:false};
const reply=(status,data)=>({status,ok:status>=200&&status<300,text:async()=>data===null?'':JSON.stringify(data)});
class MemoryStorage {
  constructor() {this.values=new Map();this.writes=0;}
  get length(){return this.values.size;}
  key(index){return [...this.values.keys()][index]??null;}
  getItem(key){return this.values.get(String(key))??null;}
  setItem(key,value){this.writes++;this.values.set(String(key),String(value));}
  removeItem(key){this.values.delete(String(key));}
}
let checks=0;
const check=(actual,expected,message)=>{assert.deepEqual(actual,expected,message);checks++;};
const reject=async(work,pattern)=>{await assert.rejects(work,pattern);checks++;};
const defaultFetcher=async url=>{
  if(url.endsWith('/auth/v1/verify'))return reply(200,session);
  if(url.endsWith('/auth/v1/user'))return reply(200,user);
  if(url.endsWith('/rest/v1/rpc/get_my_membership'))return reply(200,member);
  if(url.includes('/auth/v1/logout'))return reply(204,null);
  if(url.endsWith('/auth/v1/token?grant_type=refresh_token'))return reply(200,{...session,access_token:'rotated-access-fixture',refresh_token:'rotated-refresh-fixture'});
  throw new Error('Unexpected synthetic request: '+url);
};
if(process.argv.includes('--baseline')){
  const storage=new MemoryStorage();
  const before=new MembershipApi(config,defaultFetcher,()=>Date.now(),storage);
  await before.verify(email,'123456');
  check(before.session.email,email,'Initial email verification is authenticated');
  const after=new MembershipApi(config,defaultFetcher,()=>Date.now(),storage);
  check(after.session,null,'Baseline: a fresh page instance loses the memory-only login');
  check(storage.length,0,'Baseline: no recoverable credentials were saved');
  check(typeof after.restore,'undefined','Baseline: no server-verified restore API exists');
  console.log(`REPRODUCED: successful email login is lost after page recreation (${checks} baseline assertions). No live requests.`);
  process.exit(0);
}

const makeApi=(storage,fetcher=defaultFetcher,configuration=config,now=()=>Date.now())=>new MembershipApi(configuration,fetcher,now,storage);
const seed=async(storage=new MemoryStorage())=>{
  const api=makeApi(storage);
  await api.verify(email,'123456');
  return {api,storage,key:api.storageKey,cached:JSON.parse(storage.getItem(api.storageKey))};
};
const {api:loggedIn,storage,key,cached}=await seed();
check(loggedIn.session.email,email,'Successful login remains authenticated');
check(storage.length,1,'One tab-scoped session entry is written');
check(key,'nephylo.auth.session.v1:'+config.SupabaseUrl,'Session key is isolated by configured authentication origin');
check(Object.keys(cached).sort(),['access','expires','origin','refresh','schemaVersion'],'Persistence contains credentials and expiry only');
check(cached.schemaVersion,1,'Stored format is versioned');
check(cached.origin,config.SupabaseUrl,'Stored origin matches configured authentication service');
check(cached.access,session.access_token,'Access token persisted for same-tab restoration');
check(cached.refresh,session.refresh_token,'Refresh token persisted for token rotation');
check(typeof cached.expires,'number','Expiry is numeric');
check(storage.getItem(key).includes('provider-token'),false,'Google provider credentials are excluded');
check(storage.getItem(key).includes(email),false,'Unnecessary identity data is excluded');
check(storage.getItem(key).includes('is_admin'),false,'Authorization flags are never persisted');
const restoredCalls=[];
const restored=makeApi(storage,async(url,options)=>{restoredCalls.push({url,options});return defaultFetcher(url,options);});
check(restored.session,null,'Page recreation has no authenticated state before verification');
check(restored.hasRestorableSession,true,'Page recreation detects tab credentials');
const restoredMember=await restored.restore();
check(restoredMember.plan,'free','Reload restores server-confirmed membership');
check(restored.session.email,email,'Reload restores server-confirmed identity');
check(restoredCalls.map(call=>new URL(call.url).pathname),['/auth/v1/user','/rest/v1/rpc/get_my_membership'],'Identity verification precedes membership verification');
check(restoredCalls[0].options.method,'GET','Restore uses authenticated getUser endpoint');
check(restoredCalls.every(call=>call.options.headers.Authorization==='Bearer '+session.access_token),true,'Restore uses saved token only against configured service');
check(restoredCalls.every(call=>call.options.redirect==='error'&&call.options.credentials==='omit'&&call.options.cache==='no-store'),true,'Restore rejects redirects, cookies and HTTP caching');

const forged=await seed();
forged.storage.setItem(forged.key,JSON.stringify({...forged.cached,userId:'b0000000-0000-0000-0000-000000000002',email:'attacker@example.com',plan:'pro',is_admin:true,user_metadata:{is_admin:true,plan:'pro'}}));
const forgedApi=makeApi(forged.storage);
const forgedMember=await forgedApi.restore();
check(forgedMember.plan,'free','Forged persisted Pro flag cannot override server Free plan');
check(forgedMember.is_admin,false,'Forged persisted admin flag cannot override server authority');
check(forgedApi.session.email,email,'Persisted identity claims cannot override getUser identity');
const actualPro=await seed();
const proMember={...member,plan:'pro',status:'active',expires_at:'2026-10-13T09:00:00Z',is_admin:true};
const actualProApi=makeApi(actualPro.storage,async(url,options)=>url.endsWith('/get_my_membership')?reply(200,proMember):defaultFetcher(url,options));
const verifiedPro=await actualProApi.restore();
check(verifiedPro.plan,'pro','Actual Pro membership is restored only from server response');
check(verifiedPro.is_admin,true,'Actual admin membership is restored only from server response');
check(actualPro.storage.getItem(actualPro.key).includes('is_admin'),false,'Even confirmed admin role is not cached for authorization');

const expired=await seed();
expired.storage.setItem(expired.key,JSON.stringify({...expired.cached,expires:Date.now()-1000}));
const expiredCalls=[];
const expiredApi=makeApi(expired.storage,async(url,options)=>{expiredCalls.push({url,options});return defaultFetcher(url,options);});
check((await expiredApi.restore()).plan,'free','Expired access token restores by refreshing first');
check(expiredCalls[0].url.endsWith('/auth/v1/token?grant_type=refresh_token'),true,'Expired token is refreshed before identity lookup');
check(JSON.parse(expiredCalls[0].options.body),{refresh_token:session.refresh_token},'Refresh uses saved refresh token');
check(expiredCalls[1].options.headers.Authorization,'Bearer rotated-access-fixture','Identity verification uses rotated token');
check(JSON.parse(expired.storage.getItem(expired.key)).refresh,'rotated-refresh-fixture','Refresh-token rotation is persisted immediately');
const nextReload=makeApi(expired.storage);
await nextReload.restore();
check(nextReload.session.refresh,'rotated-refresh-fixture','Another reload retains the rotated refresh token');
await nextReload.refresh();
check(JSON.parse(expired.storage.getItem(expired.key)).access,'rotated-access-fixture','Ordinary refresh also persists new tokens');

const unauthorized=await seed();
let identityAttempts=0;
const unauthorizedApi=makeApi(unauthorized.storage,async(url,options)=>{
  if(url.endsWith('/auth/v1/user')&&++identityAttempts===1)return reply(401,{code:'bad_jwt'});
  return defaultFetcher(url,options);
});
await unauthorizedApi.restore();
check(identityAttempts,2,'Server 401 triggers one refresh then identity recheck');
check(unauthorizedApi.session.access,'rotated-access-fixture','Server 401 recovery replaces old access token');

for(const failAt of ['user','membership']){
  const transient=await seed();
  const temporary=makeApi(transient.storage,async(url,options)=>{
    if((failAt==='user'&&url.endsWith('/auth/v1/user'))||(failAt==='membership'&&url.endsWith('/get_my_membership')))throw new TypeError('synthetic network interruption');
    return defaultFetcher(url,options);
  });
  await reject(()=>temporary.restore(),/인터넷 연결|서버/);
  check(temporary.session,null,`${failAt}: transient error exposes no authenticated state`);
  check(temporary.hasRestorableSession,true,`${failAt}: transient error retains recoverable credentials`);
  check(transient.storage.length,1,`${failAt}: network failure does not log the user out permanently`);
  const recovered=makeApi(transient.storage);
  check((await recovered.restore()).plan,'free',`${failAt}: later page retry recovers without new OTP`);
}
const refreshedThenOffline=await seed();
refreshedThenOffline.storage.setItem(refreshedThenOffline.key,JSON.stringify({...refreshedThenOffline.cached,expires:Date.now()-1000}));
const rotateOffline=makeApi(refreshedThenOffline.storage,async(url,options)=>url.endsWith('/auth/v1/user')?reply(503,{code:'temporary'}):defaultFetcher(url,options));
await reject(()=>rotateOffline.restore(),/서버/);
check(rotateOffline.session,null,'Rotated session remains unauthorized when subsequent user lookup is unavailable');
check(JSON.parse(refreshedThenOffline.storage.getItem(refreshedThenOffline.key)).refresh,'rotated-refresh-fixture','Rotated refresh token survives later network failure');

for(const status of [400,401,403]){
  const revoked=await seed();
  revoked.storage.setItem(revoked.key,JSON.stringify({...revoked.cached,expires:Date.now()-1000}));
  const revokedApi=makeApi(revoked.storage,async()=>reply(status,{code:'refresh_token_not_found'}));
  await reject(()=>revokedApi.restore(),Error);
  check(revokedApi.session,null,'Revoked refresh leaves no authenticated state');
  check(revoked.storage.length,0,'Authoritative refresh rejection removes saved credentials');
  check(revokedApi.hasRestorableSession,false,'Revoked credentials cannot be restored again');
}
for(const [description,invalidUser] of [
  ['unconfirmed',{...user,email_confirmed_at:null}],
  ['banned',{...user,banned_until:'2099-01-01T00:00:00Z'}],
  ['invalid id',{...user,id:'not-a-user'}],
]){
  const invalid=await seed();let membershipCalls=0;
  const invalidApi=makeApi(invalid.storage,async(url,options)=>{
    if(url.endsWith('/auth/v1/user'))return reply(200,invalidUser);
    if(url.endsWith('/get_my_membership'))membershipCalls++;
    return defaultFetcher(url,options);
  });
  await reject(()=>invalidApi.restore(),Error);
  check(invalidApi.session,null,`${description}: invalid identity cannot authenticate`);
  check(membershipCalls,0,`${description}: membership is not requested before valid identity`);
  check(invalid.storage.length,0,`${description}: invalid identity clears cached credentials`);
}
const wrongMember=await seed();
const wrongMemberApi=makeApi(wrongMember.storage,async(url,options)=>url.endsWith('/get_my_membership')?reply(200,{...member,user_id:'b0000000-0000-0000-0000-000000000002',plan:'pro',is_admin:true}):defaultFetcher(url,options));
await reject(()=>wrongMemberApi.restore(),/권한/);
check(wrongMemberApi.session,null,'Membership belonging to another user cannot authenticate this session');

const logout=await seed();
await logout.api.signOut();
check(logout.api.session,null,'Sign-out clears current authentication');
check(logout.storage.length,0,'Sign-out removes tab credentials');
let afterLogoutRequests=0;
check(await makeApi(logout.storage,async()=>{afterLogoutRequests++;throw new Error('No request expected');}).restore(),null,'Reload after logout remains logged out');
check(afterLogoutRequests,0,'Logout cannot silently refresh old credentials');

const stale=await seed();let finishUser;
const staleApi=makeApi(stale.storage,async(url,options)=>url.endsWith('/auth/v1/user')?new Promise(resolve=>{finishUser=()=>resolve(reply(200,user));}):defaultFetcher(url,options));
const staleRestore=staleApi.restore();
await staleApi.signOut();finishUser();
await reject(()=>staleRestore,/취소/);
check(staleApi.session,null,'Late restore cannot resurrect logged-out memory state');
check(stale.storage.length,0,'Late restore cannot repersist logged-out credentials');
const staleRefresh=await seed();
staleRefresh.api.session.expires=0;
let finishRefresh;
staleRefresh.api.fetcher=async url=>url.includes('refresh_token')?new Promise(resolve=>{finishRefresh=()=>resolve(reply(200,{...session,refresh_token:'stale-refresh'}));}):reply(204,null);
const pendingRefresh=staleRefresh.api.refresh();await staleRefresh.api.signOut();finishRefresh();
await reject(()=>pendingRefresh,/취소/);
check(staleRefresh.storage.length,0,'Late refresh cannot repersist credentials after logout');

const concurrent=await seed();let concurrentCalls=0;
const concurrentApi=makeApi(concurrent.storage,async(url,options)=>{concurrentCalls++;await Promise.resolve();return defaultFetcher(url,options);});
const concurrentResults=await Promise.all([concurrentApi.restore(),concurrentApi.restore(),concurrentApi.restore()]);
check(concurrentResults.map(value=>value.plan),['free','free','free'],'Overlapping restore callers receive verified membership');
check(concurrentCalls,2,'Overlapping restoration performs one identity and one membership request');

const overlapping=await seed();let finishRotation,finishOverlapUser;
const overlapTokens=[];
overlapping.api.fetcher=async(url,options)=>{
  if(url.endsWith('/auth/v1/token?grant_type=refresh_token'))return new Promise(resolve=>{finishRotation=()=>resolve(reply(200,{...session,access_token:'overlap-new-access',refresh_token:'overlap-new-refresh'}));});
  if(url.endsWith('/auth/v1/user')){
    overlapTokens.push(options.headers.Authorization);
    return new Promise(resolve=>{finishOverlapUser=()=>resolve(reply(200,user));});
  }
  return defaultFetcher(url,options);
};
const pendingRotation=overlapping.api.refresh();
const overlappingRestore=overlapping.api.restore();
finishRotation();await pendingRotation;
await new Promise(resolve=>setImmediate(resolve));
check(typeof finishOverlapUser,'function','Restore reaches identity verification after the pending refresh');
finishOverlapUser();
check((await overlappingRestore).plan,'free','Restore can overlap an already pending refresh');
check(overlapTokens,['Bearer overlap-new-access'],'Overlapping restore verifies the newly rotated access token');
check(overlapping.api.session.refresh,'overlap-new-refresh','Overlapping restore does not roll in-memory refresh credentials back');
check(JSON.parse(overlapping.storage.getItem(overlapping.key)).refresh,'overlap-new-refresh','Overlapping restore does not overwrite persisted rotated credentials');

// Simulate a back/forward cached document completing after a newer document in
// the same tab has logged in. A failure must not delete the newer token pair.
for(const action of ['refresh','restore-refresh','google']){
  const outdated=await seed();let finishOld;
  let oldApi=outdated.api;
  if(action==='restore-refresh'){
    outdated.storage.setItem(outdated.key,JSON.stringify({...outdated.cached,expires:Date.now()-1000}));
    oldApi=makeApi(outdated.storage);
  }
  oldApi.fetcher=async()=>new Promise(resolve=>{finishOld=()=>resolve(reply(400,{code:'refresh_token_not_found'}));});
  const oldRequest=action==='refresh'?oldApi.refresh():action==='restore-refresh'?oldApi.restore():oldApi.exchangeGoogle('old-code','x'.repeat(43),oldApi.beginGoogle());
  const newer=makeApi(outdated.storage,async(url,options)=>url.endsWith('/auth/v1/verify')?reply(200,{...session,access_token:'new-document-access',refresh_token:'new-document-refresh'}):defaultFetcher(url,options));
  await newer.verify(email,'123456');
  const newerStored=outdated.storage.getItem(outdated.key);
  finishOld();
  await reject(()=>oldRequest,Error);
  check(outdated.storage.getItem(outdated.key),newerStored,`${action}: stale async failure cannot erase a newer document login`);
  check(newer.session.access,'new-document-access',`${action}: newer document identity remains authenticated`);
}

const isolated=await seed();let isolationRequests=0;
const otherConfig={...config,SupabaseUrl:'https://other-project.supabase.co'};
const otherApi=makeApi(isolated.storage,async()=>{isolationRequests++;throw new Error('No request expected');},otherConfig);
check(otherApi.hasRestorableSession,false,'Different Supabase project cannot discover this session');
check(await otherApi.restore(),null,'Different project cannot restore this session');
check(isolationRequests,0,'Different project receives no credential request');
check(isolated.storage.length,1,'Other project does not delete the original project session');
const emptyApi=makeApi(isolated.storage,async()=>{throw new Error('No request expected');},{SupabaseUrl:'',PublishableKey:''});
check(await emptyApi.restore(),null,'Unconfigured site remains an anonymous session');

for(const [description,invalid] of [
  ['malformed JSON','{bad'],
  ['oversized entry','x'.repeat(70000)],
  ['array',[]],
  ['null',null],
  ['foreign origin',{...cached,origin:'https://other-project.supabase.co'}],
  ['old schema',{...cached,schemaVersion:0}],
  ['empty token',{...cached,access:''}],
  ['nonstring token',{...cached,refresh:123}],
  ['oversized token',{...cached,access:'a'.repeat(16385)}],
  ['nonnumeric expiry',{...cached,expires:'tomorrow'}],
  ['far-future expiry',{...cached,expires:Date.now()+86400000*365}],
]){
  const invalidStore=new MemoryStorage();invalidStore.setItem(key,typeof invalid==='string'?invalid:JSON.stringify(invalid));
  let requests=0;const invalidApi=makeApi(invalidStore,async()=>{requests++;throw new Error('No request expected');});
  check(await invalidApi.restore(),null,`${description}: malformed cached session is ignored`);
  check(invalidApi.session,null,`${description}: no authentication from invalid cache`);
  check(requests,0,`${description}: no credential request from invalid cache`);
  check(invalidStore.length,0,`${description}: invalid storage entry is removed`);
}
const blocked={getItem(){throw new Error('Storage disabled');},setItem(){throw new Error('Storage disabled');},removeItem(){throw new Error('Storage disabled');}};
const temporaryApi=makeApi(blocked);
check(await temporaryApi.restore(),null,'Blocked storage does not break anonymous page load');
check((await temporaryApi.verify(email,'123456')).plan,'free','Blocked storage still allows temporary page login');
check(temporaryApi.session.email,email,'Blocked storage preserves current-memory session');
check(Boolean(temporaryApi.storageNotice),true,'Blocked persistence is disclosed to the UI');
await temporaryApi.signOut();check(temporaryApi.session,null,'Sign-out works even if storage throws');
const noStorage=makeApi(null);
check((await noStorage.verify(email,'123456')).plan,'free','Explicit memory-only mode remains supported');
const quota=await seed();
quota.storage.setItem=()=>{throw new Error('Storage quota exceeded');};
const quotaApi=makeApi(quota.storage);
check((await quotaApi.verify(email,'123456')).plan,'free','Quota-exhausted persistence still supports current-page login');
check(quota.storage.length,0,'Failed persistence cannot leave old stored credentials to reappear on reload');
check(Boolean(quotaApi.storageNotice),true,'Quota failure is disclosed to the UI');
const originalStorage=Object.getOwnPropertyDescriptor(globalThis,'sessionStorage');
try {
  Object.defineProperty(globalThis,'sessionStorage',{configurable:true,get(){throw new Error('Browser denied storage');}});
  const deniedBrowser=new MembershipApi(config,defaultFetcher);
  check((await deniedBrowser.verify(email,'123456')).plan,'free','Throwing native sessionStorage getter does not break login');
} finally {if(originalStorage)Object.defineProperty(globalThis,'sessionStorage',originalStorage);else delete globalThis.sessionStorage;}

check(/localStorage/.test(code),false,'Auth sessions are not persisted across browser sessions via localStorage');
check(/innerHTML/.test(code),false,'Auth data never enters an HTML interpolation sink');
console.log(`PASS: tab session reload, verified identity, server authority, token rotation, transient/revoked recovery, logout races and bounded storage (${checks} checks). No live requests.`);
