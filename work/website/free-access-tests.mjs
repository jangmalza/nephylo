import assert from 'node:assert/strict';
import {readFile,access} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';
import {registerTranslations,setLanguage,t} from './dist/i18n.js';
import {privacyTranslations} from './dist/privacy/privacy-translations.js';
const read=name=>readFile(new URL('./dist/'+name,import.meta.url),'utf8');
let checks=0;
const ok=(value,message)=>{assert.ok(value,message);checks++;};
const account=await read('account/index.html');
const publicScript=await read('account/free-access.js');
const admin=await read('account/admin.html');
const controller=await read('account/account.js');
const home=await read('index.html');
const preview=await read('app.js');
ok(account.includes('free-access.js'),'Public account URL loads the free-access entry point');
ok(!/<form|id="(?:google-login|billing|member-card)|src="account.js/.test(account),'Public account page has no sign-in, entitlement or payment controls');
ok(!/membership-api|google-auth|payments-ui|billing-ui|fetch\(|config.json/.test(publicScript),'Public entry point has no auth or billing dependency');
ok(admin.includes('id="admin-panel"')&&admin.includes('id="google-login"'),'Separate administrator page retains authentication and member administration');
ok(!/id="billing"|id="billing-start"|id="international-start"/.test(admin),'Administrator page exposes no checkout controls');
ok(admin.includes('수동 Pro 변경은 1.25.20 이후 무료 위젯의 기능에 영향을 주지 않습니다'),'Legacy administrator rights cannot be confused with current widget access');
ok(controller.includes('1.25.20 이후 무료 위젯의 기능에는 영향을 주지 않으며')&&!controller.includes('위젯에서 다시 확인하면 반영됩니다'),'Grant success does not promise current-widget activation');
ok(home.includes('href="/account/">무료 이용 안내')&&!home.includes('href="/account/">계정 · 이용 권한'),'Public footer describes free access rather than an entitlement check');
ok(!/createPaymentsUi|refreshBilling|billing\.(?:refresh|configure)/.test(controller),'Administrator controller cannot create billing requests');
ok(controller.includes('if(value.is_admin)await loadMembers()')&&controller.includes("if(!value.is_admin)hideAdmin()"),'Administrator controls remain conditional on verified server authorization');
const google=await read('account/google-auth.js');
ok(google.includes("GOOGLE_CALLBACK_PATH='/account/google-callback/'"),'Admin route keeps the configured Google callback path');
ok((await read('account/google-callback/index.html')).includes('href="../admin.html"'),'Callback fallback leads to the protected administrator flow');
// Loading either public information page works with every network request rejected.
let requests=0;const originalFetch=globalThis.fetch;
try{
  globalThis.fetch=()=>{requests++;throw new Error('No auth or payment request permitted');};
  await import('./dist/account/free-access.js');
  await import('./dist/privacy/privacy.js');
  assert.equal(requests,0);checks++;
}finally{globalThis.fetch=originalFetch;}
const copy=html=>[...html.matchAll(/>([^<>]+)</g)].map(m=>m[1].trim()).concat([...html.matchAll(/(?:aria-label|title|placeholder|alt|content)="([^"]+)"/g)].map(m=>m[1].trim())).filter(s=>/[가-힣]/.test(s)&&s!=='한국어');
setLanguage('en');
for(const text of copy(account))ok(!/[가-힣]/.test(t(text)),'Public account translation: '+text);
registerTranslations(privacyTranslations);
const privacy=await read('privacy/index.html');
for(const text of copy(privacy))ok(!/[가-힣]/.test(t(text)),'Privacy translation: '+text);
ok(privacy.includes('%USERPROFILE%\\.nephylo'),'Privacy identifies the canonical data profile');
for(const html of [account,admin,privacy]){
  const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
  ok(new Set(ids).size===ids.length,'Each public/admin document has unique IDs');
}
// Execute the actual theme handler, without a plan or an account fixture.
const themeSource=preview.match(/function setTheme\(theme\) \{[\s\S]*?\n\}/)?.[0];
ok(themeSource,'Preview theme handler exists');
const widget={dataset:{theme:'dark'}};
const themeButtons=['dark','light'].map(theme=>({dataset:{themeChoice:theme},setAttribute(name,value){this[name]=value;}}));
runInNewContext(themeSource+'; setTheme("light");',{widget,themeButtons});
assert.equal(widget.dataset.theme,'light');assert.equal(themeButtons[1]['aria-pressed'],'true');checks+=2;
runInNewContext(themeSource+'; setTheme("dark");',{widget,themeButtons});
assert.equal(widget.dataset.theme,'dark');checks++;
ok(!home.includes('data-demo-plan')&&!preview.includes('demoPlan'),'No preview plan gate remains');
ok(/id="demo-extra-service"\s*>/.test(home),'Second provider is visible without upgrading');
ok(/id="demo-theme-controls"\s*>/.test(home),'Theme selection is visible without upgrading');
await access(new URL('./dist/account/admin.html',import.meta.url));
console.log(`PASS: ${checks} free-access checks: no public authentication or payment requests, preserved admin boundary/callback, bilingual privacy/account pages and unrestricted preview themes.`);
