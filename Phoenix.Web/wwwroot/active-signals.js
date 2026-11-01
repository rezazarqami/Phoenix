(() => {
 'use strict';
 function mount({list,controls,all,cancel,status,refresh}) {
 list.replaceChildren();
 let signals=[],allSignals=[],admin=false,busy=false,timeframe='all',rules=[],rulesLoading=false;const selected=new Set(),rows=new Map();
 const make=(tag,value,cls)=>Object.assign(document.createElement(tag),{textContent:value,className:cls||''});
 const eligible=s=>['Pending','Submitted'].includes(s.status)&&!s.filledAtUtc&&!s.completedAtUtc;
 const names={Pending:'در انتظار ورود',Submitted:'سفارش منتظر اجرا',Submitting:'در حال ارسال',Filled:'معاملهٔ فعال',Closing:'در حال بستن',RecoveryRequired:'نیازمند تطبیق',Error:'خطا'};
 function selection(){const available=signals.filter(eligible);for(const id of selected)if(!available.some(s=>s.id===id))selected.delete(id);
  all.disabled=busy||!available.length;all.setAttribute('aria-pressed',String(available.length>0&&available.every(s=>selected.has(s.id))));cancel.disabled=busy||!selected.size;cancel.textContent='لغو انتخاب‌شده‌ها'+(selected.size?' ('+selected.size.toLocaleString('fa-IR')+')':'');
  for(const [id,row] of rows){const s=signals.find(s=>s.id===id);if(!s)continue;row.check.checked=selected.has(id);row.check.disabled=!admin||busy||!eligible(s);row.cancel.disabled=!admin||busy||!eligible(s);}
 }
 function create(s){
  const article=make('article','','active-signal-row'),main=make('div','','active-signal-main'),check=make('input','');check.type='checkbox';check.className='active-signal-check';check.setAttribute('aria-label','انتخاب '+s.symbol);check.addEventListener('change',()=>{check.checked?selected.add(s.id):selected.delete(s.id);selection();});
  const base=s.symbol.replace(/USDT$/,'');const icon=make('span',({BTC:'₿',ETH:'◆',SOL:'≋',BNB:'◇',XRP:'✕',DOGE:'Ð',TON:'◈'})[base]||base.slice(0,2),'active-coin');icon.dataset.coin=base;
  const symbol=make('button',s.symbol,'active-symbol');symbol.title=s.symbol;symbol.type='button';const direction=make('span','','active-direction'),phase=make('span','','active-phase'),remove=make('button','لغو سیگنال','active-cancel'),toggle=make('button','⌄','active-expand');remove.type=toggle.type='button';
  const body=make('div','','active-signal-detail');body.hidden=true;const metrics=make('div','','active-signal-metrics'),chartHost=make('div','','signal-live-host');let chart;
  const renderMetrics=signal=>{metrics.replaceChildren();for(const [label,value] of [['ورود',signal.entryPrice],['تارگت',signal.takeProfit],['استاپ',signal.stopLoss],['انقضا',signal.expirePrice],['لوریج',signal.leverage],['ورودی USDT',signal.positionSizeUsdt]]){const cell=make('div','');cell.append(make('small',label),make('b',value==null?'—':Number(value).toLocaleString('en-US',{maximumFractionDigits:8})));metrics.append(cell);}};
  function expand(){body.hidden=!body.hidden;toggle.setAttribute('aria-expanded',String(!body.hidden));symbol.setAttribute('aria-expanded',String(!body.hidden));toggle.textContent=body.hidden?'⌄':'⌃';if(body.hidden){chart?.stop();chart=null;}else{renderMetrics(signals.find(x=>x.id===s.id)||s);chart=window.PhoenixLiveChart?.mount(chartHost,s.id);}}
  symbol.addEventListener('click',expand);toggle.addEventListener('click',expand);toggle.setAttribute('aria-label','نمودار و جزئیات '+s.symbol);toggle.setAttribute('aria-expanded','false');
  remove.addEventListener('click',()=>cancelIds([s.id]));body.append(chartHost,metrics);main.append(check,icon,symbol,direction,phase,remove,toggle);article.append(main,body);return {article,check,direction,phase,cancel:remove,stop:()=>chart?.stop()};
 }
 const riskPanel=make('div','','timeframe-risk-panel'),tabs=make('div','','timeframe-risk-tabs'),settings=make('div','','timeframe-risk-settings');riskPanel.append(tabs,settings);list.before(riskPanel);
 if(!document.getElementById('timeframe-risk-style')){const css=document.createElement('link');css.id='timeframe-risk-style';css.rel='stylesheet';css.href='/timeframe-risk.css?v=20261030';document.head.append(css);}
 const label=key=>({'all':'همه','1':'۱ دقیقه','5':'۵ دقیقه','15':'۱۵ دقیقه','30':'۳۰ دقیقه','60':'۱ ساعت','240':'۴ ساعت','D':'روزانه','W':'هفتگی','M':'ماهانه','3M':'سه‌ماهه','unknown':'نامشخص'})[key]||key;
 function paintRisk(){tabs.replaceChildren();const keys=['all','1','5','15','30','60','240','D','W','M','3M',...allSignals.map(s=>s.timeframe||'unknown'),...rules.map(x=>x.rule.timeframe)];for(const key of [...new Set(keys)]){const count=allSignals.filter(s=>key==='all'||(s.timeframe||'unknown')===key).length;const b=make('button',label(key)+' · '+count);b.type='button';b.setAttribute('aria-pressed',String(timeframe===key));b.dataset.timeframe=key;b.onclick=()=>{timeframe=key;paintRisk();filter();};tabs.append(b);}if(settings.contains(document.activeElement)&&document.activeElement.tagName==='INPUT')return;settings.replaceChildren();if(timeframe==='all')return;const current=rules.find(x=>x.rule.timeframe===timeframe);if(!current){settings.append(make('span','در حال دریافت قواعد تایم‌فریم…'));return;}const rule=current.rule;settings.append(make('span','معاملات باز: '+current.open+' · '+(rule.blocked?'قفل پس از دو استاپ متوالی':rule.consecutiveStops>0?(current.awaitingResult?'انتظار نتیجهٔ معاملهٔ باز پس از یک استاپ':'فقط یک معامله پس از یک استاپ'):'فعال')+' · ظرفیت فعلی: '+(current.effectiveMaximumOpen??(rule.blocked?0:rule.consecutiveStops>0?1:rule.maximumOpen))));const field=make('label','سقف معاملات هم‌زمان '),input=make('input','');input.type='number';input.min='1';input.max='100';input.value=rule.maximumOpen;input.setAttribute('aria-label','حداکثر معاملات هم‌زمان '+label(timeframe));input.disabled=!admin;field.append(input);settings.append(field);const save=make('button','ذخیره سقف');save.type='button';save.disabled=!admin;save.onclick=()=>changeRule('/api/signals/timeframe-rules/'+timeframe,{maximumOpen:Number(input.value)});settings.append(save);settings.append(make('small','پس از یک استاپ، فقط یک معامله تا تعیین نتیجه مجاز است؛ تارگت ظرفیت عادی را برمی‌گرداند و استاپ دوم تایم‌فریم را قفل می‌کند.'));if(rule.blocked){const resume=make('button','آزاد کردن');resume.type='button';resume.disabled=!admin;resume.onclick=async()=>{if(await phoenixConfirm('ورودهای جدید این تایم‌فریم دوباره آزاد شوند؟ سیگنال‌هایی که هنگام توقف به ورود رسیده‌اند برنمی‌گردند.'))changeRule('/api/signals/timeframe-rules/'+timeframe+'/resume',{});};settings.append(resume);}}
 async function changeRule(url,body){settings.querySelectorAll('button,input').forEach(e=>e.disabled=true);try{const r=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});if(!r.ok)throw Error((await r.json()).error||'ذخیره انجام نشد.');await loadRules();await refresh();}catch(e){status.textContent=e.message;paintRisk();}}
 async function loadRules(){if(rulesLoading)return;rulesLoading=true;try{const r=await fetch('/api/signals/timeframe-rules',{cache:'no-store'});if(r.ok){rules=await r.json();if(!Array.isArray(rules))rules=[];paintRisk();}}catch{}finally{rulesLoading=false;}}
 function filter(){renderRows(allSignals.filter(s=>timeframe==='all'||(s.timeframe||'unknown')===timeframe));}
 function render(next){allSignals=next;paintRisk();filter();loadRules();}
 function renderRows(next){signals=next;const ids=new Set(next.map(s=>s.id));for(const [id,row] of rows)if(!ids.has(id)){row.stop();row.article.remove();rows.delete(id);}
  if(!next.length){list.replaceChildren(make('p','سیگنال فعالی وجود ندارد.','active-empty'));selection();return;}list.querySelector('.active-empty')?.remove();
  for(const s of next){let row=rows.get(s.id);if(!row){row=create(s);rows.set(s.id,row);}row.direction.textContent=s.direction==='Long'?'Long ↗':'Short ↘';row.direction.dataset.side=s.direction;row.phase.textContent=names[s.status]||s.status;row.phase.dataset.filled=String(Boolean(s.filledAtUtc)||s.status==='Filled');row.cancel.textContent=s.filledAtUtc||['Filled','Closing'].includes(s.status)?'پوزیشن باز':'لغو سیگنال';row.article.dataset.id=s.id;list.append(row.article);}
  selection();
 }
 async function cancelIds(ids){if(busy||!admin||!ids.length)return;busy=true;selection();
  try{if(!await phoenixConfirm(ids.length.toLocaleString('fa-IR')+' سیگنال انتخاب‌شدهٔ منتظر ورود لغو شود؟'))return;
   status.textContent='در حال لغو انتخاب‌شده‌ها…';const response=await fetch('/api/signals/cancel-selected',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids})});const data=await response.json();if(!response.ok)throw Error(data.error||(response.status===401?'نشست منقضی شده است.':'لغو انجام نشد.'));
   for(const item of data.items)if(item.cancelled)selected.delete(item.id);status.textContent=Number(data.cancelled).toLocaleString('fa-IR')+' سیگنال لغو شد.'+(data.items.some(x=>!x.cancelled)?' '+data.items.filter(x=>!x.cancelled).map(x=>x.message).join(' · '):'');
   await refresh();
  }catch(error){status.textContent=error.message;}finally{busy=false;selection();}
 }
 all.addEventListener('click',()=>{const available=signals.filter(eligible);const clear=available.every(s=>selected.has(s.id));for(const s of available)clear?selected.delete(s.id):selected.add(s.id);selection();});cancel.addEventListener('click',()=>cancelIds([...selected]));
 fetch('/api/auth/me',{cache:'no-store'}).then(r=>r.json()).then(session=>{admin=!!session.isAdmin;controls.hidden=!admin;selection();paintRisk();}).catch(()=>{admin=false;selection();});
 return {render,stop(){for(const row of rows.values())row.stop();}};
 }
 window.PhoenixActiveSignalsUI={mount};
 const list=document.getElementById('orders');if(!list)return;
 const component=mount({list,controls:document.getElementById('activeSelectionControls'),all:document.getElementById('selectAllSignals'),cancel:document.getElementById('cancelSelectedSignals'),status:document.getElementById('activeActionStatus'),refresh:async()=>{await refreshSignals();await refreshHistory();}});
 window.PhoenixActiveSignals=component;window.addEventListener('phoenix-signals-updated',event=>component.render(document.body.classList.contains('organized-shell')?event.detail.filter(s=>!s.filledAtUtc):event.detail));if(window.phoenixActiveSignals)component.render(window.phoenixActiveSignals);
})();
