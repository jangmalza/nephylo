import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {existsSync,lstatSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {privatePath} from '../.github/scripts/scan-secrets.mjs';
const root=fileURLToPath(new URL('../',import.meta.url));
const top=new Set(['LICENSE','THIRD-PARTY-NOTICES.txt','CONTRIBUTING.md','.gitattributes','.gitignore','.gitleaks.toml']);
const githubScripts=new Set(['scan-secrets.mjs','secret-policy.test.mjs','check-ci-result.mjs','run-node-checks.mjs']);
const workFiles=new Set(['build.ps1','build-setup.ps1','compose-notices.ps1','generate-app-icon.ps1','generate-service-icons.ps1','Install.cmd','sign-release.ps1','README.txt','UPDATE-RELEASES.md']);
const operationalHelpers=new Set(['work/configure-membership.ps1','work/package-release.ps1']);
const publicImages=new Set(['docs/images/widget.png','docs/images/minibar.png','docs/images/dashboard.png','docs/images/history.png']);
const publicDocs=new Map([['docs/public/RELEASING.md','docs/RELEASING.md'],['docs/public/UPDATE-RELEASES.md','work/UPDATE-RELEASES.md'],['docs/public/WEBSITE.md','work/website/README.md'],['docs/public/MEMBERSHIP.md','work/membership/README.md']]);
const jsonFiles=new Set(['work/membership/client-config.example.json','work/membership/package.json','work/membership/package-lock.json','work/membership/setup/auth-rate-limit-policy.v1.json','work/website/account-config.example.json','work/website/dist/account/config.json','work/website/dist/downloads/release.json','work/website/dist/downloads/update.json']);
export function otherProductPath(name){
  return /^(?:work\/src\/Studio[^/]*\.cs|work\/tests\/studio-[^/]*\.(?:cs|ps1)|work\/run-ade-dev\.ps1|docs\/(?:ADE-[^/]*\.md|AI-STUDIO\.md|validation\/(?:ade|studio)-[^/]*))$/i.test(name);
}
export function publicSourcePath(name){
  if(!name||name.includes('\\')||path.isAbsolute(name)||name.split('/').some(p=>p==='..'||p===''))return false;
  if(privatePath(name))return false;
  if(otherProductPath(name))return false;
  if(/\.(exe|dll|pdb|zip|dpapi|pem|pfx|p12|log|dat)$/i.test(name))return false;
  if(/(?:^|\/)(?:node_modules|outputs|\.git|\.temp|\.branches)(?:\/|$)/.test(name))return false;
  if(/(?:^|\/)(?:client-config\.json|account-config\.local\.json|auth\.json|settings\.json|usage-journal\.json)$/.test(name))return false;
  if(/(?:^|\/)\.env(?:\..*)?$/.test(name)&&!name.endsWith('/.env.example'))return false;
  if(top.has(name))return true;
  if(name==='.github/FUNDING.yml')return true;
  if(publicImages.has(name))return true;
  if(/^docs\/(?:OPEN-SOURCE|PUBLIC-README|LICENSES)\.md$/.test(name))return true;
  if(publicDocs.has(name))return true;
  if(/^scripts\/(?:prepare-open-source|check-public-source|configure-account|open-source\.test)\.mjs$/.test(name))return true;
  if(name==='.github/templates/public-ci.yml')return true;
  if(name.startsWith('.github/scripts/')&&githubScripts.has(path.posix.basename(name)))return true;
  if(name.startsWith('work/src/'))return /\.(cs|manifest|config)$/.test(name);
  if(name.startsWith('work/assets/'))return /\.(svg|png|ico|xml|md|txt)$/.test(name);
  if(name.startsWith('work/tests/'))return /\.(cs|ps1|py)$/.test(name);
  if(name.startsWith('work/installer/'))return /\.(iss|md)$/.test(name);
  if(name.startsWith('work/')&&workFiles.has(name.slice(5)))return true;
  if(name.startsWith('work/membership/'))return jsonFiles.has(name)||name==='work/membership/README.md'||/\.(mjs|ps1|html|sql|ts|toml)$/.test(name)||name==='work/membership/.gitignore'||name==='work/membership/supabase/.env.example';
  if(name.startsWith('work/website/'))return jsonFiles.has(name)||/\.(mjs|js|css|html|svg|png|ico|xml|txt|ps1)$/.test(name)||name==='work/website/README.md'||name==='work/website/.gitignore';
  return false;
}
export function exclusionReason(name){
  if(publicSourcePath(name))return null;
  if(otherProductPath(name))return 'other-product-source';
  if(privatePath(name)||/\.(exe|dll|pdb|zip|dpapi|pem|pfx|p12|log|dat)$/i.test(name)||/(?:^|\/)(?:node_modules|outputs|\.git|\.temp|\.branches)(?:\/|$)/.test(name))return 'private-or-generated';
  if(operationalHelpers.has(name)||(name.startsWith('work/membership/')&&name.endsWith('.md'))||name.startsWith('migration/')||name.startsWith('.github/')||name.startsWith('docs/'))return 'operational-or-private-document';
  if(['README.md','CHANGELOG.md','source-manifest.json','public-sync.json'].includes(name))return 'replaced-or-generated-document';
  return 'unclassified-source';
}
function regularFile(repository,name){
  const resolved=path.resolve(repository,name);let p=resolved;
  assert.ok(resolved.startsWith(path.resolve(repository)+path.sep),'Source escaped workspace');
  while(p!==path.resolve(repository)){assert.ok(!lstatSync(p).isSymbolicLink(),'Source symlink refused');p=path.dirname(p);}
  assert.ok(lstatSync(resolved).isFile(),'Source is not a regular file');return resolved;
}
export function prepareOpenSource({repository=root,output}={}){
  assert.ok(output,'Provide a new export directory under outputs/open-source');
  const destination=path.resolve(repository,output),allowed=path.resolve(repository,'outputs/open-source');
  assert.ok(destination.startsWith(allowed+path.sep)&&destination!==allowed,'Export must be a new child of outputs/open-source');
  assert.ok(!existsSync(destination),'Never merge into or overwrite an existing export');
  let ancestor=path.dirname(destination);while(ancestor!==path.resolve(repository)){if(existsSync(ancestor))assert.ok(!lstatSync(ancestor).isSymbolicLink(),'Export parent symlink refused');ancestor=path.dirname(ancestor);}
  const git=args=>execFileSync('git',args,{cwd:repository,encoding:'utf8',windowsHide:true});
  const names=[...new Set(git(['ls-files','--cached','--others','--exclude-standard','-z']).split('\0').filter(Boolean))];
  const excluded=names.filter(name=>!publicSourcePath(name)).map(name=>({path:name,reason:exclusionReason(name)}));
  if(excluded.some(item=>item.reason==='unclassified-source')){
    const error=new Error('Unclassified source files require explicit public-export review');error.exclusions=excluded;throw error;
  }
  const files=new Map();
  for(const name of names.filter(publicSourcePath))files.set(name,readFileSync(regularFile(repository,name)));
  const version=files.get('work/src/AssemblyInfo.cs')?.toString('utf8').match(/AssemblyFileVersion\("(\d+\.\d+\.\d+)\.0"\)/)?.[1];
  assert.ok(version,'Missing canonical application version');
  for(const required of ['LICENSE','docs/PUBLIC-README.md','.github/templates/public-ci.yml','work/website/account-config.example.json',...publicDocs.keys()])assert.ok(files.has(required),'Missing public template: '+required);
  const productionConfig=JSON.parse(files.get('work/website/dist/account/config.json').toString('utf8'));
  const projectRef=productionConfig.SupabaseUrl?.match(/^https:\/\/([a-z0-9]{20})\.supabase\.co$/)?.[1];
  for(const [name,bytes] of files){
    if(!name.endsWith('.md')||!projectRef)continue;
    const text=bytes.toString('utf8');
    if(text.includes(projectRef))files.set(name,Buffer.from('> 공개용 사본: 운영 프로젝트 식별자를 예제로 바꿨습니다. 이전 버전의 인증 설명은 현재 웹 관리자 구성에만 참고하세요.\n\n'+text.replaceAll(projectRef,'YOUR_PROJECT_REF')));
  }
  files.set('README.md',files.get('docs/PUBLIC-README.md'));
  files.set('work/README.txt',files.get('docs/PUBLIC-README.md'));
  for(const [template,target] of publicDocs)files.set(target,files.get(template));
  files.set('.github/workflows/ci.yml',files.get('.github/templates/public-ci.yml'));
  files.set('work/website/dist/account/config.json',files.get('work/website/account-config.example.json'));
  // Reusable templates remain intact so a public fork can export a later version again.
  for(const name of ['README.md','work/README.txt',...publicDocs.values()])files.set(name,Buffer.from(files.get(name).toString('utf8').replaceAll('{{SOURCE_VERSION}}',version)));
  // Developer snapshots deliberately contain neither Git history nor releases.
  // The current official update verification public key is public, not a secret.
  const inventory=[];
  for(const [name,bytes] of [...files].sort(([a],[b])=>a.localeCompare(b))){
    const target=path.join(destination,name);mkdirSync(path.dirname(target),{recursive:true});writeFileSync(target,bytes,{flag:'wx'});
    inventory.push({path:name,bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')});
  }
  const result={format:1,sourceCommit:git(['rev-parse','HEAD']).trim(),includesWorkingTreeChanges:!!git(['status','--porcelain']).trim(),
    gitHistoryIncluded:false,releaseBinariesIncluded:false,productionAccountConfigIncluded:false,files:inventory};
  writeFileSync(path.join(destination,'source-manifest.json'),JSON.stringify(result,null,2)+'\n');
  return {directory:destination,fileCount:inventory.length,manifest:result,excluded};
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{const result=prepareOpenSource({output:process.argv[2]});console.log(JSON.stringify({directory:result.directory,fileCount:result.fileCount,productionConfig:false,gitHistory:false}));}
  catch(error){console.error(error.message);process.exitCode=1;}
}
