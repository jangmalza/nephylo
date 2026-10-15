"""Run the actual fixed WSL scripts against a disposable HOME, never user config.

On Linux the flock lifetime case uses the real OS primitive. Git Bash has no
flock utility, so Windows runs a narrow command-boundary fixture for that call;
path selection, shell argument handling and all file operations remain real.
"""
import base64
import json
import os
import pathlib
import re
import shutil
import signal
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
RUNTIME = (ROOT / 'src/WslRuntime.cs').read_text(encoding='utf-8-sig')
CLAUDE = (ROOT / 'src/WslClaudeUsage.cs').read_text(encoding='utf-8-sig')
WINDOWS = os.name == 'nt'
SHELL = pathlib.Path('C:/Program Files/Git/bin/bash.exe') if WINDOWS else pathlib.Path(shutil.which('bash') or '/bin/sh')
checks = 0


def check(condition, message):
    global checks
    if not condition:
        raise AssertionError(message)
    checks += 1


def verbatim(name):
    match = re.search(r'internal const string ' + name + r' = (?:LinuxPathScript \+ )?@"((?:""|[^"])*)";', RUNTIME)
    assert match, name
    return match.group(1).replace('""', '"')


def quoted(name):
    chunk = CLAUDE.split('internal const string ' + name + ' =', 1)[1].split('\n\n', 1)[0]
    # Remove full-line comments before parsing C# strings. These fixed scripts
    # only use escapes shared with JSON, so no source code is evaluated here.
    chunk = '\n'.join(line for line in chunk.splitlines() if not line.lstrip().startswith('//'))
    return ''.join(json.loads(value) for value in re.findall(r'"(?:\\.|[^"\\])*"', chunk))


def linux_path(path):
    value = str(path.resolve()).replace('\\', '/')
    return '/' + value[0].lower() + value[2:] if WINDOWS else value


def executable(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')
    path.chmod(0o700)


path_script = verbatim('LinuxPathScript')
codex_script = path_script + verbatim('LinuxCodexScript')
read_script = quoted('WslReadConfigScript')
write_script = quoted('WslWriteConfigScript')

with tempfile.TemporaryDirectory(prefix='nephylo-wsl-shell-') as temporary:
    temporary_root = pathlib.Path(temporary).resolve()
    assert temporary_root.parent == pathlib.Path(tempfile.gettempdir()).resolve()
    assert temporary_root.name.startswith('nephylo-wsl-shell-')
    home = temporary_root / "fixture home ' $literal"
    home.mkdir()
    env = {key: value for key, value in os.environ.items() if key.upper() in ('SYSTEMROOT', 'WINDIR', 'COMSPEC', 'TEMP', 'TMP', 'LANG')}
    env.update(HOME=linux_path(home), PATH='/usr/bin:/bin', LC_ALL='C')
    shell_options = [str(SHELL), '--noprofile', '--norc'] if SHELL.name in ('bash', 'bash.exe') else [str(SHELL)]

    def run(script, args=(), stdin=None, overrides=None, timeout=8):
        result = subprocess.run(shell_options + ['-c', script, 'nephylo-fixture'] + list(args),
                                input=None if stdin is None else stdin.encode('utf-8'), capture_output=True,
                                env={**env, **(overrides or {})}, cwd=home, timeout=timeout,
                                creationflags=subprocess.CREATE_NO_WINDOW if WINDOWS else 0)
        result.stdout = result.stdout.decode('utf-8')
        result.stderr = result.stderr.decode('utf-8')
        return result

    native_codex = home / '.local/bin/codex'
    native_claude = home / '.local/bin/claude'
    executable(native_codex, '#!/bin/sh\nexit 0\n')
    executable(native_claude, '#!/bin/sh\nexit 0\n')
    for version in ('v20.9.0', 'v20.10.0', 'v9.99.0', 'v999.invalid'):
        binary_dir = home / '.nvm/versions/node' / version / 'bin'
        executable(binary_dir / 'codex', '#!/bin/sh\nprintf "BIN:%s\\nARG:%s\\nNODE:%s\\n" "$0" "$1" "$(command -v node)"\n')
        executable(binary_dir / 'node', '#!/bin/sh\nexit 0\n')
        executable(binary_dir / 'claude', '#!/bin/sh\nexit 0\n')

    def probe():
        result = run(path_script + '\nprintf "CODEX:%s\\nCLAUDE:%s\\n" "$(find_tool codex)" "$(find_tool claude)"')
        check(result.returncode == 0, 'fixed path probe must finish')
        return dict(line.split(':', 1) for line in result.stdout.splitlines())

    found = probe()
    check(found['CODEX'] == linux_path(native_codex), 'native Codex must win over every nvm copy')
    check(found['CLAUDE'] == linux_path(native_claude), 'native Claude must win over every nvm copy')
    native_codex.unlink()
    found = probe()
    check(found['CODEX'].endswith('/v20.10.0/bin/codex'), 'nvm fallback compares numeric major/minor versions and rejects invalid version names')
    check(found['CLAUDE'] == linux_path(native_claude), 'per-tool fallback does not displace another native tool')
    executable(home / '.nvm/versions/node/v22.0.0/bin/node', '#!/bin/sh\nexit 0\n')
    result = run(codex_script)
    check(result.returncode == 0, 'fixed Codex launch script must start the fixture')
    launched = dict(line.split(':', 1) for line in result.stdout.splitlines())
    check(launched['BIN'].endswith('/v20.10.0/bin/codex'), 'version without Codex must not hide the installed fallback')
    check(launched['NODE'].endswith('/v20.10.0/bin/node'), 'selected nvm Codex uses its matching node runtime')
    check(launched['ARG'] == 'app-server', 'Codex launch keeps its app-server protocol argument')
    native_claude.unlink()
    check(probe()['CLAUDE'].endswith('/v20.10.0/bin/claude'), 'Claude fallback also follows numeric version order')

    # Fixed scripts are exercised with literal paths containing shell syntax.
    tools = home / 'fixture-tools'
    tools.mkdir()
    real_flock = not WINDOWS and shutil.which('flock') is not None
    if not real_flock:
        executable(tools / 'flock', '#!/bin/sh\n[ "$*" = "-x -w 3 9" ] || exit 92\nprintf "" >&9 || exit 93\n[ "${NEPHYLO_FIXTURE_LOCK_BUSY:-0}" != 1 ]\n')
    command_path = linux_path(tools) + ':/usr/bin:/bin'
    config = home / '.claude' / "settings 'quoted' $(literal).json"
    config.parent.mkdir()
    before = b'\xef\xbb\xbf{"theme":"dark","permissions":{"deny":["fixture"]}}\n'
    after = b'{"theme":"dark","permissions":{"deny":["fixture"]},"statusLine":{"type":"command","command":"fixture"}}'
    config.write_bytes(before)

    def write(path, original, updated, backup_name, overrides=None):
        payload = ('MISSING' if original is None else base64.b64encode(original).decode()) + '\n' + base64.b64encode(updated).decode() + '\n'
        return run(write_script, [linux_path(path), linux_path(path.parent / backup_name)], payload,
                   {'PATH': command_path, **(overrides or {})})

    result = run(read_script, [linux_path(config)])
    check(result.returncode == 0 and result.stdout.startswith('DATA:'), 'read script accepts literal config paths')
    check(base64.b64decode(result.stdout[5:]) == before, 'read preserves exact UTF-8 BOM and original bytes')
    result = write(config, before, after, 'first-backup.json')
    check(result.returncode == 0 and result.stdout == 'OK', f'write script acquires the lifetime lock and commits (exit {result.returncode}: {result.stderr.strip()})')
    check(config.read_bytes() == after, 'updated config bytes are exact')
    check((config.parent / 'first-backup.json').read_bytes() == before, 'backup preserves the exact previous config')
    marker = pathlib.Path(str(config) + '.nephylo-lock')
    check(marker.is_file() and marker.read_text() == 'NEPHYLO_FLOCK_V1\n', 'legacy exclusion marker identifies the new protocol')
    result = run('mkdir -- "$1"', [linux_path(marker)])
    check(result.returncode != 0 and marker.is_file(), 'old mkdir-based writers cannot race with the new protocol')
    result = write(config, after, b'{}', 'second-backup.json')
    check(result.returncode == 0 and config.read_bytes() == b'{}', 'persistent marker and lock file never strand the next completed operation')
    check(not list(config.parent.glob('.nephylo-before-*')) and not list(config.parent.glob('.nephylo-after-*')), 'successful writes remove temporary config copies')
    result = write(config, after, b'{"stale":true}', 'stale-backup.json')
    check(result.returncode == 44 and config.read_bytes() == b'{}', 'stale readers cannot overwrite a newer config')
    check(not (config.parent / 'stale-backup.json').exists(), 'rejected stale update does not create a misleading backup')

    old_config = config.parent / 'legacy-settings.json'
    old_config.write_bytes(before)
    old_lock = pathlib.Path(str(old_config) + '.nephylo-lock')
    old_lock.mkdir()
    (old_lock / 'unowned.txt').write_text('preserve', encoding='utf-8')
    result = write(old_config, before, after, 'legacy-backup.json')
    check(result.returncode == 45, 'unowned legacy directory gets a distinct recovery diagnostic')
    check(old_lock.is_dir() and (old_lock / 'unowned.txt').read_text() == 'preserve', 'unowned legacy lock is neither deleted nor claimed')
    check(old_config.read_bytes() == before and not (config.parent / 'legacy-backup.json').exists(), 'legacy conflict never changes settings or backup')
    unknown_config = config.parent / 'unknown-marker-settings.json'
    unknown_config.write_bytes(before)
    unknown_marker = pathlib.Path(str(unknown_config) + '.nephylo-lock')
    unknown_marker.write_text('NEPHYLO_FLOCK_V1\n\n', encoding='utf-8', newline='\n')
    result = write(unknown_config, before, after, 'unknown-marker-backup.json')
    check(result.returncode == 45 and unknown_config.read_bytes() == before and unknown_marker.read_bytes() == b'NEPHYLO_FLOCK_V1\n\n', 'only exact owned marker bytes can be reused; unknown files are preserved')

    missing_tools = home / 'without-flock'
    executable(missing_tools / 'mkdir', '#!/bin/sh\nexec /usr/bin/mkdir "$@"\n')
    result = write(config, b'{}', after, 'missing-flock-backup.json', {'PATH': linux_path(missing_tools)})
    check(result.returncode == 46 and config.read_bytes() == b'{}', 'missing flock fails before modifying settings')
    if real_flock:
        holder = subprocess.Popen(shell_options + ['-c', 'exec 9>>"$1"; flock -x -w 3 9 || exit 1; printf READY; sleep 30', 'holder', linux_path(pathlib.Path(str(config) + '.nephylo-flock'))],
                                  env=env, cwd=home, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        try:
            check(holder.stdout.read(5) == b'READY', 'real flock survives the acquisition utility exiting')
            result = write(config, b'{}', after, 'busy-backup.json')
            check(result.returncode == 43 and config.read_bytes() == b'{}', 'competing writer waits only the bounded lock interval')
        finally:
            os.killpg(holder.pid, signal.SIGKILL)
            holder.communicate(timeout=5)
        result = write(config, b'{}', after, 'after-crash-backup.json')
        check(result.returncode == 0, 'OS releases flock after a killed process without deleting lock files')
    else:
        result = write(config, b'{}', after, 'busy-backup.json', {'NEPHYLO_FIXTURE_LOCK_BUSY': '1'})
        check(result.returncode == 43 and config.read_bytes() == b'{}', 'flock contention diagnostic preserves settings')

print(f'PASS: {checks} isolated actual-shell WSL script checks; no user configuration or live CLI accessed.')
print('Lock boundary: real Linux flock lifetime verified.' if real_flock else 'Lock boundary: Git Bash fixture; real Linux flock lifetime not executed on this Windows host.')
