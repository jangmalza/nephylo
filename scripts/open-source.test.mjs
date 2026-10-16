import test from 'node:test';
import assert from 'node:assert/strict';
import {publicSourcePath,prepareOpenSource,exclusionReason} from './prepare-open-source.mjs';
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
  assert.equal(publicSourcePath('.github/FUNDING.yml'),true);
  assert.equal(publicSourcePath('.github/private-funding.yml'),false);
  for(const name of ['outputs/profile-before/secret.dat','.git/config','docs/releases/1.25.20.json','migration/lightsail/STATE.md','.github/workflows/cd.yml','work/website/dist/downloads/Nephylo-Setup.exe','work/website/dist/downloads/x.zip','work/membership/client-config.json','work/website/account-config.local.json','../outside','work/src/../../outside','work/membership/node_modules/a/index.js','work/package-release.ps1','work/membership/GOOGLE-LOGIN.md'])assert.equal(publicSourcePath(name),false,name);
  for(const name of ['LICENSE','work/src/Program.cs','work/assets/update-signing-public.xml','work/membership/supabase/.env.example','work/website/account-config.example.json','work/website/dist/downloads/update.json','docs/images/widget.png','docs/images/minibar.png','docs/images/dashboard.png','docs/images/history.png'])assert.equal(publicSourcePath(name),true,name);
  for(const name of ['docs/images/private-account.png','docs/images/capture.json','docs/images/widget.png/../secret.png'])assert.equal(publicSourcePath(name),false,name);
});
test('export neutralizes account configuration and refuses an existing output',()=>{
  const parent=path.resolve('outputs/oss-preparation-tests');mkdirSync(parent,{recursive:true});const repo=mkdtempSync(path.join(parent,'repo-'));
  execFileSync('git',['init','-q'],{cwd:repo,stdio:'pipe',windowsHide:true});
  const write=(name,value)=>{mkdirSync(path.dirname(path.join(repo,name)),{recursive:true});writeFileSync(path.join(repo,name),value);};
  write('LICENSE','MIT License\n');write('docs/PUBLIC-README.md','# Public source {{SOURCE_VERSION}}\n');write('.github/templates/public-ci.yml','name: Source CI\n');
  write('.github/FUNDING.yml','github: jangmalza\n');
  write('work/src/AssemblyInfo.cs','[assembly: AssemblyFileVersion("1.25.21.0")]');
  for(const name of ['RELEASING','UPDATE-RELEASES','WEBSITE','MEMBERSHIP'])write('docs/public/'+name+'.md','# Public developer guide\n');
  const empty=JSON.stringify({SupabaseUrl:'',PublishableKey:'',GoogleEnabled:false,EmailEnabled:false});write('work/website/account-config.example.json',empty);
  write('work/website/dist/account/config.json',JSON.stringify({SupabaseUrl:'https://'+'a'.repeat(20)+'.supabase.co',PublishableKey:'public',GoogleEnabled:true,EmailEnabled:true}));
  write('work/src/Program.cs','// source');write('work/website/dist/downloads/Nephylo-Setup.exe','MZ');write('docs/releases/private.json','private evidence');write('.gitignore','outputs/\n');
  execFileSync('git',['add','.'],{cwd:repo,stdio:'pipe',windowsHide:true});execFileSync('git',['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture'],{cwd:repo,stdio:'pipe',windowsHide:true});
  const result=prepareOpenSource({repository:repo,output:'outputs/open-source/result'});
  assert.equal(readFileSync(path.join(result.directory,'README.md'),'utf8'),'# Public source 1.25.21\n');
  assert.equal(readFileSync(path.join(result.directory,'.github/FUNDING.yml'),'utf8'),'github: jangmalza\n');
  assert.equal(readFileSync(path.join(result.directory,'docs/PUBLIC-README.md'),'utf8'),'# Public source {{SOURCE_VERSION}}\n');
  assert.equal(readFileSync(path.join(result.directory,'work/website/dist/account/config.json'),'utf8'),empty);
  assert.ok(!existsSync(path.join(result.directory,'.git')));assert.ok(!existsSync(path.join(result.directory,'docs/releases/private.json')));
  assert.ok(!existsSync(path.join(result.directory,'work/website/dist/downloads/Nephylo-Setup.exe')));
  assert.throws(()=>prepareOpenSource({repository:repo,output:'outputs/open-source/result'}),/overwrite/);
  assert.throws(()=>prepareOpenSource({repository:repo,output:'../outside'}),/child/);
  // Exported templates must support a second generation with a changed app version.
  execFileSync('git',['init','-q'],{cwd:result.directory,stdio:'pipe',windowsHide:true});
  writeFileSync(path.join(result.directory,'work/src/AssemblyInfo.cs'),'[assembly: AssemblyFileVersion("1.25.22.0")]');
  execFileSync('git',['add','.'],{cwd:result.directory,stdio:'pipe',windowsHide:true});
  execFileSync('git',['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture'],{cwd:result.directory,stdio:'pipe',windowsHide:true});
  const second=prepareOpenSource({repository:result.directory,output:'outputs/open-source/second'});
  assert.equal(readFileSync(path.join(second.directory,'README.md'),'utf8'),'# Public source 1.25.22\n');
  assert.equal(readFileSync(path.join(second.directory,'docs/PUBLIC-README.md'),'utf8'),'# Public source {{SOURCE_VERSION}}\n');
  write('work/tests/fixtures/new-data.json','{}');
  assert.throws(()=>prepareOpenSource({repository:repo,output:'outputs/open-source/rejected'}),error=>error.exclusions.some(x=>x.path==='work/tests/fixtures/new-data.json'&&x.reason==='unclassified-source'));
  assert.ok(!existsSync(path.join(repo,'outputs/open-source/rejected')),'Unknown files fail before a snapshot is written');
});

test('unknown source formats fail explicitly while private data remain excluded',()=>{
 for(const name of ['work/src/View.xaml','work/assets/prices.json','work/tests/fixture.json','work/new-helper.ps1','package.json','resources/NewView.resx'])assert.equal(exclusionReason(name),'unclassified-source');
 for(const name of ['work/tests/auth.json','outputs/backup.json','work/website/dist/downloads/Nephylo-Setup.exe'])assert.equal(exclusionReason(name),'private-or-generated');
 for(const name of ['work/configure-membership.ps1','work/package-release.ps1'])assert.equal(exclusionReason(name),'operational-or-private-document');
});
