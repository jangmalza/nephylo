import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import path from 'node:path';
import {privatePath,scanSecrets} from './scan-secrets.mjs';

test('private data files are rejected while source examples and public keys remain allowed',()=>{
  for(const value of ['outputs/profile-before/settings.json','auth.json','x/key.pem','x/key.dpapi','x/.env.production','x/elevenlabs-key.dat','work/website/account-config.local.json'])assert.equal(privatePath(value),true,value);
  for(const value of ['work/membership/supabase/.env.example','work/membership/client-config.example.json','work/assets/update-signing-public.xml','work/website/account-config.example.json'])assert.equal(privatePath(value),false,value);
});
test('real detector rejects a new token even inside an otherwise allowlisted test file',()=>{
  assert.ok(process.env.GITLEAKS_BINARY,'Test requires the real pinned scanner');
  const root=path.resolve('outputs/ci/secret-policy');mkdirSync(root,{recursive:true});const fixture=mkdtempSync(path.join(root,'fixture-'));
  const git=args=>execFileSync('git',args,{cwd:fixture,stdio:'pipe',windowsHide:true});
  git(['init','-q']);git(['config','user.name','Fixture']);git(['config','user.email','fixture@example.invalid']);
  const file=path.join(fixture,'work/website/billing-tests.mjs');mkdirSync(path.dirname(file),{recursive:true});
  writeFileSync(file,"const config={clientKey:'test_ck_fixture123'};\n");git(['add','.']);git(['commit','-qm','Synthetic baseline']);
  const output=path.join(fixture,'../results-'+randomBytes(8).toString('hex'));
  const args={repository:fixture,config:path.resolve('.gitleaks.toml'),output};
  assert.equal(scanSecrets(args).success,true,'Reviewed exact sentinel is allowed');
  const token=randomBytes(32).toString('base64url');writeFileSync(file,`const config={clientKey:'test_ck_fixture123'};\nconst api_key='${token}';\n`);
  const failed=scanSecrets(args);assert.equal(failed.success,false);assert.ok(failed.findings.some(f=>f.rule==='generic-api-key'),'Generic key detection stays enabled even inside an allowlisted test path');
  assert.ok(!readFileSync(path.join(output,'summary.json'),'utf8').includes(token),'Uploaded summary never contains token material');
  writeFileSync(file,"const config={clientKey:'test_ck_fixture123'};\n");writeFileSync(path.join(fixture,'auth.json'),'{}');
  const forbidden=scanSecrets(args);assert.equal(forbidden.success,false);assert.ok(forbidden.forbiddenPaths.includes('auth.json'));
});
