const storeHost=/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.lemonsqueezy\.com$/;
function storeOrigin(value){
  let url;try{url=new URL(value);}catch{throw new Error('해외 결제 주소를 확인하지 못했습니다.');}
  if(url.protocol!=='https:'||url.username||url.password||url.port||url.pathname!=='/'||url.search||url.hash||!storeHost.test(url.hostname))throw new Error('해외 결제 주소를 확인하지 못했습니다.');
  return url.origin;
}
export function internationalConfiguration(value){
  if(value?.enabled!==true)return {enabled:false,provider:'lemonsqueezy',amount:3000,currency:'KRW',interval:'month',testMode:false};
  if(value.provider!=='lemonsqueezy'||value.amount!==3000||value.currency!=='KRW'||value.interval!=='month'||typeof value.testMode!=='boolean'||typeof value.termsVersion!=='string'||!value.termsVersion)throw new Error('해외 결제 설정을 확인하지 못했습니다.');
  const checkoutOrigin=storeOrigin(value.checkoutOrigin);
  for(const key of ['termsUrl','privacyUrl','refundUrl']){let url;try{url=new URL(value[key]);}catch{throw new Error('결제 안내 주소를 확인하지 못했습니다.');}if(url.protocol!=='https:'||url.username||url.password)throw new Error('결제 안내 주소를 확인하지 못했습니다.');}
  return {...value,checkoutOrigin};
}
export function verifiedInternationalUrl(value,config,kind,now=Date.now()){
  if(!config.enabled)throw new Error('해외 결제 연결을 준비하고 있습니다. 아직 청구되지 않습니다.');
  let url;try{url=new URL(value?.[kind==='checkout'?'checkoutUrl':'portalUrl']);}catch{throw new Error('해외 결제 주소를 확인하지 못했습니다.');}
  const validPath=kind==='checkout'?/^\/checkout\/custom\/[A-Za-z0-9_-]+\/?$/.test(url.pathname):/^\/billing(?:\/|$)/.test(url.pathname);
  if(url.origin!==storeOrigin(config.checkoutOrigin)||url.username||url.password||url.hash||!validPath)throw new Error('해외 결제 주소를 확인하지 못했습니다.');
  if(kind==='checkout'&&(!Number.isFinite(Date.parse(value.expiresAt))||Date.parse(value.expiresAt)<=now))throw new Error('해외 결제 요청이 만료되었습니다. 결과를 확인한 뒤 다시 진행해 주세요.');
  return url.href;
}
const statuses=new Set(['on_trial','active','past_due','unpaid','cancelled','canceled','expired','paused']);
export function checkedInternationalStatus(value){
  if(!value||typeof value.pendingCheckout!=='boolean'||value.subscription===undefined)throw new Error('해외 구독 상태를 확인하지 못했습니다.');
  const sub=value.subscription;
  if(sub!==null&&(!sub||sub.provider!=='lemonsqueezy'||!statuses.has(sub.status)||typeof sub.auto_renew!=='boolean'||typeof sub.cancel_at_period_end!=='boolean'))throw new Error('해외 구독 상태를 확인하지 못했습니다.');
  if(sub)for(const key of ['current_period_end','next_billing_at'])if(sub[key]!=null&&!Number.isFinite(Date.parse(sub[key])))throw new Error('해외 구독 상태를 확인하지 못했습니다.');
  return value;
}
export function internationalBlocksCheckout(subscription,pendingCheckout,now=Date.now()){
  if(pendingCheckout)return true;
  if(!subscription)return false;
  return subscription.auto_renew||['active','on_trial','past_due','unpaid','paused'].includes(subscription.status)||Date.parse(subscription.current_period_end)>now;
}
