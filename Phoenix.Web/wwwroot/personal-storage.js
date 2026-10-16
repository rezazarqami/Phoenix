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
  async function save(values, existing) {
    const id = existing?.id || crypto.randomUUID(),
      pending = await read("pending", id);
    const entry = {
      ...values,
      id,
      revision: existing?.revision || 1,
      createdAt: existing?.createdAt || new Date().toISOString(),
      updatedAt: existing ? new Date().toISOString() : null,
      _pending: true,
    };
    const operation = {
      id,
      operationId: crypto.randomUUID(),
      action: pending?.action === "create" || !existing ? "create" : "update",
      entry: input(entry),
      revision: pending?.revision || entry.revision,
      blocked: false,
    };
    await write([
      { store: "records", value: entry },
      { store: "pending", value: operation },
    ]);
    return entry;
  }
  async function remove(entry) {
    const pending = await read("pending", entry.id);
    if (pending?.action === "create" && !inFlightIds.has(entry.id)) {
      await write([
        { store: "records", remove: entry.id },
        { store: "pending", remove: entry.id },
      ]);
      return;
    }
    await write([
      { store: "records", remove: entry.id },
      {
        store: "pending",
        value: {
          id: entry.id,
          operationId: crypto.randomUUID(),
          action: "delete",
          revision: pending?.revision || entry.revision,
          blocked: false,
        },
      },
    ]);
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
        (a, b) => (order[a.entry?.kind] ?? 2) - (order[b.entry?.kind] ?? 2) || (a.entry?.kind === "category" && b.entry?.kind === "category" ? a.entry.title.split(" / ").length - b.entry.title.split(" / ").length : 0),
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
          const current = await read("pending", job.id);
          if (current?.operationId === job.operationId) {
            const changes = [{ store: "pending", remove: job.id }];
            if (result) changes.push({ store: "records", value: result });
            await write(changes);
          } else if (current && result) {
            const local = await read("records", job.id);
            const changes = [
              {
                store: "pending",
                value: {
                  ...current,
                  action: current.action === "delete" ? "delete" : "update",
                  revision: result.revision,
                },
              },
            ];
            if (local)
              changes.push({
                store: "records",
                value: { ...local, revision: result.revision },
              });
            await write(changes);
          }
        } catch (error) {
          if (error.status === 401 || error.status === 403) throw error;
          if (error instanceof ApiError) {
            const latest = await read("pending", job.id);
            if (latest?.operationId === job.operationId)
              await write([
                {
                  store: "pending",
                  value: {
                    ...job,
                    blocked: true,
                    error: error.message,
                    current: error.current || null,
                  },
                },
              ]);
            onChange();
            continue;
          }
          throw error;
        } finally {
          inFlightIds.delete(job.id);
        }
      }
      const entries = await api("entries"),
        jobs = await read("pending"),
        local = await read("records"),
        protectedIds = new Set(jobs.map((j) => j.id));
      const changes = local
        .filter((e) => !protectedIds.has(e.id))
        .map((e) => ({ store: "records", remove: e.id }));
      for (const e of entries)
        if (!protectedIds.has(e.id))
          changes.push({ store: "records", value: e });
      changes.push({
        store: "meta",
        value: { key: "lastSync", value: new Date().toISOString() },
      });
      await write(changes);
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
