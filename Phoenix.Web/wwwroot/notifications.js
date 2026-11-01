(() => {
 'use strict';
 const timeline=document.getElementById('eventTimeline'),status=document.getElementById('historyStatus'),older=document.getElementById('loadOlder'),panel=document.getElementById('journalPanel');
 const params=new URLSearchParams(location.search),focus=params.get('eventId'),initialView=params.get('view')==='results'?'results':'all';
 const titles={TimeframeLocked:'قفل شدن تایم‌فریم',EntryBlocked:'ورود انجام نشد؛ تایم‌فریم قفل است',EntryError:'خطای ورود',ManualClosed:'بستن دستی',NearEntry:'نزدیک ورود',Opened:'ورود تأیید شد',FirstProtection:'پلهٔ اول ریسک‌فری',RiskFreeReached:'پلهٔ دوم ریسک‌فری',Target:'تارگت',StopLoss:'استاپ‌لاس',RiskFree:'خروج ریسک‌فری',Expired:'انقضای سیگنال نزدیک ورود'};
 const messages={TimeframeLocked:'تایم‌فریم به‌دلیل دو استاپ پیاپی قفل شد.',EntryBlocked:'قیمت به نقطهٔ ورود رسید، اما به‌دلیل قفل بودن تایم‌فریم معامله باز نشد.',EntryError:'ورود معامله با خطا مواجه شد.',ManualClosed:'این معامله به صورت دستی بسته شد.',NearEntry:'قیمت به محدودهٔ ورود سیگنال نزدیک شد.',Opened:'ورود معامله در بای‌بیت تأیید شد.',FirstProtection:'پلهٔ اول ریسک‌فری فعال شد.',RiskFreeReached:'پلهٔ دوم ریسک‌فری فعال شد.',Target:'سیگنال به تارگت رسید.',StopLoss:'معامله با استاپ‌لاس پایان یافت.',RiskFree:'معامله با ریسک‌فری بسته شد.',Expired:'سیگنالی که قبلاً نزدیک ورود شده بود، منقضی شد.'};
 const exits={FirstStepLimit:'پلهٔ اول ریسک‌فری (استاپ‌لیمیت)',SecondStepLimit:'پلهٔ دوم ریسک‌فری (استاپ‌لیمیت)',SafetyStopMarket:'محافظ ریسک‌فری (استاپ‌مارکت)'};
 const resultKinds=new Set(['Target','RiskFree','StopLoss','ManualClosed','EntryError','EntryBlocked']),tabs=[...document.querySelectorAll('[data-journal-tab]')];
 const fresh=()=>({entries:new Map(),before:null,hasMore:false,busy:false,initialized:false});
 const sources={normal:{all:fresh(),results:fresh()},manual:{all:fresh(),results:fresh()},strategy2:{all:fresh(),results:fresh()}};
 let source=['normal','manual','strategy2'].includes(params.get('source'))?params.get('source'):'normal',states=sources[source],view=initialView;
 let sourceResolved=!!params.get('source');
 const openedDetails=new Set();
 const eventDate=new Intl.DateTimeFormat('fa-IR',{timeZone:'Asia/Tehran',year:'numeric',month:'2-digit',day:'2-digit'});
 const eventClock=new Intl.DateTimeFormat('fa-IR',{timeZone:'Asia/Tehran',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'});
 const text=(tag,value)=>Object.assign(document.createElement(tag),{textContent:value});
 let cacheKey=null;
 const cacheLimit=400;
 function persist(){
  if(!cacheKey)return;
  try{
   const data={version:1,sources:{}};
   for(const [name,views] of Object.entries(sources)){
    data.sources[name]={};
    for(const [tab,state] of Object.entries(views)){
     const entries=[...state.entries.values()].sort((a,b)=>Date.parse(b.at)-Date.parse(a.at)||b.sequence-a.sequence).slice(0,cacheLimit);
     data.sources[name][tab]={entries,before:entries.at(-1)?.sequence??null,hasMore:state.hasMore||state.entries.size>cacheLimit,initialized:state.initialized,etag:state.etag,etagUrl:state.etagUrl};
    }
   }
   localStorage.setItem(cacheKey,JSON.stringify(data));
  }catch{/* Storage can be full or disabled; live history remains available. */}
 }
 function restore(){
  try{
   const data=JSON.parse(localStorage.getItem(cacheKey));if(data?.version!==1)return;
   for(const name of ['normal','manual','strategy2'])for(const tab of ['all','results']){
    const saved=data.sources?.[name]?.[tab];if(!saved||!Array.isArray(saved.entries))continue;
    const state=sources[name][tab];
    state.entries=new Map(saved.entries.slice(0,cacheLimit).filter(e=>typeof e.id==='string'&&Number.isFinite(e.sequence)).map(e=>[e.id,e]));
    state.etag=saved.etag;state.etagUrl=saved.etagUrl;state.before=saved.before;state.hasMore=!!saved.hasMore;state.initialized=!!saved.initialized;
   }
  }catch{/* Ignore corrupt or older cache records. */}
 }
 function visible(){try{return !document.hidden&&(!window.frameElement||window.frameElement.getClientRects().length>0);}catch{return !document.hidden;}}
 async function start(){
  try{
   let identity;
   if(parent!==window&&parent.location.origin===location.origin)identity=parent.PhoenixSessionIdentity;
   if(!identity){
    const response=await fetch('/api/auth/me',{cache:'no-store'});
    if(response.status===401){status.textContent='برای مشاهدهٔ رویدادها دوباره وارد فونیکس شو.';return;}
    if(response.ok)identity=await response.json();
   }
   if(typeof identity?.username==='string'&&identity.username){cacheKey='phoenix-journal-v1:'+identity.username.toLowerCase();restore();}
  }catch{/* A failed identity check must never expose another account's stored results. */}
  select(view);recovery();
  if(focus)[...timeline.children].find(x=>x.dataset.eventId===focus)?.scrollIntoView?.({block:'center'});
 }
 function eventMessage(e){
  if(e.kind==='FirstProtection'||e.kind==='RiskFreeReached')return messages[e.kind];
  if(e.kind==='RiskFree'){
   if(e.exitKind==='FirstStepLimit')return 'معامله با پلهٔ اول ریسک‌فری بسته شد.';
   if(e.exitKind==='SecondStepLimit')return 'معامله با پلهٔ دوم ریسک‌فری بسته شد.';
   if(e.exitKind==='SafetyStopMarket')return 'معامله با محافظ ریسک‌فری بسته شد.';
  }
  return e.reason||messages[e.kind]||'رویداد سیگنال ثبت شد.';
 }
 function render(){
  const state=states[view],fragment=document.createDocumentFragment();let count=0;
  for(const e of [...state.entries.values()].sort((a,b)=>Date.parse(b.at)-Date.parse(a.at)||b.sequence-a.sequence)){
   if(view==='results'&&!resultKinds.has(e.kind)&&!(source==='strategy2'&&e.kind==='Expired'))continue;count++;
   const card=document.createElement('article');card.className='event-card'+(e.id===focus?' focused':'');card.dataset.kind=e.kind;card.dataset.eventId=e.id;
   const code=e.kind==='Target'?'TP':e.kind==='StopLoss'?'SL':e.kind==='FirstProtection'?'RF1':e.kind==='RiskFree'?(e.exitKind==='FirstStepLimit'?'RF1':e.exitKind==='SecondStepLimit'?'RF2':'RF'):e.kind==='RiskFreeReached'?'RF2':null;
   const head=document.createElement('div');head.className='event-top';
   const badge=Object.assign(text('span',code||(source==='strategy2'&&e.kind==='Expired'?'انقضای سیگنال':titles[e.kind])||e.kind),{className:'event-kind'});badge.title=titles[e.kind]||e.kind;badge.setAttribute('aria-label',(titles[e.kind]||e.kind)+(e.kind==='RiskFree'&&exits[e.exitKind]?' · '+exits[e.exitKind]:''));
   const direction=text('span',e.direction==='Long'?'Long ↗':e.direction==='Short'?'Short ↘':'');direction.className='event-direction';direction.dataset.side=e.direction;head.append(Object.assign(text('b',e.symbol||'Phoenix'),{className:'event-symbol'}),direction,badge);card.append(head);
   card.append(Object.assign(text('p',eventMessage(e)),{className:'event-message'}));
   const stamp=text('time','');stamp.className='event-time';const occurred=e.at?new Date(e.at):new Date(NaN);
   if(Number.isFinite(occurred.getTime())){stamp.dateTime=occurred.toISOString();stamp.textContent=eventDate.format(occurred)+' · '+eventClock.format(occurred);stamp.title='زمان وقوع رویداد، به وقت تهران';stamp.setAttribute('aria-label',stamp.textContent+'، به وقت تهران');}else stamp.textContent='زمان این رویداد ثبت نشده';
   card.append(stamp);
   if(e.kind==='TimeframeLocked'){fragment.append(card);continue;}
   const detail=document.createElement('details');detail.className='event-detail';
   const summary=text('summary','نمودار و جزئیات');summary.setAttribute('aria-label','نمودار ثبت‌شدهٔ سیگنال و جزئیات '+(e.symbol||'Phoenix'));detail.append(summary);
   const body=text('div','');body.className='event-detail-body';
   const id=e.id.split(':')[0];let chartRequested=false,liveChart=null;
   function showChart(){if(chartRequested)return;chartRequested=true;
    if(!/^[a-f0-9-]{36}$/i.test(id)){body.prepend(text('p','نمودار این سابقه در دسترس نیست.'));return;}
    const host=text('div','');host.className='signal-live-host';body.prepend(host);
    if(window.PhoenixLiveChart)liveChart=window.PhoenixLiveChart.mount(host,id,source);else host.append(text('p','دریافت نمودار زنده در دسترس نیست.'));
    const archive=document.createElement('details');archive.className='signal-archive-chart';archive.append(text('summary','تصویر زمان ثبت سیگنال'));
    archive.addEventListener('toggle',()=>{if(archive.open&&!archive.querySelector('img')){const image=document.createElement('img');image.src='/api/analysis/results/'+id+'/image'+(source==='strategy2'?'?strategy=strategy2':'');image.alt='تصویر زمان ثبت '+e.symbol;image.onerror=()=>image.replaceWith(text('p','تصویر زمان ثبت در دسترس نیست.'));archive.append(image);}});body.append(archive);
   }
   const levels=document.createElement('div');levels.className='event-levels';
   for(const [label,value] of [['ورود',e.entryPrice],['تارگت',e.takeProfit],['استاپ ثبت‌شده',e.stopLoss]])if(Number(value)>0){const cell=document.createElement('div');cell.append(text('small',label),text('b',Number(value).toLocaleString('en-US',{maximumFractionDigits:10})));levels.append(cell);}
   if(levels.children.length)body.append(levels);
   body.append(Object.assign(text('div',[e.direction,e.timeframe?'تایم‌فریم '+e.timeframe:null].filter(Boolean).join(' · ')),{className:'event-meta'}));
   if(e.kind==='RiskFree'&&exits[e.exitKind])body.append(text('p',exits[e.exitKind]));
   if(e.walletSource==='BybitTransaction')body.append(Object.assign(text('small','پس از تسویه در بای‌بیت'+(e.walletAtUtc?' · '+new Date(e.walletAtUtc).toLocaleString('fa-IR'):'')),{className:'wallet-source'}));
   detail.append(body);detail.open=openedDetails.has(e.id);if(detail.open)showChart();
   detail.addEventListener('toggle',()=>{if(!detail.isConnected)return;if(detail.open){openedDetails.add(e.id);showChart();}else{openedDetails.delete(e.id);liveChart?.stop();liveChart=null;chartRequested=false;body.querySelector('.signal-live-host')?.remove();body.querySelector('.signal-archive-chart')?.remove();}});card.append(detail);
   if(resultKinds.has(e.kind)&&!['EntryError','EntryBlocked'].includes(e.kind)){const wallet=text('div','');wallet.className='event-wallet';const known=e.walletBalanceUsdt!=null&&Number.isFinite(Number(e.walletBalanceUsdt));
    wallet.append(text('span','موجودی کیف پول'),Object.assign(text(known?'b':'span',known?Number(e.walletBalanceUsdt).toLocaleString('en-US',{maximumFractionDigits:8})+' USDT':'در این سابقه ثبت نشده'),{className:known?'':'wallet-missing'}));card.append(wallet);}
   fragment.append(card);
  }
  timeline.replaceChildren(fragment);if(!count)timeline.append(text('p',state.initialized?(view==='results'?'هنوز نتیجهٔ نهایی ثبت نشده است.':'هنوز رویدادی ثبت نشده است.'):'در حال دریافت…'));
  older.hidden=!state.hasMore;older.disabled=state.busy;panel.setAttribute('aria-busy',String(state.busy));
 }
 async function load(mode='latest'){
  const currentView=view,state=states[currentView];if(state.busy)return;state.busy=true;older.disabled=true;panel.setAttribute('aria-busy','true');
  const first=timeline.querySelector('.event-card'),anchor=first?.getBoundingClientRect().top;
  try{
   const query=new URLSearchParams({view:currentView,source});if(mode==='older'&&state.before)query.set('before',state.before);else if(focus&&!state.entries.has(focus)&&currentView===initialView)query.set('eventId',focus);
   const url='/api/mobile/history?'+query;
   const headers=state.etag&&state.etagUrl===url&&state.entries.size?{'If-None-Match':state.etag}:{};
   const response=await fetch(url,{cache:'no-store',headers});
   if(response.status===304){status.textContent='سوابق به‌روز است.';return;}
   if(mode!=='older'){state.etag=response.headers?.get('ETag');state.etagUrl=url;}
   if(response.status===401){try{if(cacheKey)localStorage.removeItem(cacheKey);}catch{}for(const views of Object.values(sources))for(const state of Object.values(views))state.entries.clear();render();}if(!response.ok)throw Error(response.status===401?'برای مشاهدهٔ رویدادها دوباره وارد فونیکس شو.':'دریافت تاریخچه ناموفق بود؛ دوباره تلاش کن.');
   const page=await response.json();if(states[currentView]!==state)return;
   if(focus&&!page.focusFound&&!sourceResolved&&source==='normal'){sourceResolved=true;selectSource('manual');return;}
   let changed=false;for(const e of page.events){if(JSON.stringify(state.entries.get(e.id))!==JSON.stringify(e)){state.entries.set(e.id,e);changed=true;}}
   const wasInitialized=state.initialized;if(!wasInitialized||mode==='older'){state.before=page.before;state.hasMore=page.hasMore;}state.initialized=true;persist();
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
  panel.setAttribute('aria-labelledby','journal-tab-'+view);document.getElementById('viewDescription').textContent=view==='results'?(source==='strategy2'?'نتایج استراتژی دو: تارگت، ریسک‌فری، استاپ، اکسپایر و خطای ورود':'نتایج نهایی: تارگت، خروج ریسک‌فری و استاپ‌لاس'):'تمام اعلان‌ها؛ از نزدیک ورود تا پایان معامله';
  const url=new URL(location.href);url.searchParams.set('view',view);url.searchParams.set('source',source);history.replaceState(null,'',url);
  status.textContent=states[view].initialized?'سوابق ذخیره‌شده؛ در حال بررسی موارد تازه…':'در حال دریافت…';render();load();
 }
 function selectSource(next){source=next;document.querySelectorAll('.wallet-import').forEach(e=>e.hidden=source==='strategy2');states=sources[source];for(const tab of document.querySelectorAll('[data-journal-source]'))tab.setAttribute('aria-pressed',String(tab.dataset.journalSource===source));select(view);}
 document.querySelectorAll('[data-journal-source]').forEach(tab=>tab.addEventListener('click',()=>{sourceResolved=true;selectSource(tab.dataset.journalSource);}));
 for(const tab of document.querySelectorAll('[data-journal-source]'))tab.setAttribute('aria-pressed',String(tab.dataset.journalSource===source));
 document.querySelectorAll('.wallet-import').forEach(e=>e.hidden=source==='strategy2');
 tabs.forEach((tab,i)=>{tab.addEventListener('click',()=>select(tab.dataset.journalTab));tab.addEventListener('keydown',event=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?tabs.length-1:(i+1)%tabs.length;select(tabs[next].dataset.journalTab,true);});});
 older.addEventListener('click',()=>load('older'));document.getElementById('refreshEvents').addEventListener('click',()=>load());
 const importButton=document.getElementById('importWallets'),importStatus=document.getElementById('walletImportStatus');
 importButton.addEventListener('click',async()=>{
  const file=document.getElementById('walletImportFile').files[0];if(!file){importStatus.textContent='ابتدا فایل خروجی JSON تلگرام را انتخاب کن.';return;}
  if(file.size>2*1024*1024){importStatus.textContent='فایل باید حداکثر ۲ مگابایت باشد؛ خروجی را به پیام‌های اخیر محدود کن.';return;}
  importButton.disabled=true;importStatus.textContent='در حال تطبیق پیام‌ها با ۵۰ نتیجهٔ اخیر…';
  try{const exported=JSON.parse(await file.text());const response=await fetch('/api/mobile/history/import-wallets',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({export:exported})});const result=await response.json();if(!response.ok)throw Error(result.error||'بازیابی انجام نشد.');
   importStatus.textContent=Number(result.imported).toLocaleString('fa-IR')+' موجودی بازیابی شد؛ '+Number(result.unresolved).toLocaleString('fa-IR')+' نتیجه هنوز پیام قابل تطبیق ندارد.';sources.normal={all:fresh(),results:fresh()};sources.manual={all:fresh(),results:fresh()};states=sources[source];select(view);
  }catch(error){importStatus.textContent=error instanceof SyntaxError?'فایل JSON معتبر نیست.':error.message;}finally{importButton.disabled=false;}
 });
 const recoveryButton=document.getElementById('recoverBybitWallets'),recoveryStatus=document.getElementById('bybitRecoveryStatus');let recovering=false,lastRecovery=null;
 async function recovery(manual=false){
  if(source==='strategy2')return;
  if(recovering)return;recovering=true;if(manual){recoveryButton.disabled=true;recoveryStatus.textContent='در حال بازیابی از سوابق بای‌بیت…';}
  try{const response=await fetch('/api/mobile/history/'+(manual?'recover-wallets':'wallet-recovery'),manual?{method:'POST'}:{cache:'no-store'});if(!response.ok)throw Error();const result=await response.json();
   recoveryStatus.textContent=result.state==='running'?'در حال بازیابی خودکار موجودی‌ها از بای‌بیت…':(result.message||'')+(result.atUtc?' '+Number(result.recovered||0).toLocaleString('fa-IR')+' موجودی بازیابی شد؛ '+Number(result.unresolved||0).toLocaleString('fa-IR')+' مورد بدون تطبیق قطعی.':'');
   recoveryButton.disabled=result.state==='running';if(result.atUtc!==lastRecovery&&result.state!=='running'){lastRecovery=result.atUtc;if(result.recovered>0)load();}
  }catch{recoveryStatus.textContent='وضعیت بازیابی فعلاً در دسترس نیست؛ سابقهٔ نتایج محفوظ است.';recoveryButton.disabled=false;}
  finally{recovering=false;}
 }
 recoveryButton.addEventListener('click',()=>recovery(true));setInterval(()=>{if(visible())recovery();},30000);
 setInterval(()=>{if(visible())load();},5000);
 document.addEventListener('visibilitychange',()=>{if(visible())load();});
 window.addEventListener('message',event=>{if(event.origin===location.origin&&event.source===parent&&event.data?.type==='phoenix-frame-visible')load();});
 start();
})();
