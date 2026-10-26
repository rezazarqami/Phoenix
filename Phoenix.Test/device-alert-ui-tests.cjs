const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
const flush=()=>new Promise(r=>setImmediate(r));
async function setup({native=true,denied=false}={}){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window,d=w.document;
 const dialog=d.getElementById('deviceAlertsDialog');dialog.showModal=()=>dialog.open=true;dialog.close=()=>dialog.open=false;w.setInterval=()=>0;
 const commands=[],requests=[];let status={directSupported:true,directEnabled:false,directRunning:false,notifications:false,signalChannel:false,lastConnected:0};
 if(native){w.PhoenixNative={postMessage(raw){const request=JSON.parse(raw);commands.push(request.command);if(request.command==='enableDirect'){assert.equal(request.account,'owner');assert.equal(request.cursor,10);status.directEnabled=true;status.directRunning=true;status.directState='در حال اتصال…';}if(request.command==='disableDirect'||request.command==='logout')status.directEnabled=false;queueMicrotask(()=>w.PhoenixNative.onmessage({data:JSON.stringify({id:request.id,ok:true,value:{...status}})}));}};}
 w.fetch=async url=>{requests.push(url);return{ok:!denied,status:denied?403:200,json:async()=>({cursor:10,account:'owner',events:[]})};};
 w.eval(fs.readFileSync(path.join(root,'device-alerts.js'),'utf8'));d.getElementById('deviceAlertsButton').click();await flush();return{dom,w,d,commands,requests,status};
}
(async()=>{
 const t=await setup();t.d.getElementById('enableDeviceAlerts').click();await flush();assert.ok(t.commands.includes('requestNotifications'));assert.equal(t.requests.length,0);
 t.status.notifications=t.status.signalChannel=true;t.w.dispatchEvent(new t.w.Event('phoenix-native-resume'));await flush();await flush();assert.ok(t.commands.includes('enableDirect'));assert.deepEqual(t.requests,['/api/mobile/feed']);assert.equal(t.d.getElementById('enableDeviceAlerts').disabled,true);
 t.d.getElementById('testDeviceAlerts').click();await flush();assert.ok(t.commands.includes('testDirect'));assert.match(t.d.getElementById('deviceAlertsMessage').textContent,/مجوز و نمایش/);
 t.d.getElementById('disableDeviceAlerts').click();await flush();assert.equal(t.status.directEnabled,false);
 t.d.getElementById('logoutButton').click();await flush();assert.ok(t.commands.includes('logout'));t.dom.window.close();
 const b=await setup({native:false});assert.equal(b.d.getElementById('enableDeviceAlerts').disabled,true);b.dom.window.close();
 const denied=await setup({denied:true});denied.status.notifications=denied.status.signalChannel=true;denied.d.getElementById('enableDeviceAlerts').click();await flush();assert.ok(!denied.commands.includes('enableDirect'));assert.match(denied.d.getElementById('deviceAlertsMessage').textContent,/حساب اصلی/);denied.dom.window.close();
 console.log('PASS device alert UI: explicit permission, native activation without Firebase, test, stop, logout and account denial.');
})().catch(e=>{console.error(e);process.exitCode=1;});
