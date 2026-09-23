const cacheName='phoenix-v6';
self.addEventListener('install',event=>{self.skipWaiting();event.waitUntil(caches.open(cacheName).then(cache=>cache.addAll(['/','/styles.css?v=5','/security.css?v=2','/history.css?v=1','/app.js?v=11','/install-phoenix.js?v=1','/manifest.webmanifest','/icons/phoenix-192.png','/icons/phoenix-512.png','/icons/phoenix-maskable-512.png'])))});
self.addEventListener('activate',event=>event.waitUntil(Promise.all([caches.keys().then(keys=>Promise.all(keys.filter(key=>key!==cacheName).map(key=>caches.delete(key)))),self.clients.claim()])));
self.addEventListener('fetch',event=>{if(event.request.method==='GET'&&!event.request.url.includes('/api/'))event.respondWith(fetch(event.request,{cache:'no-store'}).catch(()=>caches.match(event.request)))});
