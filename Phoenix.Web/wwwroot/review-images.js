/* Page-lifetime image cache. Never persists charts or repeats a trading decision. */
(() => {
 'use strict';
 if(typeof URL.createObjectURL!=='function')return;
 const entries=new Map(),queue=[];let active=0,current=null;
 function trim(){for(const [key,e] of entries){if(entries.size<=12)break;if(e.url&&key!==current){URL.revokeObjectURL(e.url);entries.delete(key);}}}
 function drain(){while(active<2&&queue.length){const e=queue.shift();e.started=true;active++;const timer=setTimeout(()=>e.controller.abort(),8000);
  fetch(e.key,{cache:'default',credentials:'same-origin',signal:e.controller.signal}).then(async r=>{
   if(!r.ok)throw Error(r.status===401?'نشست ورود پایان یافته است.':'دریافت نمودار ناموفق بود.');
   const blob=await r.blob();if(!blob.type.startsWith('image/'))throw Error('پاسخ نمودار معتبر نیست.');
   if(entries.get(e.key)!==e||e.controller.signal.aborted)throw Error('cancelled');
   e.url=URL.createObjectURL(blob);e.resolve(e.url);trim();
  }).catch(error=>{if(entries.get(e.key)===e)entries.delete(e.key);e.reject(error);}).finally(()=>{clearTimeout(timer);active--;drain();});
 }}
 function get(key,priority=false){if(priority){if(current&&current!==key){const previous=entries.get(current);if(previous&&!previous.url)previous.controller.abort();}current=key;}let e=entries.get(key);if(e?.controller.signal.aborted){entries.delete(key);e=null;}if(e){if(priority){const i=queue.indexOf(e);if(i>=0){queue.splice(i,1);queue.unshift(e);}}return e.promise;}
  e={key,controller:new AbortController()};e.promise=new Promise((resolve,reject)=>{e.resolve=resolve;e.reject=reject;});entries.set(key,e);priority?queue.unshift(e):queue.push(e);if(priority&&active>=2){const background=[...entries.values()].find(x=>x.started&&!x.url&&x.key!==key&&!x.controller.signal.aborted);background?.controller.abort();}drain();return e.promise;
 }
 window.PhoenixReviewImages={invalidate:key=>{const e=entries.get(key);if(e){e.controller.abort();if(e.url)URL.revokeObjectURL(e.url);entries.delete(key);}},load:key=>get(key,true),prefetch:items=>{for(const item of items.slice(0,2))get(item.image).catch(()=>{});},clear:()=>{for(const e of entries.values()){e.controller.abort();if(e.url)URL.revokeObjectURL(e.url);}entries.clear();queue.splice(0).forEach(e=>e.reject(new Error('closed')));}};
 window.addEventListener('pagehide',event=>{if(!event.persisted)window.PhoenixReviewImages.clear();});
})();
