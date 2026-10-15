import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {lstatSync,mkdirSync,mkdtempSync,readFileSync,writeFileSync,copyFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

export const scannerVersion='8.30.1';
const root=fileURLToPath(new URL('../../',import.meta.url));
export function privatePath(name){
  const base=path.posix.basename(name);
  return name.startsWith('outputs/') || name.startsWith('.git/') || /\.(pem|pfx|p12|dpapi)$/i.test(name)
    || ['auth.json','membership-session.dat','usage-journal.json','extra-settings.json','plan-settings.json','settings.json','client-config.json','account-config.local.json','release-signing-key.xml'].includes(base)
    || (base.startsWith('.env') && base!=='.env.example') || /-key\.dat$/i.test(base);
}
function command(exe,args,cwd){
  const result=spawnSync(exe,args,{cwd,encoding:'utf8',windowsHide:true,timeout:300000,maxBuffer:16*1024*1024,
    env:{...process.env,GITLEAKS_CONFIG:'',GITLEAKS_CONFIG_TOML:'',GIT_TERMINAL_PROMPT:'0'}});
  if(result.error || result.signal)throw new Error('Secret scanner process did not complete.');
  return result;
}
export function scanSecrets({repository=root,binary=process.env.GITLEAKS_BINARY,config=path.join(root,'.gitleaks.toml'),output=path.join(root,'outputs/ci/secrets')}={}){
  assert.ok(binary,'GITLEAKS_BINARY must point to the checksum-verified scanner.');
  const version=command(binary,['version'],repository);
  assert.equal(version.status,0,'Scanner unavailable');assert.equal(version.stdout.trim(),scannerVersion,'Unexpected scanner version');
  mkdirSync(output,{recursive:true});const scratch=mkdtempSync(path.join(output,'private-scan-'));
  const git=args=>{const r=command('git',args,repository);assert.equal(r.status,0,'Cannot inventory Git');return r.stdout;};
  const paths=[...new Set(git(['ls-files','--cached','--others','--exclude-standard','-z']).split('\0').filter(Boolean))];
  const forbidden=paths.filter(privatePath);
  const tree=path.join(scratch,'tree');mkdirSync(tree);
  for(const name of paths){
    assert.ok(!path.isAbsolute(name)&&!name.split('/').includes('..'),'Invalid tracked path');
    const source=path.resolve(repository,name);let current=source;
    while(current!==path.resolve(repository)){
      assert.ok(current.startsWith(path.resolve(repository)+path.sep),'Path escaped repository');
      assert.ok(!lstatSync(current).isSymbolicLink(),'Symlink cannot enter scan snapshot');current=path.dirname(current);
    }
    assert.ok(lstatSync(source).isFile(),'Tracked entry is not a regular file');
    const destination=path.join(tree,name);mkdirSync(path.dirname(destination),{recursive:true});copyFileSync(source,destination);
  }
  const findings=[],phases=[];
  for(const mode of ['git','dir']){
    const report=path.join(scratch,mode+'.json');
    const args=[mode,'--config',path.resolve(config),'--ignore-gitleaks-allow','--gitleaks-ignore-path',scratch,
      '--redact=100','--no-banner','--no-color','--max-archive-depth','3','--report-format','json','--report-path',report];
    if(mode==='git')args.push('--log-opts=--all --full-history',repository);else args.push('.');
    const result=command(binary,args,mode==='dir'?tree:repository);
    assert.ok(result.status===0||result.status===1,'Secret scan failed to execute; refusing a green result');
    let detected;try{detected=JSON.parse(readFileSync(report,'utf8'));}catch(error){throw new Error('Scan has no valid diagnostic report; refusing a green result');}
    assert.ok(Array.isArray(detected),'Invalid scanner report');
    for(const item of detected)findings.push({phase:mode,rule:item.RuleID,path:mode==='dir'?path.relative(tree,path.resolve(tree,item.File)).replaceAll('\\','/'):item.File,line:item.StartLine,commit:item.Commit||null});
    phases.push({mode,exitCode:result.status,findings:detected.length});
  }
  const result={success:!forbidden.length&&phases.every(p=>p.exitCode===0),scannerVersion,workingTreeFiles:paths.length,forbiddenPaths:forbidden,phases,findings};
  // Only this allowlisted summary is uploaded; never publish Match/Secret/context.
  writeFileSync(path.join(output,'summary.json'),JSON.stringify(result,null,2)+'\n');
  return result;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{const result=scanSecrets();console.log(JSON.stringify(result));process.exitCode=result.success?0:1;}
  catch(error){console.error(error.message);process.exitCode=1;}
}
