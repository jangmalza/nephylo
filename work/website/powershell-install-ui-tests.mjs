import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash, webcrypto } from 'node:crypto';
import { initPowerShellInstall, powershellInstallerSha256 } from './dist/powershell-install.js';
import { mainTranslations } from './dist/main-translations.js';

const html = await readFile(new URL('./dist/index.html', import.meta.url), 'utf8');
const scriptBody = "param([string]$Language = 'ko')\r\n# inspectable <script> and 한글 are plain text\r\nWrite-Host $Language\r\n";
const scriptBytes = Buffer.from(scriptBody, 'utf8');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
let checks = 0;
const equal = (actual, expected, message) => { assert.deepEqual(actual, expected, message); checks++; };
const ok = (value, message) => { assert.ok(value, message); checks++; };

function fixture({ bytes = scriptBytes, fetchScript, clipboard, crypto = webcrypto, language = 'ko', expectedSha256 = hash(bytes) } = {}) {
  const nodes = new Map();
  for (const match of html.matchAll(/<([a-z][a-z0-9-]*)\b([^>]*\bid="(powershell-[^"]+)"[^>]*)>/g)) {
    const attrs = match[2];
    const item = {
      hidden: /\bhidden\b/.test(attrs), disabled: /\bdisabled\b/.test(attrs), open: /\bopen\b/.test(attrs),
      textContent: '', events: new Map(), attributes: new Map(), focused: 0, selected: 0,
      setAttribute(name, value) { this.attributes.set(name, String(value)); },
      getAttribute(name) { return this.attributes.get(name) ?? null; },
      addEventListener(type, fn) { this.events.set(type, fn); },
      focus() { this.focused++; }, select() { this.selected++; },
      async fire(type) { return this.events.get(type)?.(); }
    };
    Object.defineProperty(item, 'innerHTML', { set() { throw new Error('The installer must never insert HTML'); } });
    nodes.set(match[3], item);
  }
  const requests = [], copied = [], languageCallbacks = [];
  const state = { language };
  const runtime = initPowerShellInstall({
    root: { querySelector(selector) { return nodes.get(selector.slice(1)); } },
    fetchScript: async (path, options) => { requests.push({ path, options }); return fetchScript ? fetchScript(path, options) : new Response(bytes); },
    clipboard: clipboard === undefined ? { async writeText(text) { copied.push(text); } } : clipboard,
    crypto, expectedSha256, getLanguage: () => state.language,
    t: text => state.language === 'en' ? mainTranslations[text] ?? text : text,
    onLanguageChange(fn) { languageCallbacks.push(fn); return () => { state.destroyed = true; }; }
  });
  const node = id => { assert.ok(nodes.has(id), `Missing page target #${id}`); return nodes.get(id); };
  return {
    runtime, node, requests, copied, state,
    async open() { node('powershell-install').open = true; await node('powershell-install').fire('toggle'); return runtime.loadScript(); },
    setLanguage(value) { state.language = value; for (const fn of languageCallbacks) fn(); },
    copy: () => node('powershell-copy').fire('click')
  };
}

// The real controller runs against nodes from the actual homepage, with fixture
// network bytes and browser clipboard I/O. No installer or PowerShell is run.
const ui = fixture();
equal(ui.requests.length, 0, 'Page load does not download the optional script');
equal(ui.copied.length, 0, 'Page load never writes to the clipboard');
equal(ui.node('powershell-copy').hidden, false, 'The controller reveals the optional copy button');
equal(ui.node('powershell-command').getAttribute('aria-label'), 'PowerShell 설치 명령 전체', 'The raw code viewer has a Korean accessible label');
ok(await ui.open(), 'Opening the disclosure verifies the script');
equal(ui.requests.length, 1, 'Disclosure and caller share a single in-flight download');
const { signal: requestSignal, ...requestOptions } = ui.requests[0].options;
equal({ path: ui.requests[0].path, options: requestOptions }, { path: '/downloads/install-nephylo.ps1', options: { cache: 'no-store', credentials: 'omit', redirect: 'error' } }, 'Only the relative public script is fetched, without credentials or redirects');
ok(requestSignal instanceof AbortSignal, 'The script request has a bounded cancellation signal');
equal(ui.copied.length, 0, 'Viewing code does not write to the clipboard');
equal(ui.node('powershell-command').textContent, `& {\n${scriptBody}\n} -Language 'ko'`, 'Every script character survives the Korean wrapper');
equal(ui.node('powershell-command').disabled, false, 'Only verified script code becomes selectable');
await ui.copy();
equal(ui.copied, [`& {\n${scriptBody}\n} -Language 'ko'`], 'Copying writes only the verified inspectable command');
ok(ui.node('powershell-status').textContent.includes('복사했습니다'), 'Copy completion is announced');
ui.setLanguage('en');
equal(ui.node('powershell-command').textContent, `& {\n${scriptBody}\n} -Language 'en'`, 'English changes only the wrapper language argument');
equal(ui.node('powershell-copy').textContent, 'Copy installation command', 'Dynamic button uses the chosen display language');
equal(ui.node('powershell-command').getAttribute('aria-label'), 'Full PowerShell installation command', 'The raw code viewer label translates without changing the script body');
ok(!/[가-힣]/.test(ui.node('powershell-status').textContent), 'Dynamic success status translates on language change');
await ui.copy();
equal(ui.copied.at(-1), `& {\n${scriptBody}\n} -Language 'en'`, 'Copy follows the current language');
equal(ui.requests.length, 1, 'Changing language and copying reuses verified script bytes');
ui.setLanguage("en'; Write-Host injected; '");
equal(ui.node('powershell-command').textContent, `& {\n${scriptBody}\n} -Language 'ko'`, 'Unsupported language cannot inject PowerShell arguments');
ui.runtime.destroy();
equal(ui.state.destroyed, true, 'Language listener can be detached');

for (const clipboard of [null, { async writeText() { throw new Error('Clipboard permission denied'); } }]) {
  const blocked = fixture({ clipboard, language: 'en' });
  await blocked.open();
  await blocked.copy();
  equal(blocked.node('powershell-command-details').open, true, 'Blocked copying opens the complete command');
  equal(blocked.node('powershell-select').hidden, false, 'Blocked copying exposes manual selection');
  equal(blocked.node('powershell-command').focused, 1, 'Manual copy focuses the code');
  equal(blocked.node('powershell-command').selected, 1, 'Manual copy selects all verified code');
  ok(blocked.node('powershell-status').textContent.includes('Ctrl+C'), 'Clipboard fallback clearly explains manual copying');
  await blocked.node('powershell-select').fire('click');
  equal(blocked.node('powershell-command').selected, 2, 'Manual selection button remains usable');
}

let changed = true;
const tampered = fixture({ expectedSha256: hash(scriptBytes), fetchScript: () => new Response(changed ? Buffer.from(scriptBody + '# altered') : scriptBytes) });
equal(await tampered.open(), false, 'Changed public script fails the pinned SHA-256 check');
equal(tampered.node('powershell-command').textContent, '', 'Unverified script is never exposed as an install command');
equal(tampered.node('powershell-command').disabled, true, 'Unverified code cannot be selected');
await tampered.copy();
equal(tampered.copied.length, 0, 'Hash mismatch never reaches the clipboard');
equal(tampered.node('powershell-copy').disabled, false, 'Failed verification leaves a retry available');
changed = false;
await tampered.copy();
equal(tampered.copied.length, 1, 'The next explicit click can retry and copy a verified script');
equal(tampered.requests.length, 3, 'Failed attempts are not cached indefinitely');

let digestCalls = 0;
const countedCrypto = { subtle: { async digest(...args) { digestCalls++; return webcrypto.subtle.digest(...args); } } };
const oversize = fixture({ fetchScript: () => new Response('small', { headers: { 'content-length': '65537' } }), crypto: countedCrypto });
equal(await oversize.open(), false, 'Oversized declared content is rejected');
equal(digestCalls, 0, 'Declared oversize stops before hashing or copying');
let cancelled = false;
const largeStream = fixture({ crypto: countedCrypto, fetchScript: () => new Response(new ReadableStream({
  start(controller) { controller.enqueue(new Uint8Array(65536)); controller.enqueue(new Uint8Array(1)); },
  cancel() { cancelled = true; }
})) });
equal(await largeStream.open(), false, 'Streaming content is bounded even without Content-Length');
equal(cancelled, true, 'An oversized stream is cancelled');
equal(digestCalls, 0, 'Oversized streaming data never reaches the hash verification');
for (const bytes of [Buffer.alloc(0), Buffer.from([0xff, 0xfe, 0xff])]) {
  const invalid = fixture({ bytes });
  equal(await invalid.open(), false, 'Empty or invalid UTF-8 script cannot be installed');
  equal(invalid.node('powershell-command').textContent, '', 'Invalid bytes leave the command empty');
  equal(invalid.copied.length, 0, 'Invalid bytes are never copied');
}
const noCrypto = fixture({ crypto: null });
equal(await noCrypto.open(), false, 'Unavailable SHA-256 verification fails closed');
equal(noCrypto.requests.length, 0, 'A browser without verification support never requests script execution data');
const outage = fixture({ fetchScript: () => new Response('outage', { status: 503 }) });
equal(await outage.open(), false, 'HTTP errors cannot produce a command');
ok(outage.node('powershell-status').textContent.includes('EXE 다운로드'), 'Network or verification failure offers the existing installer');
const synchronousFailure = fixture({ fetchScript() { throw new Error('Offline'); } });
equal(await synchronousFailure.open(), false, 'Synchronous network failure is handled');
await synchronousFailure.copy();
equal(synchronousFailure.requests.length, 2, 'Even synchronous failure allows an explicit retry');

let completeCopy;
let copyCalls = 0;
const delayed = fixture({ clipboard: { writeText() { copyCalls++; return new Promise(resolve => { completeCopy = resolve; }); } } });
await delayed.open();
const copies = [delayed.copy(), delayed.copy()];
await Promise.resolve();
equal(delayed.node('powershell-copy').disabled, true, 'Pending clipboard operation disables the copy button');
ok(typeof completeCopy === 'function', 'One clipboard operation started');
equal(copyCalls, 1, 'Repeated clicks cannot start duplicate clipboard operations');
completeCopy();
await Promise.all(copies);
equal(delayed.node('powershell-copy').disabled, false, 'Clipboard completion restores the copy button');

const canonicalBytes = await readFile(new URL('./dist/downloads/install-nephylo.ps1', import.meta.url));
const originalSetTimeout = globalThis.setTimeout;
let timeoutCallback;
try {
  globalThis.setTimeout = (callback, delay) => {
    equal(delay, 15000, 'Installer source request has a fifteen-second deadline');
    timeoutCallback = callback;
    return 0;
  };
  const stalled = fixture({ fetchScript: (_, { signal }) => new Promise((_, reject) => signal.addEventListener('abort', () => reject(new Error('Timeout')))) });
  const pendingLoad = stalled.open();
  await Promise.resolve();
  timeoutCallback();
  equal(await pendingLoad, false, 'A stalled request exits through the verification failure state');
  equal(stalled.node('powershell-copy').disabled, false, 'Timeout restores an explicit retry');
  equal(stalled.copied.length, 0, 'Timeout never copies unverified code');
} finally { globalThis.setTimeout = originalSetTimeout; }
equal(hash(canonicalBytes), powershellInstallerSha256, 'The published script matches the pin in the actual page module');
ok(canonicalBytes.length > 0 && canonicalBytes.length <= 65536, 'The canonical script fits the bounded viewer');
ok(/<textarea[^>]+id="powershell-command"[^>]+readonly[^>]+data-i18n-ignore/.test(html), 'Raw command viewer is read-only and excluded from translation');
ok(/id="powershell-status"[^>]+role="status"[^>]+aria-live="polite"/.test(html), 'Copy and verification results have an accessible live region');
ok(/id="powershell-install"/.test(html) && html.includes('Windows용 무료 다운로드'), 'PowerShell is optional alongside the existing EXE download');
console.log(`PASS: ${checks} PowerShell installation UI checks.`);
