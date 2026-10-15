import assert from 'node:assert/strict';
import {readdirSync,lstatSync,readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import path from 'node:path';
const root=path.resolve(process.argv[2]||'.');
const license=readFileSync(path.join(root,'LICENSE'),'utf8');
assert.match(license,/MIT License/);assert.match(license,/Copyright \(c\) 2026 Nephylo contributors/);
const config=JSON.parse(readFileSync(path.join(root,'work/website/dist/account/config.json'),'utf8'));
assert.deepEqual(config,{SupabaseUrl:'',PublishableKey:'',GoogleEnabled:false,EmailEnabled:false},'Public source must not target the production membership project');
// README images must be present in the exported source, not just the private checkout.
const readme=readFileSync(path.join(root,'README.md'),'utf8');
const images=[...readme.matchAll(/!\[[^\]]*\]\(([^\s)]+)\)|<img\s[^>]*src="([^"]+)"/g)].map(match=>match[1]||match[2]);
for(const image of images){
  assert.match(image,/^docs\/images\/[a-z-]+\.png$/,'README screenshots must use reviewed local PNG paths');
  const bytes=readFileSync(path.join(root,image));
  assert.ok(bytes.length>24&&bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])),'Invalid README PNG: '+image);
  assert.ok(bytes.readUInt32BE(16)>0&&bytes.readUInt32BE(20)>0,'Empty README screenshot: '+image);
}
let scripts=0;
function visit(directory){for(const name of readdirSync(directory)){
  if(['.git','outputs','node_modules'].includes(name))continue;
  const file=path.join(directory,name),stat=lstatSync(file);assert.ok(!stat.isSymbolicLink(),'Symlinks are not permitted');
  if(stat.isDirectory()){visit(file);continue;}
  assert.ok(!/\.(exe|dll|pdb|zip|pem|pfx|p12|dpapi|dat)$/i.test(name),'Source distribution contains executable/private artifact');
  if(/\.(?:md|json|js|mjs|ps1|txt|html|yml)$/.test(name)){
    const text=readFileSync(file,'utf8');
    assert.ok(!/https:\/\/[a-z0-9]{20}\.supabase\.co/.test(text),'Production project reference remains: '+path.relative(root,file));
  }
  if(/\.(mjs|js)$/.test(name)){const result=spawnSync(process.execPath,['--check',file],{encoding:'utf8',windowsHide:true});assert.equal(result.status,0,'JS syntax failed: '+path.relative(root,file));scripts++;}
}}
visit(root);console.log(JSON.stringify({passed:true,license:'MIT',publicConfigInactive:true,scriptsChecked:scripts,readmeScreenshots:images.length}));
