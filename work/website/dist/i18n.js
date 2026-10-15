// Local display preference only; never reads or changes service/account credentials.
const translations = { ko: Object.create(null), en: Object.create(null) };
const listeners = new Set();
const originals = new WeakMap();
const boundSelects = new WeakSet();
let language = 'ko';
try { if (globalThis.localStorage?.getItem('nephylo-language') === 'en') language = 'en'; } catch { /* Storage may be unavailable in a private browser. */ }
export function getLanguage() { return language; }
export function registerTranslations(catalog) {
  if (catalog.ko && catalog.en) {
    Object.assign(translations.ko, catalog.ko);
    Object.assign(translations.en, catalog.en);
  } else Object.assign(translations.en, catalog);
}
export function t(source, args = {}) {
  const text = translations[language][source] ?? translations.ko[source] ?? source;
  return String(text).replace(/\{(\w+)\}/g, (match, key) => Object.hasOwn(args, key) ? String(args[key]) : match);
}
export function onLanguageChange(callback) { listeners.add(callback); return () => listeners.delete(callback); }
export function setLanguage(next) {
  if (next !== 'ko' && next !== 'en') return;
  language = next;
  try { globalThis.localStorage?.setItem('nephylo-language', next); } catch { /* Continue with the in-memory preference. */ }
  if (globalThis.document) translateDocument();
  for (const callback of listeners) callback(next);
}
function translateValue(node, key, current, write) {
  let entries = originals.get(node);
  if (!entries) { entries = Object.create(null); originals.set(node, entries); }
  let entry = entries[key];
  // A renderer may replace a status message after the initial translation.
  if (!entry || current !== entry.rendered) entry = entries[key] = { source: current, rendered: current };
  const trimmed = entry.source.trim();
  const translated = t(trimmed);
  const next = trimmed ? entry.source.replace(trimmed, () => translated) : entry.source;
  if (current !== next) write(next);
  entry.rendered = next;
}
export function translateDocument(root = globalThis.document) {
  if (!root) return;
  const doc = root.ownerDocument || root;
  doc.documentElement.lang = language;
  const visit = node => {
    if (node.nodeType === 3) {
      translateValue(node, 'text', node.nodeValue, value => { node.nodeValue = value; });
      return;
    }
    if (node.nodeType !== 1 && node.nodeType !== 9) return;
    if (node.nodeType === 1) {
      if (node.matches('script,style,textarea,[data-i18n-ignore],[contenteditable="true"]')) return;
      for (const attr of ['aria-label', 'title', 'placeholder', 'alt']) {
        if (node.hasAttribute(attr)) translateValue(node, attr, node.getAttribute(attr), value => node.setAttribute(attr, value));
      }
      if (node.tagName === 'META' && /^(description|og:title|og:description)$/.test(node.getAttribute('name') || node.getAttribute('property') || ''))
        translateValue(node, 'content', node.getAttribute('content') || '', value => node.setAttribute('content', value));
      if (node.matches('[data-language-select]')) {
        node.value = language;
        if (!boundSelects.has(node)) {
          node.addEventListener('change', () => setLanguage(node.value));
          boundSelects.add(node);
        }
        return;
      }
      if (node.matches('input,option:not([value]),[data-i18n-value]')) return;
    }
    for (const child of node.childNodes) visit(child);
  };
  visit(root);
}
