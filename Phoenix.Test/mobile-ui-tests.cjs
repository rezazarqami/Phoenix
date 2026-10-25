const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM,VirtualConsole}=require('./ui/node_modules/jsdom');
const assets=path.join(__dirname,'../Phoenix.Web/wwwroot');
const pause=()=>new Promise(r=>setTimeout(r,10));
async function settle(check){for(let i=0;i<100;i++){if(check())return;await pause();}throw Error('UI did not settle');}
function boot(native,configured=false){
 const errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
 const dom=new JSDOM(fs.readFileSync(path.join(assets,'index.html'),'utf8'),{url:'https://marketsignalist.ir/',runScripts:'outside-only',virtualConsole:vc});
 const w=dom.window,calls=[],posts=[];
 let state={reminderVersion:2,deviceId:'f2bba862-1d0e-466a-81b8-e49d98e3ede2',deviceLock:true,notifications:false,reminderChannel:false,signalChannel:false,exactAlarms:false,pushConfigured:false,pushEnabled:false,reminders:[]};
 w.fetch=async(url,options={})=>{posts.push({url,options});return{ok:true,json:async()=>({pushConfigured:configured,clientConfig:configured?{appId:"fixture",apiKey:"fixture",projectId:"fixture",senderId:"123"}:null})};};
 if(native)w.OrbitNative={postMessage(raw){const req=JSON.parse(raw);calls.push(req);
  setTimeout(()=>{let value=state,error;
   if(req.command==='schedule'){
    if(!state.notifications||!state.exactAlarms)error='ابتدا مجوزها را فعال کن.';
    else{state.reminders=state.reminders.filter(r=>r.id!==req.reminderId);state.reminders.push({id:req.reminderId,title:req.title,at:req.at,delivered:false,repeat:req.repeat,days:req.days,entryId:req.entryId,entryKind:req.entryKind});value={saved:true};}
   }
   if(req.command==='snooze'){state.reminders.find(r=>r.id===req.reminderId).snoozeAt=Date.now()+req.minutes*60000;}
   if(req.command==='configurePush')state.pushConfigured=true;
   if(req.command==='enablePush'){if(!state.notifications)error='اجازهٔ اعلان لازم است.';else Object.assign(state,{pushEnabled:true,token:'fixture-token-1234567890123456',enrollmentId:'cbd632a1-40fa-45cd-a1cf-6f137f976689'});}
   if(req.command==='cancel')state.reminders=state.reminders.filter(r=>r.id!==req.reminderId);
   if(req.command==='logout')state.reminders=[];
   if(req.command==='disablePush')state.pushEnabled=false;
   w.OrbitNative.onmessage({data:JSON.stringify({id:req.id,ok:!error,value,error})});
  },0);
 }};
 w.eval(fs.readFileSync(path.join(assets,'mobile.js'),'utf8'));
 return {dom,w,errors,calls,posts,allow(){Object.assign(state,{notifications:true,reminderChannel:true,signalChannel:true,exactAlarms:true});},state:()=>state};
}
(async()=>{
 let x=boot(false);assert(x.w.document.querySelector('#mobileReminderForm').hidden);assert(!x.w.document.querySelector('#mobileLock'));assert.equal(x.posts.length,0);x.dom.window.close();
 x=boot(true);const $=s=>x.w.document.querySelector(s);await settle(()=>$('#mobileSecurityStatus').textContent.includes('نیاز'));
 assert($('#mobileEnablePush').disabled);assert($('#mobilePushTest').disabled);
 $('#mobileReminderTitle').value='<img src=x onerror=alert(1)>';
 const date=new Date(Date.now()+600000);date.setSeconds(0,0);const local=new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,16);$('#mobileReminderTime').value=local;
 $('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>$('#mobileMessage').classList.contains('error'));assert.equal(x.state().reminders.length,0);
 x.allow();$('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>$('#mobileReminderList strong'));
 assert.equal($('#mobileReminderList img'),null,'Reminder title became HTML');assert.equal($('#mobileReminderList strong').textContent,'<img src=x onerror=alert(1)>');
 assert.equal(x.calls.filter(c=>c.command==='schedule').length,2);assert.equal(typeof x.calls.find(c=>c.command==='schedule').at,'number');
 $('#mobileReminderList button').click();await settle(()=>!$('#mobileReminderList strong'));assert.equal(x.state().reminders.length,0);

 // Linked habits default to daily; edit preserves the stable reminder ID and relation.
 const entry={id:'67112a8e-3640-41f7-976c-882852fc244a',kind:'habit',title:'تمرین روزانه',details:{}};
 let opened=null;x.w.OrbitRecords={settings:async()=>{},available:id=>id===entry.id,open:async id=>{opened=id;}};
 await x.w.OrbitMobile.openForEntry(entry);assert.equal($('#mobileReminderRepeat').value,'daily');
 $('#mobileReminderRepeat').value='weekly';$('#mobileReminderRepeat').dispatchEvent(new x.w.Event('change'));assert.equal($('#mobileReminderDays').hidden,false);
 $('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>$('#mobileMessage').textContent.includes('حداقل'));assert.equal(x.state().reminders.length,0);
 $('#mobileReminderDays input[value="6"]').checked=true;
 $('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>x.state().reminders.length===1);await settle(()=>$('#mobileReminderList strong'));
 const saved=x.state().reminders[0];assert.equal(saved.entryId,entry.id);assert.equal(saved.repeat,'weekly');assert.deepEqual(saved.days,[6]);
 Array.from($('#mobileReminderList').querySelectorAll('button')).find(b=>b.textContent==='ویرایش').click();await settle(()=>$('#mobileReminderSave').textContent.includes('تغییرات'));
 $('#mobileReminderTitle').value='تمرین جدید';$('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>x.state().reminders[0].title==='تمرین جدید');assert.equal(x.state().reminders.length,1);assert.equal(x.state().reminders[0].id,saved.id);
 x.state().reminders[0].lastDeliveredAt=Date.now();x.w.dispatchEvent(new x.w.Event('orbit-native-resume'));await settle(()=>Array.from($('#mobileReminderList').querySelectorAll('button')).some(b=>b.textContent.includes('تعویق')));
 Array.from($('#mobileReminderList').querySelectorAll('button')).find(b=>b.textContent.includes('۱۰ دقیقه')).click();await settle(()=>x.calls.some(c=>c.command==='snooze'));await settle(()=>$('#mobileReminderList').textContent.includes('تعویق تا'));
 assert.equal(x.state().reminders[0].repeat,'weekly','Snooze changed recurrence');
 Array.from($('#mobileReminderList').querySelectorAll('button')).find(b=>b.textContent==='بازکردن ثبت مرتبط').click();await settle(()=>opened===entry.id);
 await x.w.OrbitMobile.cancelForEntry(entry.id);assert.equal(x.state().reminders.length,0);
 await x.w.OrbitMobile.prepareLogout();assert(x.calls.some(c=>c.command==='logout'));assert(x.calls.some(c=>c.command==='disablePush'));
 assert.deepEqual(x.errors,[]);x.dom.window.close();
 x=boot(true,true);await settle(()=>x.calls.some(c=>c.command==='configurePush'));await settle(()=>!x.w.document.querySelector('#mobileEnablePush').disabled);
 x.w.document.querySelector('#mobileEnablePush').click();await settle(()=>x.w.document.querySelector('#mobileMessage').classList.contains('error'));assert.equal(x.state().pushEnabled,false);
 x.allow();x.w.document.querySelector('#mobileEnablePush').click();await settle(()=>x.posts.some(p=>p.url.endsWith('/devices')));
 const registration=JSON.parse(x.posts.find(p=>p.url.endsWith('/devices')).options.body);assert.equal(registration.enrollmentId,x.state().enrollmentId);assert.equal(registration.token,x.state().token);
 await x.w.OrbitMobile.prepareLogout();assert(x.posts.some(p=>p.options.method==='DELETE'));x.dom.window.close();
 console.log('PASS mobile UI: browser fallback, denied permissions, native scheduling/cancel, safe reminder text, explicit disabled Push, and logout cleanup.');
})().catch(e=>{console.error(e);process.exitCode=1;});
