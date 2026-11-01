(() => {
 const panel=document.getElementById('panel-home'),map={XAU:'gold',BRENT:'brent'},format=new Intl.NumberFormat('en-US',{maximumFractionDigits:2,minimumFractionDigits:2});
 const date=new Intl.DateTimeFormat('fa-IR',{timeZone:'Asia/Tehran',dateStyle:'short',timeStyle:'medium'});let busy=false;
 async function refresh(){
  if(busy||document.hidden||panel.hidden)return;busy=true;const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),12000);
  try{const r=await fetch('/api/market/commodities',{cache:'no-store',signal:controller.signal});if(!r.ok)throw Error();const data=await r.json();
   for(const [symbol,id] of Object.entries(map)){
    const q=data.items?.find(x=>x.symbol===symbol),status=document.getElementById(id+'Status'),time=document.getElementById(id+'Time');
    if(!q?.price||!q.atUtc){status.dataset.error='true';status.textContent='دریافت نرخ تازه ممکن نشد؛ دوباره تلاش می‌کنیم.';time.textContent='زمان نرخ دریافت نشده';continue;}const at=new Date(q.atUtc);if(!Number.isFinite(at.getTime())||!Number.isFinite(Number(q.price))||Number(q.price)<=0){status.dataset.error='true';status.textContent='نرخ نامعتبر؛ دوباره تلاش می‌کنیم.';continue;}
    document.getElementById(id+'Price').textContent=format.format(q.price)+' USD';time.dateTime=at.toISOString();time.textContent=date.format(at)+' · تهران';
    const old=Date.now()-at.getTime()>(q.delayed?30:3)*60000;
    status.dataset.error=String(!!q.error);status.textContent=q.source+' · '+(q.error?'به‌روزرسانی ناموفق؛ آخرین نرخ دریافتی':old?'آخرین نرخ ثبت‌شدهٔ بازار':q.delayed?'با تأخیر منبع':'به‌روز');
   }
  }catch{for(const id of Object.values(map)){const status=document.getElementById(id+'Status');status.dataset.error='true';status.textContent='دریافت نرخ تازه ممکن نشد؛ دوباره تلاش می‌کنیم.';}}
  finally{clearTimeout(timer);busy=false;}
 }
 window.addEventListener('phoenix-tab-changed',refresh);document.addEventListener('visibilitychange',refresh);window.addEventListener('phoenix-native-resume',refresh);setInterval(refresh,30000);refresh();
})();
