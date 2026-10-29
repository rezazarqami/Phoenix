(() => {
 'use strict';
 function mount({list,controls,all,cancel,status,refresh}) {
 list.replaceChildren();
 let signals=[],admin=false,busy=false;const selected=new Set(),rows=new Map();
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
 function render(next){signals=next;const ids=new Set(next.map(s=>s.id));for(const [id,row] of rows)if(!ids.has(id)){row.stop();row.article.remove();rows.delete(id);}
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
 fetch('/api/auth/me',{cache:'no-store'}).then(r=>r.json()).then(session=>{admin=!!session.isAdmin;controls.hidden=!admin;selection();}).catch(()=>{admin=false;selection();});
 return {render,stop(){for(const row of rows.values())row.stop();}};
 }
 window.PhoenixActiveSignalsUI={mount};
 const list=document.getElementById('orders');if(!list)return;
 const component=mount({list,controls:document.getElementById('activeSelectionControls'),all:document.getElementById('selectAllSignals'),cancel:document.getElementById('cancelSelectedSignals'),status:document.getElementById('activeActionStatus'),refresh:async()=>{await refreshSignals();await refreshHistory();}});
 window.PhoenixActiveSignals=component;window.addEventListener('phoenix-signals-updated',event=>component.render(event.detail));if(window.phoenixActiveSignals)component.render(window.phoenixActiveSignals);
})();
