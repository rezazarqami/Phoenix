// Private pages and API data must never survive logout in an offline cache.
self.addEventListener('install',()=>self.skipWaiting());
self.addEventListener('activate',event=>event.waitUntil(Promise.all([caches.keys().then(keys=>Promise.all(keys.filter(key=>key.startsWith('phoenix-')).map(key=>caches.delete(key)))),self.clients.claim()])));
