/* Native bridge is injected by AndroidX only into approved main-frame origins.
   No passwords, unlock flags or biometric assertions are stored in JavaScript. */
(() => {
  'use strict';
  const $=id=>document.getElementById(id), pending=new Map();
  const native=!!window.OrbitNative?.postMessage;
  let state=null,serverConfigured=false,refreshing=false,serial=0,editingReminder=null,linkedEntry=null;
  function note(text,error=false){$('mobileMessage').textContent=text;$('mobileMessage').classList.toggle('error',error);}
  function command(command,values={}){
    if(!native)return Promise.reject(new Error('این قابلیت در نسخهٔ نصب‌شدنی اندروید فعال است.'));
    return new Promise((resolve,reject)=>{
      const id=String(++serial),timer=setTimeout(()=>{pending.delete(id);reject(new Error('پاسخ گوشی دریافت نشد؛ دوباره تلاش کن.'));},30000);
      pending.set(id,{resolve,reject,timer});
      try{window.OrbitNative.postMessage(JSON.stringify({...values,id,command}));}catch(error){clearTimeout(timer);pending.delete(id);reject(error);}
    });
  }
  if(native)window.OrbitNative.onmessage=event=>{
    let data;try{data=JSON.parse(event.data);}catch{return;}
    const job=pending.get(data.id);if(!job)return;clearTimeout(job.timer);pending.delete(data.id);
    if(data.ok)job.resolve(data.value);else job.reject(new Error(data.error||'عملیات گوشی انجام نشد.'));
  };
  async function api(path,options={}){
    if(window.OrbitPreview)throw new Error('اتصال سرور در پیش‌نمایش فعال نیست.');
    const response=await fetch('/api/personal/mobile/'+path,{...options,credentials:'same-origin',headers:{'Content-Type':'application/json'},cache:'no-store'});
    let data;try{data=await response.json();}catch{}
    if(!response.ok)throw new Error(data?.error||'این قابلیت سرور هنوز در دسترس نیست؛ نسخهٔ جدید باید روی سرور منتشر شود.');
    return data;
  }
  const controls=['mobileLock','mobileNotifyPermission','mobileExactPermission','mobileTest','mobileEnablePush','mobileDisablePush','mobilePushTest','mobileReminderSave'];
  if(!native){
    $('mobileSecurityStatus').textContent='در نسخهٔ وب، قفل اثر انگشت گوشی و یادآوری زمان‌دار پس‌زمینه در دسترس نیست. برای این دو قابلیت نسخهٔ اندروید را نصب کن.';
    $('mobilePushStatus').textContent='اعلان‌های سرور در نسخهٔ اندروید مدیریت می‌شوند.';
    for(const id of controls)$(id).disabled=true;
    $('mobileReminderForm').hidden=true;
  }
  const dayNames={6:'شنبه',7:'یکشنبه',1:'دوشنبه',2:'سه‌شنبه',3:'چهارشنبه',4:'پنجشنبه',5:'جمعه'};
  function localValue(at){const d=new Date(at);return new Date(d.getTime()-d.getTimezoneOffset()*60000).toISOString().slice(0,16);}
  function resetForm(){editingReminder=null;linkedEntry=null;$('mobileReminderForm').reset();$('mobileReminderDays').hidden=true;$('mobileReminderLink').textContent='';$('mobileReminderSave').textContent='ثبت یادآوری';}
  function fillReminder(row){
    editingReminder=row.id;linkedEntry=row.entryId?{id:row.entryId,kind:row.entryKind}:null;
    $('mobileReminderTitle').value=row.title;$('mobileReminderTime').value=localValue(Math.max(row.at,Date.now()+600000));
    $('mobileReminderRepeat').value=row.repeat||'none';$('mobileReminderDays').hidden=row.repeat!=='weekly';
    for(const box of $('mobileReminderDays').querySelectorAll('input'))box.checked=(row.days||[]).includes(Number(box.value));
    $('mobileReminderLink').textContent=linkedEntry?'متصل به ثبت اصلی؛ ویرایش یادآور زمانِ خود ثبت را تغییر نمی‌دهد.':'';
    $('mobileReminderSave').textContent='ذخیرهٔ تغییرات';$('mobileReminderTitle').focus();
  }
  function drawReminders(){
    const host=$('mobileReminderList');host.replaceChildren();
    const dateText=at=>new Intl.DateTimeFormat('fa-IR',{dateStyle:'medium',timeStyle:'short'}).format(new Date(at));
    const rows=[...(state?.reminders||[])].sort((a,b)=>Number(a.delivered)-Number(b.delivered)||a.at-b.at);
    for(const row of rows){
      const item=document.createElement('div'),text=document.createElement('div'),title=document.createElement('strong'),time=document.createElement('small'),actions=document.createElement('div');
      item.className='mobile-reminder';actions.className='button-row';title.textContent=row.title;
      const recurrence=row.repeat==='daily'?'هر روز':row.repeat==='weekly'?(row.days||[]).map(d=>dayNames[d]).join('، '):'یک‌بار';
      time.textContent=dateText(row.at)+' · '+recurrence+' · '+(row.delivered?'موعد گذشته / پردازش‌شده':'در انتظار');
      text.append(title,time);
      if(row.snoozeAt){const snooze=document.createElement('small');snooze.textContent='تعویق تا '+dateText(row.snoozeAt);text.append(snooze);}
      function action(label,work){const b=document.createElement('button');b.type='button';b.className='quiet';b.textContent=label;b.setAttribute('aria-label',label+' '+row.title);b.addEventListener('click',()=>run(b,work));actions.append(b);}
      action('حذف',async()=>{await command('cancel',{reminderId:row.id});if(editingReminder===row.id)resetForm();await refresh();});
      action('ویرایش',async()=>fillReminder(row));
      if(state?.reminderVersion>=2&&(row.delivered||row.lastDeliveredAt)){
        for(const minutes of [10,30,60])action(new Intl.NumberFormat('fa-IR').format(minutes)+' دقیقه تعویق',async()=>{await command('snooze',{reminderId:row.id,minutes});note('تعویق ثبت شد؛ برنامهٔ تکرار اصلی حفظ می‌شود.');await refresh();});
      }
      if(row.entryId)action('بازکردن ثبت مرتبط',async()=>{await window.OrbitRecords?.open(row.entryId);});
      item.append(text,actions);host.append(item);
    }
    if(!host.children.length)host.textContent='یادآوری‌ای روی این گوشی ثبت نشده است.';
  }
  $('mobileReminderRepeat').addEventListener('change',()=>$('mobileReminderDays').hidden=$('mobileReminderRepeat').value!=='weekly');
  $('mobileReminderReset').addEventListener('click',resetForm);
  async function refresh(){
    if(!native||refreshing)return;refreshing=true;
    try{
      state=await command('status');
      $('mobileSecurityStatus').textContent='قفل دستگاه: '+(state.deviceLock?'فعال':'غیرفعال')+' · اعلان: '+(state.notifications&&state.reminderChannel?'مجاز':'نیاز به اجازه')+' · آلارم دقیق: '+(state.exactAlarms?'مجاز':'نیاز به اجازه');
      const advanced=state.reminderVersion>=2;$('mobileReminderRepeat').disabled=!advanced;
      drawReminders();
      try{const server=await api('status');serverConfigured=server.pushConfigured;if(!state.pushConfigured&&server.clientConfig)state=await command('configurePush',{config:server.clientConfig});if(serverConfigured&&state.pushEnabled&&state.token)await api('devices',{method:'POST',body:JSON.stringify({deviceId:state.deviceId,token:state.token,enrollmentId:state.enrollmentId})});}catch{serverConfigured=false;}
      $('mobilePushStatus').textContent=state.pushConfigured&&serverConfigured?(state.pushEnabled?'دریافت اعلان سیگنال و تغییر وضعیت معاملات روی این گوشی فعال است.':'سرویس آماده است؛ دریافت اعلان را فعال کن.'):'یادآوری گوشی آماده است؛ برای اعلان سیگنال، تنظیم سرویس Push در برنامه و سرور لازم است.';
      $('mobilePushTest').disabled=!state.pushEnabled||!serverConfigured;
      $('mobileEnablePush').disabled=!state.pushConfigured||!serverConfigured;
    }catch(error){note(error.message,true);}finally{refreshing=false;}
  }
  async function run(button,action){button.disabled=true;try{note('');await action();}catch(error){note(error.message,true);}finally{button.disabled=false;}}
  for(const [id,action] of Object.entries({mobileLock:'lock',mobileNotifyPermission:'requestNotifications',mobileExactPermission:'requestExactAlarms',mobileTest:'testNotification'}))$(id).addEventListener('click',event=>run(event.currentTarget,async()=>{await command(action);if(action==='testNotification')note('اعلان آزمایشی به اندروید تحویل داده شد.');}));
  $('mobileEnablePush').addEventListener('click',event=>run(event.currentTarget,async()=>{
    if(window.OrbitPreview)throw new Error('اتصال سرور در پیش‌نمایش فعال نیست.');
    const device=await command('enablePush');
    try{await api('devices',{method:'POST',body:JSON.stringify({deviceId:device.deviceId,token:device.token,enrollmentId:device.enrollmentId})});}
    catch(error){await command('disablePush').catch(()=>{});throw error;}
    note('اعلان‌های سرور برای این گوشی فعال شد.');await refresh();
  }));
  async function disablePush(){
    const device=state||await command('status');
    await command('disablePush');
    // The local token is invalidated even when the server is unreachable.
    try{await api('devices/'+encodeURIComponent(device.deviceId),{method:'DELETE'});}catch{}
  }
  $('mobileDisablePush').addEventListener('click',event=>run(event.currentTarget,async()=>{await disablePush();note('دریافت اعلان سرور خاموش شد.');await refresh();}));
  $('mobilePushTest').addEventListener('click',event=>run(event.currentTarget,async()=>{const r=await api('test',{method:'POST',body:'{}'});if(!r.configured||!r.sent)throw new Error('ارسال آزمایشی انجام نشد؛ تنظیم سرویس و ثبت دستگاه را بررسی کن.');note('پیام آزمایشی به سرویس Push ارسال شد.');}));
  let reminderBusy=false;
  $('mobileReminderForm').addEventListener('submit',async event=>{
    event.preventDefault();if(reminderBusy)return;reminderBusy=true;
    try{await run($('mobileReminderSave'),async()=>{
      const date=new Date($('mobileReminderTime').value),title=$('mobileReminderTitle').value.trim();
      if(!title||!Number.isFinite(date.getTime()))throw new Error('عنوان و زمان معتبر وارد کن.');
      const repeat=$('mobileReminderRepeat').value,days=Array.from($('mobileReminderDays').querySelectorAll('input:checked'),box=>Number(box.value));
      if(repeat!=='none'&&!(state?.reminderVersion>=2))throw new Error('برای تکرار، نسخهٔ ۱٫۷ اندروید را نصب کن.');
      if(repeat==='weekly'&&!days.length)throw new Error('حداقل یک روز هفته را انتخاب کن.');
      if(linkedEntry&&window.OrbitRecords&&!window.OrbitRecords.available(linkedEntry.id))throw new Error('ثبت مرتبط حذف یا غیرفعال شده است؛ فرم را پاک کن.');
      await command('schedule',{reminderId:editingReminder||crypto.randomUUID(),title,at:date.getTime(),repeat,days,entryId:linkedEntry?.id||'',entryKind:linkedEntry?.kind||''});
      resetForm();note('یادآوری روی همین گوشی ثبت شد.');await refresh();
    });}finally{reminderBusy=false;}
  });
  window.OrbitMobile={
    available:()=>native,
    async openForEntry(entry){
      if(!native)throw new Error('برای یادآور زمان‌دار، نسخهٔ اندروید را باز کن.');
      if(!(state?.reminderVersion>=2))throw new Error('برای اتصال یادآور به ثبت، نسخهٔ ۱٫۷ اندروید را نصب کن.');
      await window.OrbitRecords.settings();await refresh();
      const existing=state.reminders.find(r=>r.entryId===entry.id);
      if(existing)fillReminder(existing);
      else{
        resetForm();linkedEntry={id:entry.id,kind:entry.kind};$('mobileReminderTitle').value=entry.title;
        const d=typeof entry.details==='string'?JSON.parse(entry.details):entry.details||{};
        let at=entry.kind==='task'?new Date(entry.date+'T'+(d.dueTime||'09:00')).getTime():Date.now()+600000;
        if(!Number.isFinite(at)||at<Date.now()+15000)at=Date.now()+600000;
        $('mobileReminderTime').value=localValue(at);$('mobileReminderRepeat').value=entry.kind==='habit'?'daily':'none';
        $('mobileReminderLink').textContent='متصل به: '+entry.title;$('mobileReminderTitle').focus();
      }
    },
    async cancelForEntry(id){
      if(!native)return;
      const device=await command('status');for(const row of device.reminders||[])if(row.entryId===id)await command('cancel',{reminderId:row.id});
      await refresh();
    },
    prepareLogout:async()=>{if(!native)return;await disablePush();await command('logout');}};
  // Refresh on native unlock as WebView's visibility event is not sufficient.
  window.addEventListener('orbit-native-resume',()=>refresh());
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
  if(native)refresh();
  if(new URLSearchParams(location.search).get('reminders')==='1')setTimeout(()=>$('profileButton').click(),0);
})();
