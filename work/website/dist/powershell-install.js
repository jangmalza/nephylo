// This pin covers the public, inspectable installer script. The script itself
// verifies the signed release metadata and the downloaded EXE before opening it.
export const powershellInstallerSha256 = 'ce81e228ac16220bf65751b3c333c277bc5d465b1e22c1419b05d662d91f6279';
const scriptPath = '/downloads/install-nephylo.ps1';
const maximumScriptBytes = 64 * 1024;

async function readBounded(response) {
  if (!response.ok) throw new Error('Script unavailable');
  if (Number(response.headers.get('content-length')) > maximumScriptBytes) throw new Error('Script too large');
  if (!response.body?.getReader) {
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (!bytes.length || bytes.length > maximumScriptBytes) throw new Error('Invalid script size');
    return bytes;
  }
  const reader = response.body.getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > maximumScriptBytes) throw new Error('Script too large');
      chunks.push(value);
    }
    if (!length) throw new Error('Empty script');
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return bytes;
}

export function initPowerShellInstall({
  root = globalThis.document,
  fetchScript = globalThis.fetch?.bind(globalThis),
  crypto = globalThis.crypto,
  clipboard = globalThis.navigator?.clipboard,
  getLanguage = () => 'ko',
  t = value => value,
  onLanguageChange = () => () => {},
  expectedSha256 = powershellInstallerSha256
} = {}) {
  const disclosure = root?.querySelector('#powershell-install');
  if (!disclosure) return null;
  const copyButton = root.querySelector('#powershell-copy');
  const selectButton = root.querySelector('#powershell-select');
  const commandDetails = root.querySelector('#powershell-command-details');
  const command = root.querySelector('#powershell-command');
  const status = root.querySelector('#powershell-status');
  let script = null;
  let pending = null;
  let loading = false;
  let copying = false;
  let message = '설치 명령을 준비하려면 안내를 펼치세요.';

  function render() {
    copyButton.textContent = t(copying ? '복사 중…' : loading ? '설치 명령 확인 중…' : '설치 명령 복사');
    copyButton.disabled = loading || copying;
    command.setAttribute('aria-label', t('PowerShell 설치 명령 전체'));
    status.textContent = t(message);
    if (script !== null) {
      // Keep every character in the verified script. Translate only the wrapper
      // argument; code is assigned as plain text and excluded from page i18n.
      const language = getLanguage() === 'en' ? 'en' : 'ko';
      const next = `& {\n${script}\n} -Language '${language}'`;
      if (command.textContent !== next) command.textContent = next;
    }
  }
  async function loadScript() {
    if (script !== null) return true;
    if (pending) return pending;
    loading = true;
    message = '설치 명령 확인 중…';
    render();
    pending = Promise.resolve().then(async () => {
      let timeout;
      try {
        if (!fetchScript || !crypto?.subtle || !globalThis.AbortController || !/^[a-f0-9]{64}$/i.test(expectedSha256)) throw new Error('Verification unavailable');
        const abort = new AbortController();
        timeout = globalThis.setTimeout(() => abort.abort(), 15000);
        const response = await fetchScript(scriptPath, { cache: 'no-store', credentials: 'omit', redirect: 'error', signal: abort.signal });
        const bytes = await readBounded(response);
        const digest = await crypto.subtle.digest('SHA-256', bytes);
        const actual = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
        if (actual !== expectedSha256.toLowerCase()) throw new Error('Script integrity mismatch');
        script = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        command.disabled = false;
        message = '설치 명령을 확인했습니다. 내용을 확인한 뒤 복사하세요.';
        return true;
      } catch {
        script = null;
        command.textContent = '';
        command.disabled = true;
        selectButton.hidden = true;
        message = '설치 명령을 확인하지 못했습니다. 다시 복사하거나 EXE 다운로드를 이용해 주세요.';
        return false;
      } finally {
        globalThis.clearTimeout(timeout);
        loading = false;
        pending = null;
        render();
      }
    });
    return pending;
  }
  disclosure.addEventListener('toggle', () => {
    if (disclosure.open) void loadScript();
  });
  copyButton.addEventListener('click', async () => {
    if (copying || loading) return;
    if (!(await loadScript())) return;
    if (copying) return;
    copying = true;
    render();
    try {
      if (!clipboard?.writeText) throw new Error('Clipboard unavailable');
      await clipboard.writeText(command.textContent);
      message = '복사했습니다. 일반 PowerShell 창에 붙여넣고 Enter를 누르세요.';
      selectButton.hidden = true;
    } catch {
      commandDetails.open = true;
      selectButton.hidden = false;
      command.focus();
      command.select();
      message = '자동 복사가 차단됐습니다. 선택된 명령을 Ctrl+C로 복사해 주세요.';
    } finally {
      copying = false;
      render();
    }
  });
  selectButton.addEventListener('click', () => {
    if (script === null) return;
    commandDetails.open = true;
    command.focus();
    command.select();
  });
  copyButton.hidden = false;
  const unsubscribe = onLanguageChange(render);
  render();
  if (disclosure.open) void loadScript();
  return { loadScript, destroy: unsubscribe };
}
