import { appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const requiredJobs = ['site-check', 'widget-check', 'membership-check', 'secret-check'];

export function evaluateResults(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  return Object.keys(value).length === requiredJobs.length
    && requiredJobs.every(job => value[job]?.result === 'success');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const value = JSON.parse(process.env.CI_JOB_RESULTS || 'null');
    const success = evaluateResults(value);
    const lines = ['## Nephylo CI', '', '| Required job | Result |', '| --- | --- |',
      ...requiredJobs.map(job => `| ${job} | ${['success', 'failure', 'cancelled', 'skipped'].includes(value?.[job]?.result) ? value[job].result : 'missing or invalid'} |`), '',
      success ? 'PASS: all required jobs succeeded.' : 'FAIL: every required job must succeed; cancelled, skipped, and missing jobs are not accepted.',
      '', 'These checks do not deploy a site or publish a Windows release.', ''];
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, lines.join('\n'), 'utf8');
    console.log(lines.join('\n'));
    process.exitCode = success ? 0 : 1;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
