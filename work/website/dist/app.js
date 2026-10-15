import { serviceGuides, faqItems } from './guides.js?v=20261015-v12520-free';
import { guideTranslations, faqTranslations } from './guides-en.js?v=20261015-v12520-free';
import { mainTranslations } from './main-translations.js?v=20261015-oss';
import { getLanguage, registerTranslations, t, onLanguageChange, translateDocument } from './i18n.js';
import { initScrollReveal } from './scroll-reveal.js?v=20261012-reveal';
import { initPowerShellInstall } from './powershell-install.js?v=20261013-ps-install';

registerTranslations(mainTranslations);
const guideCatalog = {};
for (const guide of serviceGuides) {
  const english = guideTranslations.find(item => item.id === guide.id);
  for (const key of ['name', 'method', 'summary', 'docsLabel']) guideCatalog[guide[key]] = english[key];
  for (const key of ['steps', 'troubleshooting']) guide[key].forEach((line, index) => { guideCatalog[line] = english[key][index]; });
}
faqItems.forEach((item, index) => {
  guideCatalog[item.question] = faqTranslations[index].question;
  guideCatalog[item.answer] = faqTranslations[index].answer;
});
registerTranslations(guideCatalog);

const header = document.querySelector('.site-header');
const navigation = document.querySelector('#site-navigation');
const menuToggle = document.querySelector('#menu-toggle');
const compactHeader = window.matchMedia('(max-width: 1150px)');
header.dataset.menuReady = '';
menuToggle.hidden = false;
function setMenuOpen(open, returnFocus = false) {
  header.toggleAttribute('data-menu-open', open);
  menuToggle.setAttribute('aria-expanded', String(open));
  if (returnFocus) menuToggle.focus();
}
menuToggle.addEventListener('click', () => setMenuOpen(menuToggle.getAttribute('aria-expanded') !== 'true'));
navigation.addEventListener('click', event => {
  const link = event.target.closest('a');
  if (!link || !compactHeader.matches) return;
  setMenuOpen(false);
  if (link.hash) {
    const destination = document.getElementById(link.hash.slice(1));
    if (destination) { destination.tabIndex = -1; destination.focus({ preventScroll: true }); }
  }
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && menuToggle.getAttribute('aria-expanded') === 'true') {
    event.preventDefault();
    setMenuOpen(false, true);
  }
});
document.addEventListener('click', event => {
  if (!header.contains(event.target)) setMenuOpen(false);
});
compactHeader.addEventListener('change', () => setMenuOpen(false));

const widget = document.querySelector('#widget-demo');
const themeButtons = [...document.querySelectorAll('[data-theme-choice]')];
const themeShortcut = document.querySelector('#demo-theme-shortcut');
function setTheme(theme) {
  widget.dataset.theme = theme;
  themeButtons.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.themeChoice === widget.dataset.theme)));
}
themeButtons.forEach(button => button.addEventListener('click', () => setTheme(button.dataset.themeChoice)));
themeShortcut.addEventListener('click', () => setTheme(widget.dataset.theme === 'dark' ? 'light' : 'dark'));
function updateDemoLabels() {
  document.querySelector('#demo-caption').textContent = t('모든 기능 무료 · 여러 서비스');
  document.querySelector('#demo-plan-label').textContent = t('/ 예시');
  widget.setAttribute('aria-label', t('Nephylo의 여러 서비스 사용량 예시 화면'));
}

document.querySelectorAll('.demo-service-toggle').forEach(button => {
  button.addEventListener('click', () => {
    const expanded = button.getAttribute('aria-expanded') === 'true';
    button.setAttribute('aria-expanded', String(!expanded));
    document.getElementById(button.getAttribute('aria-controls')).hidden = expanded;
    button.nextElementSibling.hidden = !expanded;
  });
});
const refresh = document.querySelector('#demo-refresh');
const status = document.querySelector('#demo-status');
status.dataset.i18nIgnore = '';
let refreshing = false;
function updateDemoStatus() {
  status.textContent = t(refreshing ? '예시 화면 새로고침 중…' : '예시 데이터 · 실제 API 호출 없음');
}
refresh.addEventListener('click', () => {
  refresh.disabled = true;
  refresh.classList.add('spinning');
  widget.classList.add('demo-refreshing');
  refreshing = true;
  updateDemoStatus();
  window.setTimeout(() => {
    refresh.disabled = false;
    refresh.classList.remove('spinning');
    widget.classList.remove('demo-refreshing');
    refreshing = false;
    updateDemoStatus();
  }, 750);
});

const tabList = document.querySelector('#guide-tabs');
const panel = document.querySelector('#guide-panel');
panel.dataset.i18nIgnore = '';
let selectedGuide = 'codex';
const serviceIcons = { codex:'codex', eleven:'elevenlabs', openai:'openai', claude:'claude', gemini:'gemini', cursor:'cursor', higgsfield:'higgsfield', 'higgsfield-api':'higgsfield' };
function fillList(selector, lines) {
  const list = document.querySelector(selector);
  list.replaceChildren(...lines.map(text => {
    const item = document.createElement('li');
    item.textContent = t(text);
    return item;
  }));
}
function selectGuide(id, moveFocus = false) {
  const guide = serviceGuides.find(item => item.id === id) || serviceGuides[0];
  selectedGuide = guide.id;
  tabList.querySelectorAll('button').forEach(button => {
    const selected = button.dataset.service === guide.id;
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
    if (selected && moveFocus) { button.focus(); button.scrollIntoView({block:'nearest', inline:'nearest'}); }
  });
  panel.setAttribute('aria-labelledby', `guide-tab-${guide.id}`);
  document.querySelector('#guide-name').textContent = t('{name} 연결', { name: t(guide.name) });
  document.querySelector('#guide-method').textContent = t(guide.method);
  document.querySelector('#guide-summary').textContent = t(guide.summary);
  panel.querySelector('h4').lastChild.nodeValue = ` ${t('연결이 안 되나요?')}`;
  fillList('#guide-steps', guide.steps);
  fillList('#guide-fixes', guide.troubleshooting);
  const docs = document.querySelector('#guide-docs');
  docs.textContent = `${t(guide.docsLabel)} ↗`;
  docs.href = guide.docsUrl;
}
serviceGuides.forEach((guide, index) => {
  const button = document.createElement('button');
  button.type = 'button';
  button.id = `guide-tab-${guide.id}`;
  button.dataset.service = guide.id;
  button.setAttribute('role', 'tab');
  button.setAttribute('aria-controls', 'guide-panel');
  button.setAttribute('aria-selected', String(index === 0));
  button.tabIndex = index === 0 ? 0 : -1;
  const icon = document.createElement('img');
  icon.src = `assets/service-icons/${serviceIcons[guide.id]}.svg`;
  icon.alt = '';
  button.append(icon, document.createTextNode(guide.name));
  button.addEventListener('click', () => selectGuide(guide.id));
  button.addEventListener('keydown', event => {
    let next;
    if (event.key === 'ArrowRight') next = (index + 1) % serviceGuides.length;
    else if (event.key === 'ArrowLeft') next = (index - 1 + serviceGuides.length) % serviceGuides.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = serviceGuides.length - 1;
    else return;
    event.preventDefault();
    selectGuide(serviceGuides[next].id, true);
  });
  tabList.append(button);
});
document.querySelectorAll('.service-link, .service-link-guide').forEach(link => link.addEventListener('click', () => selectGuide(link.dataset.service)));
selectGuide('codex');

const faqList = document.querySelector('#faq-list');
// Put installation, Free, sign-in, privacy and service basics first, retaining every answer.
const essentialFaqs = [21, 0, 7, 11, 15, 1];
const faqOrder = [...essentialFaqs, ...faqItems.map((_, index) => index).filter(index => !essentialFaqs.includes(index))];
faqOrder.forEach(index => {
  const { question, answer } = faqItems[index];
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  const paragraph = document.createElement('p');
  summary.textContent = question;
  paragraph.textContent = answer;
  details.append(summary, paragraph);
  faqList.append(details);
});

// Move nodes once; opening an answer or changing language never replaces it.
function createDisclosure(list, visibleCount, button, id, moreLabel, lessLabel) {
  const remaining = [...list.children].slice(visibleCount);
  const archive = document.createElement('div');
  archive.id = id;
  archive.hidden = true;
  archive.append(...remaining);
  list.append(archive);
  button.hidden = remaining.length === 0;
  button.dataset.i18nIgnore = '';
  button.setAttribute('aria-controls', id);
  let expanded = false;
  function updateLabel() {
    button.textContent = t(expanded ? lessLabel : moreLabel, { count: remaining.length });
    button.setAttribute('aria-expanded', String(expanded));
  }
  button.addEventListener('click', () => {
    expanded = !expanded;
    archive.hidden = !expanded;
    updateLabel();
  });
  updateLabel();
  return updateLabel;
}
const updateFaqDisclosure = createDisclosure(faqList, 6, document.querySelector('#faq-more'), 'faq-more-items', '질문 {count}개 더 보기', '추가 질문 접기');
const releaseList = document.querySelector('.release-notes');
const releaseMore = document.createElement('button');
releaseMore.id = 'release-more';
releaseMore.type = 'button';
releaseMore.className = 'disclosure-button';
const updateReleaseDisclosure = createDisclosure(releaseList, 3, releaseMore, 'release-more-items', '이전 업데이트 {count}개 보기', '이전 업데이트 접기');
releaseList.append(releaseMore);

let checksumUnavailable = false;
let checksumLoading = true;
const checksum = document.querySelector('#download-checksum');
checksum.dataset.i18nIgnore = '';
checksum.textContent = t('다운로드 정보 확인 중');
function applyLanguage() {
  selectGuide(selectedGuide);
  updateDemoLabels();
  updateDemoStatus();
  updateFaqDisclosure();
  updateReleaseDisclosure();
  if (checksumLoading) checksum.textContent = t('다운로드 정보 확인 중');
  if (checksumUnavailable) checksum.textContent = t('파일 해시는 다운로드 폴더의 SHA256SUMS.txt에서 확인할 수 있습니다.');
  for (const link of document.querySelectorAll('a[href^="mailto:"]')) {
    const url = new URL(link.href);
    url.searchParams.set('subject', getLanguage() === 'en' ? 'Nephylo inquiry' : 'Nephylo 문의');
    link.href = url.href;
  }
}
onLanguageChange(applyLanguage);
translateDocument();
applyLanguage();
initPowerShellInstall({ getLanguage, t, onLanguageChange });

fetch('downloads/release.json').then(response => {
  if (!response.ok) throw new Error('Release metadata unavailable');
  return response.json();
}).then(release => {
  const artifact = release.installer || release;
  const isInstaller = Boolean(release.installer);
  const file = isInstaller ? 'Nephylo-Setup.exe' : release.file;
  if (artifact.file !== file || !/^[a-zA-Z0-9._-]+$/.test(file) || !Number.isSafeInteger(artifact.bytes) || artifact.bytes <= 0 || !/^[a-f0-9]{64}$/i.test(artifact.sha256)) throw new Error('Invalid release metadata');
  checksumLoading = false;
  document.querySelectorAll('[data-download-size]').forEach(item => { item.textContent = `${Math.ceil(artifact.bytes / 1024)} KB ${isInstaller ? 'EXE' : 'ZIP'}`; });
  document.querySelectorAll('.download-link').forEach(link => {
    link.href = `downloads/${file}`;
    link.download = file;
  });
  checksum.textContent = artifact.sha256;
}).catch(() => { checksumLoading = false; checksumUnavailable = true; checksum.textContent = t('파일 해시는 다운로드 폴더의 SHA256SUMS.txt에서 확인할 수 있습니다.'); });

initScrollReveal();
