import {text,date,onLanguageChange,getLocale} from './localization.js?v=20261012-session1';
import {internationalConfiguration,verifiedInternationalUrl,checkedInternationalStatus,internationalBlocksCheckout} from './international-billing-model.js';
const $=id=>document.getElementById(id);
const statusLabels={on_trial:'체험 이용 중',active:'매월 자동 갱신',past_due:'결제 실패 · 확인 필요',unpaid:'미결제 · 확인 필요',cancelled:'자동 갱신 해지',canceled:'자동 갱신 해지',expired:'이용기간 종료',paused:'구독 일시 중지'};

export function createInternationalBillingUi({getApi,run,onMembership,otherBlocks=()=>false,onState=()=>{},returned=false,navigate=url=>location.assign(url)}){
  let config={enabled:false},subscription=null,loaded=false,pending=false,owner=null,epoch=0,busy=false,uncertain=false;
  const session=()=>getApi()?.session;
  const canStart=()=>config.enabled&&!!session()&&loaded&&!pending&&!uncertain&&!internationalBlocksCheckout(subscription,false)&&!otherBlocks();
  // Owner + API generation + local epoch prevent late answers from a prior
  // account or a sign-out from changing access or opening a hosted checkout.
  const capture=()=>({api:getApi(),userId:session()?.userId,generation:getApi()?.generation,epoch});
  const current=token=>token.api===getApi()&&token.userId===session()?.userId&&token.generation===getApi()?.generation&&token.epoch===epoch&&!!token.userId;
  function controls(value){
    busy=value;
    $('international-consent').disabled=busy||!canStart();
    $('international-start').disabled=busy||!canStart()||!$('international-consent').checked;
    $('international-refresh').disabled=busy||!session()||!getApi()?.config;
    $('international-portal').disabled=busy||!config.enabled||!session()||!loaded||!subscription;
  }
  function render(){
    text('international-badge',!config.enabled?'연결 준비 중':config.testMode?'테스트 모드':'해외 월 구독');
    text('international-intro',config.enabled?'Lemon Squeezy의 보안 결제창에서 해외 결제를 진행합니다. 결제가 서버에서 확인되면 같은 계정에 Pro가 적용됩니다.':'해외 결제 연결을 준비하고 있습니다. 아직 결제창을 열거나 실제 청구를 하지 않습니다.');
    $('international-login-note').hidden=!config.enabled||!!session();
    $('international-policies').hidden=!config.enabled;
    if(config.enabled)for(const [id,key]of [['international-terms','termsUrl'],['international-privacy','privacyUrl'],['international-refunds','refundUrl']])$(id).href=config[key];
    $('international-purchase').hidden=!canStart();
    $('international-unavailable').hidden=config.enabled;
    $('international-subscription').hidden=!session()||!subscription;
    text('international-status',subscription?.cancel_at_period_end?'자동 갱신 해지':statusLabels[subscription?.status]||'서버 확인 필요');
    text('international-until',()=>date(subscription?.current_period_end));
    text('international-next',subscription?.auto_renew?()=>date(subscription.next_billing_at||subscription.current_period_end):'자동 결제 없음');
    $('international-refresh').hidden=!session()||!getApi()?.config;
    $('international-portal').hidden=!session()||!subscription;
    $('international-other').hidden=!session()||!otherBlocks();
    $('international-pending').hidden=!session()||(!pending&&!uncertain);
    $('international-return').hidden=!returned;
    controls(busy);
  }
  async function applyResult(result,token){
    if(!current(token))return false;
    const checked=checkedInternationalStatus(result);
    if(checked.membership)await onMembership(checked.membership);
    if(!current(token))return false;
    subscription=checked.subscription;pending=checked.pendingCheckout;loaded=true;uncertain=false;
    if(!pending&&checked.membership?.plan==='pro')returned=false;
    render();onState();return true;
  }
  async function refresh(){
    if(!session()){subscription=null;loaded=false;pending=false;owner=null;render();return;}
    if(owner!==session().userId){epoch++;owner=session().userId;subscription=null;loaded=false;pending=false;uncertain=false;$('international-consent').checked=false;}
    const token=capture();await applyResult(await getApi().internationalBilling('status'),token);
  }
  $('international-consent').addEventListener('change',()=>controls(busy));
  $('international-start').addEventListener('click',()=>run('international-message',async()=>{
    if(!canStart()||!$('international-consent').checked)return;
    const token=capture();uncertain=true;render();onState();
    try{
      const result=await getApi().internationalBilling('checkout',{consent:true,termsVersion:config.termsVersion,locale:getLocale().startsWith('en')?'en':'ko'});
      if(!current(token))return;
      const url=verifiedInternationalUrl(result,config,'checkout');
      navigate(url);
    }catch(error){if(current(token)){text('international-message','결제창을 열지 못했거나 결과가 아직 확인되지 않았습니다. 새 결제 전에 결과 다시 확인을 눌러 주세요.');render();onState();}throw error;}
  }));
  $('international-refresh').addEventListener('click',()=>run('international-message',async()=>{
    if(!session())return;
    const token=capture(),result=await getApi().internationalBilling('sync');
    if(!await applyResult(result,token))return;
    text('international-message',result.pendingCheckout?'결제 결과를 확인 중입니다. 추가 결제를 하지 말고 잠시 후 다시 확인해 주세요.':result.membership?.plan==='pro'?'Pro 이용 권한과 결제 상태를 확인했습니다. 로그인된 위젯으로 돌아가세요.':'최신 구독 상태를 확인했습니다.');
  }));
  $('international-portal').addEventListener('click',()=>run('international-message',async()=>{
    if(!config.enabled||!session()||!loaded||!subscription)return;
    const token=capture(),result=await getApi().internationalBilling('portal');
    if(current(token))navigate(verifiedInternationalUrl(result,config,'portal'));
  }));
  render();onLanguageChange(render);
  return {controls,render,refresh,
    blocksOther:()=>!!session()&&(pending||uncertain||internationalBlocksCheckout(subscription,false)),
    reset(){epoch++;owner=null;subscription=null;pending=false;uncertain=false;loaded=false;returned=false;$('international-consent').checked=false;text('international-message','');render();onState();},
    async configure(){try{config=internationalConfiguration(await getApi().internationalBilling('config'));}catch{config={enabled:false};text('international-message','해외 결제 서버 연결을 준비하고 있습니다. 무료 버전은 계속 이용할 수 있습니다.');}render();}
  };
}
