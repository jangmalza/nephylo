import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));

export function runStep(step, outputDirectory, { timeoutMs = 300000 } = {}) {
  mkdirSync(outputDirectory, { recursive: true });
  const startedAt = new Date().toISOString();
  const started = Date.now();
  // Only fixed npm arguments below use the Windows command shim. No shell
  // command is constructed from a PR title, branch name, or other event input.
  const result = spawnSync(step.executable, step.args, {
    cwd: step.cwd,
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 16 * 1024 * 1024,
    windowsHide: true,
    shell: process.platform === 'win32' && step.executable === 'npm.cmd',
    env: { ...process.env, CI: 'true', npm_config_audit: 'false', npm_config_fund: 'false' },
  });
  const stdout = result.stdout || '';
  const stderr = (result.stderr || '') + (result.error ? `\n${result.error.message}\n` : '');
  writeFileSync(path.join(outputDirectory, `${step.id}.stdout.log`), stdout, 'utf8');
  writeFileSync(path.join(outputDirectory, `${step.id}.stderr.log`), stderr, 'utf8');
  process.stdout.write(stdout);
  process.stderr.write(stderr);
  return {
    id: step.id,
    startedAt,
    elapsedMilliseconds: Date.now() - started,
    exitCode: result.status ?? 1,
    signal: result.signal ?? null,
    timedOut: result.error?.code === 'ETIMEDOUT',
    success: result.status === 0 && !result.error && !result.signal,
  };
}

export function checkGroup(group) {
  if (!['site', 'membership'].includes(group)) throw new Error('Use site or membership.');
  const outputDirectory = path.join(repositoryRoot, 'outputs/ci', group);
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  const steps = group === 'site' ? [
    { id: 'site-and-published-assets', executable: process.execPath, args: ['work/website/check.mjs'], cwd: repositoryRoot },
  ] : [
    { id: 'locked-dependencies', executable: npm, args: ['ci', '--ignore-scripts', '--no-audit', '--no-fund'], cwd: path.join(repositoryRoot, 'work/membership') },
    { id: 'membership-regressions', executable: npm, args: ['test'], cwd: path.join(repositoryRoot, 'work/membership') },
    { id: 'auth-rate-limits', executable: process.execPath, args: ['tests/auth-rate-limits.test.mjs'], cwd: path.join(repositoryRoot, 'work/membership') },
  ];
  const summary = {
    schemaVersion: 1,
    group,
    commit: process.env.GITHUB_SHA || null,
    node: process.version,
    platform: process.platform,
    scope: group === 'site' ? 'Static site and existing published artifacts; no source-to-release claim.' : 'Isolated server fixtures; no production credentials.',
    startedAt: new Date().toISOString(),
    success: false,
    steps: [],
  };
  mkdirSync(outputDirectory, { recursive: true });
  const save = () => writeFileSync(path.join(outputDirectory, 'summary.json'), JSON.stringify(summary, null, 2) + '\n', 'utf8');
  save();
  for (const step of steps) {
    console.log(`\nChecking ${step.id} (${process.version}, ${process.platform})`);
    summary.steps.push(runStep(step, outputDirectory));
    save();
    if (!summary.steps.at(-1).success) break;
  }
  summary.finishedAt = new Date().toISOString();
  summary.success = summary.steps.length === steps.length && summary.steps.every(step => step.success);
  save();
  if (process.env.GITHUB_STEP_SUMMARY) {
    const report = [`### ${group} checks`, '', '| Step | Result |', '| --- | --- |',
      ...summary.steps.map(step => `| ${step.id} | ${step.success ? 'PASS' : 'FAIL'} |`), '', summary.scope, ''];
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, report.join('\n'), 'utf8');
  }
  return summary.success;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.exitCode = checkGroup(process.argv[2]) ? 0 : 1; }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
