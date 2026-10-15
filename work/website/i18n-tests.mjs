import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { mainTranslations } from './dist/main-translations.js';
import { serviceGuides, faqItems } from './dist/guides.js';
import { guideTranslations, faqTranslations } from './dist/guides-en.js';

const runtime = await readFile(new URL('./dist/i18n.js', import.meta.url), 'utf8');
let sequence = 0;
const fresh = () => import(`data:text/javascript;base64,${Buffer.from(runtime + `\n// test instance ${++sequence}`).toString('base64')}`);
const store = new Map();
globalThis.localStorage = {getItem: key => store.get(key), setItem: (key, value) => store.set(key, value)};
const i18n = await fresh();
assert.equal(i18n.getLanguage(), 'ko', 'First visit keeps the existing Korean default');
i18n.registerTranslations(mainTranslations);
i18n.setLanguage('en');
assert.equal(store.get('nephylo-language'), 'en');
assert.equal((await fresh()).getLanguage(), 'en', 'Language survives a fresh page runtime');
i18n.setLanguage('fr');
assert.equal(i18n.getLanguage(), 'en', 'Unsupported choices cannot change language');
store.set('nephylo-language', '<script>');
assert.equal((await fresh()).getLanguage(), 'ko', 'Invalid saved preference falls back to Korean');
globalThis.localStorage = {getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); }};
const blocked = await fresh();
assert.equal(blocked.getLanguage(), 'ko');
blocked.setLanguage('en');
assert.equal(blocked.getLanguage(), 'en', 'Choice works even when storage is blocked');

// Every original main-page text/accessible label has an English rendering.
const html = await readFile(new URL('./dist/index.html', import.meta.url), 'utf8');
const sources = [...html.matchAll(/>([^<>]+)</g)].map(match => match[1].trim()).concat([...html.matchAll(/(?:aria-label|title|placeholder|alt|content)="([^"]+)"/g)].map(match => match[1].trim()));
for (const source of sources.filter(text => /[가-힣]/.test(text) && text !== '한국어')) {
  assert.ok(Object.hasOwn(mainTranslations, source), `Missing main translation: ${source}`);
  assert.ok(!/[가-힣]/.test(i18n.t(source)), `Untranslated main text: ${source}`);
}
assert.match(i18n.t('모든 기능 무료'), /All features free/);
assert.equal('0' + i18n.t('원'), '0 KRW', 'The free price separates the amount and currency');
assert.equal('3,000' + i18n.t('원'), '3,000 KRW', 'The paid price separates the amount and currency');
assert.equal(i18n.t('{name} 연결', {name:'<img src=x onerror=alert(1)>'}), 'Connect <img src=x onerror=alert(1)>', 'Interpolation returns plain text, never markup');
assert.match(html, /<select data-language-select aria-label="표시 언어">/);
assert.equal(i18n.t('설치 파일을 실행해 위젯을 설치하세요.') + i18n.t('상단'), 'Run the installer to install the widget. Open', 'Hidden mobile line breaks must not join English sentences');
assert.match(i18n.t('의 서비스 관리에서 사용할 서비스'), /^ at the top\. $/);
assert.match(i18n.t('한 뒤 아래 방법으로 연결합니다.'), /^ and follow/, 'Inline emphasis must not join English words');
assert.deepEqual(guideTranslations.map(item => item.id), serviceGuides.map(item => item.id));
assert.equal(faqTranslations.length, faqItems.length);
for (const [index, english] of guideTranslations.entries()) {
  const korean = serviceGuides[index];
  for (const field of ['name','method','summary','docsLabel']) assert.ok(english[field] && !/[가-힣]/.test(english[field]));
  for (const field of ['steps','troubleshooting']) {
    assert.equal(english[field].length, korean[field].length);
    english[field].forEach(text => assert.ok(text && !/[가-힣]/.test(text)));
  }
}
for (const item of faqTranslations) for (const field of ['question','answer']) assert.ok(item[field] && !/[가-힣]/.test(item[field]));
assert.match(guideTranslations.find(item => item.id === 'higgsfield-api').summary, /automatic connection is not offered/);
assert.match(faqTranslations[2].answer, /no longer has a paid plan/);
assert.match(faqTranslations[0].answer, /free without a Nephylo account or payment/);

// A small DOM fixture checks identity-preserving translation and excludes user input.
class Element {
  constructor(tag, attrs = {}, children = []) { this.nodeType=1; this.tagName=tag.toUpperCase(); this.attrs={...attrs}; this.childNodes=children; this.events={}; this.value=attrs.value || ''; }
  hasAttribute(name) { return Object.hasOwn(this.attrs, name); }
  getAttribute(name) { return this.attrs[name] ?? null; }
  setAttribute(name,value) { this.attrs[name]=value; }
  addEventListener(name,handler) { (this.events[name] ||= []).push(handler); }
  matches(selector) { return selector.split(',').some(raw => { const s=raw.trim(); if(s==='option:not([value])') return this.tagName==='OPTION'&&!this.hasAttribute('value'); const m=s.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/); return m ? this.hasAttribute(m[1])&&(m[2]===undefined||this.getAttribute(m[1])===m[2]) : this.tagName.toLowerCase()===s; }); }
}
const text = value => ({nodeType:3,nodeValue:value});
const heading=text('요금제');
const details=new Element('details',{open:''},[text('연결 방법')]);
const input=new Element('input',{value:'사용자 입력'}); input.value='내 이메일@example.com';
const user=text('사용자 메모'); const ignored=new Element('p',{'data-i18n-ignore':''},[user]);
const manual=text('수동 기록'); const option=new Element('option',{value:'manual'},[manual]);
const select=new Element('select',{value:'manual'},[option]);
const languageSelect=new Element('select',{'data-language-select':'','aria-label':'표시 언어'},[new Element('option',{value:'ko'},[text('한국어')])]);
const root=new Element('html',{},[new Element('body',{},[heading,details,input,ignored,select,languageSelect])]);
globalThis.document={nodeType:9,documentElement:root,childNodes:[root]};
i18n.translateDocument();
assert.equal(heading.nodeValue,'Pricing');
assert.equal(details.childNodes[0].nodeValue,'Connect');
assert.ok(details.hasAttribute('open'),'Open details state is preserved');
assert.equal(input.value,'내 이메일@example.com');
assert.equal(user.nodeValue,'사용자 메모');
assert.equal(manual.nodeValue,'Manual');
assert.equal(option.getAttribute('value'),'manual');
assert.equal(select.value,'manual');
assert.equal(languageSelect.value,'en');
assert.equal(languageSelect.childNodes[0].childNodes[0].nodeValue,'한국어');
let callbacks=0; const remove=i18n.onLanguageChange(() => { callbacks++; assert.equal(root.lang,'ko'); });
i18n.setLanguage('ko'); remove();
assert.equal(callbacks,1);
assert.equal(heading.nodeValue,'요금제');
assert.equal(manual.nodeValue,'수동 기록');
assert.equal(languageSelect.events.change.length,1,'Repeated translation binds the selector once');
assert.equal(details.childNodes.length,1,'Nodes and interaction state are not replaced');
delete globalThis.document;
delete globalThis.localStorage;
console.log(`PASS: bilingual persistence, blocked storage, safe DOM switching, ${new Set(sources).size} main strings, ${guideTranslations.length} guides, ${faqTranslations.length} FAQs, KRW pricing and scope disclosures.`);
