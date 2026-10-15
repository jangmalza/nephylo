import {text,date,onLanguageChange} from './localization.js?v=20261012-session1';
import {billingConfiguration,readBillingReturn,verifiedCheckout,canBeginSubscription} from './billing-model.js';

// Redirect credentials live only in this page's memory and never enter storage or links.
let returned=readBillingReturn(location.href);
if(new URL(location.href).searchParams.has('billing'))history.replaceState(null,'',location.pathname+'#billing');
const $=id=>document.getElementById(id);
const statusLabels={none:'구독 없음',pending:'결제 확인 중',active:'매월 자동 갱신',canceled:'자동 갱신 해지',past_due:'결제 실패 · 갱신 중단',expired:'이용기간 종료',review:'결제 확인 필요',refunded:'환불 완료'};
let sdkPromise=null;
function loadSdk(){
  if(window.TossPayments)return Promise.resolve(window.TossPayments);
  if(sdkPromise)return sdkPromise;
  sdkPromise=new Promise((resolve,reject)=>{
    const script=document.createElement('script');script.src='https://js.tosspayments.com/v2/standard';script.async=true;script.referrerPolicy='no-referrer';
    const timer=setTimeout(()=>{script.remove();sdkPromise=null;reject(new Error('결제창을 불러오지 못했습니다. 인터넷 연결을 확인해 주세요.'));},15000);
    script.onload=()=>{clearTimeout(timer);if(window.TossPayments)resolve(window.TossPayments);else{sdkPromise=null;reject(new Error('결제창을 불러오지 못했습니다.'));}};
    script.onerror=()=>{clearTimeout(timer);script.remove();sdkPromise=null;reject(new Error('결제창 연결을 확인해 주세요.'));};document.head.append(script);
  });return sdkPromise;
}

export function createBillingUi({getApi,run,onMembership,otherBlocks=()=>false,onState=()=>{}}){
  let config={enabled:false},subscription=null,loaded=false,owner=null,awaiting=false;
  function controls(busy){
    const signedIn=!!getApi()?.session,canStart=config.enabled&&signedIn&&loaded&&canBeginSubscription(subscription)&&!awaiting&&returned?.kind!=='success'&&!otherBlocks();
    $('billing-consent').disabled=busy||!canStart;
    $('billing-start').disabled=busy||!canStart||!$('billing-consent').checked;
    $('billing-confirm').disabled=busy||!config.enabled||!signedIn||returned?.kind!=='success'||awaiting;
    $('billing-refresh').disabled=busy||!signedIn||!getApi()?.config;
    $('billing-cancel').disabled=busy||!signedIn||!subscription?.auto_renew;
    $('billing-cancel-confirm').disabled=busy||!signedIn||!subscription?.auto_renew;
    $('billing-cancel-back').disabled=busy;
  }
  function render(){
    const signedIn=!!getApi()?.session;
    text('billing-badge',config.enabled?(config.clientKey.startsWith('test_')?'테스트 모드':'월 구독'):'연결 준비 중');
    text('billing-intro',config.enabled?'Pro 기능을 월 3,000원에 이용합니다. 첫 결제 후 매월 같은 날짜에 자동 결제되며, 언제든 다음 갱신을 해지할 수 있습니다.':'아직 결제 서비스가 연결되지 않았습니다. 현재는 카드 등록이나 실제 청구가 이루어지지 않습니다.');
    $('billing-login-note').hidden=!config.enabled||signedIn;
    $('billing-policies').hidden=!config.enabled;
    if(config.enabled)for(const [id,key] of [['billing-terms','termsUrl'],['billing-privacy','privacyUrl'],['billing-refunds','refundUrl']])$(id).href=config[key];
    $('billing-purchase').hidden=!config.enabled||!signedIn||!loaded||returned?.kind==='success'||!canBeginSubscription(subscription)||awaiting||otherBlocks();
    const otherNote=$('billing-other');if(otherNote)otherNote.hidden=!signedIn||!otherBlocks();
    $('billing-subscription').hidden=!signedIn||!subscription;
    text('billing-status',statusLabels[subscription?.status]||'서버 확인 필요');
    text('billing-until',()=>date(subscription?.current_period_end));
    text('billing-next',subscription?.auto_renew?()=>date(subscription.next_billing_at||subscription.current_period_end):'자동 결제 없음');
    $('billing-cancel').hidden=!subscription?.auto_renew;
    $('billing-refresh').hidden=!signedIn||!getApi()?.config;
    $('billing-return').hidden=!returned;
    text('billing-return-title',returned?.kind==='success'?'카드 등록을 확인했습니다.':'카드 등록을 완료하지 못했습니다.');
    text('billing-return-note',returned?.kind==='success'?'카드를 등록한 계정과 같은 이메일로 로그인한 뒤 아래 버튼으로 첫 결제를 완료하세요. 새로고침하면 다시 카드 등록이 필요할 수 있습니다.':returned?.kind==='failed'?'카드 등록이 취소되거나 실패했습니다. 결제된 내역이 있다면 결과 다시 확인을 눌러 주세요.':'요청 정보를 확인하지 못했습니다. 결제 상태를 확인한 뒤 다시 진행해 주세요.');
    $('billing-confirm').hidden=returned?.kind!=='success';
  }
  async function applyResult(result){
    if(result?.subscription===undefined)throw new Error('구독 상태를 확인하지 못했습니다.');
    subscription=result.subscription;loaded=true;awaiting=['pending','review'].includes(result.status);
    if(result.membership)await onMembership(result.membership);
    render();onState();
  }
  async function refresh(){
    if(!getApi()?.session){subscription=null;loaded=false;render();return;}
    const userId=getApi().session.userId;
    if(owner!==userId){owner=userId;subscription=null;loaded=false;awaiting=false;$('billing-cancel-review').hidden=true;}
    await applyResult(await getApi().billing('status'));
  }
  $('billing-consent').addEventListener('change',()=>controls(false));
  $('billing-start').addEventListener('click',()=>run('billing-message',async()=>{
    if(!config.enabled||!$('billing-consent').checked||!getApi()?.session||!loaded||!canBeginSubscription(subscription)||awaiting||otherBlocks())return;
    const checkout=verifiedCheckout(await getApi().billing('checkout',{consent:true,termsVersion:config.termsVersion}),config,location.origin);
    const TossPayments=await loadSdk();
    try{await TossPayments(checkout.clientKey).payment({customerKey:checkout.customerKey}).requestBillingAuth({method:'CARD',successUrl:checkout.successUrl,failUrl:checkout.failUrl});}
    catch{throw new Error('카드 등록창을 닫았거나 연결하지 못했습니다. 결제 상태를 확인한 뒤 다시 진행해 주세요.');}
  }));
  $('billing-confirm').addEventListener('click',()=>run('billing-message',async()=>{
    if(returned?.kind!=='success'||!config.enabled||!getApi()?.session||awaiting)return;
    awaiting=true;render();text('billing-message','최대 1분 정도 걸릴 수 있습니다. 결제 결과를 확인하고 있습니다.');
    try{
      const result=await getApi().billing('confirm',{checkoutId:returned.checkoutId,state:returned.state,customerKey:returned.customerKey,authKey:returned.authKey});
      await applyResult(result);returned=null;
      text('billing-message',result.status==='active'?(result.membership?.plan==='pro'?'결제가 확인되어 Pro가 활성화되었습니다. 로그인된 위젯으로 돌아오면 권한을 다시 확인합니다.':'이전 결제 내역은 확인되었지만 현재 Pro 이용기간은 종료되었습니다. 최신 구독 상태를 확인해 주세요.'):result.status==='failed'?'결제 승인이 완료되지 않았습니다. 추가 청구 전에 상태를 확인해 주세요.':'결제 결과를 확인 중입니다. 새 결제 대신 결과 다시 확인을 눌러 주세요.');
    }catch(error){
      if(['CHECKOUT_EXPIRED','PAYMENT_FAILED'].includes(error.code)){
        // These failures precede invoice creation. Verify that the server has no
        // unresolved charge before allowing another card registration attempt.
        try{await refresh();if(canBeginSubscription(subscription)){returned=null;awaiting=false;}}catch{awaiting=true;}
      }
      text('billing-message','결제 결과가 아직 확인되지 않았습니다. 새 결제 대신 결과 다시 확인을 눌러 주세요.');throw error;
    }finally{render();}
  }));
  $('billing-refresh').addEventListener('click',()=>run('billing-message',async()=>{
    const result=await getApi().billing('sync');await applyResult(result);
    if(result.status==='active'){returned=null;text('billing-message',result.membership?.plan==='pro'?'Pro 이용 권한과 결제 상태를 확인했습니다. 로그인된 위젯으로 돌아가세요.':'이전 결제 내역은 확인되었지만 현재 Pro 이용기간은 종료되었습니다. 최신 구독 상태를 확인해 주세요.');}
    else text('billing-message',result.status==='pending'?'결제 결과를 확인 중입니다. 추가 결제를 하지 말고 잠시 후 다시 확인해 주세요.':result.status==='review'?'결제 내역 확인이 필요합니다. 추가 결제 없이 문의해 주세요.':'최신 구독 상태를 확인했습니다.');render();
  }));
  $('billing-cancel').addEventListener('click',()=>{$('billing-cancel-review').hidden=false;});
  $('billing-cancel-back').addEventListener('click',()=>{$('billing-cancel-review').hidden=true;});
  $('billing-cancel-confirm').addEventListener('click',()=>run('billing-message',async()=>{
    await applyResult(await getApi().billing('cancel'));$('billing-cancel-review').hidden=true;
    text('billing-message','자동 갱신을 해지했습니다. 이미 결제한 이용기간은 유지됩니다. 진행 중인 결제가 있다면 결과를 다시 확인해 주세요.');
  }));
  render();
  onLanguageChange(render);
  return {
    controls,refresh,render,
    blocksOther:()=>!!getApi()?.session&&(awaiting||returned?.kind==='success'||!canBeginSubscription(subscription)),
    reset(){subscription=null;owner=null;loaded=false;awaiting=false;returned=null;$('billing-consent').checked=false;$('billing-cancel-review').hidden=true;text('billing-message','');render();onState();},
    async configure(){try{config=billingConfiguration(await getApi().billing('config'));}catch{config={enabled:false};text('billing-message','결제 서버 연결을 준비하고 있습니다. 무료 버전은 계속 이용할 수 있습니다.');}render();}
  };
}
