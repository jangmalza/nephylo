import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

// Each case gets a fresh ESM module graph and browser globals. The real page
// controller, auth API, billing UI and translations run against fixture I/O.
const cases=['pending-restore','transient-retry','disabled-provider-retry','invalid-session','blocked-storage','logout','late-clear','billing-offline','admin-auth-revoked'];
if(!process.argv.includes('--case')){
  let checks=0;
  for(const name of cases){
    const result=spawnSync(process.execPath,[fileURLToPath(import.meta.url),'--case',name],{encoding:'utf8',timeout:20000});
    assert.equal(result.status,0,`${name}: ${result.stderr||result.stdout||result.error?.message}`);
    const count=result.stdout.match(/^PASS: (\d+) session UI checks/m);
    assert.ok(count,`${name}: fixture did not finish`);checks+=Number(count[1]);
  }
  console.log(`PASS: ${checks} session restoration UI checks across ${cases.length} isolated page boots.`);
}else{
  await runCase(process.argv[process.argv.indexOf('--case')+1]);
}

async function runCase(name){
  assert.ok(cases.includes(name));
  let checks=0;
  const equal=(actual,expected,message)=>{assert.deepEqual(actual,expected,message);checks++;};
  const ok=(value,message)=>{assert.ok(value,message);checks++;};
  const root=new URL('./dist/account/',import.meta.url);
  const html=await readFile(new URL('admin.html',root),'utf8');
  const accountSource=await readFile(new URL('account.js',root),'utf8');
  const nodes=new Map(),intervals=[],events=new Map(),requests=[],persistentPreferences=new Map(),stored=new Map();
  const config={SupabaseUrl:'https://session-fixture.supabase.co',PublishableKey:'sb_publishable_sessionfixture',EmailEnabled:name!=='disabled-provider-retry',GoogleEnabled:name!=='disabled-provider-retry'};
  const key='nephylo.auth.session.v1:'+config.SupabaseUrl;
  const userId='e2000000-0000-4000-8000-000000000002';
  const user={id:userId,email:'restored@example.test',email_confirmed_at:'2026-10-01T00:00:00Z',banned_until:null};
  const serverMember={user_id:userId,email:user.email,plan:'free',status:'free',expires_at:null,server_time:new Date().toISOString(),is_admin:name==='admin-auth-revoked'};
  const pair={schemaVersion:1,origin:config.SupabaseUrl,access:'fixture-access',refresh:'fixture-refresh',expires:Date.now()+3600000};
  // These fields are deliberately hostile cached claims, never server authority.
  if(name!=='blocked-storage')stored.set(key,JSON.stringify({...pair,plan:'pro',is_admin:true,email:'unverified@example.test'}));
  function element(tag='div'){
    return {nodeType:1,tagName:tag.toUpperCase(),childNodes:[],attributes:new Map(),listeners:new Map(),value:'',checked:false,disabled:false,hidden:false,textContent:'',
      get children(){return this.childNodes;},append(...items){this.childNodes.push(...items);},replaceChildren(...items){this.childNodes=[...items];},focus(){},
      setAttribute(key,value){this.attributes.set(key,String(value));},getAttribute(key){return this.attributes.get(key)??null;},hasAttribute(key){return this.attributes.has(key);},
      matches(selector){return selector.split(',').some(part=>part==='[data-language-select]'?this.hasAttribute('data-language-select'):part==='[data-i18n-ignore]'?this.hasAttribute('data-i18n-ignore'):part===this.tagName.toLowerCase());},
      addEventListener(type,fn){this.listeners.set(type,fn);},querySelector(){return null;}
    };
  }
  for(const match of html.matchAll(/<([a-z][a-z0-9-]*)\b([^>]*\bid="([^"]+)"[^>]*)>([^<]*)/g)){
    const item=element(match[1]);
    for(const attr of match[2].matchAll(/([a-z][a-z0-9-]*)(?:="([^"]*)")?/g))item.setAttribute(attr[1],attr[2]??'');
    item.hidden=item.hasAttribute('hidden');item.disabled=item.hasAttribute('disabled');item.textContent=match[4].trim();
    nodes.set(match[3],item);
  }
  const node=id=>{assert.ok(nodes.has(id),`Page is missing fixture target #${id}`);return nodes.get(id);};
  const language=element('select');language.setAttribute('data-language-select','');language.value='ko';
  node('grant-plan').value='free';
  globalThis.document={nodeType:9,documentElement:{lang:'ko'},childNodes:[language],hidden:false,getElementById:node,createElement:element,
    addEventListener(type,fn){events.set(type,fn);},querySelectorAll(){return [...nodes.values()].filter(item=>['INPUT','SELECT','BUTTON'].includes(item.tagName));}};
  globalThis.localStorage={getItem:key=>persistentPreferences.get(key)??null,setItem:(key,value)=>persistentPreferences.set(key,String(value))};
  let removeCalls=0,openedPopups=0;
  const sessionStorage={getItem:key=>stored.get(key)??null,setItem:(key,value)=>stored.set(key,String(value)),removeItem:key=>{removeCalls++;stored.delete(key);}};
  if(name==='blocked-storage')Object.defineProperty(globalThis,'sessionStorage',{configurable:true,get(){throw new Error('Fixture storage is blocked');}});
  else globalThis.sessionStorage=sessionStorage;
  globalThis.location=new URL('https://nephylo.example/account/');globalThis.history={replaceState(){}};
  globalThis.window={open(){openedPopups++;return null;}};
  globalThis.setInterval=callback=>{intervals.push(callback);return intervals.length;};
  let releaseMember,memberPending=false,transient=name==='transient-retry'||name==='disabled-provider-retry';
  let blockMembership=name==='pending-restore'||name==='late-clear',restoreCalls=0,observedApi=null;
  const response=(value,status=200)=>new Response(JSON.stringify(value),{status,headers:{'content-type':'application/json'}});
  globalThis.fetch=async(url,options={})=>{
    url=String(url);requests.push({url,method:options.method||'GET',body:options.body});
    if(url==='config.json')return response(config);
    if(url.endsWith('/functions/v1/billing-api')){
      const body=JSON.parse(options.body);
      if(name==='billing-offline'&&body.action==='config')return response({message:'Fixture billing configuration outage'},503);
      return response(body.action==='config'?{enabled:false,amount:3000,currency:'KRW',interval:'month'}:{subscription:null,status:'none'});
    }
    if(url.endsWith('/auth/v1/user'))return response(name==='invalid-session'?{msg:'Fixture invalid session'}:user,name==='invalid-session'?401:200);
    if(url.endsWith('/auth/v1/token?grant_type=refresh_token'))return response({msg:'Fixture expired refresh'},401);
    if(url.endsWith('/rest/v1/rpc/get_my_membership')){
      if(blockMembership){memberPending=true;return new Promise(resolve=>{releaseMember=value=>{memberPending=false;blockMembership=false;resolve(response(value));};});}
      if(transient)return response({message:'Fixture transient outage'},503);
      return response(serverMember);
    }
    if(url.endsWith('/rest/v1/rpc/admin_list_members'))return response(name==='admin-auth-revoked'?{message:'Fixture session revoked'}:{members:[],has_more:false},name==='admin-auth-revoked'?401:200);
    if(url.endsWith('/auth/v1/logout?scope=local'))return response({});
    if(url.endsWith('/auth/v1/otp'))return response({});
    if(url.endsWith('/auth/v1/verify'))return response({access_token:'fixture-new-access',refresh_token:'fixture-new-refresh',expires_in:3600,user});
    throw new Error('Unexpected fixture request path: '+new URL(url).pathname);
  };
  const apiPath=accountSource.match(/from '(\.\/membership-api\.js(?:\?[^']*)?)'/)?.[1];
  ok(apiPath,'Test loads the exact API module used by the account controller');
  const {MembershipApi}=await import(new URL(apiPath,root).href);
  const restore=MembershipApi.prototype.restore;
  ok(typeof restore==='function','Auth API exposes its real restoration implementation');
  MembershipApi.prototype.restore=function(...args){restoreCalls++;observedApi=this;return restore.apply(this,args);};
  const rewritten=accountSource.replace(/from '(\.\/[^']+)'/g,(_,path)=>'from '+JSON.stringify(new URL(path,root).href));
  const boot=import('data:text/javascript;base64,'+Buffer.from(rewritten+'\nexport {api,run,controls,restoreSession,signedOut};').toString('base64'));
  // Install a rejection handler immediately so assertion failures report cleanly.
  boot.catch(()=>{});
  const tick=async()=>{for(const fn of intervals)await fn();};
  const submit=id=>node(id).listeners.get('submit')({preventDefault(){}});
  const click=id=>node(id).listeners.get('click')();
  const membershipCalls=()=>requests.filter(item=>item.url.endsWith('/rest/v1/rpc/get_my_membership')).length;
  const privilegedCalls=()=>requests.filter(item=>item.url.includes('/admin_'));
  const waitFor=async(predicate,message)=>{
    const deadline=Date.now()+3000;
    while(!predicate()&&Date.now()<deadline)await new Promise(resolve=>setImmediate(resolve));
    ok(predicate(),message);
  };

  if(name==='pending-restore'){
    await waitFor(()=>memberPending,'Page starts verifying saved login on boot');
    equal(restoreCalls,1,'Boot requests restoration exactly once');
    equal(observedApi.session,null,'Pending server membership is not an authenticated UI session');
    equal(node('session-restore-note').hidden,false,'Pending restoration is visible');
    ok(node('session-restore-message').textContent.length>0,'Pending restoration has a status message');
    equal(node('retry-session').hidden,true,'Retry is withheld until a failed attempt finishes');
    equal(node('retry-session').disabled,true,'Pending restore cannot be retried concurrently');
    equal(node('member-card').hidden,true,'Pending restoration does not render cached identity');
    equal(node('admin-panel').hidden,true,'Cached administrator claim never opens administrator UI');
    for(const id of ['email','send-code','verify-code','google-login'])equal(node(id).disabled,true,`${id} is disabled during restoration`);
    node('email').value='other@example.test';node('code').value='123456';
    const before=requests.length;
    await submit('email-form');await submit('code-form');await click('google-login');await click('retry-session');await click('sign-out');
    await tick();await tick();
    equal(requests.length,before,'Programmatic conflicting login/logout handlers and timer ticks cannot overlap restoration');
    equal(restoreCalls,1,'Repeated controls/ticks do not duplicate restoration');
    equal(openedPopups,0,'Google popup never opens during restoration');
    equal(privilegedCalls().length,0,'No administrator requests precede server membership validation');
    const pro={...serverMember,plan:'pro',status:'active',expires_at:new Date(Date.now()+86400000).toISOString(),is_admin:true};
    releaseMember(pro);await boot;
    equal(node('member-card').hidden,false,'Verified member appears when restoration completes');
    equal(node('login-card').hidden,true,'Verified restored login hides login form');
    equal(node('member-email').textContent,user.email,'Displayed identity comes from verified server data');
    equal(node('plan-badge').textContent,'Pro','Only the verified server Pro plan is displayed');
    equal(node('admin-panel').hidden,false,'Verified server administrator can open admin panel');
    equal(privilegedCalls().length,1,'Verified administrator loads the member list once');
    equal(node('session-restore-note').hidden,true,'Successful restoration removes the recovery notice');
    equal(restoreCalls,1,'Successful restoration does not immediately restore a second time');
    equal(membershipCalls(),1,'Successful restoration fetches membership once on boot');
    const saved=JSON.parse(stored.get(key));equal(Object.keys(saved).sort(),['access','expires','origin','refresh','schemaVersion'].sort(),'Successful restore rewrites storage as a token pair without cached privileges');
  }else if(name==='late-clear'){
    await waitFor(()=>memberPending,'Restoration reaches pending membership before credentials are cleared');
    observedApi.clear();
    equal(stored.has(key),false,'An explicit clear removes credentials while membership is pending');
    releaseMember({...serverMember,plan:'pro',status:'active',expires_at:new Date(Date.now()+86400000).toISOString(),is_admin:true});
    const account=await boot;
    equal(account.api.session,null,'Late server response cannot revive cleared authentication');
    equal(account.api.hasRestorableSession,false,'Late response cannot revive a saved token pair');
    equal(stored.has(key),false,'Late response cannot repersist cleared credentials');
    equal(node('member-card').hidden,true,'Late response cannot display a cleared account');
    equal(node('admin-panel').hidden,true,'Late Pro/admin response cannot reveal administrator controls');
    equal(privilegedCalls().length,0,'Late response cannot trigger administrator queries');
    equal(node('login-card').hidden,false,'Canceled restoration returns to sign-in');
    equal(node('retry-session').hidden,true,'Canceled restoration does not offer obsolete credentials for retry');
  }else if(name==='billing-offline'){
    const account=await boot;
    equal(restoreCalls,1,'Billing configuration outage does not skip session restoration');
    equal(account.api.session?.email,user.email,'Authentication verifies despite unrelated billing failure');
    equal(node('member-card').hidden,false,'Verified account remains accessible while billing is offline');
    equal(node('plan-badge').textContent,'Free','Displayed rights still come from the membership server');
    equal(node('admin-panel').hidden,true,'Billing outage cannot enable cached administrator claims');
    equal(node('session-restore-note').hidden,true,'Completed authentication does not leave a retry notice for a billing outage');
    equal(node('send-code').disabled,false,'Completed bootstrap releases the authentication busy state');
  }else if(name==='admin-auth-revoked'){
    const account=await boot;
    equal(restoreCalls,1,'Server membership initially restores the administrator once');
    equal(privilegedCalls().length,1,'Verified administrator attempts to load its member list');
    equal(account.api.session,null,'Revoked session during administrator loading is cleared');
    equal(account.api.hasRestorableSession,false,'Rejected refresh after administrator loading removes recovery credentials');
    equal(stored.has(key),false,'Revoked administrator credentials are removed from storage');
    equal(node('member-card').hidden,true,'Revocation during administrator loading hides already-rendered account');
    equal(node('admin-panel').hidden,true,'Revocation during administrator loading hides all administrator controls');
    equal(node('login-card').hidden,false,'Revocation returns to sign-in');
    ok(node('login-message').textContent.trim().length>0,'Revocation explains why authentication is needed');
    equal(node('retry-session').hidden,true,'Revoked administrator credentials are not offered for recovery');
  }else if(name==='transient-retry'||name==='disabled-provider-retry'){
    const account=await boot;
    equal(restoreCalls,1,'First restoration attempt runs once');
    equal(account.api.session,null,'Transient verification failure grants no authenticated session');
    equal(account.api.hasRestorableSession,true,'Transient verification failure preserves the retryable credentials');
    equal(JSON.parse(stored.get(key)).refresh,pair.refresh,'Transient server failure does not discard saved refresh token');
    equal(node('member-card').hidden,true,'Transient error hides cached membership');
    equal(node('admin-panel').hidden,true,'Transient error cannot restore cached administrator privileges');
    equal(node('session-restore-note').hidden,false,'Transient error leaves the recovery notice visible');
    equal(node('retry-session').tagName,'BUTTON','Recovery control is a semantic button');
    equal(node('retry-session').hidden,false,'Recovery button is visible after a transient failure');
    equal(node('retry-session').disabled,false,'Recovery can be requested even when new sign-in providers are disabled');
    ok(node('retry-session').textContent.trim().length>0,'Recovery button has an accessible text label');
    ok(node('session-restore-note').getAttribute('role')==='status'||node('session-restore-message').getAttribute('role')==='status','Recovery feedback is announced as a status');
    if(name==='disabled-provider-retry'){
      for(const id of ['email','send-code','verify-code','google-login'])equal(node(id).disabled,true,`${id} stays disabled while configured providers are unavailable`);
      equal(node('setup-note').hidden,false,'Provider readiness notice remains visible alongside recovery');
    }
    const before=requests.length;await tick();await tick();
    equal(requests.length,before,'A transient failure does not start an unbounded timer retry loop');
    transient=false;blockMembership=true;
    const retry=click('retry-session');await waitFor(()=>memberPending,'Explicit retry starts a new server check');
    equal(node('retry-session').disabled,true,'Retry disables itself while the next attempt is pending');
    await click('retry-session');equal(restoreCalls,2,'Double click cannot start a parallel recovery attempt');
    releaseMember(serverMember);await retry;
    equal(account.api.session?.email,user.email,'Successful retry establishes the verified identity');
    equal(node('member-card').hidden,false,'Successful retry presents the account');
    equal(node('plan-badge').textContent,'Free','Server Free overrides stale cached Pro');
    equal(node('admin-panel').hidden,true,'Server nonadministrator overrides cached administrator flag');
    equal(node('member-email').textContent,user.email,'Server email overrides unverified cached email');
    equal(privilegedCalls().length,0,'Stale administrator claims never trigger an admin request');
    equal(node('retry-session').hidden,true,'Successful retry removes recovery control');
    equal(node('session-restore-note').hidden,true,'Successful retry removes recovery notice');
  }else if(name==='invalid-session'){
    const account=await boot;
    equal(restoreCalls,1,'Invalid session is checked once at page boot');
    equal(account.api.session,null,'Rejected credentials never produce a logged-in session');
    equal(account.api.hasRestorableSession,false,'Rejected refresh credentials are not retryable');
    equal(stored.has(key),false,'Rejected credentials are removed from session storage');
    equal(node('member-card').hidden,true,'Rejected credentials leave member details hidden');
    equal(node('admin-panel').hidden,true,'Rejected credentials leave administration hidden');
    equal(node('login-card').hidden,false,'Rejected credentials return to sign-in');
    equal(node('session-restore-note').hidden,true,'Invalid credentials do not show a misleading recovery retry');
    equal(node('retry-session').hidden,true,'Invalid credentials hide recovery control');
    ok(node('login-message').textContent.trim().length>0,'Sign-in form explains why login is needed again');
    equal(membershipCalls(),0,'Invalid authentication never queries privileges');
    equal(node('send-code').disabled,false,'Fresh email authentication remains available');
    equal(node('google-login').disabled,false,'Fresh Google authentication remains available');
  }else if(name==='blocked-storage'){
    const account=await boot;
    equal(account.api.session,null,'Blocked storage starts without an authenticated account');
    equal(account.api.hasRestorableSession,false,'Blocked storage cannot invent a restorable login');
    equal(membershipCalls(),0,'Blocked storage does not contact membership until user authentication');
    equal(node('session-storage-note').hidden,false,'Storage restriction is explained visibly');
    ok(node('session-storage-note').textContent.trim().length>0,'Storage warning is not empty');
    equal(node('retry-session').disabled,true,'No saved credentials means recovery is unavailable');
    equal(node('send-code').disabled,false,'Blocked persistence still permits ordinary email sign-in');
    node('email').value=user.email;await submit('email-form');node('code').value='123456';await submit('code-form');
    equal(account.api.session?.email,user.email,'Ordinary authentication works in memory when storage is blocked');
    equal(node('member-card').hidden,false,'New login still displays verified account details');
    equal(node('plan-badge').textContent,'Free','New login still uses server membership');
    equal(node('session-storage-note').hidden,false,'In-memory login does not hide the refresh persistence warning');
    await click('sign-out');equal(account.api.session,null,'Sign-out clears in-memory login under blocked storage');
    equal(node('member-card').hidden,true,'Sign-out hides account under blocked storage');
    equal(node('login-card').hidden,false,'Sign-out returns to login under blocked storage');
    equal(persistentPreferences.size,0,'Authentication does not leak into persistent localStorage');
  }else if(name==='logout'){
    const account=await boot;
    equal(node('member-card').hidden,false,'Stored login restores before logout test');
    equal(account.api.session?.email,user.email,'Restored session is verified before logout');
    await click('sign-out');
    equal(account.api.session,null,'Sign-out clears authenticated in-memory state');
    equal(account.api.hasRestorableSession,false,'Sign-out clears pending restoration credentials');
    equal(stored.has(key),false,'Sign-out removes saved login from the tab');
    ok(removeCalls>0,'Sign-out actively removes the storage entry');
    equal(node('member-card').hidden,true,'Sign-out hides member card');
    equal(node('admin-panel').hidden,true,'Sign-out hides administrative controls');
    equal(node('login-card').hidden,false,'Sign-out returns to login controls');
    equal(node('session-restore-note').hidden,true,'Sign-out does not leave a recovery notice');
    equal(node('retry-session').hidden,true,'Sign-out does not offer recovery for removed credentials');
    equal(requests.filter(item=>item.url.endsWith('/auth/v1/logout?scope=local')).length,1,'Sign-out revokes the current server session once');
    const after=new MembershipApi(config,globalThis.fetch,()=>Date.now(),sessionStorage);
    equal(after.hasRestorableSession,false,'A fresh controller API cannot restore after sign-out');
    const before=requests.length;equal(await after.restore(),null,'Page refresh after logout has no session');
    equal(requests.length,before,'Page refresh after logout makes no auth verification request');
    equal(persistentPreferences.size,0,'Authentication never uses persistent localStorage');
  }
  console.log(`PASS: ${checks} session UI checks (${name}).`);
}
