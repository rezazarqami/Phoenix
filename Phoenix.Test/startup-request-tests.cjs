const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),vm=require('vm');
const {JSDOM,VirtualConsole}=require('./ui/node_modules/jsdom'),root=path.join(__dirname,'../Phoenix.Web/wwwroot'),read=f=>fs.readFileSync(path.join(root,f),'utf8'),flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
 let calls=0,release;const events={},context={window:{addEventListener:(n,f)=>events[n]=f},document:{addEventListener:()=>{}},Date,Response,AbortController,setTimeout,clearTimeout,fetch:()=>{calls++;return new Promise(r=>release=()=>r(Response.json({isAdmin:true})));}};
 vm.runInNewContext(read('session-request.js'),context);const request=context.window.PhoenixSessionRequest;
 const consumers=Array.from({length:4},()=>request().then(r=>r.json()));assert.equal(calls,1);release();assert.ok((await Promise.all(consumers)).every(x=>x.isAdmin));await request().then(r=>r.json());assert.equal(calls,1);events['phoenix-native-resume']();const renewed=request();assert.equal(calls,2);release();await renewed.then(r=>r.json());
 // The deadline covers the response body as well as the initial headers.
 let expire;const stalled={...context,window:{addEventListener:()=>{}},setTimeout:fn=>{expire=fn;return 1;},clearTimeout:()=>{},fetch:async(url,o)=>({ok:true,status:200,headers:{},text:()=>new Promise((resolve,reject)=>o.signal.addEventListener('abort',()=>reject(Error('body timeout'))))})};
 vm.runInNewContext(read('session-request.js'),stalled);const bodyRequest=stalled.window.PhoenixSessionRequest();await flush();expire();await assert.rejects(bodyRequest,/body timeout/);
 for(const native of [false,true]){
  const html=native?fs.readFileSync(path.join(__dirname,'../android/app/src/main/assets/phoenix-login.html'),'utf8'):read('login.html');
  const console=new VirtualConsole(),dom=new JSDOM(html,{url:'https://fixture.test/login',runScripts:'outside-only',virtualConsole:console}),w=dom.window,d=w.document,timers=[];let posts=0;
  w.setTimeout=(fn,ms)=>{timers.push({fn,ms});return timers.length;};w.clearTimeout=()=>{};w.fetch=async(url,o)=>{posts++;return new Promise((resolve,reject)=>o.signal.addEventListener('abort',()=>reject(new w.DOMException('timeout','AbortError'))));};
  w.eval(native?d.querySelector('script').textContent:read('login.js'));
  const form=d.querySelector('#loginForm');form.dispatchEvent(new w.Event('submit',{cancelable:true}));form.dispatchEvent(new w.Event('submit',{cancelable:true}));assert.equal(posts,1,'A stalled login is never duplicated');
  timers.find(t=>t.ms===12000).fn();await flush();assert.equal(d.querySelector('.submit').disabled,false);assert.match(d.getElementById('loginMessage').textContent,/طول کشید/);dom.window.close();
 }
 const dom=new JSDOM(read('phoenix.html'),{url:'https://fixture.test/phoenix',runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window,d=w.document,counts={};w.setInterval=()=>0;w.scrollTo=()=>{};w.ResizeObserver=class{observe(){}disconnect(){}};
 w.fetch=async url=>{counts[url]=(counts[url]||0)+1;let data={};if(url==='/api/auth/me')data={isAdmin:true};if(url==='/api/instruments')data={symbols:['BTCUSDT']};if(url.includes('/limits'))data={maximumLeverage:100};if(url==='/api/signals')data=[];if(url==='/api/signals/active-count')data={count:2};return {ok:true,json:async()=>data};};
 w.eval(read('app.js'));w.eval(read('home-tabs.js'));await flush();assert.equal(counts['/api/instruments']||0,0);assert.equal(counts['/api/signals']||0,0);assert.equal(d.getElementById('count').textContent,'۲');
 w.PhoenixNavigate('crypto',true,'section=manual&mode=form');await flush();assert.equal(counts['/api/instruments'],1);assert.equal(counts['/api/instruments/BTCUSDT/limits'],1);
 w.PhoenixNavigate('home');w.PhoenixNavigate('crypto',true,'section=manual&mode=form');await flush();assert.equal(counts['/api/instruments'],1,'Reopening the same form reuses its catalog');
 w.PhoenixNavigate('tracking');await flush();assert.equal(counts['/api/signals'],1,'The visible tracking list retains the original detailed endpoint');dom.window.close();
 console.log('PASS startup: coalesced identity, web/native login timeout and duplicate exclusion, deferred manual data, retained detailed tracking.');
})().catch(e=>{console.error(e);process.exitCode=1;});
