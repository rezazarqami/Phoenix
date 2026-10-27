(() => {
 'use strict';
 const timeline=document.getElementById('eventTimeline'),status=document.getElementById('historyStatus'),older=document.getElementById('loadOlder'),panel=document.getElementById('journalPanel');
 const params=new URLSearchParams(location.search),focus=params.get('eventId'),initialView=params.get('view')==='results'?'results':'all';
 const titles={NearEntry:'نزدیک ورود',Opened:'ورود تأیید شد',FirstProtection:'پلهٔ اول محافظت',RiskFreeReached:'محدودهٔ ریسک‌فری',Target:'تارگت',StopLoss:'استاپ‌لاس',RiskFree:'خروج ریسک‌فری',Expired:'انقضای سیگنال نزدیک ورود'};
 const messages={NearEntry:'قیمت به محدودهٔ ورود سیگنال نزدیک شد.',Opened:'ورود معامله در بای‌بیت تأیید شد.',FirstProtection:'پلهٔ اول محافظت از معامله فعال شد.',RiskFreeReached:'معامله به محدودهٔ ریسک‌فری رسید.',Target:'سیگنال به تارگت رسید.',StopLoss:'معامله با استاپ‌لاس پایان یافت.',RiskFree:'معامله با ریسک‌فری بسته شد.',Expired:'سیگنالی که قبلاً نزدیک ورود شده بود، منقضی شد.'};
 const exits={FirstStepLimit:'پلهٔ اول ریسک‌فری (استاپ‌لیمیت)',SecondStepLimit:'پلهٔ دوم ریسک‌فری (استاپ‌لیمیت)',SafetyStopMarket:'محافظ ریسک‌فری (استاپ‌مارکت)'};
 const resultKinds=new Set(['Target','RiskFree','StopLoss']),tabs=[...document.querySelectorAll('[data-journal-tab]')];
 const fresh=()=>({entries:new Map(),before:null,hasMore:false,busy:false,initialized:false});
 let states={all:fresh(),results:fresh()},view=initialView;
 const text=(tag,value)=>Object.assign(document.createElement(tag),{textContent:value});
 function render(){
  const state=states[view],fragment=document.createDocumentFragment();let count=0;
  for(const e of [...state.entries.values()].sort((a,b)=>Date.parse(b.at)-Date.parse(a.at)||b.sequence-a.sequence)){
   if(view==='results'&&!resultKinds.has(e.kind))continue;count++;
   const card=document.createElement('article');card.className='event-card'+(e.id===focus?' focused':'');card.dataset.kind=e.kind;card.dataset.eventId=e.id;
   const head=document.createElement('div');head.className='event-top';head.append(Object.assign(text('b',e.symbol||'Phoenix'),{className:'event-symbol'}),Object.assign(text('span',titles[e.kind]||e.kind),{className:'event-kind'}));card.append(head);
   card.append(text('p',messages[e.kind]||'رویداد سیگنال ثبت شد.'));
   if(e.kind==='RiskFree'&&exits[e.exitKind])card.append(text('p',exits[e.exitKind]));
   const levels=document.createElement('div');levels.className='event-levels';
   for(const [label,value] of [['ورود',e.entryPrice],['تارگت',e.takeProfit],['استاپ ثبت‌شده',e.stopLoss]])if(Number(value)>0){const cell=document.createElement('div');cell.append(text('small',label),text('b',Number(value).toLocaleString('en-US',{maximumFractionDigits:10})));levels.append(cell);}
   if(levels.children.length)card.append(levels);
   card.append(Object.assign(text('div',[e.direction,e.timeframe?'تایم‌فریم '+e.timeframe:null].filter(Boolean).join(' · ')),{className:'event-meta'}));
   const id=e.id.split(':')[0];if(/^[a-f0-9-]{36}$/i.test(id)){const chart=document.createElement('details');chart.append(text('summary','نمودار ثبت‌شدهٔ سیگنال'));chart.addEventListener('toggle',()=>{if(chart.open&&!chart.querySelector('img')){const image=document.createElement('img');image.src='/api/analysis/results/'+id+'/image';image.alt='نمودار '+e.symbol;image.onerror=()=>{image.remove();chart.append(text('p','نمودار این سابقه در دسترس نیست.'));};chart.append(image);}});card.append(chart);}
   const date=text('time',new Date(e.at).toLocaleString('fa-IR'));date.dateTime=e.at;card.append(date);
   if(resultKinds.has(e.kind)){const wallet=text('div','');wallet.className='event-wallet';const known=e.walletBalanceUsdt!=null&&Number.isFinite(Number(e.walletBalanceUsdt));wallet.append(text('span','موجودی کیف پول'),Object.assign(text(known?'b':'span',known?Number(e.walletBalanceUsdt).toLocaleString('en-US',{maximumFractionDigits:8})+' USDT':'در این سابقه ثبت نشده'),{className:known?'':'wallet-missing'}));if(e.walletSource==='BybitTransaction'){const source=text('small','پس از تسویه در بای‌بیت'+(e.walletAtUtc?' · '+new Date(e.walletAtUtc).toLocaleTimeString('fa-IR'):''));source.style.flexBasis='100%';wallet.append(source);}card.append(wallet);}
   fragment.append(card);
  }
  timeline.replaceChildren(fragment);if(!count)timeline.append(text('p',state.initialized?(view==='results'?'هنوز نتیجهٔ نهایی ثبت نشده است.':'هنوز رویدادی ثبت نشده است.'):'در حال دریافت…'));
  older.hidden=!state.hasMore;older.disabled=state.busy;panel.setAttribute('aria-busy',String(state.busy));
 }
 async function load(mode='latest'){
  const currentView=view,state=states[currentView];if(state.busy)return;state.busy=true;older.disabled=true;panel.setAttribute('aria-busy','true');
  const first=timeline.querySelector('.event-card'),anchor=first?.getBoundingClientRect().top;
  try{
   const query=new URLSearchParams({view:currentView});if(mode==='older'&&state.before)query.set('before',state.before);else if(!state.initialized&&focus&&currentView===initialView)query.set('eventId',focus);
   const response=await fetch('/api/mobile/history?'+query,{cache:'no-store'});if(!response.ok)throw Error(response.status===401?'برای مشاهدهٔ رویدادها دوباره وارد فونیکس شو.':'دریافت تاریخچه ناموفق بود؛ دوباره تلاش کن.');
   const page=await response.json();if(states[currentView]!==state)return;
   let changed=false;for(const e of page.events){if(JSON.stringify(state.entries.get(e.id))!==JSON.stringify(e)){state.entries.set(e.id,e);changed=true;}}
   const wasInitialized=state.initialized;if(!wasInitialized||mode==='older'){state.before=page.before;state.hasMore=page.hasMore;}state.initialized=true;
   if(view!==currentView)return;
   if(changed||!wasInitialized)render();older.hidden=!state.hasMore;
   status.textContent=!page.focusFound&&focus&&!wasInitialized&&currentView===initialView?'رویداد انتخاب‌شده در این نما پیدا نشد؛ جدیدترین موارد نمایش داده شده‌اند.':state.entries.size.toLocaleString('fa-IR')+(view==='results'?' نتیجهٔ نهایی':' رویداد')+' نمایش داده شده؛ سوابق قدیمی‌تر محفوظ‌اند.';
   if(!wasInitialized){const selected=[...timeline.children].find(x=>x.dataset.eventId===focus);if(selected)selected.scrollIntoView?.({block:'center'});}
   else if(changed&&anchor!=null&&anchor<0){const retained=[...timeline.children].find(x=>x.dataset.eventId===first.dataset.eventId);if(retained)window.scrollBy?.(0,retained.getBoundingClientRect().top-anchor);}
  }catch(error){if(view===currentView&&states[currentView]===state)status.textContent=error.message;}
  finally{state.busy=false;if(view===currentView&&states[currentView]===state){older.disabled=false;panel.setAttribute('aria-busy','false');}}
 }
 function select(next,focusTab=false){
  view=next;for(const tab of tabs){const selected=tab.dataset.journalTab===view;tab.setAttribute('aria-selected',String(selected));tab.tabIndex=selected?0:-1;if(selected&&focusTab)tab.focus();}
  panel.setAttribute('aria-labelledby','journal-tab-'+view);document.getElementById('viewDescription').textContent=view==='results'?'نتایج نهایی: تارگت، خروج ریسک‌فری و استاپ‌لاس':'تمام اعلان‌ها؛ از نزدیک ورود تا پایان معامله';
  const url=new URL(location.href);url.searchParams.set('view',view);history.replaceState(null,'',url);
  status.textContent='در حال دریافت…';render();load();
 }
 tabs.forEach((tab,i)=>{tab.addEventListener('click',()=>select(tab.dataset.journalTab));tab.addEventListener('keydown',event=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?tabs.length-1:(i+1)%tabs.length;select(tabs[next].dataset.journalTab,true);});});
 older.addEventListener('click',()=>load('older'));document.getElementById('refreshEvents').addEventListener('click',()=>load());
 const importButton=document.getElementById('importWallets'),importStatus=document.getElementById('walletImportStatus');
 importButton.addEventListener('click',async()=>{
  const file=document.getElementById('walletImportFile').files[0];if(!file){importStatus.textContent='ابتدا فایل خروجی JSON تلگرام را انتخاب کن.';return;}
  if(file.size>2*1024*1024){importStatus.textContent='فایل باید حداکثر ۲ مگابایت باشد؛ خروجی را به پیام‌های اخیر محدود کن.';return;}
  importButton.disabled=true;importStatus.textContent='در حال تطبیق پیام‌ها با ۵۰ نتیجهٔ اخیر…';
  try{const exported=JSON.parse(await file.text());const response=await fetch('/api/mobile/history/import-wallets',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({export:exported})});const result=await response.json();if(!response.ok)throw Error(result.error||'بازیابی انجام نشد.');
   importStatus.textContent=Number(result.imported).toLocaleString('fa-IR')+' موجودی بازیابی شد؛ '+Number(result.unresolved).toLocaleString('fa-IR')+' نتیجه هنوز پیام قابل تطبیق ندارد.';states={all:fresh(),results:fresh()};select(view);
  }catch(error){importStatus.textContent=error instanceof SyntaxError?'فایل JSON معتبر نیست.':error.message;}finally{importButton.disabled=false;}
 });
 const recoveryButton=document.getElementById('recoverBybitWallets'),recoveryStatus=document.getElementById('bybitRecoveryStatus');let recovering=false,lastRecovery=null;
 async function recovery(manual=false){
  if(recovering)return;recovering=true;if(manual){recoveryButton.disabled=true;recoveryStatus.textContent='در حال بازیابی از سوابق بای‌بیت…';}
  try{const response=await fetch('/api/mobile/history/'+(manual?'recover-wallets':'wallet-recovery'),manual?{method:'POST'}:{cache:'no-store'});if(!response.ok)throw Error();const result=await response.json();
   recoveryStatus.textContent=result.state==='running'?'در حال بازیابی خودکار موجودی‌ها از بای‌بیت…':(result.message||'')+(result.atUtc?' '+Number(result.recovered||0).toLocaleString('fa-IR')+' موجودی بازیابی شد؛ '+Number(result.unresolved||0).toLocaleString('fa-IR')+' مورد بدون تطبیق قطعی.':'');
   recoveryButton.disabled=result.state==='running';if(result.atUtc!==lastRecovery&&result.state!=='running'){lastRecovery=result.atUtc;if(result.recovered>0)load();}
  }catch{recoveryStatus.textContent='وضعیت بازیابی فعلاً در دسترس نیست؛ سابقهٔ نتایج محفوظ است.';recoveryButton.disabled=false;}
  finally{recovering=false;}
 }
 recoveryButton.addEventListener('click',()=>recovery(true));setInterval(()=>{if(!document.hidden)recovery();},30000);
 setInterval(()=>{if(!document.hidden)load();},5000);select(view);recovery();
})();
