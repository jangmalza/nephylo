import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';

// Exercise the actual UI event handlers and rendered control states. No browser,
// network, payment SDK, accounts, storage, or additional test dependency is used.
const model=await readFile(new URL('./dist/account/billing-model.js',import.meta.url),'utf8');
const modelUrl='data:text/javascript;base64,'+Buffer.from(model).toString('base64');
const source=(await readFile(new URL('./dist/account/billing-ui.js',import.meta.url),'utf8'))
  .replace(/(['"])\.\/billing-model\.js(?:\?[^'"]*)?\1/,JSON.stringify(modelUrl))
  .replace(/(['"])(\.\/localization\.js(?:\?[^'"]*)?)\1/,(_,quote,path)=>JSON.stringify(new URL(path,new URL('./dist/account/',import.meta.url)).href));
const initialGlobals=Object.fromEntries(['document','location','history','window','fetch'].map(key=>[key,Object.getOwnPropertyDescriptor(globalThis,key)]));
const id='cb220718-12f7-4197-86a4-d7271d2b8201',state='fixture_nonce_32_characters_long_123',customerKey='customer-random-owner',authKey='fixture-auth-key';
const callback='https://widget.example/account/?'+new URLSearchParams({billing:'success',checkoutId:id,state,customerKey,authKey});
const config={enabled:true,amount:3000,currency:'KRW',interval:'month',clientKey:'test_ck_fixture123',termsVersion:'2026-10-12-v1',termsUrl:'https://widget.example/terms',privacyUrl:'https://widget.example/privacy',refundUrl:'https://widget.example/refunds'};
const future=new Date(Date.now()+30*86400000).toISOString();
const active={id,status:'active',auto_renew:true,current_period_end:future,next_billing_at:future};
const canceled={...active,status:'canceled',auto_renew:false,next_billing_at:null};
const free={user_id:id,email:'member@example.test',plan:'free',status:'free',expires_at:null};
const pro={...free,plan:'pro',status:'active',expires_at:future};
let checks=0,scenarios=0;
const equal=(actual,expected,message)=>{assert.deepEqual(actual,expected,message);checks++;};
const ok=(value,message)=>{assert.ok(value,message);checks++;};
const result=(subscription=null,status='none',membership=free)=>({subscription,status,membership});
const error=code=>Object.assign(new Error('fixture '+code),{code});
async function harness({url='https://widget.example/account/#billing',signedIn=true,configured=true,configuration=config,otherBlocks=false}={}){
  const nodes=new Map(),calls=[],errors=[],changes=[],historyCalls=[],answers=new Map();
  const element=name=>{
    if(!nodes.has(name))nodes.set(name,{hidden:name==='billing-cancel-review',disabled:false,textContent:'',checked:false,href:'',listeners:new Map(),
      setAttribute(){},addEventListener(type,handler){this.listeners.set(type,handler);}});
    return nodes.get(name);
  };
  globalThis.document={getElementById:element,createElement(){throw new Error('Payment SDK must not be loaded in these state tests');},head:{append(){throw new Error('No external script allowed');}}};
  globalThis.location=new URL(url);globalThis.history={replaceState(...args){historyCalls.push(args);}};globalThis.window={};
  globalThis.fetch=()=>{throw new Error('No network allowed');};
  const api={config:configured?{}:null,session:signedIn?{userId:id,email:'member@example.test'}:null,
    async billing(action,body){calls.push({action,body});const queue=answers.get(action);if(queue?.length){const answer=queue.shift();if(answer instanceof Error)throw answer;return answer;}
      if(action==='config')return configuration;if(action==='status')return result();throw new Error('Unexpected fixture action: '+action);}};
  let ui;
  const run=async(target,work)=>{ui.controls(true);element(target).textContent='';try{await work();}catch(e){errors.push(e);element(target).textContent=e.message;}finally{ui.controls(false);}};
  const module=await import('data:text/javascript;base64,'+Buffer.from(source+'\n// scenario '+(++scenarios)).toString('base64'));
  ui=module.createBillingUi({getApi:()=>api,run,onMembership:async value=>{changes.push(value);},otherBlocks:()=>otherBlocks});ui.controls(false);
  const queue=(action,...values)=>{answers.set(action,[...(answers.get(action)||[]),...values]);};
  const fire=async(name,type='click')=>{const node=element(name);if(node.disabled)return false;const handler=node.listeners.get(type);assert.ok(handler,`No ${type} handler for ${name}`);await handler({preventDefault(){}});return true;};
  const initialize=async()=>{await ui.configure();await ui.refresh();ui.controls(false);};
  return{ui,api,calls,errors,changes,historyCalls,element,queue,fire,initialize};
}

try{
  let h=await harness({configuration:{enabled:false},configured:false,signedIn:false});await h.initialize();
  equal(h.element('billing-purchase').hidden,true,'Unconfigured purchase UI is hidden');
  equal(h.element('billing-start').disabled,true,'Unconfigured card registration disabled');
  equal(h.element('billing-confirm').disabled,true,'Unconfigured charge confirmation disabled');
  equal(h.element('billing-refresh').hidden,true,'No configured account cannot query billing');
  equal(await h.fire('billing-start'),false,'Disabled payment control cannot dispatch');
  equal(h.calls.map(x=>x.action),['config'],'Unconfigured guest does not query a user subscription');

  h=await harness({signedIn:false});await h.initialize();
  equal(h.element('billing-login-note').hidden,false,'Configured guest is prompted to log in');
  equal(h.element('billing-purchase').hidden,true,'Guest cannot begin a subscription');
  equal(h.element('billing-start').disabled,true,'Guest payment control remains disabled');
  equal(h.calls.map(x=>x.action),['config'],'Guest does not call authenticated status');
  h=await harness({otherBlocks:true});await h.initialize();h.element('billing-consent').checked=true;await h.fire('billing-consent','change');
  equal(h.element('billing-purchase').hidden,true,'International active/pending subscription blocks domestic purchase');
  equal(h.element('billing-start').disabled,true,'International subscription cannot launch a second domestic checkout');
  equal(h.element('billing-other').hidden,false,'Domestic panel explains how to manage the international subscription');

  h=await harness({url:callback});await h.initialize();
  equal(h.historyCalls,[[null,'','/account/#billing']],'Callback credentials are removed from URL immediately');
  equal(h.element('billing-return').hidden,false,'Return explains first payment confirmation');
  equal(h.element('billing-confirm').disabled,false,'Signed-in return can explicitly confirm');
  equal(h.element('billing-purchase').hidden,true,'Return cannot start a second checkout');
  h.queue('confirm',result(active,'active',pro));await h.fire('billing-confirm');
  equal(h.calls.find(x=>x.action==='confirm').body,{checkoutId:id,state,customerKey,authKey},'Callback credentials are sent only in the confirmation body');
  equal(h.element('billing-return').hidden,true,'Confirmed payment clears return prompt');
  equal(h.element('billing-confirm').disabled,true,'Completed callback cannot charge twice');
  equal(h.element('billing-purchase').hidden,true,'Active auto renewal blocks another subscription');
  equal(h.changes.at(-1),pro,'Verified membership is propagated to the account UI');
  equal(h.element('billing-status').textContent,'매월 자동 갱신','Active subscription status rendered');

  h=await harness({url:callback});await h.initialize();h.queue('confirm',result({status:'pending',auto_renew:true,current_period_end:null},'pending',free));
  await h.fire('billing-confirm');equal(h.element('billing-confirm').disabled,true,'Pending response blocks duplicate confirmation');
  equal(h.element('billing-purchase').hidden,true,'Pending response blocks fresh card registration');
  equal(h.changes.some(value=>value.plan==='pro'),false,'Pending payment never paints Pro');
  equal(await h.fire('billing-confirm'),false,'Disabled pending button does not send another request');
  equal(h.calls.filter(x=>x.action==='confirm').length,1,'Only one confirmation was sent');
  h.queue('sync',result(active,'active',pro));await h.fire('billing-refresh');
  equal(h.changes.at(-1).plan,'pro','Status reconciliation can activate confirmed Pro');
  equal(h.element('billing-return').hidden,true,'Sync completion clears callback prompt');
  equal(h.element('billing-status').textContent,'매월 자동 갱신','Sync updates subscription display');

  h=await harness({url:callback});await h.initialize();h.queue('confirm',error('NETWORK_UNCERTAIN'));await h.fire('billing-confirm');
  equal(h.element('billing-confirm').disabled,true,'Ambiguous network failure blocks repeat payment');
  equal(h.element('billing-purchase').hidden,true,'Ambiguous failure blocks new registration');
  equal(h.changes.some(value=>value.plan==='pro'),false,'Failed confirmation does not invent paid access');
  equal(await h.fire('billing-confirm'),false,'Ambiguous confirmation cannot be repeated');
  h.queue('sync',result(active,'active',pro));await h.fire('billing-refresh');equal(h.changes.at(-1),pro,'Reconciliation resolves ambiguity using server result');

  for(const code of ['CHECKOUT_EXPIRED','PAYMENT_FAILED']){
    h=await harness({url:callback});await h.initialize();h.queue('confirm',error(code));h.queue('status',result());await h.fire('billing-confirm');
    equal(h.element('billing-return').hidden,true,code+' clears an unusable return after server reports no invoice');
    equal(h.element('billing-purchase').hidden,false,code+' permits fresh registration after checking server status');
    equal(h.element('billing-start').disabled,true,code+' still requires explicit consent');
    h.element('billing-consent').checked=true;await h.fire('billing-consent','change');
    equal(h.element('billing-start').disabled,false,code+' unlocks the next consented checkout');
    equal(h.calls.filter(x=>x.action==='status').length,2,code+' performed authoritative status recheck');
  }

  h=await harness({url:callback});await h.initialize();h.queue('confirm',error('PAYMENT_FAILED'));h.queue('status',result({status:'pending',auto_renew:true},'pending',free));await h.fire('billing-confirm');
  equal(h.element('billing-purchase').hidden,true,'Terminal error does not override an unresolved server invoice');
  equal(h.element('billing-confirm').disabled,true,'Unresolved invoice cannot repeat confirmation');

  h=await harness({url:callback});await h.initialize();h.queue('confirm',error('CHECKOUT_EXPIRED'));h.queue('status',error('STATUS_OFFLINE'));await h.fire('billing-confirm');
  equal(h.element('billing-purchase').hidden,true,'If authoritative recovery status fails, new checkout stays locked');
  equal(h.element('billing-confirm').disabled,true,'Offline recovery does not retry confirmation');

  h=await harness();h.queue('status',result(canceled,'active',{...pro,status:'canceled'}));await h.initialize();
  equal(h.element('billing-purchase').hidden,true,'Canceled but still paid period cannot purchase overlapping month');
  equal(h.element('billing-cancel').hidden,true,'Already canceled subscription hides cancel button');
  equal(h.element('billing-cancel').disabled,true,'Already canceled subscription cannot submit cancellation again');
  equal(h.element('billing-next').textContent,'자동 결제 없음','Canceled plan has no next automatic charge');

  h=await harness();h.queue('status',result(active,'active',pro));await h.initialize();const renderedEnd=h.element('billing-until').textContent;
  equal(h.element('billing-cancel').hidden,false,'Renewing subscription exposes cancellation');
  await h.fire('billing-cancel');equal(h.element('billing-cancel-review').hidden,false,'Cancel first opens a review step');
  equal(h.calls.filter(x=>x.action==='cancel').length,0,'Opening review does not cancel');
  await h.fire('billing-cancel-back');equal(h.element('billing-cancel-review').hidden,true,'Back dismisses cancellation review');
  await h.fire('billing-cancel');h.queue('cancel',result(canceled,'active',{...pro,status:'canceled'}));await h.fire('billing-cancel-confirm');
  equal(h.calls.filter(x=>x.action==='cancel').length,1,'Only explicit final confirmation cancels');
  equal(h.element('billing-cancel-review').hidden,true,'Successful cancellation closes review');
  equal(h.element('billing-until').textContent,renderedEnd,'Cancellation keeps paid-through date');
  equal(h.changes.at(-1).plan,'pro','Cancellation keeps existing paid entitlement');
  equal(h.element('billing-purchase').hidden,true,'Canceled paid period is not offered another charge');

  h=await harness({url:callback});await h.initialize();h.queue('confirm',result({status:'past_due',auto_renew:false,current_period_end:null},'failed',free));await h.fire('billing-confirm');
  equal(h.element('billing-purchase').hidden,false,'Confirmed card decline can start a later fresh registration');
  equal(h.changes.at(-1).plan,'free','Card decline leaves Free');
  equal(h.element('billing-return').hidden,true,'Definitive failed result consumes old callback');
  h.ui.reset();h.ui.controls(false);equal(h.element('billing-consent').checked,false,'Sign-out reset clears purchase consent');
  equal(h.element('billing-purchase').hidden,true,'Reset requires a fresh server status before registration');
  equal(h.element('billing-cancel-review').hidden,true,'Reset removes stale cancellation confirmation');

  h=await harness({url:callback});await h.initialize();
  const past=new Date(Date.now()-86400000).toISOString();
  h.queue('sync',result({...canceled,current_period_end:past},'active',{...free,status:'expired',expires_at:past}));await h.fire('billing-refresh');
  equal(h.changes.at(-1).plan,'free','A historical paid invoice does not replace effective expired membership');
  equal(h.element('billing-return').hidden,true,'Historical verified result still clears obsolete callback');
  ok(!h.element('billing-message').textContent.includes('Pro 이용 권한과 결제 상태를 확인했습니다')&&!h.element('billing-message').textContent.includes('Pro가 활성화'),'Historical payment does not announce current Pro activation');
  ok(h.element('billing-message').textContent.includes('이전 결제')||h.element('billing-message').textContent.includes('이용기간'),'Expired paid result explains historical payment or period end');

  h=await harness();h.queue('config',error('CONFIG_OFFLINE'));await h.initialize();
  equal(h.element('billing-purchase').hidden,true,'Config errors fail closed');
  equal(h.element('billing-start').disabled,true,'Config errors cannot enable payment');
  ok(h.element('billing-message').textContent.includes('무료 버전'),'Disconnected setup explains Free remains available');
  console.log(`PASS: ${checks} billing DOM-state checks across ${scenarios} isolated UI instances. No network, SDK or payments used.`);
}finally{
  for(const[key,descriptor]of Object.entries(initialGlobals)){if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];}
}
