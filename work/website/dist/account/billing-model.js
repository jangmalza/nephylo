const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function billingConfiguration(value){
  if(!value||value.enabled!==true)return {enabled:false,amount:3000,currency:'KRW',interval:'month'};
  if(value.amount!==3000||value.currency!=='KRW'||value.interval!=='month'||! /^(test|live)_ck_[A-Za-z0-9_-]+$/.test(value.clientKey||'')||typeof value.termsVersion!=='string'||!value.termsVersion)throw new Error('결제 설정을 확인하지 못했습니다.');
  for(const key of ['termsUrl','privacyUrl','refundUrl']){let url;try{url=new URL(value[key]);}catch{throw new Error('결제 안내 주소를 확인하지 못했습니다.');}if(url.protocol!=='https:'||url.username||url.password)throw new Error('결제 안내 주소를 확인하지 못했습니다.');}
  return value;
}
export function readBillingReturn(url){
  const query=new URL(url).searchParams,kind=query.get('billing');
  if(!kind)return null;
  if(kind==='fail')return {kind:'failed'};
  const checkoutId=query.get('checkoutId')||'',state=query.get('state')||'',customerKey=query.get('customerKey')||'',authKey=query.get('authKey')||'';
  if(kind!=='success'||!uuid.test(checkoutId)||!/^[A-Za-z0-9_-]{24,128}$/.test(state)||!/^[A-Za-z0-9_=.@-]{2,50}$/.test(customerKey)||!authKey||authKey.length>300||/[\x00-\x20\x7f]/.test(authKey))return {kind:'invalid'};
  return {kind:'success',checkoutId,state,customerKey,authKey};
}
export function verifiedCheckout(value,config,origin){
  if(!value||!uuid.test(value.checkoutId||'')||!/^[A-Za-z0-9_-]{24,128}$/.test(value.state||'')||value.amount!==3000||value.currency!=='KRW'||value.clientKey!==config.clientKey||!/^[A-Za-z0-9_=.@-]{2,50}$/.test(value.customerKey||''))throw new Error('카드 등록 요청을 확인하지 못했습니다.');
  for(const key of ['successUrl','failUrl']){const url=new URL(value[key]);if(url.origin!==origin||url.username||url.password||url.pathname!=='/account/'||url.searchParams.get('billing')!==(key==='successUrl'?'success':'fail')||url.searchParams.get('checkoutId')!==value.checkoutId||url.searchParams.get('state')!==value.state)throw new Error('결제 완료 주소를 확인하지 못했습니다.');}
  return value;
}
export function canBeginSubscription(subscription,now=Date.now()){
  if(!subscription)return true;
  if(subscription.auto_renew||['pending','review'].includes(subscription.status))return false;
  const end=Date.parse(subscription.current_period_end);
  return !Number.isFinite(end)||end<=now||!['active','canceled'].includes(subscription.status);
}
