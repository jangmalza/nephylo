import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {accountTranslations} from './dist/account/account-translations.js';
const root=new URL('./dist/account/',import.meta.url);
let checks=0;
const equal=(actual,expected,message)=>{assert.deepEqual(actual,expected,message);checks++;};
const ok=(value,message)=>{assert.ok(value,message);checks++;};
// Read product strings, excluding JS comments. Unknown server/provider text is
// intentionally never interpolated into these public error messages.
function literals(source){
  const values=[];
  for(let i=0;i<source.length;i++){
    if(source.slice(i,i+2)==='//'){i=source.indexOf('\n',i);if(i<0)break;}
    else if(source.slice(i,i+2)==='/*'){i=source.indexOf('*/',i+2)+1;}
    else if(source[i]==="'"||source[i]==='"'){
      const quote=source[i];let value='';
      for(i++;i<source.length;i++){if(source[i]==='\\'){value+=source[i]+source[++i];continue;}if(source[i]===quote)break;value+=source[i];}
      if(/[가-힣]/.test(value))values.push(value.replaceAll('\\n','\n'));
    }
  }
  return values;
}
for(const file of ['account.js','admin-members.js','billing-ui.js','billing-model.js','international-billing-ui.js','international-billing-model.js','payments-ui.js','membership-api.js','google-auth.js','google-callback/callback.js']){
  for(const source of literals(await readFile(new URL(file,root),'utf8')))ok(accountTranslations[source],`${file}: untranslated message ${source}`);
}
const html=await readFile(new URL('admin.html',root),'utf8');
for(const file of ['admin.html','google-callback/index.html']){
  const source=await readFile(new URL(file,root),'utf8');
  for(const match of source.matchAll(/>([^<>]+)</g)){
    const copy=match[1].trim();if(/[가-힣]/.test(copy)&&copy!=='한국어')ok(accountTranslations[copy],`${file}: untranslated static copy ${copy}`);
  }
  for(const match of source.matchAll(/(?:aria-label|placeholder|title|alt)="([^"]*)"/g))if(/[가-힣]/.test(match[1]))ok(accountTranslations[match[1]],`${file}: untranslated attribute`);
}
const saved=Object.fromEntries(['document','window','location','history','fetch','setInterval','localStorage'].map(key=>[key,Object.getOwnPropertyDescriptor(globalThis,key)]));
const nodes=new Map(),requests=[],storage=new Map(),historyChanges=[],intervals=[];
function element(tag='div'){
  return {nodeType:1,tagName:tag.toUpperCase(),childNodes:[],attributes:new Map(),listeners:new Map(),value:'',checked:false,disabled:false,hidden:false,textContent:'',
    get children(){return this.childNodes;},append(...items){this.childNodes.push(...items);},replaceChildren(...items){this.childNodes=[...items];},focus(){},
    setAttribute(key,value){this.attributes.set(key,value);},getAttribute(key){return this.attributes.get(key)??null;},hasAttribute(key){return this.attributes.has(key);},
    matches(selector){return selector.split(',').some(part=>part==='[data-language-select]'?this.hasAttribute('data-language-select'):part==='[data-i18n-ignore]'?this.hasAttribute('data-i18n-ignore'):part===this.tagName.toLowerCase());},
    addEventListener(type,fn){this.listeners.set(type,fn);},querySelector(){return null;}
  };
}
for(const match of html.matchAll(/<([a-z][a-z0-9-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)){
  const node=element(match[1]);node.hidden=/\bhidden\b/.test(match[2]);node.disabled=/\bdisabled\b/.test(match[2]);node.setAttribute('id',match[3]);
  if(/\bdata-i18n-ignore\b/.test(match[2]))node.setAttribute('data-i18n-ignore','');
  nodes.set(match[3],node);
}
const selector=element('select');selector.setAttribute('data-language-select','');selector.value='ko';
const node=id=>{if(!nodes.has(id))nodes.set(id,element());return nodes.get(id);};
node('grant-plan').value='free';
const config={SupabaseUrl:'https://fixture.supabase.co',PublishableKey:'sb_publishable_languagefixture',EmailEnabled:false,GoogleEnabled:false};
const userId='a2000000-0000-4000-8000-000000000002',future=new Date(Date.now()+30*86400000).toISOString();
const member={user_id:userId,email:'person@example.test',plan:'pro',status:'active',expires_at:future,server_time:new Date().toISOString(),is_admin:true};
const empty={plan:'free',status:'free',expires_at:null,updated_at:null,active:false};
const memberRow={...member,manual:empty,billing:{status:'active',expires_at:future,active:true,auto_renew:true,invoice_status:'paid'}};
const billingConfig={enabled:true,amount:3000,currency:'KRW',interval:'month',clientKey:'test_ck_fixture123',termsVersion:'fixture',termsUrl:'https://fixture.test/terms',privacyUrl:'https://fixture.test/privacy',refundUrl:'https://fixture.test/refunds'};
try{
  globalThis.document={nodeType:9,documentElement:{lang:'ko'},childNodes:[selector],hidden:false,getElementById:node,createElement:element,addEventListener(){},
    querySelectorAll(){return [...nodes.values()].filter(item=>['INPUT','SELECT','BUTTON'].includes(item.tagName));}};
  globalThis.localStorage={getItem:key=>storage.get(key)??null,setItem:(key,value)=>storage.set(key,value)};
  globalThis.location=new URL('https://nephylo.example/account/?international=return');globalThis.history={replaceState(...args){historyChanges.push(args[2]);}};globalThis.window={};globalThis.setInterval=callback=>{intervals.push(callback);return intervals.length;};
  globalThis.fetch=async(url,options={})=>{
    requests.push({url,body:options.body});
    let result;if(url==='config.json')result=config;
    else if(url.endsWith('/international-billing-api')){const request=JSON.parse(options.body);result=request.action==='config'?{enabled:false}:{subscription:null,pendingCheckout:false};}
    else if(url.endsWith('/billing-api')){const request=JSON.parse(options.body);result=request.action==='config'?billingConfig:{subscription:null,status:'none'};}
    else if(url.endsWith('/admin_list_members'))result={members:[memberRow],has_more:false};
    else if(url.endsWith('/admin_get_membership_audit'))result={entries:[{legacy:false,created_at:future,reason:'한국어 사용자 사유 <script> 그대로',before_manual:empty,after_manual:empty}]};
    else if(url.endsWith('/get_my_membership'))result=member;
    else throw new Error('Unexpected test request: '+url);
    return {ok:true,status:200,json:async()=>result,text:async()=>JSON.stringify(result)};
  };
  const runtime=await import('./dist/i18n.js');
  const accountSource=await readFile(new URL('account.js',root),'utf8');
  const localizationPath=accountSource.match(/from '(\.\/localization\.js(?:\?[^']*)?)'/)?.[1];
  ok(localizationPath,'Load the exact localization module used by the account page');
  const localization=await import(new URL(localizationPath,root).href);
  const source=accountSource.replace(/from '(\.\/[^']+)'/g,(_,path)=>'from '+JSON.stringify(new URL(path,root).href));
  const account=await import('data:text/javascript;base64,'+Buffer.from(source+'\nexport {api,run,renderMember,loadMembers,updateMembership,signedOut,controls};').toString('base64'));
  equal(selector.disabled,false,'Language control remains available without a login');
  equal(account.api.config.origin,config.SupabaseUrl,'Disabled providers do not discard the connected server');
  equal(node('setup-note').hidden,false,'Connected but unready providers retain their preparation notice');
  equal(node('setup-title').textContent,'로그인 준비 중');
  equal(node('setup-message').textContent,'로그인 서비스를 준비하고 있습니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.');
  const emailControls=['email','code','send-code','resend-code','verify-code','change-email'];
  for(const id of emailControls)equal(node(id).disabled,true,`${id} remains disabled until email is ready`);
  equal(node('google-login').disabled,true,'Unready Google sign-in remains disabled');
  const beforeBlockedLogin=requests.length;
  await account.run('login-message',()=>account.api.sendCode('person@example.test'));
  await account.run('login-message',()=>account.api.verify('person@example.test','123456'));
  equal(requests.length,beforeBlockedLogin,'Programmatic attempts cannot bypass disabled email sign-in');
  equal(account.api.session,null,'Disabled email verification never starts a session');
  equal(requests.some(request=>/billing-api/.test(request.url)),false,'Administrator startup makes no domestic or international billing requests');
  ok(!html.includes('id="billing"')&&!html.includes('billing-start'),'Administrator page contains no checkout form');
  equal(node('member-card').hidden,true,'Legacy callback query cannot grant membership or sign in');
  node('email').value='draft@example.test';node('code').value='123';node('grant-reason').value='한국어 초안 <strong> unchanged';
  runtime.setLanguage('en');
  for(const id of emailControls)equal(node(id).disabled,true,`${id} cannot be re-enabled by an English language change`);
  equal(node('setup-message').textContent,accountTranslations['로그인 서비스를 준비하고 있습니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.'],'Preparation notice follows the chosen language');
  equal(node('login-message').textContent,accountTranslations['이메일 로그인은 연결 준비 중입니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.'],'Blocked sign-in message follows the chosen language');
  equal('0'+runtime.t('원'),'0 KRW','Free price separates amount from its currency unit');
  equal('3,000'+runtime.t('원 / 월'),'3,000 KRW / month','Pro price separates amount, currency and billing interval');
  equal(node('email').value,'draft@example.test','Language switch keeps email draft');
  equal(node('code').value,'123','Language switch keeps verification-code draft');
  equal(node('grant-reason').value,'한국어 초안 <strong> unchanged','Language switch keeps administrator draft verbatim');
  equal(node('google-note').textContent,accountTranslations['Google 로그인 연결 준비 중입니다. 연결 후 이 버튼으로 간편하게 로그인할 수 있습니다.'],'Unconfigured Google notice is English');
  equal(storage.get('nephylo-language'),'en','Only display preference is persisted');
  equal([...storage.keys()],['nephylo-language'],'No credentials are written to storage');
  localization.text('login-message','인증번호가 만료되었거나 올바르지 않습니다. 새 번호를 받아 주세요.');
  ok(node('login-message').textContent.startsWith('The code expired'),'Authentication errors render in English');
  runtime.setLanguage('ko');equal(node('login-message').textContent,'인증번호가 만료되었거나 올바르지 않습니다. 새 번호를 받아 주세요.','Existing error restores Korean without retrying authentication');
  const session={access:'fixture-token',refresh:'fixture-refresh',userId,email:member.email,expires:Date.now()+3600000};account.api.session=session;
  await account.updateMembership();
  equal(requests.some(request=>/billing-api/.test(request.url)),false,'Authenticated refresh makes no billing requests');
  ok(node('members').children[0].children[3].textContent.includes('Toss Payments')&&!node('members').children[0].children[3].textContent.includes('Lemon Squeezy'),'Admin with no overseas history shows domestic billing only');
  await node('members').children[0].children[4].children[0].listeners.get('click')();
  // The click handler returns the actual async run promise.
  node('grant-reason').value='한국어 사용자 사유 <script> 그대로';
  const before=requests.length;
  runtime.setLanguage('en');
  equal(account.api.session,session,'Language switch preserves the authenticated session object');
  equal(requests.length,before,'Language switch makes no auth, billing or admin requests');
  equal(node('member-email').textContent,member.email,'Member email remains verbatim');
  equal(node('member-status').textContent,'Using Pro','Active plan status translated');
  equal(node('member-expiry').textContent,new Date(future).toLocaleString('en-US',{dateStyle:'medium',timeStyle:'short'}),'Membership date uses English locale');
  equal(node('grant-reason').value,'한국어 사용자 사유 <script> 그대로','Loaded administrator draft preserved');
  ok(node('grant-audit').textContent.includes('Reason: 한국어 사용자 사유 <script> 그대로'),'Audit labels translated but user-provided reason remains plain text');
  equal(node('grant-plan').value,'free','Manual entitlement select value is unchanged');
  equal(node('grant-free-label').textContent,'No manual Pro','Admin option label is translated independently of value');
  node('grant-plan').value='pro';await node('grant-plan').listeners.get('change')();
  equal(node('grant-duration-30').getAttribute('aria-pressed'),'true','New grant defaults to 30 days in English too');
  await node('grant-duration-90').listeners.get('click')();
  const durationPreview=node('grant-expiry-preview').textContent;
  ok(durationPreview.length>10&&!/[가-힣]/.test(durationPreview),'90-day expiry is visible in English before review');
  runtime.setLanguage('ko');equal(node('grant-duration-90').getAttribute('aria-pressed'),'true','Korean switch keeps selected period');
  ok(node('grant-expiry-preview').textContent.startsWith('종료:'),'Calculated expiry follows the selected language');
  runtime.setLanguage('en');equal(node('grant-expiry-preview').textContent,durationPreview,'Language changes keep the exact expiry draft');
  equal(node('grant-reason').value,'한국어 사용자 사유 <script> 그대로','Period selection preserves the entered audit reason');
  node('grant-plan').value='free';await node('grant-plan').listeners.get('change')();
  equal(node('grant-duration-90').disabled,true,'Free disables retained duration draft');
  await node('grant-form').listeners.get('submit')({preventDefault(){}});
  const review=node('grant-summary').textContent;
  ok(review.includes('Manual access:')&&review.includes('한국어 사용자 사유 <script> 그대로'),'Review translates labels and preserves user reason');
  runtime.setLanguage('ko');
  ok(node('grant-summary').textContent.includes('수동 권한:')&&node('grant-review').hidden===false,'Language switch keeps the pending grant review open');
  let release;const pending=account.run('login-message',()=>new Promise(resolve=>{release=resolve;}));
  runtime.setLanguage('en');equal(selector.disabled,false,'Language control remains enabled during a pending request');equal(node('email').disabled,true,'Busy authentication controls remain disabled');release();await pending;
  for(const id of emailControls)equal(node(id).disabled,true,`${id} remains disabled when a pending request finishes`);
  account.api.clear();account.signedOut();account.api.config.emailEnabled=true;
  let otpNow=Date.parse('2026-10-12T09:00:00Z'),releaseOtp,otpCalls=0;
  account.api.now=()=>otpNow;
  account.api.fetcher=async()=>{otpCalls++;return new Promise(resolve=>{releaseOtp=resolve;});};
  account.controls();runtime.setLanguage('ko');node('email').value='person@example.test';
  const submit=id=>node(id).listeners.get('submit')({preventDefault(){}});
  const sendPending=submit('email-form');
  await submit('email-form');await node('resend-code').listeners.get('click')();await submit('code-form');
  equal(otpCalls,1,'Sending blocks duplicate sends and conflicting verification handlers');
  equal(node('send-code').disabled,true,'Sending disables the send control');
  runtime.setLanguage('en');equal(node('verify-code').disabled,true,'Language change retains the active-request guard');
  releaseOtp(new Response('{}',{status:200}));await sendPending;
  equal(node('send-code').textContent,'Send in 60s');equal(node('resend-code').textContent,'Resend in 60s');
  equal(node('send-cooldown').textContent,'Please wait 60s before requesting another code.');
  equal(node('verify-code').disabled,false,'Normal resend cooldown permits code verification');
  equal(node('code-form').hidden,false,'A sent code opens the verification form');
  runtime.setLanguage('ko');equal(node('resend-code').textContent,'60초 후 다시 받기');
  equal(node('send-cooldown').textContent,'인증번호 발송 대기 중입니다. 60초 후 다시 요청할 수 있습니다.');
  node('code').value='123456';const verificationPending=submit('code-form');await submit('code-form');await node('resend-code').listeners.get('click')();
  equal(otpCalls,2,'Verification blocks duplicate verifies and conflicting sends');
  releaseOtp(new Response(JSON.stringify({error_code:'over_request_rate_limit'}),{status:429,headers:{'Retry-After':'90'}}));await verificationPending;
  equal(node('verify-code').disabled,true);equal(node('verify-code').textContent,'90초 후 인증');
  equal(node('verify-cooldown').textContent,'인증 시도 대기 중입니다. 90초 후 다시 인증할 수 있습니다.');
  equal(node('login-message').textContent,'인증 시도가 많습니다. 잠시 후 다시 인증해 주세요.');
  await submit('code-form');equal(otpCalls,2,'Programmatic form submission cannot bypass a verification cooldown');
  otpNow+=61000;for(const tick of intervals)tick();
  equal(node('resend-code').disabled,false,'Send wait expires independently of verification throttling');
  equal(node('send-cooldown').hidden,true);equal(node('verify-code').textContent,'29초 후 인증');
  runtime.setLanguage('en');equal(node('verify-code').textContent,'Verify in 29s');
  equal(node('verify-cooldown').textContent,'Please wait 29s before verifying your code again.');
  const resendPending=node('resend-code').listeners.get('click')();
  releaseOtp(new Response(JSON.stringify({code:'over_email_send_rate_limit'}),{status:429,headers:{'Retry-After':'Mon, 12 Oct 2026 09:03:00 GMT'}}));await resendPending;
  equal(node('send-code').textContent,'Send in 119s');equal(node('resend-code').disabled,true);
  equal(node('login-message').textContent,'Too many code requests. Please wait before requesting another code.');
  await node('change-email').listeners.get('click')();account.api.clear();account.signedOut();account.controls();
  equal(node('send-code').disabled,true,'Changing email and signing out preserve the active send deadline');
  equal(node('verify-code').textContent,'Verify in 29s','Sign-out preserves the independent verification deadline');
  otpNow+=29000;for(const tick of intervals)tick();
  equal(node('verify-code').disabled,false);equal(node('verify-cooldown').hidden,true);
  equal(node('send-code').textContent,'Send in 90s','Expiring verification does not reset send throttling');
  runtime.setLanguage('ko');equal(node('send-code').textContent,'90초 후 받기');
  equal([...storage.keys()],['nephylo-language'],'OTP waits, emails and codes never enter browser storage');
  account.api.config=null;runtime.setLanguage('ko');equal(selector.disabled,false,'Language selection works while auth is unconfigured');equal(node('send-code').disabled,true,'Unconfigured auth still fails closed');
  await account.run('member-message',async()=>{account.api.clear();account.signedOut();});
  equal(requests.some(request=>request.url.endsWith('/international-billing-api')),false,'No overseas API calls throughout login, language change, refresh and sign-out');
  console.log(`PASS: ${checks} administrator translation and handler checks: messages, dates, session, drafts, pending review, no billing requests and busy/unconfigured language controls. No network.`);
}finally{for(const[key,value]of Object.entries(saved)){if(value)Object.defineProperty(globalThis,key,value);else delete globalThis[key];}}
