const membershipStates=new Set(['free','active','canceled','expired','revoked']);
const billingStates=new Set(['none','pending','active','canceled','past_due','review','ended']);
const invoiceStates=new Set(['pending','charging','unknown','paid','failed','canceled','review','refunded','partial_refund']);
const internationalStates=new Set(['none','on_trial','active','past_due','unpaid','cancelled','canceled','expired','paused']);
const validDate=value=>value===null||(typeof value==='string'&&Number.isFinite(Date.parse(value)));
const identity=(source,args={})=>source.replace(/\{(\w+)\}/g,(_,key)=>args[key]??'{'+key+'}');
const date=(value,locale)=>value?new Date(value).toLocaleString(locale,{dateStyle:'medium',timeStyle:'short'}):'—';
function validManual(value){return value&&['free','pro'].includes(value.plan)&&membershipStates.has(value.status)&&validDate(value.expires_at)&&validDate(value.updated_at)&&typeof value.active==='boolean';}
export function checkedAdminMember(value){
  if(!value||typeof value.user_id!=='string'||typeof value.email!=='string'||!['free','pro'].includes(value.plan)||!membershipStates.has(value.status)||!validDate(value.expires_at))throw new Error('회원 정보를 확인하지 못했습니다. 다시 조회해 주세요.');
  if(!validManual(value.manual)||!value.billing||!billingStates.has(value.billing.status)||!validDate(value.billing.expires_at)||typeof value.billing.active!=='boolean'||typeof value.billing.auto_renew!=='boolean'||!(value.billing.invoice_status===null||invoiceStates.has(value.billing.invoice_status)))throw new Error('회원 관리 서버 업데이트가 필요합니다. 수동 권한과 결제 권한을 구분할 수 없어 편집을 중단했습니다.');
  const international=value.international;
  if(international!==undefined&&(!international||international.provider!=='lemonsqueezy'||!internationalStates.has(international.status)||typeof international.active!=='boolean'||(international.status!=='none'&&(typeof international.auto_renew!=='boolean'||!validDate(international.expires_at)))))throw new Error('해외 구독 응답을 확인하지 못했습니다. 회원 관리 서버 설정을 확인해 주세요.');
  return value;
}
export function manualEditorValues(value){
  const manual=checkedAdminMember(value).manual;
  return {plan:manual.plan,expiry:manual.expires_at||''};
}
export function checkedManualExpiry(value,now=Date.now()){
  const expiry=typeof value==='string'?Date.parse(value):NaN;
  if(!Number.isFinite(now)||!Number.isFinite(expiry)||expiry<=now||expiry>now+366*86400000)throw new Error('Pro 종료 시각은 현재 이후부터 366일 이내로 입력해 주세요.');
  return value;
}
export function presetManualExpiry(days,now=Date.now()){
  if(![7,30,90,365].includes(days))throw new Error('지원하지 않는 이용기간입니다.');
  const expiry=new Date(now+days*86400000);
  if(!Number.isFinite(now)||!Number.isFinite(expiry.getTime()))throw new Error('Pro 종료 시각은 현재 이후부터 366일 이내로 입력해 주세요.');
  return checkedManualExpiry(expiry.toISOString(),now);
}
export function customManualExpiry(value,now=Date.now()){
  const match=typeof value==='string'&&/^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if(!match)throw new Error('유효한 종료 날짜를 선택해 주세요.');
  const year=Number(match[1]),month=Number(match[2]),day=Number(match[3]);
  const expiry=new Date(year,month-1,day,23,59,0,0);
  if(expiry.getFullYear()!==year||expiry.getMonth()!==month-1||expiry.getDate()!==day||expiry.getHours()!==23||expiry.getMinutes()!==59)throw new Error('유효한 종료 날짜를 선택해 주세요.');
  return checkedManualExpiry(expiry.toISOString(),now);
}
export function manualDescription(value,t=identity,locale='ko-KR'){
  if(value.plan!=='pro')return t(value.status==='revoked'?'수동 권한 해제':'수동 부여 없음');
  return t(value.active?'Pro · {date}까지':'만료된 수동 Pro · {date}까지',{date:date(value.expires_at,locale)});
}
export function billingDescription(value,t=identity,locale='ko-KR'){
  if(value.status==='none')return t('결제 구독 없음');
  if(value.active)return t('Pro · {date}까지',{date:date(value.expires_at,locale)})+'\n'+t(value.auto_renew?'자동 갱신 켜짐':'자동 갱신 꺼짐')+(value.invoice_status==='partial_refund'?'\n'+t('부분 환불 · 확인 필요'):value.status==='review'?'\n'+t('운영 확인 필요'):'');
  const labels={refunded:'전액 환불',partial_refund:'부분 환불 · 확인 필요',failed:'결제 실패',review:'운영 확인 필요',unknown:'결제 결과 확인 중',charging:'결제 처리 중',pending:'결제 대기',canceled:'결제 중단'};
  return t(labels[value.invoice_status]||({past_due:'결제 실패',review:'운영 확인 필요',ended:'구독 종료',canceled:'자동 갱신 꺼짐',pending:'결제 준비 중'}[value.status])||'결제 이용기간 종료');
}
export function internationalDescription(value,t=identity,locale='ko-KR'){
  if(!value)return t('해외 구독 조회 · 서버 업데이트 필요');
  if(value.status==='none')return t('결제 구독 없음');
  if(value.active)return t('Pro · {date}까지',{date:date(value.expires_at,locale)})+'\n'+t(value.auto_renew?'자동 갱신 켜짐':'자동 갱신 꺼짐');
  const labels={on_trial:'체험 이용 중',active:'이용기간 확인 필요',past_due:'결제 실패 · 확인 필요',unpaid:'미결제 · 확인 필요',cancelled:'자동 갱신 해지',canceled:'자동 갱신 해지',expired:'이용기간 종료',paused:'구독 일시 중지'};
  return t(labels[value.status]||'서버 확인 필요');
}
export function auditDescription(entry,t=identity,locale='ko-KR'){
  if(!entry||typeof entry.legacy!=='boolean'||typeof entry.reason!=='string'||typeof entry.created_at!=='string'||!validDate(entry.created_at))throw new Error('권한 변경 이력을 확인하지 못했습니다.');
  if(entry.legacy)return t('{date} · 이전 형식 기록\n당시 적용 권한만 기록되어 수동 변경 내역은 구분할 수 없습니다.\n사유: {reason}',{date:date(entry.created_at,locale),reason:entry.reason});
  if(!validManual(entry.before_manual)||!validManual(entry.after_manual))throw new Error('수동 권한 변경 이력을 확인하지 못했습니다.');
  return t('{date}\n{before} → {after}\n사유: {reason}',{date:date(entry.created_at,locale),before:manualDescription(entry.before_manual,t,locale),after:manualDescription(entry.after_manual,t,locale),reason:entry.reason});
}
