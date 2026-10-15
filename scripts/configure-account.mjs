import assert from 'node:assert/strict';
import {readFileSync,writeFileSync,renameSync,unlinkSync,existsSync,lstatSync} from 'node:fs';
import {randomUUID} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../',import.meta.url));
export function validateAccountConfig(value){
  const fields=['EmailEnabled','GoogleEnabled','PublishableKey','SupabaseUrl'];
  const require=(ok,message)=>{if(!ok)throw new Error(message);};
  require(value&&typeof value==='object'&&!Array.isArray(value),'Expected public account configuration');
  require(JSON.stringify(Object.keys(value).sort())===JSON.stringify(fields),'Only four public fields are allowed; never add secret/service-role keys');
  require(typeof value.SupabaseUrl==='string'&&typeof value.PublishableKey==='string','URL and public key must be strings');
  require(typeof value.GoogleEnabled==='boolean'&&typeof value.EmailEnabled==='boolean','Provider switches must be booleans');
  if(value.SupabaseUrl===''&&value.PublishableKey==='')require(!value.GoogleEnabled&&!value.EmailEnabled,'Empty configuration cannot enable authentication');
  else{require(/^https:\/\/[a-z0-9]{20}\.supabase\.co$/.test(value.SupabaseUrl),'Use your Supabase project HTTPS origin');require(/^sb_publishable_[A-Za-z0-9_-]{8,}$/.test(value.PublishableKey),'Only a publishable key is allowed');}
  return {SupabaseUrl:value.SupabaseUrl,PublishableKey:value.PublishableKey,GoogleEnabled:value.GoogleEnabled,EmailEnabled:value.EmailEnabled};
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  let temporary;
  try{
    const input=path.resolve(process.argv[2]||path.join(root,'work/website/account-config.local.json'));
    assert.ok(lstatSync(input).isFile()&&!lstatSync(input).isSymbolicLink(),'Configuration must be a regular file');
    assert.ok(lstatSync(input).size<=16384,'Configuration too large');
    let parsed;try{parsed=JSON.parse(readFileSync(input,'utf8').replace(/^\uFEFF/,''));}catch{throw new Error('Public configuration must contain valid JSON');}
    const value=validateAccountConfig(parsed);
    const output=path.join(root,'work/website/dist/account/config.json');
    for(let current=output;current!==path.resolve(root);current=path.dirname(current))if(existsSync(current))assert.ok(!lstatSync(current).isSymbolicLink(),'Output cannot traverse a symlink');
    assert.notEqual(input,output,'Use a separate local input');
    temporary=output+'.'+randomUUID()+'.tmp';writeFileSync(temporary,JSON.stringify(value,null,2)+'\n',{flag:'wx'});renameSync(temporary,output);temporary=null;
    console.log(JSON.stringify({configured:!!value.SupabaseUrl,target:'work/website/dist/account/config.json',secretFieldsAccepted:false}));
  }catch(error){console.error(error.message);process.exitCode=1;}
  finally{if(temporary&&existsSync(temporary))unlinkSync(temporary);}
}
