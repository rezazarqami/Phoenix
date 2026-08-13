const cacheName='phoenix-v4';
self.addEventListener('install',e=>e.waitUntil(caches.open(cacheName).then(c=>c.addAll(['/','/styles.css','/security.css','/history.css','/app.js']))));
self.addEventListener('fetch',e=>{if(e.request.method==='GET'&&!e.request.url.includes('/api/'))e.respondWith(fetch(e.request).catch(()=>caches.match(e.request)));});
