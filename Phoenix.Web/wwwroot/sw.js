// Phoenix is network-first. Do not retain private pages in HTTP caches.
self.addEventListener('install',()=>self.skipWaiting());
self.addEventListener('activate',event=>event.waitUntil((async()=>{
 for(const name of await caches.keys())if(name.startsWith('personal-shell-')||name.startsWith('phoenix-'))await caches.delete(name);
 await self.clients.claim();
})()));
