const assert = require("node:assert/strict"),
  fs = require("node:fs"),
  path = require("node:path");
const { JSDOM, VirtualConsole } = require("./ui/node_modules/jsdom");
const { IDBFactory } = require("./ui/node_modules/fake-indexeddb");
const assets = path.join(__dirname, "../Phoenix.Web/wwwroot"),
  html = fs.readFileSync(path.join(assets, "index.html"), "utf8"),
  C = require(path.join(assets, "personal-core.js"));
const factory = new IDBFactory(),
  rows = new Map();
let offline = false,
  delayCreate = null,
  createStarted = null;
const pause = () => new Promise((resolve) => setTimeout(resolve, 5));
async function waitFor(check, message) {
  for (let i = 0; i < 200; i++) {
    if (await check()) return;
    await pause();
  }
  throw Error(message || "Condition did not settle.");
}
const response = (value, status = 200) =>
  new Response(status === 204 ? null : JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
async function mockFetch(url, options = {}) {
  if (offline) throw new TypeError("Network unavailable");
  const pathname = String(url).split("?")[0],
    method = options.method || "GET";
  if (pathname === "/api/auth/me")
    return response({ username: "fixture-owner", isAdmin: true });
  if (pathname === "/api/personal/entries" && method === "GET")
    return response([...rows.values()]);
  if (pathname === "/api/personal/entries" && method === "POST") {
    const input = JSON.parse(options.body);
    if (delayCreate) {
      createStarted?.();
      await delayCreate;
      delayCreate = null;
    }
    const previous = rows.get(input.clientId);
    if (previous) return response(previous);
    const row = {
      ...input,
      id: input.clientId,
      createdAt: new Date().toISOString(),
      revision: 1,
    };
    delete row.clientId;
    rows.set(row.id, row);
    return response(row);
  }
  if (pathname.startsWith("/api/personal/entries/")) {
    const id = pathname.split("/")[4],
      previous = rows.get(id);
    if (method === "PUT") {
      if (!previous) return response({}, 404);
      const update = JSON.parse(options.body);
      if (previous.revision !== update.revision)
        return response(
          { error: "نسخه تغییر کرده است.", current: previous },
          409,
        );
      const row = {
        ...update.entry,
        id,
        revision: previous.revision + 1,
        createdAt: previous.createdAt,
      };
      delete row.clientId;
      rows.set(id, row);
      return response(row);
    }
    if (method === "DELETE") {
      rows.delete(id);
      return response(null, 204);
    }
    if (pathname.endsWith("/history")) return response([]);
  }
  return response({ error: "Unknown fixture route" }, 404);
}
async function boot() {
  const errors = [],
    virtual = new VirtualConsole();
  virtual.on("jsdomError", (error) => {
    if (!error.message.includes("Not implemented: navigation"))
      errors.push(error);
  });
  const dom = new JSDOM(html, {
    url: "http://localhost/",
    runScripts: "outside-only",
    pretendToBeVisual: true,
    virtualConsole: virtual,
  });
  const w = dom.window;
  w.indexedDB = factory;
  w.fetch = mockFetch;
  w.Response = Response;
  w.confirm = () => true;
  w.scrollTo = () => {};
  w.print = () => {};
  w.URL.createObjectURL = () => "blob:fixture";
  w.URL.revokeObjectURL = () => {};
  if (!w.AbortSignal.timeout)
    w.AbortSignal.timeout = AbortSignal.timeout.bind(AbortSignal);
  w.HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  w.HTMLDialogElement.prototype.close = function () {
    this.removeAttribute("open");
    this.dispatchEvent(new w.Event("close"));
  };
  for (const name of ["personal-core.js", "personal-storage.js", "personal.js"])
    w.eval(fs.readFileSync(path.join(assets, name), "utf8"));
  await waitFor(
    () => w.document.querySelector("#importantTasks").children.length,
    "Initial UI did not render.",
  );
  return { dom, w, errors };
}
function field(w, name, value) {
  const input = w.document.querySelector(`#editorForm [name="${name}"]`);
  assert(input, "Missing field " + name);
  if (input.type === "checkbox") input.checked = !!value;
  else input.value = value;
  input.dispatchEvent(new w.Event("change", { bubbles: true }));
  return input;
}
async function submit(w) {
  const form = w.document.querySelector("#editorForm");
  assert(
    form.checkValidity(),
    "Editor inputs invalid: " +
      [...form.elements]
        .filter((e) => e.validity && !e.validity.valid)
        .map((e) => e.name)
        .join(","),
  );
  form.dispatchEvent(
    new w.Event("submit", { bubbles: true, cancelable: true }),
  );
  await waitFor(
    () => !w.document.querySelector("#editorDialog").open,
    "Editor did not close: " +
      w.document.querySelector("#editorError").textContent,
  );
}
async function openNew(w, kind) {
  const button = w.document.querySelector(`[data-new="${kind}"]`);
  assert(button, "Missing new button " + kind);
  button.click();
  await waitFor(() => w.document.querySelector("#editorDialog").open);
}
async function run() {
  let { dom, w, errors } = await boot();
  const $ = (selector) => w.document.querySelector(selector);
  for (const page of ["records", "path", "experience", "review", "today"]) {
    $(`[data-page="${page}"]`).click();
    assert.equal($("#page-" + page).hidden, false);
  }
  await openNew(w, "goal");
  field(w, "title", "هدف آزمایشی");
  field(w, "d.expectedOutcome", "معیار موفقیت");
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "goal"));
  const goal = [...rows.values()].find((e) => e.kind === "goal");
  await openNew(w, "project");
  field(w, "title", "پروژه آزمایشی");
  field(w, "d.goalId", goal.id);
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "project"));
  await openNew(w, "checkin");
  field(w, "title", "وضعیت روز");
  field(w, "d.energy", "4");
  field(w, "d.focus", "3");
  field(w, "d.activities", "پیاده‌روی، کارگاه");
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "checkin"));
  const checkin = [...rows.values()].find((e) => e.kind === "checkin");
  assert.equal(typeof checkin.details.energy, "number");
  assert.equal(checkin.details.energy, 4);
  assert.deepEqual(checkin.details.activities, ["پیاده‌روی", "کارگاه"]);
  $("#quickButton").click();
  $("#captureText").value =
    "روغن ماشین کیلومتر ۸۴۵۰۰ بود و یک میلیون و هشتصد هزار تومان شد";
  $("#parseCapture").click();
  await waitFor(() => $("#editorDialog").open);
  assert.equal($('#editorForm [name="amount"]').value, "1800000");
  assert.equal($('#editorForm [name="odometer"]').value, "84500");
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "vehicle"));
  $("#newExperience").click();
  await waitFor(() => $("#editorDialog").open);
  field(w, "title", "تصمیم نمونه");
  field(w, "d.hypothesis", "دلیل اولیه");
  field(w, "d.expectedOutcome", "انتظار اولیه");
  field(w, "d.reviewDate", C.iso());
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "decision"));
  $('[data-page="experience"]').click();
  const decisionCard = $("#experienceList .record");
  [...decisionCard.querySelectorAll("button")]
    .find((b) => b.textContent === "مرور نتیجه")
    .click();
  await waitFor(() => $("#editorDialog").open);
  assert.equal($('#editorForm [name="d.hypothesis"]').disabled, true);
  field(w, "d.actualOutcome", "نتیجهٔ واقعی");
  await submit(w);
  await waitFor(
    () =>
      [...rows.values()].find((e) => e.kind === "decision").details
        .actualOutcome === "نتیجهٔ واقعی",
  );
  assert.equal(
    [...rows.values()].find((e) => e.kind === "decision").details.hypothesis,
    "دلیل اولیه",
  );
  $('[data-kind="craft"]').click();
  $("#newExperience").click();
  await waitFor(() => $("#editorDialog").open);
  field(w, "title", "تجربهٔ ساخت");
  field(w, "d.method", "روش ساخت آزمایشی");
  field(w, "d.goalId", goal.id);
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "craft"));
  $('[data-kind="experiment"]').click();
  $("#newExperience").click();
  await waitFor(() => $("#editorDialog").open);
  field(w, "title", "آزمایش تمرکز");
  field(w, "d.hypothesis", "شروع کار بدون پیام‌ها");
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "experiment"));
  assert($("#experienceList").textContent.includes("دادهٔ مقایسه کم است"));
  $('[data-page="review"]').click();
  assert($("#reportMetrics").textContent.includes("۱٬۸۰۰٬۰۰۰"));
  $("#weeklyReview").click();
  await waitFor(() => $("#editorDialog").open);
  field(w, "d.expectedOutcome", "قدم کوچک هفتهٔ بعد");
  await submit(w);
  await waitFor(() => [...rows.values()].some((e) => e.kind === "review"));
  // Persist offline, close the app, reopen on the same device, then synchronize exactly once.
  offline = true;
  await openNew(w, "journal");
  field(w, "title", "یادداشت آفلاین");
  field(w, "notes", "با قطع اینترنت محفوظ می‌ماند");
  await submit(w);
  assert((await w.PersonalStorage.read("pending")).length > 0);
  await w.PersonalStorage.read("meta", "owner");
  w.dispatchEvent(new w.Event("personal-close-db"));
  dom.window.close();
  ({ dom, w, errors } = await boot());
  assert(
    (await w.PersonalStorage.read("records")).some(
      (e) => e.title === "یادداشت آفلاین",
    ),
  );
  offline = false;
  await w.PersonalStorage.sync();
  assert.equal(
    [...rows.values()].filter((e) => e.title === "یادداشت آفلاین").length,
    1,
  );
  // An edit while its create is in flight must not be overwritten by the older response.
  const initial = await w.PersonalStorage.save({
    kind: "journal",
    title: "ایجاد همزمان",
    date: C.iso(),
    notes: "قدیمی",
    category: "",
    details: { ...C.defaults },
    amount: null,
    flow: null,
    vehicle: "",
    odometer: null,
  });
  let release, started;
  delayCreate = new Promise((resolve) => (release = resolve));
  const createSignal = new Promise((resolve) => (started = resolve));
  createStarted = started;
  const syncing = w.PersonalStorage.sync();
  await createSignal;
  await w.PersonalStorage.save(
    { ...w.PersonalStorage.input(initial), notes: "جدید" },
    initial,
  );
  release();
  await syncing;
  assert.equal(
    (await w.PersonalStorage.read("records", initial.id)).notes,
    "جدید",
  );
  await w.PersonalStorage.sync();
  assert.equal(rows.get(initial.id).notes, "جدید");
  assert.equal(rows.get(initial.id).revision, 2);
  createStarted = null;
  // Deleting a create already in flight must enqueue a compensating delete, never resurrect it.
  const deleting = await w.PersonalStorage.save({
    kind: "journal",
    title: "حذف حین ارسال",
    date: C.iso(),
    notes: "",
    category: "",
    details: { ...C.defaults },
    amount: null,
    flow: null,
    vehicle: "",
    odometer: null,
  });
  let releaseDelete, deleteStarted;
  delayCreate = new Promise((resolve) => (releaseDelete = resolve));
  const deleteSignal = new Promise((resolve) => (deleteStarted = resolve));
  createStarted = deleteStarted;
  const sendingDelete = w.PersonalStorage.sync();
  await deleteSignal;
  await w.PersonalStorage.remove(deleting);
  releaseDelete();
  await sendingDelete;
  assert.equal(await w.PersonalStorage.read("records", deleting.id), undefined);
  await w.PersonalStorage.sync();
  assert.equal(rows.has(deleting.id), false);
  createStarted = null;
  assert.deepEqual(errors, []);
  await w.PersonalStorage.clear();
  dom.window.close();
  console.log(
    "PASS personal UI: every page, linked goal/project, numeric check-ins, reviewed capture, decision provenance, craft, experiments, weekly review, offline restart and in-flight edit/delete preservation.",
  );
}
run().catch((error) => {
  console.error(error.stack);
  process.exit(1);
});
