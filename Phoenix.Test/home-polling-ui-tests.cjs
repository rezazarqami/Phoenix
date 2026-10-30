const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window,intervals=[],counts={};
 Object.defineProperty(w.document,'hidden',{value:false});w.setInterval=(fn,ms)=>{intervals.push({fn,ms});return 0;};
 let statusRelease,signalsRelease;
 w.fetch=async url=>{counts[url]=(counts[url]||0)+1;
  if(url==='/api/status')return new Promise(r=>{statusRelease=()=>r({ok:true,json:async()=>({})});});
  if(url==='/api/signals')return new Promise(r=>{signalsRelease=()=>r({ok:true,json:async()=>[]});});
  return {ok:true,json:async()=>url.startsWith('/api/history')?[]:{isAdmin:true,symbols:[]}};
 };
 w.eval(fs.readFileSync(path.join(root,'app.js'),'utf8'));await flush();
 for(const i of intervals.filter(i=>i.ms===1000||i.ms===5000)){i.fn();i.fn();}
 assert.equal(counts['/api/status'],1,'Slow status responses cannot accumulate parallel polls');assert.equal(counts['/api/signals'],1,'Slow signal responses cannot accumulate parallel polls');
 const historyUrl='/api/history?days=30&limit=1000&source=normal';assert.equal(counts[historyUrl],1);
 w.document.querySelector('#panel-home').hidden=true;intervals.find(i=>i.ms===30000).fn();assert.equal(counts[historyUrl],1,'A hidden home panel must not reload a thousand archive rows');
 statusRelease();signalsRelease();await flush();
 dom.window.close();console.log('PASS home polling: no overlapping slow requests and no hidden archive polling.');
})().catch(e=>{console.error(e);process.exitCode=1;});
