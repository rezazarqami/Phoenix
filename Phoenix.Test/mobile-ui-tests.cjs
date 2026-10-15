const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM,VirtualConsole}=require('./ui/node_modules/jsdom');
const assets=path.join(__dirname,'../Phoenix.Web/wwwroot');
const pause=()=>new Promise(r=>setTimeout(r,10));
async function settle(check){for(let i=0;i<100;i++){if(check())return;await pause();}throw Error('UI did not settle');}
function boot(native){
 const errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
 const dom=new JSDOM(fs.readFileSync(path.join(assets,'index.html'),'utf8'),{url:'https://marketsignalist.ir/',runScripts:'outside-only',virtualConsole:vc});
 const w=dom.window,calls=[],posts=[];
 let state={deviceId:'f2bba862-1d0e-466a-81b8-e49d98e3ede2',deviceLock:true,notifications:false,reminderChannel:false,signalChannel:false,exactAlarms:false,pushConfigured:false,pushEnabled:false,reminders:[]};
 w.fetch=async(url,options={})=>{posts.push({url,options});return{ok:true,json:async()=>({pushConfigured:false})};};
 if(native)w.OrbitNative={postMessage(raw){const req=JSON.parse(raw);calls.push(req);
  setTimeout(()=>{let value=state,error;
   if(req.command==='schedule'){
    if(!state.notifications||!state.exactAlarms)error='ابتدا مجوزها را فعال کن.';
    else{state.reminders.push({id:req.reminderId,title:req.title,at:req.at,delivered:false});value={saved:true};}
   }
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
 let x=boot(false);assert(x.w.document.querySelector('#mobileReminderForm').hidden);assert(x.w.document.querySelector('#mobileLock').disabled);assert.equal(x.posts.length,0);x.dom.window.close();
 x=boot(true);const $=s=>x.w.document.querySelector(s);await settle(()=>$('#mobileSecurityStatus').textContent.includes('نیاز'));
 assert($('#mobileEnablePush').disabled);assert($('#mobilePushTest').disabled);
 $('#mobileReminderTitle').value='<img src=x onerror=alert(1)>';
 const date=new Date(Date.now()+600000);date.setSeconds(0,0);const local=new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,16);$('#mobileReminderTime').value=local;
 $('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>$('#mobileMessage').classList.contains('error'));assert.equal(x.state().reminders.length,0);
 x.allow();$('#mobileReminderForm').dispatchEvent(new x.w.Event('submit',{cancelable:true,bubbles:true}));await settle(()=>$('#mobileReminderList strong'));
 assert.equal($('#mobileReminderList img'),null,'Reminder title became HTML');assert.equal($('#mobileReminderList strong').textContent,'<img src=x onerror=alert(1)>');
 assert.equal(x.calls.filter(c=>c.command==='schedule').length,2);assert.equal(typeof x.calls.find(c=>c.command==='schedule').at,'number');
 $('#mobileReminderList button').click();await settle(()=>!$('#mobileReminderList strong'));assert.equal(x.state().reminders.length,0);
 await x.w.OrbitMobile.prepareLogout();assert(x.calls.some(c=>c.command==='logout'));assert(x.calls.some(c=>c.command==='disablePush'));
 assert.deepEqual(x.errors,[]);x.dom.window.close();
 console.log('PASS mobile UI: browser fallback, denied permissions, native scheduling/cancel, safe reminder text, explicit disabled Push, and logout cleanup.');
})().catch(e=>{console.error(e);process.exitCode=1;});
