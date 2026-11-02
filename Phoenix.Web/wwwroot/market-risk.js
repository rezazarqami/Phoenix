(() => {
 'use strict';
 const $=id=>document.getElementById(id), states={normal:'شرایط معمول',warning:'احتیاط',danger:'خطر بالا',calibrating:'در حال کالیبراسیون',partial:'دادهٔ ناقص',unavailable:'داده در دسترس نیست'};
 const num=new Intl.NumberFormat('fa-IR',{maximumFractionDigits:2}), compact=new Intl.NumberFormat('en-US',{notation:'compact',maximumFractionDigits:2});
 const clock=v=>new Date(v).toLocaleTimeString('fa-IR',{timeZone:'Asia/Tehran',hour:'2-digit',minute:'2-digit'}), day=v=>new Date(v).toLocaleDateString('fa-IR',{timeZone:'Asia/Tehran',month:'short',day:'numeric'});
 let last=null,busy=false,eventPage=0,alertPage=0;
 const node=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
 function view(name){
  for(const b of document.querySelectorAll('[data-view]')){const selected=b.dataset.view===name;b.setAttribute('aria-selected',String(selected));b.tabIndex=selected?0:-1;}
  $('calendarPanel').hidden=name!=='calendar';$('livePanel').hidden=name!=='live';
 }
 const tabs=[...document.querySelectorAll('[data-view]')];
 tabs.forEach((b,i)=>{b.addEventListener('click',()=>view(b.dataset.view));b.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const next=e.key==='Home'?0:e.key==='End'?1:1-i;tabs[next].focus();view(tabs[next].dataset.view);});});
 function value(v,unit){if(v==null)return '—';return (unit==='USDT'?compact.format(v):num.format(v))+' '+unit;}
 function render(data){
  last=data;$('connectionState').textContent='آخرین دریافت '+clock(data.at)+' · پایش عمومی بای‌بیت · مستقل از معاملات';
  $('riskSummary').dataset.state=data.state;$('riskState').textContent=states[data.state]||'نامشخص';$('riskScore').textContent=data.score==null?'—':num.format(data.score);
  $('riskSummaryHint').textContent=data.state==='partial'?'بعضی شاخص‌ها هنوز آماده نیستند؛ وضعیت کامل بازار مشخص نیست.':data.state==='unavailable'?'دادهٔ کافی و تازه برای ارزیابی نداریم.':data.state==='calibrating'?'در حال جمع‌آوری مبنای مقایسه؛ این وضعیت به معنی امن‌بودن نیست.':'شدت تغییرات نسبت به مبنای آماری؛ بدون تغییر در معاملات';
  const bars=$('riskBars');bars.replaceChildren();
  for(const m of data.metrics){
   const ready=m.state==='live'&&m.score!=null,card=node('article',undefined,'metric');card.dataset.state=!ready?'unknown':m.score>=90?'danger':m.score>=60?'warning':'normal';
   card.append(node('h3',m.label));const track=node('div',undefined,'metric-track');track.setAttribute('role','meter');track.setAttribute('aria-label',m.label);track.setAttribute('aria-valuemin','0');track.setAttribute('aria-valuemax','100');
   if(ready){track.setAttribute('aria-valuenow',String(m.score));const fill=node('div',undefined,'metric-fill');fill.style.setProperty('--level',m.score+'%');track.append(fill);}else track.setAttribute('aria-valuetext',m.state==='calibrating'?'در حال کالیبراسیون':'داده نامشخص');
   card.append(track,node('b',ready?num.format(m.score):'—','metric-score'),node('span',value(m.value,m.unit),'metric-value'),node('span','مبنا '+value(m.baseline,m.unit),'metric-base'),node('span',ready?'تازه':m.state==='calibrating'?num.format(m.samples)+' از ۶۰ مشاهده':m.state==='stale'?'دادهٔ قدیمی':'قطع داده','metric-status'));bars.append(card);
  }
  if(!data.metrics.length)bars.append(node('p','پایش در حال راه‌اندازی است؛ هنوز داده‌ای دریافت نشده.','empty'));
  $('sampleHistory').textContent=num.format(data.storedSamples)+' مشاهده در سابقهٔ مستقل رادار ذخیره شده است.';
  $('calendarSources').replaceChildren(...data.sources.map(s=>node('span',s.name+' · '+(s.state==='live'?'به‌روز':s.state==='stale'?'قدیمی':'دریافت ناموفق')+(s.updatedAt?' · '+day(s.updatedAt)+' '+clock(s.updatedAt):''),s.state==='live'?'':'bad')));
  const events=$('calendarList');events.replaceChildren();
  eventPage=Math.min(eventPage,Math.max(0,Math.ceil(data.events.length/3)-1));
  for(const e of data.events.slice(eventPage*3,eventPage*3+3)){const card=node('article',undefined,'event-card');if(new Date(e.at)-Date.now()<3600000&&new Date(e.at)>new Date())card.classList.add('near');const time=node('div',undefined,'event-time');time.append(node('b',clock(e.at)),node('small',day(e.at)));const info=node('div',undefined,'event-info');info.append(node('h3',e.title));const link=node('a','منبع رسمی · '+e.source);try{const u=new URL(e.url);if(u.protocol==='https:')link.href=u.href;}catch{}link.target='_blank';link.rel='noopener noreferrer';info.append(link);const left=Math.ceil((new Date(e.at)-Date.now())/60000);if(left>0&&left<=60)info.append(node('p','کمتر از '+num.format(left)+' دقیقه مانده'));card.append(time,info);events.append(card);}
  if(!data.events.length)events.append(node('p',data.sources.every(s=>s.state==='live')?'رویداد زمان‌دارِ مرتبطی در دادهٔ منابع ثبت نشده است.':'تقویم هنوز کامل دریافت نشده؛ نبود رویداد به معنی نبود خطر نیست.','empty'));
  pager(events,data.events.length,3,eventPage,next=>{eventPage=next;render(last);});
  renderAlerts(data.alerts);
 }
 function pager(host,total,size,current,change){
  const count=Math.ceil(total/size);if(count<2)return;
  const nav=node('nav',undefined,'risk-pagination');nav.setAttribute('aria-label',host.id==='calendarList'?'صفحات تقویم':'صفحات هشدار');
  const previous=node('button','قبلی'),next=node('button','بعدی');previous.type=next.type='button';previous.disabled=current===0;next.disabled=current>=count-1;
  previous.addEventListener('click',()=>change(current-1));next.addEventListener('click',()=>change(current+1));
  nav.append(previous,node('span',num.format(current+1)+' / '+num.format(count)),next);host.append(nav);
 }
 const method=document.querySelector('.method'),help=node('dialog',undefined,'risk-help'),closeHelp=node('button','بستن');
 closeHelp.type='button';closeHelp.addEventListener('click',()=>help.close());help.append(closeHelp,node('h2','روش محاسبهٔ رادار'));
 method.querySelector('summary').addEventListener('click',e=>{e.preventDefault();help.querySelector('h2').textContent='روش محاسبهٔ رادار';help.querySelectorAll('p').forEach(p=>p.remove());for(const p of method.querySelectorAll('p')){const copy=p.cloneNode(true);copy.removeAttribute('id');help.append(copy);}help.showModal();});
 function showAlert(a){help.querySelectorAll('p').forEach(p=>p.remove());help.querySelector('h2').textContent=a.title;help.append(node('p',a.detail),node('p',day(a.at)+' · '+clock(a.at)));help.showModal();}
 help.addEventListener('click',e=>{if(e.target===help){const r=help.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)help.close();}});document.body.append(help);
 function readAt(){try{return Number(sessionStorage.getItem('phoenix-risk-read')||0);}catch{return 0;}}
 function renderAlerts(alerts){const root=$('riskAlerts');root.replaceChildren();alertPage=Math.min(alertPage,Math.max(0,alerts.length-1));for(const a of alerts.slice(alertPage,alertPage+1)){const card=node('article',undefined,'alert-card '+a.severity);card.dataset.read=String(new Date(a.at).getTime()<=readAt());card.tabIndex=0;card.setAttribute('role','button');card.setAttribute('aria-label','جزئیات هشدار: '+a.title);card.addEventListener('click',()=>showAlert(a));card.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();showAlert(a);}});card.append(node('b',a.title),node('p',a.detail));const t=node('time',day(a.at)+' · '+clock(a.at));t.dateTime=a.at;card.append(t);root.append(card);}if(!alerts.length)root.append(node('p','هنوز هشداری ثبت نشده است.','empty'));pager(root,alerts.length,1,alertPage,next=>{alertPage=next;renderAlerts(alerts);});}
 $('markRead').addEventListener('click',()=>{try{sessionStorage.setItem('phoenix-risk-read',String(Date.now()));}catch{}$('markRead').textContent='خوانده شد';if(last)renderAlerts(last.alerts);});
 async function poll(){if(busy||document.hidden)return;busy=true;try{const r=await fetch('/api/market-risk',{cache:'no-store',signal:AbortSignal.timeout(10000)});if(!r.ok)throw Error('unavailable');render(await r.json());}catch{ $('connectionState').textContent='دریافت داده ناموفق است؛ ارزیابی فعلی قابل اتکا نیست.';$('riskSummary').dataset.state='unavailable';$('riskState').textContent='اتصال قطع است';$('riskScore').textContent='—';$('riskBars').replaceChildren(node('p','دادهٔ تازه دریافت نشده؛ وضعیت بازار نامشخص است.','empty'));$('calendarSources').replaceChildren(node('span','وضعیت منابع قابل بررسی نیست','bad'));}finally{busy=false;}}
 window.addEventListener('message',e=>{if(e.origin===location.origin&&e.source===parent&&e.data?.type==='phoenix-frame-visible')poll();});document.addEventListener('visibilitychange',()=>{if(!document.hidden)poll();});
 poll();setInterval(poll,15000);parent.postMessage({type:'phoenix-frame-ready'},location.origin);
})();
