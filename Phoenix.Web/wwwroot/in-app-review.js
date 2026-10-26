(() => {
 'use strict';
 const list=document.getElementById('reviewList'),detail=document.getElementById('reviewDetail'),status=document.getElementById('reviewStatus');
 let items=[],selected=null,busy=false,loading=false;
 const text=(tag,value)=>Object.assign(document.createElement(tag),{textContent:value});
 function render(){
  document.getElementById('reviewBadge').textContent=items.length.toLocaleString('fa-IR');
  document.getElementById('reviewCapacity').textContent=items.length.toLocaleString('fa-IR')+' / ۱۰';
  list.replaceChildren();
  for(const item of items){const button=text('button',item.symbol);button.type='button';button.className=item.key===selected?'selected':'';button.disabled=busy;button.append(text('small',new Date(item.createdAt).toLocaleTimeString('fa-IR',{hour:'2-digit',minute:'2-digit'})));button.onclick=()=>{selected=item.key;render();};list.append(button);}
  const item=items.find(x=>x.key===selected);detail.replaceChildren();
  if(!item){detail.append(text('p','صف آمادهٔ دریافت پیشنهاد است. برای شروع، در تب ایجاد سیگنال جست‌وجو را فعال کن.'));return;}
  detail.append(text('h3',item.symbol));const image=document.createElement('img');image.src=item.image;image.alt='نمودار '+item.symbol;detail.append(image,text('pre',item.caption.replace(/\n\nآیا این سیگنال ثبت شود؟$/,'')));
  if(item.evidence){const audit=document.createElement('details');audit.append(text('summary','جزئیات مقایسه با سابقه'));
   const e=item.evidence;audit.append(text('p',`قابل‌مقایسه: ${e.sameTimeframeComparable??0} هم‌تایم‌فریم، ${e.otherTimeframeComparable??0} سایر؛ اطلاعات ناکافی: ${e.insufficientFeatureCount??0}`));
   for(const example of e.nearestExamples||[])audit.append(text('p',`${example.outcome} · ${example.timeframe} · شباهت ${example.score}٪ · ${example.nearby?'نزدیک':'دور'} · شناسه ${example.signalId}`));
   detail.append(audit);
  }
  if(item.state!=='Ready'){detail.append(text('p',item.error||'وضعیت ارسال قبلی نیازمند بررسی است؛ ارسال دوباره مسدود شد.'));return;}
  const reason=document.createElement('textarea');reason.placeholder='دلیل رد (اختیاری)';reason.maxLength=1500;reason.setAttribute('aria-label','دلیل رد');detail.append(reason);
  const actions=document.createElement('div');actions.className='review-actions';
  for(const [action,label] of [['register','تأیید و ثبت'],['observe','فقط تأیید'],['reject','رد پیشنهاد']]){const button=text('button',label);button.type='button';button.disabled=busy;button.onclick=()=>decide(item.key,action,reason.value);actions.append(button);}
  detail.append(actions,text('small','تأیید و ثبت، سیگنال را وارد سامانهٔ معامله می‌کند. «فقط تأیید» صرفاً برای پایش است.'));
 }
 async function refresh(){if(loading||busy)return;loading=true;try{
  const response=await fetch('/api/analysis/proposals',{cache:'no-store'});if(!response.ok)throw Error('دریافت صف ناموفق بود؛ دوباره تلاش می‌کنیم.');
  const next=await response.json(),fingerprint=JSON.stringify(next);
  if(fingerprint!==JSON.stringify(items)){items=next;if(!items.some(x=>x.key===selected))selected=items[0]?.key;render();}
  status.textContent=items.length===10?'صف کامل است؛ تحلیل تا بررسی یک پیشنهاد متوقف می‌ماند.':items.length+' پیشنهاد آمادهٔ بررسی؛ حداکثر ۱۰ مورد.';
 }catch(error){status.textContent=error.message;}finally{loading=false;}}
 async function decide(key,action,reason){if(busy)return;busy=true;detail.querySelectorAll('button,textarea').forEach(x=>x.disabled=true);try{
  const response=await fetch('/api/analysis/proposals/'+encodeURIComponent(key)+'/decision',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,reason})});
  if(!response.ok){const body=await response.json();throw Error(body.error||'ثبت پاسخ ناموفق بود.');}
  status.textContent='پاسخ ثبت شد.';
 }catch(error){status.textContent=error.message;}finally{busy=false;await refresh();render();}}
 document.getElementById('market-tab-review').addEventListener('click',refresh);
 document.getElementById('startBatch').addEventListener('click',()=>setTimeout(()=>document.getElementById('market-tab-review').click(),500));
 setInterval(()=>{if(!document.hidden)refresh();},2500);render();refresh();
})();
