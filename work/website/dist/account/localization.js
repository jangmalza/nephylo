import {getLanguage,t,onLanguageChange,registerTranslations,translateDocument} from '../i18n.js';
import {accountTranslations} from './account-translations.js?v=20261015-free-access';

registerTranslations(accountTranslations);
export {t,onLanguageChange,translateDocument};
export const getLocale=()=>getLanguage()==='en'?'en-US':'ko-KR';
export const date=value=>value?new Date(value).toLocaleString(getLocale(),{dateStyle:'medium',timeStyle:'short'}):'—';
const content=new Map();
function paint(id,value){
  const node=document.getElementById(id);
  if(!node)return;
  node.setAttribute('data-i18n-ignore','');
  const output=typeof value==='function'?value():t(value);
  if(node.textContent!==output)node.textContent=output;
}
// Keep message sources in memory so switching languages changes existing status
// and error messages without restarting login, checkout or admin workflows.
export function text(id,value){content.set(id,value);paint(id,value);}
onLanguageChange(()=>{for(const [id,value] of content)paint(id,value);});
