import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const root=new URL('./dist/account/',import.meta.url);
const model=await import(new URL('international-billing-model.js',root));
const {MembershipApi}=await import(new URL('membership-api.js',root));
const source=(await readFile(new URL('international-billing-ui.js',root),'utf8')).replace(/from '(\.\/[^']+)'/g,(_,path)=>'from '+JSON.stringify(new URL(path,root).href));
const globals=Object.fromEntries(['document','location','history','window','fetch'].map(key=>[key,Object.getOwnPropertyDescriptor(globalThis,key)]));
let count=0,scenario=0;
const eq=(a,b,message)=>{assert.deepEqual(a,b,message);count++;};
const throws=work=>{assert.throws(work);count++;};
const reject=async work=>{await assert.rejects(work);count++;};
const id='a0000000-0000-4000-8000-000000000001',otherId='a0000000-0000-4000-8000-000000000002';
const future=new Date(Date.now()+86400000).toISOString();
const config={enabled:true,provider:'lemonsqueezy',amount:3000,currency:'KRW',interval:'month',testMode:true,termsVersion:'fixture-v1',termsUrl:'https://nephylo.example/terms',privacyUrl:'https://nephylo.example/privacy',refundUrl:'https://nephylo.example/refund',checkoutOrigin:'https://nephylo-test.lemonsqueezy.com'};
const checkout={checkoutUrl:config.checkoutOrigin+'/checkout/custom/fixture-id',expiresAt:future};
const active={provider:'lemonsqueezy',status:'active',auto_renew:true,cancel_at_period_end:false,current_period_end:future,next_billing_at:future};
const free={user_id:id,plan:'free',status:'free'},pro={user_id:id,plan:'pro',status:'active'};
const result=(subscription=null,pendingCheckout=false,membership=free)=>({subscription,pendingCheckout,membership});
eq(model.internationalConfiguration({enabled:false}).enabled,false);
eq(model.internationalConfiguration(config).checkoutOrigin,config.checkoutOrigin);
for(const patch of [{provider:'evil'},{amount:1},{currency:'USD'},{interval:'year'},{testMode:null},{termsVersion:''},{checkoutOrigin:'https://lemonsqueezy.com.evil.test'},{checkoutOrigin:'https://a.b.lemonsqueezy.com'},{checkoutOrigin:'http://nephylo-test.lemonsqueezy.com'},{checkoutOrigin:config.checkoutOrigin+'/checkout'},{termsUrl:'javascript:alert(1)'}])throws(()=>model.internationalConfiguration({...config,...patch}));
eq(model.verifiedInternationalUrl(checkout,config,'checkout'),checkout.checkoutUrl);
eq(model.verifiedInternationalUrl({portalUrl:config.checkoutOrigin+'/billing/fixture?expires=123&signature=safe'},config,'portal'),config.checkoutOrigin+'/billing/fixture?expires=123&signature=safe');
for(const url of ['http://nephylo-test.lemonsqueezy.com/checkout/custom/x','https://nephylo-test.lemonsqueezy.com.evil.test/checkout/custom/x','https://another.lemonsqueezy.com/checkout/custom/x','https://user:pass@nephylo-test.lemonsqueezy.com/checkout/custom/x',config.checkoutOrigin+'/checkout/custom/x#secret',config.checkoutOrigin+'/billing/x',config.checkoutOrigin+'/checkout/custom/../evil'])throws(()=>model.verifiedInternationalUrl({...checkout,checkoutUrl:url},config,'checkout'));
throws(()=>model.verifiedInternationalUrl({...checkout,expiresAt:'2000-01-01'},config,'checkout'));
throws(()=>model.verifiedInternationalUrl({portalUrl:config.checkoutOrigin+'/checkout/custom/x'},config,'portal'));
throws(()=>model.checkedInternationalStatus({subscription:null}));
throws(()=>model.checkedInternationalStatus(result({...active,provider:'toss'})));
eq(model.internationalBlocksCheckout(null,false),false);
eq(model.internationalBlocksCheckout(null,true),true);
eq(model.internationalBlocksCheckout({...active,status:'cancelled',auto_renew:false},false),true,'Canceled but paid period prevents overlap');
eq(model.internationalBlocksCheckout({...active,status:'expired',auto_renew:false,current_period_end:'2000-01-01'},false),false);

async function harness({signedIn=true,configuration=config,returned=false,otherBlocks=false}={}){
  const nodes=new Map(),calls=[],errors=[],changes=[],navigations=[],answers=new Map();
  const node=name=>{if(!nodes.has(name))nodes.set(name,{hidden:false,disabled:false,checked:false,textContent:'',href:'',listeners:new Map(),setAttribute(){},addEventListener(type,handler){this.listeners.set(type,handler);}});return nodes.get(name);};
  globalThis.document={getElementById:node,createElement(){throw new Error('No checkout SDK permitted');},head:{append(){throw new Error('No third-party script permitted');}}};
  globalThis.location=new URL('https://nephylo.example/account/');globalThis.history={replaceState(){}};globalThis.window={};globalThis.fetch=()=>{throw new Error('No external network permitted');};
  const api={config:{},generation:1,session:signedIn?{userId:id}:null,
    async internationalBilling(action,body){calls.push({action,body});const queue=answers.get(action);if(queue?.length){const value=queue.shift();if(value instanceof Error)throw value;return await value;}if(action==='config')return configuration;if(action==='status')return result();throw new Error('Unexpected fixture action '+action);}};
  let ui;
  const run=async(target,work)=>{ui.controls(true);try{await work();}catch(error){errors.push(error);}finally{ui.controls(false);}};
  const module=await import('data:text/javascript;base64,'+Buffer.from(source+'\n// scenario '+(++scenario)).toString('base64'));
  ui=module.createInternationalBillingUi({getApi:()=>api,run,onMembership:value=>changes.push(value),otherBlocks:()=>otherBlocks,returned,navigate:url=>navigations.push(url)});
  const fire=async(name,type='click')=>{if(node(name).disabled)return false;await node(name).listeners.get(type)();return true;};
  const queue=(action,...values)=>answers.set(action,[...(answers.get(action)||[]),...values]);
  const init=async()=>{await ui.configure();await ui.refresh();ui.controls(false);};
  const consent=async()=>{node('international-consent').checked=true;await fire('international-consent','change');};
  return {node,api,ui,calls,errors,changes,navigations,fire,queue,init,consent};
}

try{
  let h=await harness({signedIn:false,configuration:{enabled:false}});await h.init();
  eq(h.node('international-unavailable').hidden,false,'Coming-soon control visible when unconfigured');
  eq(h.node('international-start').disabled,true,'Unconfigured checkout disabled');
  eq(h.node('international-purchase').hidden,true);eq(h.calls.map(x=>x.action),['config'],'Guest does not send authenticated requests');
  h=await harness({signedIn:false});await h.init();eq(h.node('international-login-note').hidden,false);eq(await h.fire('international-start'),false);
  h=await harness({returned:true});await h.init();eq(h.node('international-return').hidden,false);eq(h.changes.some(x=>x.plan==='pro'),false,'Return hint alone cannot grant Pro');eq(h.node('international-start').disabled,true,'Consent required after login');
  await h.consent();h.queue('checkout',checkout);await h.fire('international-start');
  eq(h.navigations,[checkout.checkoutUrl]);eq(h.calls.at(-1),{action:'checkout',body:{consent:true,termsVersion:config.termsVersion,locale:'ko'}});
  eq(h.node('international-start').disabled,true,'In-flight hosted checkout cannot be repeated');eq(h.ui.blocksOther(),true,'In-flight checkout blocks other provider');
  eq(h.changes.some(x=>x.plan==='pro'),false,'Creating checkout cannot grant Pro');
  h=await harness();h.queue('status',result(null,true));await h.init();eq(h.node('international-pending').hidden,false);eq(h.node('international-purchase').hidden,true);eq(h.ui.blocksOther(),true);
  h.queue('sync',result(active,false,pro));await h.fire('international-refresh');eq(h.changes.at(-1),pro,'Only verified server membership activates Pro');eq(h.node('international-status').textContent,'매월 자동 갱신');eq(h.node('international-portal').hidden,false);
  h.queue('portal',{portalUrl:config.checkoutOrigin+'/billing/fixture'});await h.fire('international-portal');eq(h.navigations,[config.checkoutOrigin+'/billing/fixture']);
  h.queue('portal',{portalUrl:'https://evil.example/billing/fixture'});await h.fire('international-portal');eq(h.navigations.length,1,'Unsafe portal cannot navigate');eq(h.errors.length,1);
  h=await harness({otherBlocks:true});await h.init();await h.consent();eq(h.node('international-other').hidden,false);eq(h.node('international-start').disabled,true);eq(h.calls.some(x=>x.action==='checkout'),false,'Domestic active/pending blocks overseas checkout');
  h=await harness();await h.init();await h.consent();h.queue('checkout',new Error('Offline'));await h.fire('international-start');eq(h.node('international-start').disabled,true,'Ambiguous failure does not permit new checkout');eq(h.node('international-pending').hidden,false);h.queue('sync',result(null,true));await h.fire('international-refresh');eq(h.node('international-start').disabled,true,'Pending server result stays locked');h.queue('sync',result());await h.fire('international-refresh');eq(h.node('international-start').disabled,false,'Authoritative empty status permits retry with existing explicit consent');
  h=await harness();await h.init();await h.consent();let resolve;h.queue('checkout',new Promise(done=>resolve=done));const late=h.fire('international-start');h.api.session={userId:otherId};h.api.generation++;resolve(checkout);await late;eq(h.navigations.length,0,'Late checkout cannot open after account changes');
  h=await harness();await h.init();await h.consent();h.queue('checkout',new Promise(done=>resolve=done));const reset=h.fire('international-start');h.ui.reset();resolve(checkout);await reset;eq(h.navigations.length,0,'Reset invalidates in-flight checkout');eq(h.node('international-consent').checked,false,'Sign-out clears consent');
  h=await harness();await h.init();h.queue('sync',new Promise(done=>resolve=done));const stale=h.fire('international-refresh');h.api.session={userId:otherId};h.api.generation++;resolve(result(active,false,pro));await stale;eq(h.changes.some(x=>x.plan==='pro'),false,'Stale account response cannot grant another account Pro');
  h=await harness();h.queue('config',{...config,checkoutOrigin:'https://evil.example'});await h.init();eq(h.node('international-start').disabled,true,'Invalid checkout host fails closed');

  const requests=[],api=new MembershipApi({SupabaseUrl:'https://fixture.supabase.co',PublishableKey:'sb_publishable_fixture123'},async(url,options)=>{requests.push({url,options});return {ok:true,status:200,text:async()=>JSON.stringify(result())};});
  api.session={userId:id,email:'fixture@example.test',access:'fixture-token',refresh:'fixture-refresh',expires:Date.now()+3600000};
  await api.internationalBilling('checkout',{consent:true,termsVersion:'fixture',locale:'en'});
  eq(requests[0].url,'https://fixture.supabase.co/functions/v1/international-billing-api');eq(requests[0].options.headers.Authorization,'Bearer fixture-token');eq(JSON.parse(requests[0].options.body).locale,'en');eq(requests[0].options.redirect,'error');
  await reject(()=>api.internationalBilling('refund'));api.clear();await reject(()=>api.internationalBilling('portal'));
  const guest=new MembershipApi({},()=>{throw new Error('No config must not fetch');});eq((await guest.internationalBilling('config')).enabled,false);
  console.log(`PASS: ${count} international billing model/API/DOM checks across ${scenario} scenarios. No real accounts, checkout SDK, network or payments.`);
}finally{for(const [key,descriptor]of Object.entries(globals)){if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];}}
