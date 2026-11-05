(() => {
 const panel=document.getElementById('panel-home'),map={XAU:'gold',BRENT:'brent',USD_TOMAN:'dollar',GOLD18_TOMAN:'gold18',EUR_TOMAN:'euro'},toman=new Intl.NumberFormat('en-US',{maximumFractionDigits:1}),format=new Intl.NumberFormat('en-US',{maximumFractionDigits:2,minimumFractionDigits:2});
 const date=new Intl.DateTimeFormat('fa-IR',{timeZone:'Asia/Tehran',dateStyle:'short',timeStyle:'medium'});let busy=false;const history={},latest={};let historyBusy=false,historyLoadedAt=0;
 function trend(id,q){
  latest[id]=q;
  const points=history[id]||(history[id]=[]);
  const at=Date.parse(q.atUtc);
  if(Number.isFinite(at)&&(!points.length||at>=Date.parse(points.at(-1).at))){
   const day=q.atUtc.slice(0,10),last=points.at(-1);
   if(last&&last.at.slice(0,10)===day)points[points.length-1]={at:q.atUtc,value:Number(q.price)};
   else points.push({at:q.atUtc,value:Number(q.price)});
   if(points.length>14)points.shift();
  }
  const card=document.getElementById(id+'Price').closest('.market-card'),change=document.getElementById(id+'Change'),svg=document.getElementById(id+'Spark');
  if(points.length<2){change.textContent='—';change.title='در انتظار نرخ بعدی برای نمایش روند';return;}
  const percent=(points.at(-1).value/points[0].value-1)*100;card.classList.toggle('quote-negative',percent<0);change.textContent=(percent<0?'↓ ':'↑ +')+percent.toFixed(2)+'%';change.title='تغییر در بازهٔ نمودار هفت‌روزه؛ در تعطیلات، آخرین روزهای معاملاتی';
  const values=points.map(p=>p.value),low=Math.min(...values),range=Math.max(...values)-low||1,path='M'+values.map((v,i)=>(3+i*154/(values.length-1)).toFixed(1)+','+(57-(v-low)*50/range).toFixed(1)).join(' L');
  svg.querySelector('.quote-line').setAttribute('d',path);svg.querySelector('.quote-area').setAttribute('d',path+' L157,65 L3,65 Z');
 }
 async function refreshHistory(){
  if(historyBusy||document.hidden||panel.hidden||Date.now()-historyLoadedAt<30*60000)return;
  historyBusy=true;const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),12000);
  try{
   const r=await fetch('/api/market/commodities/history',{signal:controller.signal});if(!r.ok)throw Error();const data=await r.json();let complete=true;
   for(const [symbol,id] of Object.entries(map)){
    const points=data.items?.find(x=>x.symbol===symbol)?.points;
    const valid=Array.isArray(points)?points.filter(p=>Number(p.value)>0&&Number.isFinite(Date.parse(p.atUtc))&&Date.parse(p.atUtc)<=Date.now()).map(p=>({at:p.atUtc,value:Number(p.value)})).sort((a,b)=>Date.parse(a.at)-Date.parse(b.at)):[];
    if(valid.length<2){complete=false;continue;}
    history[id]=valid;
    if(latest[id])trend(id,latest[id]);
   }
   historyLoadedAt=Date.now()-(complete?0:29*60000);
  }catch{}finally{clearTimeout(timer);historyBusy=false;}
 }
 async function refresh(){
  refreshHistory();
  if(busy||document.hidden||panel.hidden)return;busy=true;const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),12000);
  try{const r=await fetch('/api/market/commodities',{cache:'no-store',signal:controller.signal});if(!r.ok)throw Error();const data=await r.json();
   for(const [symbol,id] of Object.entries(map)){
    const q=data.items?.find(x=>x.symbol===symbol),status=document.getElementById(id+'Status'),time=document.getElementById(id+'Time');
    if(!q?.price||!q.atUtc){status.dataset.error='true';status.closest('.market-card').classList.add('quote-error');status.textContent='دریافت نرخ تازه ممکن نشد؛ دوباره تلاش می‌کنیم.';time.textContent='زمان نرخ دریافت نشده';continue;}if(symbol.endsWith('_TOMAN')&&q.currency!=='TOMAN'){status.dataset.error='true';status.closest('.market-card').classList.add('quote-error');status.textContent='واحد نرخ تأیید نشد';continue;}const at=new Date(q.atUtc);if(!Number.isFinite(at.getTime())||!Number.isFinite(Number(q.price))||Number(q.price)<=0){status.dataset.error='true';status.closest('.market-card').classList.add('quote-error');status.textContent='نرخ نامعتبر؛ دوباره تلاش می‌کنیم.';continue;}
    document.getElementById(id+'Price').textContent=q.currency==='TOMAN'?toman.format(q.price):format.format(q.price);trend(id,q);time.dateTime=at.toISOString();time.textContent=date.format(at)+' · تهران';
    const old=Date.now()-at.getTime()>(q.delayed?30:3)*60000;
    status.dataset.error=String(!!q.error);status.closest('.market-card').classList.toggle('quote-error',!!q.error);status.textContent=q.source+' · '+(q.error?'به‌روزرسانی ناموفق؛ آخرین نرخ دریافتی':old?'آخرین نرخ ثبت‌شدهٔ بازار':q.delayed?'با تأخیر منبع':'به‌روز');document.getElementById(id+'Price').closest('.market-card').title=time.textContent+' · '+status.textContent;
   }
  }catch{for(const id of Object.values(map)){const status=document.getElementById(id+'Status');status.dataset.error='true';status.closest('.market-card').classList.add('quote-error');status.textContent='دریافت نرخ تازه ممکن نشد؛ دوباره تلاش می‌کنیم.';}}
  finally{clearTimeout(timer);busy=false;}
 }
 window.addEventListener('phoenix-tab-changed',refresh);document.addEventListener('visibilitychange',refresh);window.addEventListener('phoenix-native-resume',refresh);setInterval(refresh,30000);refresh();
})();
