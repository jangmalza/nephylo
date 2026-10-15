import test from 'node:test';
import assert from 'node:assert/strict';
import {publicSourcePath,prepareOpenSource} from './prepare-open-source.mjs';
import {mkdirSync,mkdtempSync,writeFileSync,readFileSync,existsSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import path from 'node:path';
import {validateAccountConfig} from './configure-account.mjs';

test('account deployment input accepts only a public key and never enables an empty backend',()=>{
  const empty={SupabaseUrl:'',PublishableKey:'',GoogleEnabled:false,EmailEnabled:false};assert.deepEqual(validateAccountConfig(empty),empty);
  assert.throws(()=>validateAccountConfig({...empty,GoogleEnabled:true}));
  const active={...empty,SupabaseUrl:'https://'+'a'.repeat(20)+'.supabase.co',PublishableKey:'sb_publishable_example_only',GoogleEnabled:true};assert.deepEqual(validateAccountConfig(active),active);
  assert.throws(()=>validateAccountConfig({...active,PublishableKey:'sb_secret_forbidden'}));
  assert.throws(()=>validateAccountConfig({...active,ServiceRoleKey:'forbidden'}));
  assert.throws(()=>validateAccountConfig({...active,SupabaseUrl:active.SupabaseUrl+'/path'}));
});

test('source export has an explicit boundary for private data, operations and binaries',()=>{
  for(const name of ['outputs/profile-before/secret.dat','.git/config','docs/releases/1.25.20.json','migration/lightsail/STATE.md','.github/workflows/cd.yml','work/website/dist/downloads/Nephylo-Setup.exe','work/website/dist/downloads/x.zip','work/membership/client-config.json','work/website/account-config.local.json','../outside','work/src/../../outside','work/membership/node_modules/a/index.js','work/package-release.ps1','work/membership/GOOGLE-LOGIN.md'])assert.equal(publicSourcePath(name),false,name);
  for(const name of ['LICENSE','work/src/Program.cs','work/assets/update-signing-public.xml','work/membership/supabase/.env.example','work/website/account-config.example.json','work/website/dist/downloads/update.json'])assert.equal(publicSourcePath(name),true,name);
});
test('export neutralizes account configuration and refuses an existing output',()=>{
  const parent=path.resolve('outputs/oss-preparation-tests');mkdirSync(parent,{recursive:true});const repo=mkdtempSync(path.join(parent,'repo-'));
  execFileSync('git',['init','-q'],{cwd:repo,stdio:'pipe',windowsHide:true});
  const write=(name,value)=>{mkdirSync(path.dirname(path.join(repo,name)),{recursive:true});writeFileSync(path.join(repo,name),value);};
  write('LICENSE','MIT License\n');write('docs/PUBLIC-README.md','# Public source\n');write('.github/templates/public-ci.yml','name: Source CI\n');
  for(const name of ['RELEASING','UPDATE-RELEASES','WEBSITE','MEMBERSHIP'])write('docs/public/'+name+'.md','# Public developer guide\n');
  const empty=JSON.stringify({SupabaseUrl:'',PublishableKey:'',GoogleEnabled:false,EmailEnabled:false});write('work/website/account-config.example.json',empty);
  write('work/website/dist/account/config.json',JSON.stringify({SupabaseUrl:'https://'+'a'.repeat(20)+'.supabase.co',PublishableKey:'public',GoogleEnabled:true,EmailEnabled:true}));
  write('work/src/Program.cs','// source');write('work/website/dist/downloads/Nephylo-Setup.exe','MZ');write('docs/releases/private.json','private evidence');write('.gitignore','outputs/\n');
  execFileSync('git',['add','.'],{cwd:repo,stdio:'pipe',windowsHide:true});execFileSync('git',['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture'],{cwd:repo,stdio:'pipe',windowsHide:true});
  const result=prepareOpenSource({repository:repo,output:'outputs/open-source/result'});
  assert.equal(readFileSync(path.join(result.directory,'work/website/dist/account/config.json'),'utf8'),empty);
  assert.ok(!existsSync(path.join(result.directory,'.git')));assert.ok(!existsSync(path.join(result.directory,'docs/releases/private.json')));
  assert.ok(!existsSync(path.join(result.directory,'work/website/dist/downloads/Nephylo-Setup.exe')));
  assert.throws(()=>prepareOpenSource({repository:repo,output:'outputs/open-source/result'}),/overwrite/);
  assert.throws(()=>prepareOpenSource({repository:repo,output:'../outside'}),/child/);
});
