(function (scope) {
  "use strict";
  const DB_NAME = "personal-workspace-v2";
  let database, flight;
  const inFlightIds = new Set();
  class ApiError extends Error {
    constructor(message, status, current) {
      super(message);
      this.status = status;
      this.current = current;
    }
  }
  async function api(path, options = {}) {
    const controller = new AbortController(),
      timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch("/api/personal/" + path, {
        ...options,
        cache: "no-store",
        signal: controller.signal,
      });
      if (!response.ok) {
        let body;
        try {
          body = await response.json();
        } catch {}
        throw new ApiError(
          body?.error || "دریافت یا ذخیره انجام نشد.",
          response.status,
          body?.current,
        );
      }
      return response.status === 204 ? null : response.json();
    } finally {
      clearTimeout(timer);
    }
  }
  function open() {
    if (database) return database;
    database = new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        for (const name of ["records", "pending", "files"])
          db.createObjectStore(name, { keyPath: "id" });
        db.createObjectStore("meta", { keyPath: "key" });
      };
      request.onsuccess = () => {
        request.result.onversionchange = () => {
          request.result.close();
          database = null;
        };
        resolve(request.result);
      };
      request.onerror = () => {
        database = null;
        reject(request.error);
      };
    });
    return database;
  }
  async function read(name, key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(name),
        request =
          key === undefined
            ? transaction.objectStore(name).getAll()
            : transaction.objectStore(name).get(key);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }
  async function write(changes) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(
        [...new Set(changes.map((c) => c.store))],
        "readwrite",
      );
      for (const c of changes) {
        const store = transaction.objectStore(c.store);
        if (c.clear) store.clear();
        else if (c.remove !== undefined) store.delete(c.remove);
        else store.put(c.value);
      }
      transaction.oncomplete = resolve;
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () =>
        reject(transaction.error || Error("ذخیره در گوشی انجام نشد."));
    });
  }
  async function clear() {
    if (database) {
      const db = await database;
      db.close();
      database = null;
    }
    await new Promise((resolve, reject) => {
      const request = indexedDB.deleteDatabase(DB_NAME);
      request.onsuccess = resolve;
      request.onerror = () => reject(request.error);
      request.onblocked = () => {
        scope.dispatchEvent(new Event("personal-close-db"));
      };
    });
  }
  async function identity() {
    const response = await fetch("/api/auth/me", {
      cache: "no-store",
      signal: AbortSignal.timeout(10000),
    });
    if (!response.ok) {
      if (response.status === 401)
        throw new ApiError("ورود دوباره لازم است.", 401);
      throw new TypeError("سرور در دسترس نیست.");
    }
    let owner;
    try {
      owner = await response.json();
    } catch {
      throw new TypeError("پاسخ سرور در دسترس نیست.");
    }
    if (!owner.isAdmin) {
      const error = new ApiError("فضای شخصی متعلق به حساب اصلی است.", 403);
      error.roleDenied = true;
      throw error;
    }
    const previous = await read("meta", "owner");
    if (previous && previous.value !== owner.username) await clear();
    await write([
      { store: "meta", value: { key: "owner", value: owner.username } },
    ]);
    return owner;
  }
  function input(entry) {
    const {
      kind,
      date,
      title,
      category,
      amount,
      flow,
      notes,
      vehicle,
      odometer,
      details,
    } = entry;
    return {
      kind,
      date,
      title,
      category,
      amount,
      flow,
      notes,
      vehicle,
      odometer,
      details,
      clientId: entry.id,
    };
  }
  // Compare and write inside one IndexedDB transaction: a server response must
  // never erase an edit made between a pending-job read and its acknowledgement.
  async function withEntry(id, change) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(["records", "pending"], "readwrite");
      let value;
      tx.oncomplete = () => resolve(value);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error || Error("ذخیره در گوشی انجام نشد."));
      const pending = tx.objectStore("pending").get(id);
      pending.onsuccess = () => {
        const record = tx.objectStore("records").get(id);
        record.onsuccess = () => {
          value = change(pending.result, record.result, tx);
        };
      };
    });
  }
  async function save(values, existing) {
    const id = existing?.id || crypto.randomUUID();
    return withEntry(id, (pending, local, tx) => {
      const entry = {
        ...values,
        id,
        revision: local?.revision || existing?.revision || 1,
        createdAt:
          local?.createdAt || existing?.createdAt || new Date().toISOString(),
        updatedAt: existing ? new Date().toISOString() : null,
        _pending: true,
      };
      tx.objectStore("records").put(entry);
      tx.objectStore("pending").put({
        id,
        operationId: crypto.randomUUID(),
        action: pending?.action === "create" || !existing ? "create" : "update",
        entry: input(entry),
        revision: pending?.revision || entry.revision,
        blocked: false,
      });
      return entry;
    });
  }
  async function remove(entry) {
    return withEntry(entry.id, (pending, local, tx) => {
      tx.objectStore("records").delete(entry.id);
      if (pending?.action === "create" && !inFlightIds.has(entry.id))
        tx.objectStore("pending").delete(entry.id);
      else
        tx.objectStore("pending").put({
          id: entry.id,
          operationId: crypto.randomUUID(),
          action: "delete",
          revision: pending?.revision || local?.revision || entry.revision,
          blocked: false,
        });
    });
  }
  async function addFile(file) {
    if (file.size > 16 * 1024 * 1024)
      throw Error("هر فایل باید حداکثر ۱۶ مگابایت باشد.");
    const id = crypto.randomUUID();
    await write([
      {
        store: "files",
        value: {
          id,
          blob: file,
          name: file.name || "voice.webm",
          mime: file.type,
          uploaded: false,
        },
      },
    ]);
    return {
      id,
      name: file.name || "صدای ثبت‌شده",
      contentType: file.type,
      size: file.size,
    };
  }
  async function uploadFiles(ids) {
    for (const id of ids) {
      const file = await read("files", id);
      if (!file || file.uploaded || !file.blob) continue;
      await api("attachments/" + id, {
        method: "POST",
        headers: {
          "Content-Type": file.mime,
          "X-File-Name": encodeURIComponent(file.name),
        },
        body: file.blob,
      });
      await write([{ store: "files", value: { ...file, uploaded: true } }]);
    }
  }
  async function sync(onChange = () => {}) {
    if (flight) return flight;
    flight = (async () => {
      let pending = await read("pending");
      // Parents are sent before linked records even when several were created offline.
      const order = { category: -1, goal: 0, project: 1, habit: 1 };
      pending.sort(
        (a, b) =>
          (order[a.entry?.kind] ?? 2) - (order[b.entry?.kind] ?? 2) ||
          (a.entry?.kind === "category" && b.entry?.kind === "category"
            ? a.entry.title.split(" / ").length -
              b.entry.title.split(" / ").length
            : 0),
      );
      for (const job of pending) {
        if (job.blocked) continue;
        inFlightIds.add(job.id);
        try {
          let result;
          if (job.action === "delete") {
            try {
              await api("entries/" + job.id + "?revision=" + job.revision, {
                method: "DELETE",
              });
            } catch (error) {
              if (error.status !== 404) throw error;
            }
          } else {
            await uploadFiles(job.entry.details?.attachmentIds || []);
            result = await api(
              job.action === "create" ? "entries" : "entries/" + job.id,
              {
                method: job.action === "create" ? "POST" : "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(
                  job.action === "create"
                    ? job.entry
                    : { entry: job.entry, revision: job.revision },
                ),
              },
            );
          }
          await withEntry(job.id, (current, local, tx) => {
            if (current?.operationId === job.operationId) {
              tx.objectStore("pending").delete(job.id);
              if (result) tx.objectStore("records").put(result);
            } else if (current && result) {
              tx.objectStore("pending").put({
                ...current,
                action: current.action === "delete" ? "delete" : "update",
                revision: result.revision,
              });
              if (local)
                tx.objectStore("records").put({
                  ...local,
                  revision: result.revision,
                });
            }
          });
        } catch (error) {
          if (error.status === 401 || error.status === 403) throw error;
          if (error instanceof ApiError) {
            await withEntry(job.id, (latest, local, tx) => {
              if (latest?.operationId === job.operationId)
                tx.objectStore("pending").put({
                  ...latest,
                  blocked: true,
                  error: error.message,
                  current: error.current || null,
                });
            });
            onChange();
            continue;
          }
          throw error;
        } finally {
          inFlightIds.delete(job.id);
        }
      }
      const entries = await api("entries"),
        db = await open();
      await new Promise((resolve, reject) => {
        const tx = db.transaction(["records", "pending", "meta"], "readwrite");
        tx.oncomplete = resolve;
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
        const jobs = tx.objectStore("pending").getAll();
        jobs.onsuccess = () => {
          const protectedIds = new Set(jobs.result.map((j) => j.id)),
            records = tx.objectStore("records"),
            local = records.getAll();
          local.onsuccess = () => {
            for (const e of local.result)
              if (!protectedIds.has(e.id)) records.delete(e.id);
            for (const e of entries)
              if (!protectedIds.has(e.id)) records.put(e);
            tx.objectStore("meta").put({
              key: "lastSync",
              value: new Date().toISOString(),
            });
          };
        };
      });
      onChange();
    })().finally(() => {
      flight = null;
    });
    return flight;
  }
  async function resolve(id, choice) {
    const job = await read("pending", id);
    if (!job) return;
    if (choice === "server") {
      const changes = [{ store: "pending", remove: id }];
      if (job.current) changes.push({ store: "records", value: job.current });
      else changes.push({ store: "records", remove: id });
      await write(changes);
    } else {
      await write([
        {
          store: "pending",
          value: {
            ...job,
            blocked: false,
            error: null,
            operationId: crypto.randomUUID(),
            revision: job.current?.revision || job.revision,
          },
        },
      ]);
    }
  }
  async function fileUrl(id) {
    let file = await read("files", id);
    if (file?.blob)
      return {
        url: URL.createObjectURL(file.blob),
        mime: file.mime,
        name: file.name,
        local: true,
      };
    if (!file) {
      try {
        const meta = await api("attachments/" + id + "/meta");
        file = { ...meta, mime: meta.contentType, uploaded: true };
        await write([{ store: "files", value: file }]);
      } catch {}
    }
    return {
      url: "/api/personal/attachments/" + id,
      mime: file?.mime,
      name: file?.name,
      local: false,
    };
  }
  scope.addEventListener("personal-close-db", async () => {
    if (database) (await database).close();
    database = null;
  });
  scope.PersonalStorage = {
    api,
    ApiError,
    identity,
    read,
    write,
    save,
    remove,
    addFile,
    sync,
    resolve,
    fileUrl,
    clear,
    input,
    DB_NAME,
  };
})(window);
