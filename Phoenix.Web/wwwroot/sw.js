const cacheName='phoenix-v5';
self.addEventListener('install',event=>{self.skipWaiting();event.waitUntil(caches.open(cacheName).then(cache=>cache.addAll(['/','/styles.css?v=3','/security.css?v=1','/history.css?v=1','/app.js?v=8'])))});
self.addEventListener('activate',event=>event.waitUntil(Promise.all([caches.keys().then(keys=>Promise.all(keys.filter(key=>key!==cacheName).map(key=>caches.delete(key)))),self.clients.claim()])));
self.addEventListener('fetch',event=>{if(event.request.method==='GET'&&!event.request.url.includes('/api/'))event.respondWith(fetch(event.request,{cache:'no-store'}).catch(()=>caches.match(event.request)))});
