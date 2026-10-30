(() => {
 'use strict';
 const list=document.getElementById('reviewList'),detail=document.getElementById('reviewDetail'),status=document.getElementById('reviewStatus'),pane=document.getElementById('market-pane-review');
 let items=[],selected=null,loading=false,autoFocus=true;const pending=new Map(),decided=new Set();let deskVersion=0,removing=false;let countVersion=0,lastActiveCount=null;const failures=new Map();
 // Bound read requests so a stalled mobile connection cannot freeze refresh forever.
 async function read(url){const controller=new AbortController();const timer=setTimeout(()=>controller.abort(),30000);try{const response=await fetch(url,{cache:'no-store',signal:controller.signal});if(!response.ok)throw Error(response.status===401?'نشست ورود پایان یافته؛ دوباره وارد شوید.':`دریافت اطلاعات ناموفق بود (HTTP ${response.status}).`);return await response.json();}finally{clearTimeout(timer);}}
 const text=(tag,value,className)=>Object.assign(document.createElement(tag),{textContent:value,...(className?{className}:{})});
 function focusReview(active){const standalone=parent===window;document.body.classList.toggle('review-focus',standalone&&active);document.getElementById('closeReviewFocus').hidden=!standalone||!active;}
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
  for(const item of items){const button=text('button',item.symbol);button.type='button';button.className=item.key===selected?'selected':'';button.disabled=false;button.onclick=()=>{selected=item.key;autoFocus=true;render();};list.append(button);}
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
  const retryImage=text('button','دریافت دوبارهٔ نمودار','review-more');retryImage.type='button';retryImage.hidden=true;
  image.onerror=()=>{retryImage.hidden=false;};image.onload=()=>{retryImage.hidden=true;};
  retryImage.onclick=()=>{retryImage.hidden=true;image.src=item.image+'?retry='+Date.now();};detail.append(retryImage);
  const grid=document.createElement('div');grid.className='review-metrics';
  for(const f of data.fields){const cell=document.createElement('div');cell.append(text('span',f.key),text('b',f.value));grid.append(cell);}detail.append(grid);
  if(data.near.length){const near=document.createElement('div');near.className='review-neighbors';for(const row of data.near){const label=row.key.replace('نزدیکِ ','').replace('نزدیکی ','');near.append(text('div',label+': '+row.value));}detail.append(near);}
  const modal=document.createElement('dialog');modal.className='review-details-dialog';
  const close=text('button','بستن');close.type='button';close.onclick=()=>modal.close();modal.append(close,text('h3','جزئیات '+item.symbol),text('pre',item.caption));
  if(item.evidence){const e=item.evidence;modal.append(text('p',`شباهت حمایت و مقاومت · مرحلهٔ ${e.referenceStage==='proposal'?'پیشنهاد':'ورود'} · نسخهٔ ${e.comparisonVersion}`),text('p',`آرشیو نتیجه‌دار پیش از این پیشنهاد: ${e.archivedCompletedCount??'—'} · بررسی‌شده: ${e.evaluatedSnapshotCount??'—'} · بدون تصویر فنی این مرحله: ${e.missingStageSnapshotCount??'—'} · ویژگی ناکافی: ${e.insufficientFeatureCount??0}`));modal.append(text('p',`نمونهٔ قابل مقایسه: ${e.sameTimeframeComparable??0} هم‌تایم‌فریم / ${e.otherTimeframeComparable??0} سایر`));for(const example of e.nearestExamples||[])modal.append(text('p',`${example.outcome} · ${example.timeframe} · ${example.score}٪ · ${example.nearby?'نزدیک':'دور'} · ${example.signalId} · فاصلهٔ حمایت/مقاومت: ${example.groupDistances?.['support-resistance']??'—'} · چیدمان سطوح: ${example.groupDistances?.['stop-level']??'—'} / ${example.groupDistances?.['target-level']??'—'}`));}
  detail.append(modal);expand.onclick=()=>modal.showModal();
  if(item.state!=='Ready'){const remove=text('button','حذف از میز تأیید','review-remove');remove.type='button';remove.onclick=()=>removeFromDesk([item]);detail.append(remove);detail.append(text('p',item.error||'وضعیت ارسال قبلی نیازمند بررسی است.','review-error'));return;}
  const reason=document.createElement('textarea');reason.rows=1;reason.placeholder='دلیل رد (اختیاری)';reason.maxLength=1500;reason.setAttribute('aria-label','دلیل رد');detail.append(reason);
  const actions=document.createElement('div');actions.className='review-actions';
  for(const [action,label] of [['register','تأیید و ثبت'],['observe','فقط تأیید'],['reject','رد']]){const button=text('button',label);button.type='button';button.disabled=false;button.onclick=()=>decide(item.key,action,reason.value);actions.append(button);}detail.append(actions);
 }
 async function refresh(){if(loading)return;loading=true;const version=deskVersion;try{
  const received=await read('/api/analysis/proposals');if(version!==deskVersion)return;const next=received.filter(x=>!pending.has(x.key)&&!decided.has(x.key));if(JSON.stringify(next)!==JSON.stringify(items)){const previous=selected,oldActive=JSON.stringify(items.find(x=>x.key===selected));items=next;if(!items.some(x=>x.key===selected))selected=items[0]?.key;if(previous!==selected||oldActive!==JSON.stringify(items.find(x=>x.key===selected))||!detail.children.length)render();else{renderList();document.getElementById('reviewCapacity').textContent=items.length.toLocaleString('fa-IR')+' / ۱۰';document.getElementById('reviewBadge').textContent=items.length.toLocaleString('fa-IR');/* Do not reset chart/rejection text while new proposals arrive. */}}
  if(!pending.size&&!failures.size)status.textContent=items.length===10?'صف کامل است؛ پس از بررسی یک مورد، تحلیل ادامه می‌یابد.':items.length.toLocaleString('fa-IR')+' پیشنهاد آمادهٔ بررسی';
 }catch(error){status.textContent=error.name==='AbortError'?'دریافت صف طول کشید؛ اطلاعات قبلی حفظ شده و دوباره بررسی می‌شود.':error.message;}finally{loading=false;if(!pane.hidden)updateActiveCount();}}
 async function updateActiveCount(){const version=++countVersion;try{const signals=await read('/api/signals');if(version!==countVersion)return;const active=signals.filter(s=>!s.completedAtUtc&&!s.targetReachedAtUtc&&!s.stopLossReachedAtUtc&&!s.riskFreeClosedAtUtc&&!s.removedAtUtc&&!['Error','Rejected','Cancelled','Deactivated','Expired'].includes(s.status));const counter=document.getElementById('activeCount');if(counter)counter.textContent=active.length.toLocaleString('fa-IR');if(lastActiveCount!==active.length){lastActiveCount=active.length;window.dispatchEvent(new CustomEvent('phoenix-signals-invalidated'));if(parent!==window)parent.postMessage({type:'phoenix-signals-invalidated'},location.origin);}}catch{}}
 function progress(){status.textContent=(pending.size?pending.size.toLocaleString('fa-IR')+' پاسخ در حال ثبت در پس‌زمینه · ':'')+(failures.size?[...failures.values()].join(' · '):items.length.toLocaleString('fa-IR')+' پیشنهاد آمادهٔ بررسی');}
 async function decide(key,action,reason){if(pending.has(key))return;const item=items.find(x=>x.key===key);if(!item||item.state!=='Ready')return;
  pending.set(key,item);decided.add(key);failures.delete(key);items=items.filter(x=>x.key!==key);selected=items[0]?.key;
  // Reserve the same chart frame before changing the image; keep the action row in place.
  const y=window.scrollY;render();window.scrollTo(0,y);progress();
  try{const response=await fetch('/api/analysis/proposals/'+encodeURIComponent(key)+'/decision',{method:'POST',keepalive:true,headers:{'Content-Type':'application/json'},body:JSON.stringify({action,reason})});
   if(!response.ok){const body=await response.json().catch(()=>({}));throw Error(body.error||'ثبت پاسخ ناموفق بود.');}
  }catch(error){decided.delete(key);failures.set(key,item.symbol+': '+error.message+' وضعیت را در صف بررسی کن؛ ارسال خودکار تکرار نمی‌شود.');}
  finally{pending.delete(key);await updateActiveCount();await refresh();progress();}
 }
 async function removeFromDesk(snapshot){
  if(removing||!snapshot.length)return;
  if(!window.confirm('پیشنهادهای انتخاب‌شده از میز تأیید حذف شوند؟ این کار معاملات باز یا ثبت قبلی را لغو نمی‌کند.'))return;
  removing=true;const button=document.getElementById('clearReviewDesk');button.disabled=true;
  try{const response=await fetch('/api/analysis/proposals/remove',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({keys:snapshot.map(x=>x.key)})});
   if(!response.ok)throw Error('حذف پیشنهادها ناموفق بود. دوباره تلاش کن.');
   deskVersion++;for(const item of snapshot){decided.add(item.key);failures.delete(item.key);}items=items.filter(x=>!snapshot.some(y=>y.key===x.key));if(!items.some(x=>x.key===selected))selected=items[0]?.key;render();progress();
  }catch(error){status.textContent=error.message;}
  finally{removing=false;button.disabled=false;await refresh();}
 }
 document.getElementById('restoreReviewDesk').addEventListener('click',async()=>{if(removing)return;try{const r=await fetch('/api/analysis/proposals/restore',{method:'POST'});if(!r.ok)throw Error('بازیابی ناموفق بود.');deskVersion++;decided.clear();await refresh();}catch(error){status.textContent=error.message;}});
 document.getElementById('clearReviewDesk').addEventListener('click',()=>removeFromDesk([...items,...pending.values()]));
 document.getElementById('closeReviewFocus').addEventListener('click',()=>{autoFocus=false;focusReview(false);});
 document.getElementById('market-tab-review').addEventListener('click',()=>{autoFocus=true;render();refresh();});
 document.getElementById('startBatch').addEventListener('click',()=>setTimeout(()=>document.getElementById('market-tab-review').click(),500));
 setInterval(()=>{if(!document.hidden)refresh();},2500);render();refresh();
})();
