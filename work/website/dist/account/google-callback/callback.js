import {text,translateDocument} from '../localization.js?v=20261012-session1';
import {readGoogleCallback,googleChannelName} from '../google-auth.js?v=1.16.1';
const result=readGoogleCallback(location.href);
history.replaceState(null,'',location.pathname);
translateDocument();
let channel=null,timeout=null;
function clean(){clearTimeout(timeout);try{channel?.close();}catch{}channel=null;window.removeEventListener('message',receiveWindow);}
function received(data){if(!result||data?.type!=='usage-widget-google-received'||data.state!==result.state)return;clean();text('callback-message','계정 페이지에서 로그인 결과를 확인해 주세요. 이 창을 닫아도 됩니다.');window.close();}
function receiveWindow(event){if(event.origin===location.origin&&event.source===window.opener)received(event.data);}
document.getElementById('close-callback').addEventListener('click',()=>{clean();window.close();});
window.addEventListener('pagehide',clean);
if(!result){text('callback-message','로그인 요청을 확인하지 못했습니다. 원래 계정 페이지에서 다시 시도해 주세요.');}else{
  window.addEventListener('message',receiveWindow);
  try{if(window.BroadcastChannel){channel=new BroadcastChannel(googleChannelName(result.state));channel.onmessage=event=>received(event.data);channel.postMessage(result);}}catch{}
  try{window.opener?.postMessage(result,location.origin);}catch{}
  timeout=setTimeout(()=>{clean();text('callback-message','원래 계정 페이지와 연결되지 않았습니다. 원래 페이지에서 Google 로그인을 취소한 뒤 다시 시도해 주세요.');},15000);
}
