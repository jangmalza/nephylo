import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('./dist/account/admin-members.js',import.meta.url),'utf8');
const model=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const accountSource=await readFile(new URL('./dist/account/account.js',import.meta.url),'utf8');
const check=(a,b,n)=>{assert.deepEqual(a,b,n);checks++;};
const ok=(v,n)=>{assert.ok(v,n);checks++;};
let checks=0,scenario=0;
const expiry=new Date(Date.now()+30*86400000).toISOString(),manualExpiry=new Date(Date.now()+4*86400000).toISOString();
const emptyManual={plan:'free',status:'free',expires_at:null,updated_at:null,active:false};
const paid={user_id:'a2000000-0000-4000-8000-000000000002',email:'paid@example.test',plan:'pro',status:'active',expires_at:expiry,manual:emptyManual,billing:{status:'active',auto_renew:true,expires_at:expiry,invoice_status:'paid',active:true}};
const manual={plan:'pro',status:'active',expires_at:manualExpiry,updated_at:new Date().toISOString(),active:true};
check(model.manualEditorValues(paid),{plan:'free',expiry:''},'Payment-only Pro must not prefill a manual Pro grant');
const values=model.manualEditorValues({...paid,manual});
check(values.plan,'pro','Actual manual grant prefilled');check(values.expiry,manualExpiry,'Untouched manual expiry preserves its exact server string and precision');
ok(!model.manualDescription(emptyManual).includes(expiry),'Empty manual source never borrows payment expiry');
ok(model.billingDescription(paid.billing).includes('자동 갱신 켜짐'),'Paid renewal visible separately');
ok(model.billingDescription({...paid.billing,auto_renew:false}).includes('꺼짐'),'Canceled renewal still shows paid access');
check(model.billingDescription({...paid.billing,active:false,invoice_status:'refunded'}),'전액 환불','Refund has explicit state');
check(model.billingDescription({status:'none'}),'결제 구독 없음','No subscription is explicit');
const international={provider:'lemonsqueezy',status:'active',auto_renew:true,expires_at:expiry,active:true};
ok(model.internationalDescription(international).includes('Pro'),'International paid access has its own source');
ok(model.internationalDescription(undefined).includes('서버 업데이트'),'Older backend is unknown, not no international subscription');
check(model.internationalDescription({provider:'lemonsqueezy',status:'none',active:false}),'결제 구독 없음','Verified absent international subscription is explicit');
check(model.manualEditorValues({...paid,international}).plan,'free','International payment never prefills manual Pro');
assert.throws(()=>model.checkedAdminMember({...paid,international:{...international,provider:'evil'}}),/해외 구독/);checks++;
for(const item of [{...paid,manual:undefined},{...paid,billing:undefined},{...paid,manual:{...manual,expires_at:'bad'}},{...paid,billing:{...paid.billing,active:'yes'}}]){assert.throws(()=>model.manualEditorValues(item),/서버 업데이트/);checks++;}
const audit={legacy:false,created_at:new Date().toISOString(),reason:'manual test',before_manual:emptyManual,after_manual:manual};
ok(model.auditDescription(audit).includes('수동 부여 없음 → Pro'),'Actual manual before/after shown');
ok(model.auditDescription({...audit,legacy:true,before_manual:null,after_manual:null}).includes('구분할 수 없습니다'),'Old audit never fabricates a manual change');
assert.throws(()=>model.auditDescription({...audit,after_manual:null}));checks++;

const clock=Date.parse('2026-10-12T06:12:34.567Z'),day=86400000;
for(const days of [7,30,90,365])check(model.presetManualExpiry(days,clock),new Date(clock+days*day).toISOString(),`Preset ${days} days is anchored to selection time`);
for(const days of [0,-7,1,366,Infinity,'30',NaN]){assert.throws(()=>model.presetManualExpiry(days,clock));checks++;}
const exact='2026-11-15T04:05:06.789+09:00';check(model.checkedManualExpiry(exact,clock),exact,'Validation never rewrites an untouched offset/fraction timestamp');
check(model.checkedManualExpiry(new Date(clock+366*day).toISOString(),clock),new Date(clock+366*day).toISOString(),'Exact server bound is accepted');
for(const value of ['','invalid',new Date(clock).toISOString(),new Date(clock-1).toISOString(),new Date(clock+366*day+1).toISOString()]){assert.throws(()=>model.checkedManualExpiry(value,clock));checks++;}
const localTarget=new Date(2026,10,4,23,59,0,0);check(model.customManualExpiry('2026-11-04',clock),localTarget.toISOString(),'Custom date ends on that local calendar day, not a UTC date');
check(model.customManualExpiry('2028-02-29',Date.parse('2028-02-01T00:00:00Z')),new Date(2028,1,29,23,59,0,0).toISOString(),'Leap day round-trips');
for(const value of ['2026-02-29','2026-04-31','2026-13-01','2026-00-01','2026-11-00','2026-11-32','2026-1-1','2026-11-04T00:00','2026-10-01','2028-11-01']){assert.throws(()=>model.customManualExpiry(value,clock));checks++;}

const originals=Object.fromEntries(['document','fetch','setInterval','__adminFixture'].map(k=>[k,Object.getOwnPropertyDescriptor(globalThis,k)]));
async function harness(member=paid,auditAnswer={entries:[audit]}){
  const nodes=new Map(),calls=[],intervals=[],languageCallbacks=[];
  function make(tag='div') {return {tag,hidden:false,disabled:false,value:'',textContent:'',children:[],listeners:new Map(),attributes:new Map(),
    append(...items){this.children.push(...items);},replaceChildren(...items){this.children=[...items];},focus(){},
    setAttribute(key,value){this.attributes.set(key,String(value));},getAttribute(key){return this.attributes.get(key)??null;},
    addEventListener(type,fn){this.listeners.set(type,fn);},querySelector(name){return element(tag+'/'+name);}};}
  function element(id){if(!nodes.has(id))nodes.set(id,make(id));return nodes.get(id);}
  globalThis.document={getElementById:element,createElement:make,querySelectorAll(){return [...nodes.values()];},addEventListener(){},hidden:false};
  globalThis.setInterval=fn=>{intervals.push(fn);return 0;};
  globalThis.fetch=async()=>({ok:true,json:async()=>({SupabaseUrl:'https://fixture.supabase.co',PublishableKey:'sb_publishable_fixture'})});
  class MembershipApi {
    constructor(){this.config={};this.session={userId:'owner'};}
    otpStatus(){return {pending:false,seconds:0};}
    async rpc(name,body){calls.push({name,body});if(name==='admin_list_members')return {members:[member],has_more:false};if(name==='admin_get_membership_audit'){if(auditAnswer instanceof Error)throw auditAnswer;return auditAnswer;}if(name==='admin_set_membership')return {plan:'pro'};throw new Error('Unexpected RPC');}
    async membership(){return {email:'owner@example.test',is_admin:true,plan:'free',status:'free',expires_at:null};}
  }
  globalThis.__adminFixture={MembershipApi,membershipFreshness:()=> 'current',checkedMembership:x=>x,createGoogleLogin:()=>({active:false}),createPaymentsUi:()=>({controls(){},async configure(){},async refresh(){},reset(){}}),...model};
  const localization={t:(source,args={})=>source.replace(/\{(\w+)\}/g,(_,key)=>args[key]??'{'+key+'}'),getLocale:()=> 'ko-KR',date:value=>value?new Date(value).toLocaleString('ko-KR',{dateStyle:'medium',timeStyle:'short'}):'—',text:(id,value)=>{element(id).textContent=typeof value==='function'?value():value;},onLanguageChange(fn){languageCallbacks.push(fn);},translateDocument(){}};
  Object.assign(globalThis.__adminFixture,localization);
  const body=accountSource.replace(/^import .*;\r?\n/gm,'');
  const prefix='const {'+Object.keys(globalThis.__adminFixture).join(',')+'}=globalThis.__adminFixture;\n';
  const module=await import('data:text/javascript;base64,'+Buffer.from(prefix+body+'\nexport {loadMembers,controls,run,pendingGrant}; // '+(++scenario)).toString('base64'));
  const fire=async(node,event='click')=>{const handler=node.listeners.get(event);assert.ok(handler);await handler({preventDefault(){}});};
  return {module,element,calls,fire,intervals,languageCallbacks};
}
try {
  let h=await harness();await h.module.loadMembers();
  const cells=h.element('members').children[0].children;
  check(cells.length,5,'Admin table retains five distinct columns');ok(cells[1].textContent.includes('Pro'),'Effective Pro visible');check(cells[2].textContent,'수동 부여 없음','Manual column says no manual grant');ok(cells[3].textContent.includes('자동 갱신'),'Billing column shows renewal');ok(!cells[3].textContent.includes('Lemon Squeezy'),'No overseas guide is shown without historical subscription');
  await h.fire(cells[4].children[0]);
  check(h.element('grant-plan').value,'free','Clicking paid member keeps manual editor Free');check(h.element('grant-expiry').value,'','Clicking paid member leaves manual expiry empty');
  ok(h.element('grant-sources').textContent.includes('현재 적용: Pro'),'Editing context retains effective state');
  check(h.calls.at(-1),{name:'admin_get_membership_audit',body:{p_user_id:paid.user_id,p_limit:10}},'Audit request is scoped to selected member');
  ok(h.element('grant-audit').textContent.includes('manual test'),'Audit reasons rendered using plain text');
  h.element('grant-reason').value='remove manual only';await h.fire(h.element('grant-form'),'submit');
  ok(h.element('grant-summary').textContent.includes('수동 Pro 권한 해제'),'Review describes only manual edit');ok(h.element('grant-summary').textContent.includes('변경하지 않습니다'),'Review explains payment remains');
  await h.fire(h.element('confirm-grant'));
  check(h.calls.find(c=>c.name==='admin_set_membership').body,{p_user_id:paid.user_id,p_plan:'free',p_expires_at:null,p_reason:'remove manual only'},'Confirm never copies effective paid expiry into manual grant');
  h=await harness({...paid,manual});await h.module.loadMembers();await h.fire(h.element('members').children[0].children[4].children[0]);
  check(h.element('grant-custom-date').hidden,true,'Existing exact manual expiry does not require date editing');
  h.element('grant-reason').value='retain precise expiry';await h.fire(h.element('grant-form'),'submit');await h.fire(h.element('confirm-grant'));
  check(h.calls.find(c=>c.name==='admin_set_membership').body.p_expires_at,manualExpiry,'Review/confirm without an expiry choice retains exact server expiry');
  h=await harness();await h.module.loadMembers();await h.fire(h.element('members').children[0].children[4].children[0]);
  for(const suffix of ['7','30','90','365','custom'])check(h.element('grant-duration-'+suffix).disabled,true,`Free disables duration ${suffix}`);
  h.element('grant-plan').value='pro';const defaultBefore=Date.now();await h.fire(h.element('grant-plan'),'change');
  check(h.element('grant-duration-30').getAttribute('aria-pressed'),'true','New Pro defaults to one explicit 30-day draft');
  check(h.element('grant-custom-date').hidden,true,'Preset mode hides manual date entry');check(h.element('grant-expiry').disabled,true,'Preset mode disables the date field');
  h.element('grant-reason').value='30-day test';await h.fire(h.element('grant-form'),'submit');
  const defaultExpiry=h.module.pendingGrant.p_expires_at;ok(Date.parse(defaultExpiry)>=defaultBefore+30*day&&Date.parse(defaultExpiry)<=Date.now()+30*day,'Default uses now, never the existing paid expiry');
  await h.fire(h.element('back-grant'));
  h.element('grant-plan').value='free';await h.fire(h.element('grant-plan'),'change');h.element('grant-plan').value='pro';await h.fire(h.element('grant-plan'),'change');
  const savedNow=Date.now;try{Date.now=()=>savedNow()+3600000;for(const tick of h.intervals)tick();for(const refresh of h.languageCallbacks)refresh();}finally{Date.now=savedNow;}
  await h.fire(h.element('grant-form'),'submit');check(h.module.pendingGrant.p_expires_at,defaultExpiry,'Toggle, timer and language refresh never move the fixed expiry');await h.fire(h.element('back-grant'));
  const chosenBefore=Date.now();await h.fire(h.element('grant-duration-7'));await h.fire(h.element('grant-form'),'submit');
  const chosenExpiry=h.module.pendingGrant.p_expires_at;ok(Date.parse(chosenExpiry)>=chosenBefore+7*day&&Date.parse(chosenExpiry)<=Date.now()+7*day,'7-day choice replaces the draft from selection time');
  check(h.element('grant-duration-7').getAttribute('aria-pressed'),'true','Selected preset has accessible pressed state');check(h.element('grant-duration-30').getAttribute('aria-pressed'),'false','Previous preset loses pressed state');
  for(const refresh of h.languageCallbacks)refresh();for(const tick of h.intervals)tick();await h.fire(h.element('confirm-grant'));
  check(h.calls.find(c=>c.name==='admin_set_membership').body.p_expires_at,chosenExpiry,'Confirmation submits the reviewed fixed expiry');
  h=await harness({...paid,manual});await h.module.loadMembers();await h.fire(h.element('members').children[0].children[4].children[0]);
  await h.fire(h.element('grant-duration-custom'));check(h.element('grant-custom-date').hidden,false,'Custom choice reveals the calendar');check(h.element('grant-expiry').disabled,false,'Custom calendar can be edited');
  h.element('grant-expiry').value='2026-02-30';await h.fire(h.element('grant-expiry'),'change');h.element('grant-reason').value='invalid date';await h.fire(h.element('grant-form'),'submit');
  check(h.module.pendingGrant,null,'Invalid calendar date never reaches review');check(h.calls.some(c=>c.name==='admin_set_membership'),false,'Invalid calendar date never writes a grant');
  const targetDate=new Date(Date.now()+10*day),targetText=[targetDate.getFullYear(),String(targetDate.getMonth()+1).padStart(2,'0'),String(targetDate.getDate()).padStart(2,'0')].join('-');
  h.element('grant-expiry').value=targetText;await h.fire(h.element('grant-expiry'),'change');
  let finishBusy;const busyWork=h.module.run('admin-message',()=>new Promise(resolve=>{finishBusy=resolve;}));
  for(const suffix of ['7','30','90','365','custom'])check(h.element('grant-duration-'+suffix).disabled,true,`Busy disables duration ${suffix}`);
  await h.fire(h.element('grant-duration-365'));check(h.element('grant-duration-custom').getAttribute('aria-pressed'),'true','Programmatic preset click cannot change a busy draft');finishBusy();await busyWork;
  await h.fire(h.element('grant-form'),'submit');check(h.module.pendingGrant.p_expires_at,model.customManualExpiry(targetText),'Custom date review uses local 23:59 exactly');
  await h.fire(h.element('back-grant'));await h.fire(h.element('grant-duration-90'));h.element('grant-reason').value='';await h.fire(h.element('grant-form'),'submit');check(h.module.pendingGrant,null,'Preset convenience does not remove the required audit reason');
  h=await harness({...paid,international});await h.module.loadMembers();const overseasCell=h.element('members').children[0].children[3].textContent;ok(overseasCell.includes('국내 · Toss Payments')&&overseasCell.includes('해외 · Lemon Squeezy'),'Admin payment column distinguishes both providers');ok(overseasCell.split('해외 · Lemon Squeezy')[1].includes('Pro'),'Overseas Pro is not mislabeled as no subscription');
  h=await harness({...paid,international:{provider:'lemonsqueezy',status:'none',active:false}});await h.module.loadMembers();ok(!h.element('members').children[0].children[3].textContent.includes('Lemon Squeezy'),'Verified absent overseas subscription has no dormant provider line');
  h=await harness({...paid,manual:undefined});await assert.rejects(h.module.loadMembers(),/서버 업데이트/);checks++;check(h.element('members').children.length,0,'Old server does not expose an unsafe editor');
  h=await harness(paid,Object.assign(new Error('forbidden'),{status:403}));await h.module.loadMembers();await h.fire(h.element('members').children[0].children[4].children[0]);check(h.element('admin-panel').hidden,true,'Revoked admin access hides panel during history lookup');
  h=await harness(paid,new Error('offline'));await h.module.loadMembers();await h.fire(h.element('members').children[0].children[4].children[0]);ok(h.element('grant-audit').textContent.includes('불러오지 못했습니다'),'History network failure is not mistaken for no history');
  console.log(`PASS: ${checks} admin source model and actual account DOM handler checks; no network.`);
} finally {for(const [key,descriptor] of Object.entries(originals)){if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];}}
