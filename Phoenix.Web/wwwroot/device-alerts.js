(() => {
 'use strict';
 const dialog=document.getElementById('deviceAlertsDialog'),state=document.getElementById('deviceAlertsState'),message=document.getElementById('deviceAlertsMessage');
 const enable=document.getElementById('enableDeviceAlerts'),stop=document.getElementById('disableDeviceAlerts'),test=document.getElementById('testDeviceAlerts'),battery=document.getElementById('batteryDeviceAlerts');
 const bridge=window.PhoenixNative,pending=new Map();let serial=0,activating=false,permissionPending=false;
 function native(command,args={}){
  if(!bridge)return Promise.reject(Error('این قابلیت در اپ اندروید فونیکس فعال می‌شود.'));
  const id='alerts-'+Date.now()+'-'+(++serial);
  return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(id);reject(Error('پاسخ اپ دریافت نشد؛ نسخهٔ اپ را بررسی کن.'));},15000);pending.set(id,{resolve,reject,timer});bridge.postMessage(JSON.stringify({id,command,...args}));});
 }
 if(bridge)bridge.onmessage=event=>{
  try{const response=JSON.parse(event.data),request=pending.get(response.id);if(!request)return;pending.delete(response.id);clearTimeout(request.timer);response.ok?request.resolve(response.value):request.reject(Error(response.error||'انجام نشد.'));}catch{}
 };
 async function refresh(){
  if(!bridge){state.textContent='برای اعلان پس‌زمینه، اپ اندروید فونیکس را باز کن.';enable.disabled=test.disabled=stop.disabled=battery.disabled=true;return;}
  try{
   const status=await native('status');
   if(!status.directSupported){state.textContent='برای اعلان زنده، اپ فونیکس را به نسخهٔ جدید به‌روزرسانی کن.';enable.disabled=test.disabled=stop.disabled=battery.disabled=true;return status;}
   enable.disabled=activating||status.directEnabled;stop.disabled=!status.directEnabled;
   state.textContent=!status.notifications||!status.signalChannel?'اجازهٔ اعلان گوشی غیرفعال است.':status.directEnabled?(status.directRunning?(status.directState||'در حال اتصال…'):'در انتظار شروع اتصال؛ فونیکس را باز نگه دار.'):'اعلان زنده غیرفعال است.';
   document.getElementById('deviceAlertsConnection').textContent=status.lastConnected?'آخرین ارتباط موفق: '+new Date(status.lastConnected).toLocaleString('fa-IR'):'';
   return status;
  }catch(error){message.textContent=error.message;}
 }
 async function activate(){
  if(activating)return;activating=true;enable.disabled=true;message.textContent='';
  try{
   const status=await native('status');
   if(!status.notifications||!status.signalChannel){permissionPending=true;await native('requestNotifications');message.textContent='اجازهٔ اعلان فونیکس را بده؛ پس از بازگشت فعال‌سازی ادامه پیدا می‌کند.';return;}
   const response=await fetch('/api/mobile/feed',{cache:'no-store'});
   if(!response.ok)throw Error(response.status===401?'دوباره وارد فونیکس شو.':response.status===403?'اعلان زنده برای حساب اصلی فونیکس فعال می‌شود.':'ارتباط با سرور اعلان برقرار نشد.');
   const page=await response.json();await native('enableDirect',{cursor:page.cursor,account:page.account});message.textContent='سرویس اعلان زنده شروع شد؛ وضعیت اتصال را در اعلان ثابت گوشی می‌بینی.';
  }catch(error){message.textContent=error.message;}
  finally{activating=false;await refresh();}
 }
 document.getElementById('deviceAlertsButton').addEventListener('click',()=>{dialog.showModal();refresh();});
 document.getElementById('closeDeviceAlerts').addEventListener('click',()=>dialog.close());
 enable.addEventListener('click',activate);
 stop.addEventListener('click',async()=>{try{await native('disableDirect');message.textContent='اعلان زنده متوقف شد.';await refresh();}catch(error){message.textContent=error.message;}});
 test.addEventListener('click',async()=>{try{await native('testDirect');message.textContent='اعلان آزمایشی گوشی ارسال شد؛ این تست مجوز و نمایش گوشی را می‌سنجد. وضعیت اتصال سرور جداگانه نمایش داده می‌شود.';}catch(error){message.textContent=error.message;}});
 battery.addEventListener('click',()=>native('batterySettings').catch(error=>message.textContent=error.message));
 window.addEventListener('phoenix-native-resume',async()=>{const status=await refresh();if(permissionPending){permissionPending=false;if(status?.notifications&&status?.signalChannel)activate();}});
 document.getElementById('logoutButton').addEventListener('click',()=>{permissionPending=false;native('logout').catch(()=>{});},true);
 setInterval(()=>{if(dialog.open&&!document.hidden)refresh();},5000);
})();
