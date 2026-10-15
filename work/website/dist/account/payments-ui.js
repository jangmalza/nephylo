import {createBillingUi} from './billing-ui.js?v=20261012-session1';
import {createInternationalBillingUi} from './international-billing-ui.js?v=20261012-session1';
import {text} from './localization.js?v=20261012-session1';
const $=id=>document.getElementById(id);
// Overseas checkout is retained for a later launch, but has no active UI or API requests.
const internationalBillingAvailable=false;
export function createPaymentsUi(options){
  const url=new URL(location.href),returned=url.searchParams.get('international')==='return';
  if(url.searchParams.has('international'))history.replaceState(null,'',location.pathname+'#billing');
  let domestic,international,busy=false;
  const state=()=>{domestic?.render();international?.render();domestic?.controls(busy);international?.controls(busy);};
  domestic=createBillingUi({...options,otherBlocks:()=>international?.blocksOther()||false,onState:state});
  if(internationalBillingAvailable)international=createInternationalBillingUi({...options,returned,otherBlocks:()=>domestic?.blocksOther()||false,onState:state});
  const select=$('billing-region');select.value=internationalBillingAvailable&&returned?'international':'domestic';
  function render(){
    if(!internationalBillingAvailable)select.value='domestic';
    const overseas=internationalBillingAvailable&&select.value==='international';
    $('billing-region-field').hidden=!internationalBillingAvailable;
    $('billing-region-note').hidden=!internationalBillingAvailable;
    $('billing-domestic').hidden=overseas;
    $('billing-international').hidden=!overseas;
    $('billing-international').inert=!internationalBillingAvailable;
    select.disabled=busy||!internationalBillingAvailable;
    state();
  }
  select.addEventListener('change',render);render();
  return {
    controls(value){busy=value;select.disabled=value||!internationalBillingAvailable;domestic.controls(value);international?.controls(value);},
    render,reset(){domestic.reset();international?.reset();render();},
    async configure(){await Promise.all([domestic.configure(),international?.configure()]);render();},
    async refresh(){const results=await Promise.allSettled([domestic.refresh(),international?.refresh()]);render();if(results[1].status==='rejected')text('international-message','해외 구독 상태를 확인하지 못했습니다.');const failure=results.find(result=>result.status==='rejected');if(failure)throw failure.reason;}
  };
}
