// Cache the generic owner shell only after an authenticated owner explicitly opens it.
// Actual records and outbox live in IndexedDB; APIs and private media are never HTTP-cached.
const PERSONAL_CACHE = "personal-shell-v8";
const SHELL = [
  "/",
  "/personal.css?v=8",
  "/fonts/Vazirmatn-variable.woff2",
  "/orbit-simple.js?v=1",
  "/personal-core.js?v=3",
  "/personal-storage.js?v=3",
  "/personal.js?v=8",
  "/mobile.js?v=3",
];
async function clearPersonal() {
  await Promise.all(
    (await caches.keys())
      .filter(
        (name) =>
          name.startsWith("personal-shell-") || name.startsWith("phoenix-"),
      )
      .map((name) => caches.delete(name)),
  );
  const clients = await self.clients.matchAll();
  for (const client of clients) client.postMessage({ type: "PERSONAL_LOGOUT" });
  await new Promise((resolve) => {
    const request = indexedDB.deleteDatabase("personal-workspace-v2");
    request.onsuccess = resolve;
    request.onerror = resolve;
    request.onblocked = resolve;
  });
}
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) =>
  event.waitUntil(
    (async () => {
      await Promise.all(
        (await caches.keys())
          .filter(
            (name) =>
              (name.startsWith("personal-shell-") && name !== PERSONAL_CACHE) ||
              name.startsWith("phoenix-"),
          )
          .map((name) => caches.delete(name)),
      );
      await self.clients.claim();
    })(),
  ),
);
self.addEventListener("message", (event) => {
  if (event.data?.type === "PERSONAL_CLEAR") {
    event.waitUntil(clearPersonal());
    return;
  }
  if (event.data?.type !== "PERSONAL_CACHE_SHELL") return;
  event.waitUntil(
    (async () => {
      try {
        const identity = await fetch("/api/auth/me", { cache: "no-store" });
        if (!identity.ok || !(await identity.json()).isAdmin) return;
        const cache = await caches.open(PERSONAL_CACHE);
        for (const url of SHELL) {
          const response = await fetch(url, { cache: "no-store" });
          if (!response.ok || response.redirected) continue;
          if (
            url === "/" &&
            !(await response.clone().text()).includes(
              'data-personal-workspace="2"',
            )
          )
            continue;
          await cache.put(url, response);
        }
      } catch {}
    })(),
  );
});
self.addEventListener("fetch", (event) => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin) return;
  if (
    event.request.method === "POST" &&
    ["/api/auth/logout", "/api/analysis/auth/logout"].includes(url.pathname)
  ) {
    event.respondWith(
      (async () => {
        const response = await fetch(event.request);
        if (response.ok) await clearPersonal();
        return response;
      })(),
    );
    return;
  }
  if (event.request.method !== "GET" || url.pathname.startsWith("/api/"))
    return;
  const match =
    url.pathname === "/" || url.pathname === "/index.html"
      ? "/"
      : SHELL.find(
          (path) =>
            new URL(path, self.location.origin).pathname === url.pathname,
        );
  if (!match) return;
  event.respondWith(
    (async () => {
      try {
        const response = await fetch(event.request, { cache: "no-store" });
        if (response.status < 500 && response.status !== 403) return response;
        const cached = await (await caches.open(PERSONAL_CACHE)).match(match);
        return cached || response;
      } catch {
        const cached = await (await caches.open(PERSONAL_CACHE)).match(match);
        return (
          cached ||
          new Response("برای اولین استفاده به اینترنت متصل شوید.", {
            status: 503,
            headers: { "Content-Type": "text/plain; charset=utf-8" },
          })
        );
      }
    })(),
  );
});
