(() => {
 'use strict';
 const list=document.getElementById('reviewList'),detail=document.getElementById('reviewDetail'),status=document.getElementById('reviewStatus'),pane=document.getElementById('market-pane-review');
 let items=[],selected=null,busy=false,loading=false,autoFocus=true;
 const text=(tag,value,className)=>Object.assign(document.createElement(tag),{textContent:value,...(className?{className}:{})});
 function focusReview(active){document.body.classList.toggle('review-focus',active);document.getElementById('closeReviewFocus').hidden=!active;if(parent!==window)parent.postMessage({type:'phoenix-review-focus',active},location.origin);}
 const shortLabels={'فاصله تا ورود':'تا ورود','فاصله ورود تا تارگت':'تا تارگت','لوریج محاسبه‌شده':'اهرم محاسبه','حداکثر لوریج ارز':'سقف اهرم','لوریج قابل استفاده با این سقف':'اهرم مجاز','تایم‌فریم سیگنال':'تایم‌فریم'};
 function summary(item){
  const fields=[],near=[];let direction='',interval='';
  for(const line of (item.caption||'').split('\n')){
   const colon=line.indexOf(':');if(colon<0)continue;const key=line.slice(0,colon).trim(),value=line.slice(colon+1).trim();
   if(key==='نماد')continue;if(key==='جهت'){direction=value;continue;}if(key==='تایم‌فریم سیگنال'){interval=value;continue;}
   if(key.startsWith('نزدیک')){near.push({key,value});continue;}
   fields.push({key:shortLabels[key]||key,value});
  }
  return {fields,near,direction,interval};
 }
 function renderList(){
  list.replaceChildren();
  for(const item of items){const button=text('button',item.symbol);button.type='button';button.className=item.key===selected?'selected':'';button.disabled=busy;button.onclick=()=>{selected=item.key;autoFocus=true;render();};list.append(button);}
 }
 function render(){
  document.getElementById('reviewBadge').textContent=items.length.toLocaleString('fa-IR');document.getElementById('reviewCapacity').textContent=items.length.toLocaleString('fa-IR')+' / ۱۰';
  list.replaceChildren();
  renderList();
  const item=items.find(x=>x.key===selected);detail.replaceChildren();
  if(!item){focusReview(false);detail.append(text('div','پیشنهادها اینجا آمادهٔ بررسی می‌شوند. برای شروع به تب ایجاد سیگنال برو.','review-empty'));return;}
  if(autoFocus&&!pane.hidden)focusReview(true);
  const data=summary(item),title=document.createElement('div');title.className='review-title';
  title.append(text('h3',item.symbol),text('span',[data.direction,data.interval].filter(Boolean).join(' · '),'review-direction'));
  const expand=text('button','جزئیات','review-more');expand.type='button';title.append(expand);detail.append(title);
  const chart=document.createElement('div');chart.className='review-chart';const image=document.createElement('img');image.src=item.image;image.alt='نمودار '+item.symbol;chart.append(image);detail.append(chart);
  const grid=document.createElement('div');grid.className='review-metrics';
  for(const f of data.fields){const cell=document.createElement('div');cell.append(text('span',f.key),text('b',f.value));grid.append(cell);}detail.append(grid);
  if(data.near.length){const near=document.createElement('div');near.className='review-neighbors';for(const row of data.near){const label=row.key.replace('نزدیکِ ','').replace('نزدیکی ','');near.append(text('div',label+': '+row.value));}detail.append(near);}
  const modal=document.createElement('dialog');modal.className='review-details-dialog';
  const close=text('button','بستن');close.type='button';close.onclick=()=>modal.close();modal.append(close,text('h3','جزئیات '+item.symbol),text('pre',item.caption));
  if(item.evidence){const e=item.evidence;modal.append(text('p',`نمونهٔ قابل مقایسه: ${e.sameTimeframeComparable??0} هم‌تایم‌فریم / ${e.otherTimeframeComparable??0} سایر`));for(const example of e.nearestExamples||[])modal.append(text('p',`${example.outcome} · ${example.timeframe} · ${example.score}٪ · ${example.nearby?'نزدیک':'دور'} · ${example.signalId}`));}
  detail.append(modal);expand.onclick=()=>modal.showModal();
  if(item.state!=='Ready'){detail.append(text('p',item.error||'وضعیت ارسال قبلی نیازمند بررسی است.','review-error'));return;}
  const reason=document.createElement('textarea');reason.rows=1;reason.placeholder='دلیل رد (اختیاری)';reason.maxLength=1500;reason.setAttribute('aria-label','دلیل رد');detail.append(reason);
  const actions=document.createElement('div');actions.className='review-actions';
  for(const [action,label] of [['register','تأیید و ثبت'],['observe','فقط تأیید'],['reject','رد']]){const button=text('button',label);button.type='button';button.disabled=busy;button.onclick=()=>decide(item.key,action,reason.value);actions.append(button);}detail.append(actions);
 }
 async function refresh(){if(loading||busy)return;loading=true;try{
  const response=await fetch('/api/analysis/proposals',{cache:'no-store'});if(!response.ok)throw Error('دریافت صف ناموفق بود؛ دوباره تلاش می‌کنیم.');
  const next=await response.json();if(JSON.stringify(next)!==JSON.stringify(items)){const previous=selected,oldActive=JSON.stringify(items.find(x=>x.key===selected));items=next;if(!items.some(x=>x.key===selected))selected=items[0]?.key;if(previous!==selected||oldActive!==JSON.stringify(items.find(x=>x.key===selected))||!detail.children.length)render();else{renderList();document.getElementById('reviewCapacity').textContent=items.length.toLocaleString('fa-IR')+' / ۱۰';document.getElementById('reviewBadge').textContent=items.length.toLocaleString('fa-IR');/* Do not reset chart/rejection text while new proposals arrive. */}}
  status.textContent=items.length===10?'صف کامل است؛ پس از بررسی یک مورد، تحلیل ادامه می‌یابد.':items.length.toLocaleString('fa-IR')+' پیشنهاد آمادهٔ بررسی';
 }catch(error){status.textContent=error.message;}finally{loading=false;}}
 async function decide(key,action,reason){if(busy)return;busy=true;list.querySelectorAll('button').forEach(x=>x.disabled=true);detail.querySelectorAll('button,textarea').forEach(x=>x.disabled=true);try{
  const response=await fetch('/api/analysis/proposals/'+encodeURIComponent(key)+'/decision',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,reason})});
  if(!response.ok){const body=await response.json();throw Error(body.error||'ثبت پاسخ ناموفق بود.');}status.textContent='پاسخ ثبت شد.';
 }catch(error){status.textContent=error.message;}finally{busy=false;await refresh();render();}}
 document.getElementById('closeReviewFocus').addEventListener('click',()=>{autoFocus=false;focusReview(false);});
 document.getElementById('market-tab-review').addEventListener('click',()=>{autoFocus=true;render();refresh();});
 document.getElementById('startBatch').addEventListener('click',()=>setTimeout(()=>document.getElementById('market-tab-review').click(),500));
 setInterval(()=>{if(!document.hidden)refresh();},2500);render();refresh();
})();
