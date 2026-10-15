export const GOOGLE_CALLBACK_PATH='/account/google-callback/';
export const GOOGLE_TIMEOUT=300000;
export const GOOGLE_MESSAGE='usage-widget-google-code';
const statePattern=/^[A-Za-z0-9_-]{43}$/;
const codePattern=/^[A-Za-z0-9._~-]{1,2048}$/;
const message='Google 로그인을 완료하지 못했습니다. 다시 시도해 주세요.';
const base64url=bytes=>btoa(String.fromCharCode(...bytes)).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
export function googleChannelName(state){if(!statePattern.test(state))throw new Error(message);return 'usage-widget-google-'+state;}
export function readGoogleCallback(href) {
  const url=new URL(href), params=url.searchParams, hash=new URLSearchParams(url.hash.slice(1));
  if(url.pathname!==GOOGLE_CALLBACK_PATH||params.getAll('uw_state').length!==1||!statePattern.test(params.get('uw_state')))return null;
  const state=params.get('uw_state');
  if(params.has('error')||hash.has('error'))return {type:GOOGLE_MESSAGE,state,error:true};
  if(params.getAll('code').length!==1||!codePattern.test(params.get('code')))return null;
  return {type:GOOGLE_MESSAGE,state,code:params.get('code')};
}
// Each attempt stays in its initiating page. No credentials are written to browser storage.
export function createGoogleLogin({getApi,browser=window,cryptoProvider=crypto,now=Date.now}) {
  let pending=null;
  const alternative=(plain,withEmail)=>getApi()?.config?.emailEnabled!==false?withEmail:plain;
  function start() {
    if(pending)return Promise.reject(new Error('Google 로그인이 진행 중입니다. 열린 창을 확인해 주세요.'));
    const api=getApi();
    if(!api?.config?.googleEnabled)return Promise.reject(new Error(alternative('Google 로그인 연결 준비 중입니다. 연결 후 이 버튼으로 간편하게 로그인할 수 있습니다.','Google 로그인은 연결 준비 중입니다. 이메일 인증을 이용해 주세요.')));
    if(!cryptoProvider?.getRandomValues||!cryptoProvider?.subtle)return Promise.reject(new Error(alternative('이 브라우저에서는 Google 로그인을 지원하지 않습니다. 다른 브라우저에서 다시 시도해 주세요.','이 브라우저에서는 Google 로그인을 지원하지 않습니다. 이메일 인증을 이용해 주세요.')));
    let popup;
    try { popup=browser.open('about:blank','_blank','popup,width=480,height=680'); } catch {}
    if(!popup)return Promise.reject(new Error(alternative('로그인 창이 차단되었습니다. 이 사이트의 팝업을 허용한 뒤 다시 시도해 주세요.','로그인 창이 차단되었습니다. 이 사이트의 팝업을 허용하거나 이메일 인증을 이용해 주세요.')));
    const generation=api.beginGoogle(), attempt={popup,generation,state:'',verifier:'',channel:null,phase:'opening',deadline:now()+GOOGLE_TIMEOUT,timer:null,finish:null};
    pending=attempt;
    return new Promise((resolve,reject)=>{
      function finish(error,value) {
        if(pending!==attempt)return;
        pending=null;
        browser.removeEventListener('message',receiveWindow);
        browser.removeEventListener('pagehide',unload);
        browser.clearInterval(attempt.timer);
        try { attempt.channel?.close(); } catch {}
        try { popup.close(); } catch {}
        attempt.state='';attempt.verifier='';attempt.channel=null;
        if(error){api.cancelGoogle(generation);reject(error);}else{resolve(value);}
      }
      attempt.finish=finish;
      const unload=()=>finish(new Error('Google 로그인 요청이 취소되었습니다.'));
      function receiveWindow(event){if(event.origin===browser.location.origin&&event.source===popup)receive(event.data);}
      function receive(data) {
        if(pending!==attempt||attempt.phase!=='waiting'||!data||data.type!==GOOGLE_MESSAGE||data.state!==attempt.state)return;
        if(now()>=attempt.deadline||api.generation!==generation){finish(new Error('Google 로그인 요청이 만료되었거나 취소되었습니다. 다시 시도해 주세요.'));return;}
        const ack={type:'usage-widget-google-received',state:attempt.state};
        try{popup.postMessage(ack,browser.location.origin);}catch{}
        try{attempt.channel?.postMessage(ack);}catch{}
        if(data.error||typeof data.code!=='string'||!codePattern.test(data.code)){finish(new Error(message));return;}
        attempt.phase='exchanging';
        api.exchangeGoogle(data.code,attempt.verifier,generation).then(value=>finish(null,value),error=>finish(error));
      }
      browser.addEventListener('message',receiveWindow);
      browser.addEventListener('pagehide',unload);
      attempt.timer=browser.setInterval(()=>{
        if(now()>=attempt.deadline){finish(new Error(alternative('Google 로그인 시간이 만료되었습니다. 다시 시도해 주세요.','Google 로그인 시간이 만료되었습니다. 다시 시도하거나 이메일 인증을 이용해 주세요.')));return;}
        if(api.generation!==generation){unload();return;}
        // COOP can sever the popup reference. Its same-origin channel still receives the return.
        if(attempt.phase!=='exchanging'&&!attempt.channel){try{if(popup.closed)finish(new Error(alternative('Google 로그인 창이 닫혔습니다. 다시 시도해 주세요.','Google 로그인 창이 닫혔습니다. 다시 시도하거나 이메일 인증을 이용해 주세요.')));}catch{}}
      },500);
      (async()=>{
        try {
          attempt.state=base64url(cryptoProvider.getRandomValues(new Uint8Array(32)));
          attempt.verifier=base64url(cryptoProvider.getRandomValues(new Uint8Array(64)));
          const digest=await cryptoProvider.subtle.digest('SHA-256',new TextEncoder().encode(attempt.verifier));
          if(pending!==attempt)return;
          const callback=new URL(GOOGLE_CALLBACK_PATH,browser.location.origin);
          callback.searchParams.set('uw_state',attempt.state);
          const url=new URL('/auth/v1/authorize',api.config.origin);
          url.search=new URLSearchParams({provider:'google',redirect_to:callback.href,code_challenge:base64url(new Uint8Array(digest)),code_challenge_method:'s256',prompt:'select_account'}).toString();
          if(browser.BroadcastChannel){try{attempt.channel=new browser.BroadcastChannel(googleChannelName(attempt.state));attempt.channel.onmessage=event=>receive(event.data);}catch{}}
          attempt.phase='waiting';
          popup.location.replace(url.href);
        } catch { finish(new Error(message)); }
      })();
    });
  }
  return {start,cancel(){pending?.finish(new Error(alternative('Google 로그인 요청을 취소했습니다. 준비되면 다시 시도해 주세요.','Google 로그인 요청을 취소했습니다. 이메일 인증도 이용할 수 있습니다.')));},get active(){return pending!==null;}};
}
