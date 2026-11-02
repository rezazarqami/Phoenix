const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
(async()=>{
 const pending=[],calls=[],handlers={},revoked=[];let serial=0;
 const window={addEventListener:(name,fn)=>handlers[name]=fn};
 const context={window,AbortController,setTimeout,clearTimeout,URL:{createObjectURL:()=>`blob:${++serial}`,revokeObjectURL:u=>revoked.push(u)},fetch:(key,options)=>{
  calls.push({key,options});return new Promise((resolve,reject)=>{const fail=()=>reject(Error('aborted'));if(options.signal.aborted)return fail();options.signal.addEventListener('abort',fail,{once:true});pending.push({key,resolve,reject});});
 }};
 vm.runInNewContext(fs.readFileSync(__dirname+'/../Phoenix.Web/wwwroot/review-images.js','utf8'),context);
 const cache=window.PhoenixReviewImages,flush=async()=>{await new Promise(r=>setImmediate(r));};
 const respond=(key,status=200,type='image/png')=>pending.find(x=>x.key===key).resolve({ok:status===200,status,blob:async()=>({type})});
 const a=cache.load('/a');cache.prefetch([{image:'/b'},{image:'/c'}]);assert.equal(calls.length,2,'At most two image requests run concurrently');
 respond('/a');assert.equal(await a,'blob:1');await flush();assert.equal(calls.length,3);
 respond('/b');respond('/c');await flush();assert.equal(await cache.load('/b'),'blob:2');assert.equal(calls.length,3,'Next chart uses prefetched bytes');
 const slow=cache.load('/slow').catch(()=>null);const next=cache.load('/next');await slow;await flush();respond('/next');await next;
 const denied=cache.load('/denied');respond('/denied',401);await assert.rejects(denied);await flush();
 const retry=cache.load('/denied');pending.filter(x=>x.key==='/denied').at(-1).resolve({ok:true,blob:async()=>({type:'image/png'})});await retry;
 const invalid=cache.load('/html');respond('/html',200,'text/html');await assert.rejects(invalid);await flush();
 handlers.pagehide({persisted:true});const before=calls.length;await cache.load('/denied');assert.equal(calls.length,before,'Back-forward cache retains valid object URLs');
 for(let i=0;i<16;i++){const key='/bounded'+i,p=cache.load(key);respond(key);await p;await flush();}
 assert.ok(revoked.length>=6,'Old image memory is bounded');assert.ok(calls.every(c=>!c.options.method),'Images never repeat a trading POST');
 handlers.pagehide({persisted:false});assert.ok(revoked.length>=16);
 console.log('PASS chart transport: bounded prefetch, cached next chart, abort, authentication/non-image failures, retry, BFCache and cleanup.');
})().catch(e=>{console.error(e);process.exitCode=1;});
