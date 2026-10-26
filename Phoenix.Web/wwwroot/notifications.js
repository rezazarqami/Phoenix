(() => {
 'use strict';
 const timeline=document.getElementById('eventTimeline'),status=document.getElementById('historyStatus'),older=document.getElementById('loadOlder');
 const focus=new URLSearchParams(location.search).get('eventId');
 const titles={NearEntry:'نزدیک ورود',Opened:'ورود تأیید شد',FirstProtection:'پلهٔ اول محافظت',RiskFreeReached:'محدودهٔ ریسک‌فری',Target:'تارگت',StopLoss:'استاپ‌لاس',RiskFree:'خروج ریسک‌فری',Expired:'انقضای سیگنال نزدیک ورود'};
 const messages={NearEntry:'قیمت به محدودهٔ ورود سیگنال نزدیک شد.',Opened:'ورود معامله در بای‌بیت تأیید شد.',FirstProtection:'پلهٔ اول محافظت از معامله فعال شد.',RiskFreeReached:'معامله به محدودهٔ ریسک‌فری رسید.',Target:'سیگنال به تارگت رسید.',StopLoss:'معامله با استاپ‌لاس پایان یافت.',RiskFree:'معامله با ریسک‌فری بسته شد.',Expired:'سیگنالی که قبلاً نزدیک ورود شده بود، منقضی شد.'};
 const exits={FirstStepLimit:'پلهٔ اول ریسک‌فری (استاپ‌لیمیت)',SecondStepLimit:'پلهٔ دوم ریسک‌فری (استاپ‌لیمیت)',SafetyStopMarket:'محافظ ریسک‌فری (استاپ‌مارکت)'};
 const entries=new Map();let before=null,hasMore=false,busy=false,initialized=false;
 const text=(tag,value)=>Object.assign(document.createElement(tag),{textContent:value});
 function render(){
  const fragment=document.createDocumentFragment();
  for(const e of [...entries.values()].sort((a,b)=>Date.parse(a.at)-Date.parse(b.at)||a.sequence-b.sequence)){
   const card=document.createElement('article');card.className='event-card'+(e.id===focus?' focused':'');card.dataset.kind=e.kind;card.dataset.eventId=e.id;
   const head=document.createElement('div');head.className='event-top';head.append(Object.assign(text('b',e.symbol||'Phoenix'),{className:'event-symbol'}),Object.assign(text('span',titles[e.kind]||e.kind),{className:'event-kind'}));card.append(head);
   card.append(text('p',messages[e.kind]||'رویداد سیگنال ثبت شد.'));
   if(e.kind==='RiskFree'&&exits[e.exitKind])card.append(text('p',exits[e.exitKind]));
   const levels=document.createElement('div');levels.className='event-levels';
   for(const [label,value] of [['ورود',e.entryPrice],['تارگت',e.takeProfit],['استاپ ثبت‌شده',e.stopLoss]])if(Number(value)>0){const cell=document.createElement('div');cell.append(text('small',label),text('b',Number(value).toLocaleString('en-US',{maximumFractionDigits:10})));levels.append(cell);}
   if(levels.children.length)card.append(levels);
   card.append(Object.assign(text('div',[e.direction,e.timeframe?'تایم‌فریم '+e.timeframe:null].filter(Boolean).join(' · ')),{className:'event-meta'}));
   const id=e.id.split(':')[0];if(/^[a-f0-9-]{36}$/i.test(id)){const chart=document.createElement('details');chart.append(text('summary','نمودار ثبت‌شدهٔ سیگنال'));chart.addEventListener('toggle',()=>{if(chart.open&&!chart.querySelector('img')){const image=document.createElement('img');image.src='/api/analysis/results/'+id+'/image';image.alt='نمودار '+e.symbol;image.onerror=()=>{image.remove();chart.append(text('p','نمودار این سابقه در دسترس نیست.'));};chart.append(image);}});card.append(chart);}
   const date=text('time',new Date(e.at).toLocaleString('fa-IR'));date.dateTime=e.at;card.append(date);fragment.append(card);
  }
  timeline.replaceChildren(fragment);if(!entries.size)timeline.append(text('p','هنوز رویدادی ثبت نشده است.'));
 }
 async function load(mode='latest'){
  if(busy)return;busy=true;older.disabled=true;const anchor=timeline.firstElementChild?.getBoundingClientRect().top,oldHeight=timeline.getBoundingClientRect().height;
  try{
   const query=new URLSearchParams();if(mode==='older'&&before)query.set('before',before);else if(!initialized&&focus)query.set('eventId',focus);
   const response=await fetch('/api/mobile/history?'+query,{cache:'no-store'});if(!response.ok)throw Error(response.status===401?'برای مشاهدهٔ رویدادها دوباره وارد فونیکس شو.':'دریافت تاریخچه ناموفق بود؛ دوباره تلاش کن.');
   const page=await response.json();let changed=false;for(const e of page.events){if(!entries.has(e.id)){entries.set(e.id,e);changed=true;}}
   if(!initialized||mode==='older'){before=page.before;hasMore=page.hasMore;}
   if(changed||!initialized)render();older.hidden=!hasMore;
   status.textContent=!page.focusFound&&focus&&!initialized?'رویداد انتخاب‌شده در سوابق این حساب پیدا نشد؛ جدیدترین موارد نمایش داده شده‌اند.':entries.size.toLocaleString('fa-IR')+' رویداد نمایش داده شده؛ سوابق قدیمی‌تر محفوظ‌اند.';
   if(!initialized){initialized=true;const selected=[...timeline.children].find(x=>x.dataset.eventId===focus);(selected||timeline.lastElementChild)?.scrollIntoView?.({block:'center'});}
   else if(mode==='older'&&anchor!=null)window.scrollBy?.(0,timeline.getBoundingClientRect().height-oldHeight);
  }catch(error){status.textContent=error.message;}finally{busy=false;older.disabled=false;}
 }
 older.addEventListener('click',()=>load('older'));document.getElementById('refreshEvents').addEventListener('click',()=>load());
 setInterval(()=>{if(!document.hidden)load();},5000);load();
})();
