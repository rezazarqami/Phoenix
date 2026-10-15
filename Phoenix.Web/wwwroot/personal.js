(function () {
  "use strict";
  const C = PersonalCore,
    S = PersonalStorage,
    $ = (selector) => document.querySelector(selector),
    all = (selector) => Array.from(document.querySelectorAll(selector)),
    nf = new Intl.NumberFormat("fa-IR", { maximumFractionDigits: 2 });
  let entries = [],
    currentPage = "today",
    experienceKind = "decision",
    visibleCount = 30,
    editing = null,
    editorKind = "journal",
    editorFiles = [],
    captureFiles = [],
    online = false,
    syncing = false,
    recorder = null,
    recordStream = null,
    recognition = null,
    reportData = null,
    reportFrom,
    reportTo,
    mediaUrls = [];
  const statusNames = {
    active: "فعال",
    todo: "برای انجام",
    done: "انجام‌شده",
    waiting: "منتظر",
    cancelled: "کنارگذاشته",
    archived: "بایگانی",
  };
  const labels = {
    hypothesis: "دلیل / فرضیه",
    alternatives: "گزینه‌های دیگر",
    expectedOutcome: "انتظار اولیه",
    actualOutcome: "نتیجهٔ واقعی",
    reviewDate: "زمان مرور",
    reviewedDate: "تاریخ مرور",
    materials: "مواد و ابزار",
    method: "روش",
    observation: "مشاهده",
    conclusion: "نتیجه و آموخته",
    durationMinutes: "زمان فعالیت (دقیقه)",
    sleepHours: "خواب (ساعت)",
    mood: "حال روز",
    energy: "انرژی",
    focus: "تمرکز",
    sleepQuality: "کیفیت خواب",
    baselineFrom: "شروع بازهٔ قبل",
    baselineTo: "پایان بازهٔ قبل",
    trialFrom: "شروع آزمایش",
    trialTo: "پایان آزمایش",
    dueDate: "زمان هدف",
    goalId: "هدف",
    projectId: "پروژه",
  };
  const fields = {
    task: [
      ["dueTime", "زمان قرار", "time"],
      ["important", "جزو سه کار مهم روز", "checkbox"],
      ["status", "وضعیت", "status"],
      ["goalId", "هدف مرتبط", "goal"],
      ["projectId", "پروژهٔ مرتبط", "project"],
    ],
    goal: [
      ["expectedOutcome", "معیار رسیدن به هدف", "textarea", true],
      ["dueDate", "زمان مورد انتظار", "date"],
      ["status", "وضعیت", "status"],
    ],
    project: [
      ["goalId", "هدف مرتبط", "goal"],
      ["expectedOutcome", "نتیجهٔ مورد انتظار", "textarea"],
      ["dueDate", "زمان پایان", "date"],
      ["status", "وضعیت", "status"],
    ],
    habit: [
      ["goalId", "هدف مرتبط", "goal"],
      ["cadence", "دورهٔ تکرار", "cadence"],
      ["target", "چند روز در هر دوره؟", "number", true, 1, 7],
      ["status", "وضعیت", "status"],
    ],
    checkin: [
      ["mood", "حال روز", "rating"],
      ["energy", "انرژی", "rating"],
      ["focus", "تمرکز", "rating"],
      ["sleepQuality", "کیفیت خواب", "rating"],
      ["sleepHours", "مدت خواب (ساعت)", "number", false, 0, 24],
      ["activities", "فعالیت‌ها؛ با ویرگول جدا کن", "activities"],
    ],
    decision: [
      ["hypothesis", "چرا این تصمیم را گرفتم؟", "textarea", true],
      ["alternatives", "چه گزینه‌های دیگری داشتم؟", "textarea"],
      ["expectedOutcome", "انتظارم از نتیجه چیست؟", "textarea", true],
      ["reviewDate", "چه زمانی نتیجه را مرور کنم؟", "date", true],
      ["actualOutcome", "نتیجهٔ واقعی و چیزی که یاد گرفتم", "textarea"],
      ["reviewedDate", "تاریخ بررسی نتیجه", "date"],
      ["goalId", "هدف مرتبط", "goal"],
      ["projectId", "پروژهٔ مرتبط", "project"],
    ],
    craft: [
      ["materials", "مواد و ابزار مورد استفاده", "textarea"],
      ["method", "چه روشی را آزمایش کردم؟", "textarea", true],
      ["observation", "چه اتفاقی افتاد؟", "textarea"],
      ["conclusion", "نتیجه و چیزی که یاد گرفتم", "textarea"],
      ["goalId", "هدف مرتبط", "goal"],
      ["projectId", "پروژهٔ مرتبط", "project"],
    ],
    time: [
      ["durationMinutes", "مدت فعالیت (دقیقه)", "number", true, 1, 1440],
      ["goalId", "هدف مرتبط", "goal"],
      ["projectId", "پروژهٔ مرتبط", "project"],
    ],
    experiment: [
      [
        "hypothesis",
        "چه تغییری را امتحان می‌کنم و انتظارم چیست؟",
        "textarea",
        true,
      ],
      ["metric", "شاخص مقایسه", "metric"],
      ["baselineFrom", "شروع بازهٔ قبل", "date", true],
      ["baselineTo", "پایان بازهٔ قبل", "date", true],
      ["trialFrom", "شروع آزمایش", "date", true],
      ["trialTo", "پایان آزمایش", "date", true],
      ["observation", "تفاوت شرایط و ملاحظات", "textarea"],
      ["conclusion", "نتیجهٔ مرور شخصی", "textarea"],
      ["goalId", "هدف مرتبط", "goal"],
      ["status", "وضعیت", "status"],
    ],
    review: [
      ["observation", "این دوره چه چیزی جلو رفت؟", "textarea"],
      ["method", "چه چیزی مانع شد یا عقب افتاد؟", "textarea"],
      ["conclusion", "چه تجربه‌ای را می‌خواهم حفظ کنم؟", "textarea"],
      ["expectedOutcome", "قدم کوچک دورهٔ بعد چیست؟", "textarea", true],
      ["baselineFrom", "شروع دورهٔ مرور", "date", true],
      ["baselineTo", "پایان دورهٔ مرور", "date", true],
    ],
  };
  function el(tag, text, cls) {
    const n = document.createElement(tag);
    if (text !== undefined) n.textContent = text;
    if (cls) n.className = cls;
    return n;
  }
  function button(text, handler, cls = "quiet") {
    const b = el("button", text, cls);
    b.type = "button";
    b.addEventListener("click", handler);
    return b;
  }
  function dateLabel(date, short = false) {
    if (!date) return "—";
    try {
      return new Intl.DateTimeFormat(
        "fa-IR",
        short ? { month: "numeric", day: "numeric" } : { dateStyle: "medium" },
      ).format(new Date(date + "T12:00:00"));
    } catch {
      return date;
    }
  }
  function notify(text, error = false) {
    $("#message").textContent = text;
    $("#message").hidden = !text;
    $("#message").classList.toggle("error", error);
  }
  function fail(error) {
    notify(error.message || "عملیات انجام نشد.", true);
  }
  function empty(
    text = "هنوز چیزی ثبت نکرده‌ای.",
    hint = "از دکمهٔ افزودن یا ثبت سریع شروع کن.",
  ) {
    const n = el("div", text, "empty");
    if (hint) n.append(el("p", hint));
    return n;
  }
  function metric(label, value, unit, extra) {
    const n = el("div", undefined, "metric");
    n.append(
      el("small", label),
      el("strong", nf.format(value), "metric-value"),
      el("span", unit, "unit"),
    );
    if (extra) n.append(el("small", extra, "comparison"));
    return n;
  }
  function linkedTitle(id) {
    return entries.find((e) => e.id === id)?.title || "";
  }
  function sortRecords(list) {
    return list
      .slice()
      .sort(
        (a, b) =>
          b.date.localeCompare(a.date) ||
          b.createdAt.localeCompare(a.createdAt),
      );
  }
  function searchMatches(entry, query) {
    const d = C.details(entry);
    return C.normalize(
      [
        entry.title,
        entry.notes,
        entry.category,
        entry.vehicle,
        ...Object.values(d).filter((v) => typeof v === "string"),
      ].join(" "),
    )
      .toLowerCase()
      .includes(C.normalize(query).toLowerCase());
  }
  function showPage(page) {
    currentPage = page;
    all(".page").forEach((n) => (n.hidden = n.id !== "page-" + page));
    all("#mainNav [data-page]").forEach((n) => {
      if (n.dataset.page === page) n.setAttribute("aria-current", "page");
      else n.removeAttribute("aria-current");
    });
    render();
    document.body.dataset.page = page;
    const heading = $("#page-" + page + " h1");
    if (heading) { heading.tabIndex = -1; heading.focus({ preventScroll: true }); }
    window.scrollTo({ top: 0, behavior: window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth" });
  }
  async function loadLocal() {
    entries = await S.read("records");
    entries = sortRecords(entries);
    render();
    await syncStatus();
  }
  async function syncStatus() {
    const jobs = await S.read("pending"),
      blocked = jobs.filter((j) => j.blocked);
    $("#syncDot").className = online && !jobs.length ? "online" : "pending";
    $("#syncLabel").textContent = syncing
      ? "در حال همگام‌سازی"
      : blocked.length
        ? nf.format(blocked.length) + " مورد نیازمند بررسی"
        : jobs.length
          ? nf.format(jobs.length) + " ثبت در انتظار"
          : online
            ? "همگام‌شده"
            : "روی این گوشی";
    $("#offlineBanner").hidden = online;
  }
  async function synchronize() {
    if (syncing) return;
    syncing = true;
    await syncStatus();
    try {
      await S.identity();
      online = true;
      await S.sync(() => loadLocal().catch(fail));
      await loadLocal();
    } catch (error) {
      online = false;
      if (error.status === 401) {
        $("#mainContent").hidden = true;
        notify(
          "برای ادامه، دوباره وارد حساب اصلی شو. ثبت‌های ارسال‌نشده نگه داشته شده‌اند.",
          true,
        );
        $("#message").append(
          button("ورود دوباره", () => {
            location.href = "/login";
          }),
        );
      } else if (error.status === 403 && error.roleDenied) {
        await S.clear();
        location.replace("/phoenix");
      } else if (error.status === 403) {
        $("#mainContent").hidden = true;
        notify(
          "سرور دسترسی به ثبت‌ها را نپذیرفت؛ داده‌های ارسال‌نشده روی گوشی حفظ شده‌اند.",
          true,
        );
      } else if (error instanceof S.ApiError) fail(error);
    } finally {
      syncing = false;
      await syncStatus();
    }
  }
  async function persist(values, existing) {
    if (
      values.kind === "task" &&
      values.details?.important &&
      values.details.status !== "cancelled" &&
      entries.filter(
        (e) =>
          e.kind === "task" &&
          e.id !== existing?.id &&
          e.date === values.date &&
          C.details(e).important &&
          C.details(e).status !== "cancelled",
      ).length >= 3
    )
      throw Error("برای این روز سه کار مهم انتخاب کرده‌ای. یکی را تغییر بده.");
    const entry = await S.save(values, existing);
    await loadLocal();
    notify("ثبت روی این گوشی ذخیره شد.");
    synchronize();
    return entry;
  }
  async function update(entry, changes) {
    const input = S.input(entry),
      d = C.details(entry);
    if (
      changes.details?.status === "done" &&
      entry.kind === "task" &&
      d.status !== "done"
    )
      changes.details.completedDate = C.iso();
    if (
      changes.details?.status &&
      changes.details.status !== "done" &&
      entry.kind === "task"
    )
      changes.details.completedDate = null;
    return persist(
      { ...input, ...changes, details: { ...d, ...changes.details } },
      entry,
    );
  }
  async function deleteRecord(entry) {
    if (!confirm("این ثبت به حذف‌شده‌ها منتقل شود؟")) return;
    await S.remove(entry);
    await loadLocal();
    notify("ثبت حذف شد. پس از همگام‌سازی از تنظیمات قابل بازیابی است.");
    synchronize();
  }
  function recordCard(entry, options = {}) {
    const d = C.details(entry),
      n = el("article", undefined, "record"),
      heading = el("div", undefined, "record-heading"),
      title = el("div");
    n.dataset.kind = entry.kind;
    title.append(el("h3", entry.title));
    const meta = el("div", undefined, "record-meta");
    meta.append(
      el("span", dateLabel(entry.date)),
      el("span", C.titles[entry.kind] || entry.kind),
    );
    if (entry.category) meta.append(el("span", entry.category));
    if (entry._pending) meta.append(el("span", "در انتظار ارسال", "tag gold"));
    if (["task", "goal", "project", "habit", "experiment"].includes(entry.kind))
      meta.append(el("span", statusNames[d.status] || d.status, "tag"));
    title.append(meta);
    heading.append(title);
    if (entry.amount != null)
      heading.append(
        el(
          "span",
          (entry.flow === "income" ? "+ " : "") +
            nf.format(entry.amount) +
            " تومان",
          "amount",
        ),
      );
    n.append(heading);
    if (entry.notes)
      n.append(
        el(
          "p",
          entry.notes.slice(0, 220) + (entry.notes.length > 220 ? "…" : ""),
          "record-notes",
        ),
      );
    const info = [];
    if (entry.vehicle) info.push(entry.vehicle);
    if (entry.odometer != null)
      info.push(nf.format(entry.odometer) + " کیلومتر");
    if (d.goalId) info.push("هدف: " + linkedTitle(d.goalId));
    if (d.projectId) info.push("پروژه: " + linkedTitle(d.projectId));
    if (d.durationMinutes != null)
      info.push(nf.format(d.durationMinutes) + " دقیقه");
    if (info.length) n.append(el("p", info.join(" · "), "record-meta"));
    const actions = el("div", undefined, "record-actions");
    actions.append(
      button("جزئیات", () => openDetail(entry)),
      button("ویرایش", () => openEditor(entry.kind, entry)),
    );
    if (entry.kind === "decision")
      actions.append(
        button("مرور نتیجه", () =>
          openEditor("decision", entry, { review: true }),
        ),
      );
    if (["goal", "project", "habit", "experiment"].includes(entry.kind)) {
      if (d.status !== "done")
        actions.append(
          button("پایان", () =>
            update(entry, { details: { status: "done" } }).catch(fail),
          ),
        );
      actions.append(
        button(d.status === "archived" ? "فعال‌کردن" : "بایگانی", () =>
          update(entry, {
            details: {
              status: d.status === "archived" ? "active" : "archived",
            },
          }).catch(fail),
        ),
      );
    }
    if (!options.noDelete)
      actions.append(button("حذف", () => deleteRecord(entry).catch(fail)));
    n.append(actions);
    return n;
  }
  function fillList(target, list, options) {
    target.replaceChildren(
      ...(list.length ? list.map((e) => recordCard(e, options)) : [empty()]),
    );
  }
  function taskRow(entry) {
    const d = C.details(entry),
      row = el(
        "div",
        undefined,
        "task-row" + (d.status === "done" ? " done" : ""),
      ),
      info = el("div", undefined, "task-info");
    const done = button(
      d.status === "done" ? "✓" : "○",
      () =>
        update(entry, {
          details: { status: d.status === "done" ? "todo" : "done" },
        }).catch(fail),
      "check-button" + (d.status === "done" ? " completed" : ""),
    );
    done.setAttribute(
      "aria-label",
      d.status === "done"
        ? "بازگرداندن کار " + entry.title
        : "انجام کار " + entry.title,
    );
    info.append(
      el("div", entry.title, "task-title"),
      el(
        "small",
        [
          d.dueTime || dateLabel(entry.date),
          statusNames[d.status],
          d.important ? "مهم" : "",
        ]
          .filter(Boolean)
          .join(" · "),
      ),
    );
    row.append(
      done,
      info,
      button("ویرایش", () => openEditor("task", entry), "small-button"),
    );
    return row;
  }
  function habitRow(habit) {
    const progress = C.habitProgress(habit, entries),
      todayLog = entries.find(
        (e) =>
          e.kind === "habit-log" &&
          C.details(e).parentId === habit.id &&
          e.date === C.iso(),
      ),
      n = el("div", undefined, "habit-row"),
      row = el("div", undefined, "habit-heading"),
      text = el("div");
    text.append(
      el("h3", habit.title),
      el(
        "small",
        `${nf.format(progress.count)} از ${nf.format(progress.target)} روز · ${C.details(habit).cadence === "day" ? "امروز" : "هفتهٔ جاری"}`,
      ),
    );
    const b = button(
      todayLog ? "✓ انجام شد" : "امروز انجام دادم",
      async () => {
        b.disabled = true;
        try {
          if (todayLog) await S.remove(todayLog);
          else
            await S.save({
              kind: "habit-log",
              date: C.iso(),
              title: habit.title,
              category: "",
              notes: "",
              amount: null,
              flow: null,
              vehicle: "",
              odometer: null,
              details: { ...C.defaults, parentId: habit.id, status: "done" },
            });
          await loadLocal();
          synchronize();
        } catch (error) {
          fail(error);
        } finally {
          b.disabled = false;
        }
      },
      "small-button",
    );
    row.append(text, b);
    const bar = el("progress", undefined, "habit-progress");
    bar.max = progress.target;
    bar.value = progress.count;
    bar.setAttribute("aria-label", "پیشرفت " + habit.title);
    n.append(row, bar);
    return n;
  }
  function renderDayStrip() {
    const today = C.iso();
    $("#dayStrip").replaceChildren(...Array.from({ length: 7 }, (_, index) => {
      const day = C.shift(today, index - 6), date = new Date(day + "T12:00:00");
      const count = entries.filter(e => e.date === day).length;
      const b = button("", () => {
        $("#recordFrom").value = day; $("#recordTo").value = day;
        $("#recordSearch").value = ""; $("#recordKind").value = "all";
        visibleCount = 30; showPage("records");
      }, "day-cell" + (day === today ? " is-today" : ""));
      b.append(el("small", new Intl.DateTimeFormat("fa-IR", { weekday: "short" }).format(date)),
        el("strong", new Intl.DateTimeFormat("fa-IR", { day: "numeric" }).format(date)),
        el("span", count ? "•" : "·", "day-dot"));
      b.setAttribute("aria-label", dateLabel(day) + "، " + nf.format(count) + " ثبت؛ نمایش ثبت‌ها");
      if (day === today) b.setAttribute("aria-current", "date");
      return b;
    }));
  }
  function renderToday() {
    const today = C.iso(),
      tasks = entries.filter(
        (e) =>
          e.kind === "task" &&
          e.date === today &&
          C.details(e).status !== "cancelled",
      ),
      important = tasks.filter((e) => C.details(e).important);
    const completedImportant = important.filter(e => C.details(e).status === "done").length;
    $("#orbitProgress").textContent = nf.format(completedImportant) + " / " + nf.format(important.length);
    $("#heroSummary").textContent = important.length
      ? (completedImportant === important.length ? "کارهای مهم امروزت انجام شد؛ برای خودت هم وقت بگذار." : nf.format(important.length - completedImportant) + " کار مهم مانده؛ قدم بعدی را خودت انتخاب کن.")
      : "جا برای کارهای مهم، لحظه‌های کوچک و ایده‌های تازه.";
    renderDayStrip();
    $("#importantTasks").replaceChildren(
      ...(important.length
        ? important.map(taskRow)
        : [
            empty(
              "سه کار مهم روزت را انتخاب کن.",
              "یک کار اضافه کن و گزینهٔ «مهم» را بزن.",
            ),
          ]),
    );
    const other = tasks.filter((e) => !C.details(e).important);
    $("#todayTasks").replaceChildren(
      ...(other.length
        ? other.map(taskRow)
        : [el("p", "کار دیگری برای امروز ثبت نشده است.")]),
    );
    const overdue = entries.filter(
      (e) =>
        e.kind === "task" &&
        e.date < today &&
        ["todo", "waiting", "active"].includes(C.details(e).status),
    );
    $("#overdueCount").textContent = "(" + nf.format(overdue.length) + ")";
    $("#overdueTasks").replaceChildren(...overdue.map(taskRow));
    const checkin = entries.find(
      (e) => e.kind === "checkin" && e.date === today,
    );
    $("#checkinButton").textContent = checkin ? "ویرایش وضعیت" : "ثبت وضعیت";
    if (checkin) {
      const grid = el("div", undefined, "checkin-values"),
        d = C.details(checkin);
      for (const name of ["energy", "focus", "mood"]) {
        const n = el("div", undefined, "checkin-value");
        n.append(
          el("strong", d[name] == null ? "—" : nf.format(d[name])),
          el("small", labels[name]),
        );
        grid.append(n);
      }
      $("#todayCheckin").replaceChildren(
        grid,
        el("p", checkin.notes || "وضعیت امروز ثبت شده است."),
      );
    } else
      $("#todayCheckin").replaceChildren(
        empty("امروز چطور گذشت؟", "انرژی، تمرکز، خواب و یک جملهٔ کوتاه."),
      );
    const habits = entries.filter(
      (e) => e.kind === "habit" && C.details(e).status === "active",
    );
    $("#todayHabits").replaceChildren(
      ...(habits.length
        ? habits.map(habitRow)
        : [el("p", "هنوز عادتی تعریف نکرده‌ای.")]),
    );
    const due = entries.filter(
      (e) =>
        e.kind === "decision" &&
        C.details(e).reviewDate &&
        C.details(e).reviewDate <= today &&
        !C.details(e).reviewedDate,
    );
    fillList($("#dueDecisions"), due);
    const data = C.report(entries, today, today);
    $("#todayMetrics").replaceChildren(
      metric("هزینهٔ ثبت‌شدهٔ امروز", data.totals.expenses, "تومان"),
      metric("زمان ثبت‌شدهٔ امروز", data.totals.minutes, "دقیقه"),
      metric(
        "کارهای انجام‌شده",
        entries.filter(
          (e) =>
            e.kind === "task" &&
            C.details(e).status === "done" &&
            (C.details(e).completedDate || e.date) === today,
        ).length,
        "کار",
      ),
      metric(
        "عادت‌های کامل این دوره",
        habits.filter((e) => C.habitProgress(e, entries).done).length,
        "عادت",
      ),
    );
    renderTimer();
  }
  function renderTimer() {
    const host = $("#timerHost"),
      running = entries.find(
        (e) =>
          e.kind === "time" &&
          C.details(e).startedAt &&
          !C.details(e).endedAt &&
          C.details(e).status !== "cancelled",
      );
    if (running) {
      const row = el("div", undefined, "timer"),
        d = C.details(running),
        text = el("div", undefined, "timer-name");
      text.append(
        el("h3", running.title),
        el(
          "small",
          [linkedTitle(d.goalId), linkedTitle(d.projectId)]
            .filter(Boolean)
            .join(" · "),
        ),
      );
      const clock = el("span", "00:00:00", "elapsed");
      clock.id = "runningClock";
      row.append(
        text,
        clock,
        button(
          "پایان فعالیت",
          async () => {
            try {
              const elapsed = Math.min(
                1440,
                (Date.now() - new Date(d.startedAt)) / 60000,
              );
              await update(running, {
                details: {
                  endedAt: new Date(
                    Math.min(
                      Date.now(),
                      new Date(d.startedAt).getTime() + 86400000,
                    ),
                  ).toISOString(),
                  durationMinutes: Math.max(0, Math.round(elapsed * 100) / 100),
                  status: "done",
                },
              });
            } catch (error) {
              fail(error);
            }
          },
          "primary",
        ),
      );
      host.replaceChildren(row);
      tickTimer();
    } else {
      const row = el("div", undefined, "timer"),
        title = el("label", "روی چه کاری تمرکز می‌کنی؟"),
        input = el("input");
      input.id = "timerTitle";
      input.maxLength = 200;
      input.placeholder = "مثلاً تمرین کوک";
      title.append(input);
      const label = el("label", "پروژهٔ مرتبط"),
        select = el("select");
      select.id = "timerProject";
      select.append(new Option("بدون پروژه", ""));
      entries
        .filter((e) => e.kind === "project" && C.details(e).status === "active")
        .forEach((e) => select.append(new Option(e.title, e.id)));
      label.append(select);
      row.append(
        title,
        label,
        button(
          "شروع زمان‌سنج",
          async () => {
            if (!input.value.trim()) {
              notify("عنوان فعالیت را وارد کن.", true);
              input.focus();
              return;
            }
            try {
              const project = entries.find((e) => e.id === select.value);
              await persist({
                kind: "time",
                date: C.iso(),
                title: input.value.trim(),
                category: "",
                notes: "",
                amount: null,
                flow: null,
                vehicle: "",
                odometer: null,
                details: {
                  ...C.defaults,
                  startedAt: new Date().toISOString(),
                  durationMinutes: null,
                  projectId: select.value || null,
                  goalId: project ? C.details(project).goalId || null : null,
                },
              });
            } catch (error) {
              fail(error);
            }
          },
          "primary",
        ),
      );
      host.replaceChildren(row);
    }
  }
  function tickTimer() {
    const clock = $("#runningClock"),
      running = entries.find(
        (e) =>
          e.kind === "time" &&
          C.details(e).startedAt &&
          !C.details(e).endedAt &&
          C.details(e).status !== "cancelled",
      );
    if (!clock || !running) return;
    let seconds = Math.max(
      0,
      Math.floor((Date.now() - new Date(C.details(running).startedAt)) / 1000),
    );
    const hh = Math.floor(seconds / 3600),
      mm = Math.floor((seconds % 3600) / 60),
      ss = seconds % 60;
    clock.textContent = [hh, mm, ss]
      .map((v) => String(v).padStart(2, "0"))
      .join(":");
  }
  function renderRecords() {
    const query = $("#recordSearch").value,
      kind = $("#recordKind").value,
      from = $("#recordFrom").value,
      to = $("#recordTo").value;
    const filtered = sortRecords(
      entries.filter(
        (e) =>
          e.kind !== "habit-log" &&
          (kind === "all" || e.kind === kind) &&
          (!from || e.date >= from) &&
          (!to || e.date <= to) &&
          searchMatches(e, query),
      ),
    );
    $("#recordCount").textContent = nf.format(filtered.length) + " ثبت";
    fillList($("#recordList"), filtered.slice(0, visibleCount));
    $("#moreRecords").hidden = filtered.length <= visibleCount;
  }
  function renderPath() {
    const archive = $("#showArchived").checked;
    for (const [kind, id] of [
      ["goal", "goalList"],
      ["project", "projectList"],
    ])
      fillList(
        $("#" + id),
        entries.filter(
          (e) =>
            e.kind === kind && (archive || C.details(e).status !== "archived"),
        ),
      );
    const habits = entries.filter(
      (e) =>
        e.kind === "habit" && (archive || C.details(e).status !== "archived"),
    );
    $("#habitList").replaceChildren(
      ...(habits.length
        ? habits.map((h) => {
            const card = recordCard(h);
            card.append(habitRow(h));
            return card;
          })
        : [empty()]),
    );
  }
  function renderExperience() {
    const hints = {
      decision:
        "دلیل و انتظار اولیه پس از ثبت حفظ می‌شود. نتیجه را در «مرور نتیجه» اضافه کن.",
      craft:
        "روش، مواد، مشاهده و نتیجه را نگه دار؛ برای مقایسه، عکس و صدای قبل و بعد را پیوست کن.",
      experiment:
        "یک تغییر مشخص، دو بازهٔ جدا و یک شاخص انتخاب کن. نتیجه از وضعیت‌های روزانهٔ ثبت‌شده محاسبه می‌شود.",
    };
    $("#experienceHint").textContent = hints[experienceKind];
    $("#newExperience").textContent = "＋ " + C.titles[experienceKind];
    const list = entries.filter(
      (e) =>
        e.kind === experienceKind &&
        searchMatches(e, $("#experienceSearch").value),
    );
    $("#experienceList").replaceChildren(
      ...(list.length
        ? list.map((e) => {
            const card = recordCard(e);
            if (e.kind === "experiment") card.append(experimentView(e));
            return card;
          })
        : [empty()]),
    );
  }
  function experimentView(entry) {
    const r = C.experiment(entry, entries),
      d = C.details(entry),
      host = el("div"),
      grid = el("div", undefined, "experiment-comparison");
    for (const [label, average, count, total, from, to] of [
      [
        "قبل",
        r.before,
        r.baselineCount,
        r.baselineDays,
        d.baselineFrom,
        d.baselineTo,
      ],
      [
        "دورهٔ تغییر",
        r.after,
        r.trialCount,
        r.trialDays,
        d.trialFrom,
        d.trialTo,
      ],
    ]) {
      const n = el("div");
      n.append(
        el("small", label + " · " + (labels[d.metric] || d.metric)),
        el("strong", average == null ? "—" : nf.format(average)),
        el("small", `${nf.format(count)} روز ثبت از ${nf.format(total)} روز`),
        el("small", dateLabel(from) + " تا " + dateLabel(to)),
      );
      grid.append(n);
    }
    host.append(
      grid,
      el(
        "p",
        r.difference == null
          ? "برای مقایسه، در هر دو بازه دادهٔ همین شاخص لازم است."
          : "تفاوت میانگین ثبت‌شده: " + nf.format(r.difference),
        "note",
      ),
      el(
        "small",
        r.enoughData
          ? "این مقایسه توصیفی است؛ تفاوت شرایط می‌تواند نتیجه را تغییر دهد."
          : "دادهٔ مقایسه کم است؛ حداقل پنج روز از هر بازه برای مرور اولیه در نظر گرفته شده است.",
      ),
    );
    if (r.recordIds.length)
      host.append(
        button(
          "مشاهدهٔ ثبت‌های مبنا",
          () => showSources(r.recordIds),
          "small-button",
        ),
      );
    return host;
  }
  function setupReportRange() {
    const today = C.iso(),
      choice = $("#reportRange").value;
    let from = reportFrom || C.weekStart(today),
      to = today;
    if (choice === "week") from = C.weekStart(today);
    else if (choice === "lastweek") {
      from = C.shift(C.weekStart(today), -7);
      to = C.shift(from, 6);
    } else if (choice === "month") from = C.persianMonthStart(today);
    else {
      from = $("#reportFrom").value || from;
      to = $("#reportTo").value || today;
    }
    $("#reportFrom").value = from;
    $("#reportTo").value = to;
    reportFrom = from;
    reportTo = to;
  }
  function renderReport() {
    if (!reportFrom) setupReportRange();
    if (reportFrom > reportTo || C.days(reportFrom, reportTo) > 367) {
      notify("بازهٔ گزارش باید معتبر و حداکثر یک‌ساله باشد.", true);
      return;
    }
    reportData = C.report(entries, reportFrom, reportTo);
    const r = reportData,
      t = r.totals,
      p = r.previous;
    $("#reportMetrics").replaceChildren(
      metric(
        "درآمد ثبت‌شده",
        t.income,
        "تومان",
        "دورهٔ قبل: " + nf.format(p.income),
      ),
      metric(
        "هزینهٔ ثبت‌شده",
        t.expenses,
        "تومان",
        "دورهٔ قبل: " + nf.format(p.expenses),
      ),
      metric(
        "زمان فعالیت",
        t.minutes,
        "دقیقه",
        "دورهٔ قبل: " + nf.format(p.minutes),
      ),
      metric(
        "روزهای ثبت وضعیت",
        t.checkinDays,
        "از " + nf.format(t.calendarDays) + " روز",
        "روزهای بدون ثبت در میانگین‌ها وارد نمی‌شوند",
      ),
    );
    renderTrend();
    $("#categoryChart").replaceChildren(
      ...(r.categories.length
        ? r.categories.map((row) => {
            const n = el("div", undefined, "category-row"),
              top = el("div");
            top.append(
              el("span", row.name),
              el("span", nf.format(row.amount) + " تومان"),
            );
            const progress = el("progress");
            progress.max = t.expenses;
            progress.value = row.amount;
            progress.setAttribute("aria-label", row.name);
            n.append(
              top,
              progress,
              button(
                "ثبت‌ها (" + nf.format(row.count) + ")",
                () => showSources(row.recordIds),
                "small-button",
              ),
            );
            return n;
          })
        : [empty("هزینه‌ای در این بازه ثبت نشده است.", "")]),
    );
    const observations = [];
    if (t.completedTasks)
      observations.push(
        `${nf.format(t.completedTasks)} کار در این دوره انجام‌شده ثبت شده است.`,
      );
    if (r.overdueTasks.length)
      observations.push(
        `${nf.format(r.overdueTasks.length)} کار تا پایان این دوره هنوز باز یا منتظر است.`,
      );
    if (r.reviewsDue.length)
      observations.push(
        `${nf.format(r.reviewsDue.length)} تصمیم موعد مرور دارد و نتیجه‌اش ثبت نشده است.`,
      );
    const inactive = r.projects.filter(
      (e) => e.status === "active" && e.recordCount === 0,
    );
    if (inactive.length)
      observations.push(
        `${nf.format(inactive.length)} هدف یا پروژهٔ فعال در این دوره ثبت مرتبط ندارد.`,
      );
    if (r.craft.length)
      observations.push(
        `${nf.format(r.craft.length)} تجربهٔ سازسازی ثبت کرده‌ای.`,
      );
    if (!observations.length)
      observations.push(
        "با ثبت کارها، وضعیت روزانه و تجربه‌ها، مرور این دوره کامل‌تر می‌شود.",
      );
    $("#reportObservations").replaceChildren(
      ...observations.map((text) => {
        const n = el("div", undefined, "observation");
        n.append(el("p", text));
        return n;
      }),
      button(
        "دیدن ثبت‌های این دوره",
        () => showSources(r.recordIds),
        "small-button",
      ),
    );
    $("#activityReport").replaceChildren(
      ...(r.activities.length
        ? r.activities.map((a) => {
            const n = el("div", undefined, "record");
            n.append(
              el("h3", a.activity),
              el(
                "p",
                `تمرکز با این فعالیت: ${a.withFocus == null ? "—" : nf.format(a.withFocus)} (${nf.format(a.withCount)} روز) · بدون این فعالیت: ${a.withoutFocus == null ? "—" : nf.format(a.withoutFocus)} (${nf.format(a.withoutCount)} روز)`,
              ),
              el(
                "small",
                a.enoughData
                  ? "برای مشاهدهٔ الگو، ثبت‌های هر دو گروه موجود است؛ سایر شرایط هم مهم‌اند."
                  : "دادهٔ کافی برای مقایسه نداریم؛ حداقل پنج روز از هر گروه لازم است.",
              ),
            );
            return n;
          })
        : [empty("هنوز فعالیت و تمرکز قابل مقایسه ثبت نکرده‌ای.", "")]),
    );
    $("#projectReport").replaceChildren(
      ...(r.projects.length
        ? r.projects.map((p) => {
            const n = el("div", undefined, "record");
            n.append(
              el("h3", p.title),
              el(
                "p",
                `${nf.format(p.minutes)} دقیقه · ${nf.format(p.completedTasks)} کار انجام‌شده · ${nf.format(p.recordCount)} ثبت مرتبط`,
              ),
            );
            if (p.recordIds.length)
              n.append(
                button(
                  "ثبت‌های مرتبط",
                  () => showSources(p.recordIds),
                  "small-button",
                ),
              );
            return n;
          })
        : [empty("هنوز هدف یا پروژه‌ای تعریف نشده است.", "")]),
    );
    fillList(
      $("#reviewList"),
      entries.filter((e) => e.kind === "review"),
    );
  }
  function renderTrend() {
    const host = $("#trendChart"),
      metric = $("#chartMetric").value,
      rows = reportData?.chart || [],
      valid = rows.filter((r) => r[metric] != null);
    host.replaceChildren();
    if (!valid.length) {
      host.append(empty("برای این شاخص داده‌ای ثبت نشده است.", ""));
      return;
    }
    const width = Math.max(280, host.clientWidth || 400),
      height = Math.round(width / 1.61803398875),
      pad = { left: 55, right: 21, top: 21, bottom: 55 },
      w = width - pad.left - pad.right,
      h = height - pad.top - pad.bottom,
      max = Math.max(
        metric === "focus" || metric === "energy" ? 5 : 1,
        ...valid.map((r) => r[metric]),
      ),
      x = (i) =>
        pad.left + (rows.length === 1 ? w / 2 : (i * w) / (rows.length - 1)),
      y = (v) => pad.top + h - (v / max) * h,
      ns = "http://www.w3.org/2000/svg",
      svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("class", "trend");
    svg.setAttribute("role", "img");
    svg.setAttribute(
      "aria-label",
      `نمودار ${labels[metric] || "زمان فعالیت"} از ${dateLabel(reportFrom)} تا ${dateLabel(reportTo)}`,
    );
    const svgEl = (tag, attrs, text) => {
      const n = document.createElementNS(ns, tag);
      for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
      if (text) n.textContent = text;
      return n;
    };
    for (let j = 0; j <= 3; j++) {
      const v = (max * j) / 3;
      svg.append(
        svgEl("line", {
          x1: pad.left,
          x2: width - pad.right,
          y1: y(v),
          y2: y(v),
          class: "grid",
        }),
        svgEl(
          "text",
          { x: pad.left - 8, y: y(v) + 4, "text-anchor": "end" },
          nf.format(Math.round(v * 10) / 10),
        ),
      );
    }
    let path = "",
      gap = true;
    rows.forEach((r, i) => {
      if (r[metric] == null) {
        gap = true;
        return;
      }
      path +=
        (gap ? "M" : "L") +
        x(i).toFixed(1) +
        "," +
        y(r[metric]).toFixed(1) +
        " ";
      gap = false;
    });
    svg.append(svgEl("path", { d: path }));
    rows.forEach((r, i) => {
      if (r[metric] == null) return;
      const circle = svgEl("circle", { cx: x(i), cy: y(r[metric]), r: 3 });
      circle.append(
        svgEl("title", {}, dateLabel(r.date) + " · " + nf.format(r[metric])),
      );
      svg.append(circle);
    });
    const ticks = [
      ...new Set([
        0,
        Math.round((rows.length - 1) / 3),
        Math.round(((rows.length - 1) * 2) / 3),
        rows.length - 1,
      ]),
    ];
    for (const i of ticks)
      svg.append(
        svgEl(
          "text",
          {
            x: x(i),
            y: height - 14,
            "text-anchor":
              i === 0 ? "start" : i === rows.length - 1 ? "end" : "middle",
          },
          dateLabel(rows[i].date, true),
        ),
      );
    host.append(svg);
    const detail = el("details", undefined, "disclosure");
    detail.append(el("summary", "دیدن مقدارهای نمودار"));
    for (const r of valid)
      detail.append(
        el(
          "p",
          dateLabel(r.date) +
            " · " +
            nf.format(r[metric]) +
            " " +
            (metric === "minutes"
              ? "دقیقه"
              : metric === "sleepHours"
                ? "ساعت"
                : "از ۵"),
        ),
      );
    host.append(detail);
  }
  function render() {
    if (currentPage === "today") renderToday();
    if (currentPage === "records") renderRecords();
    if (currentPage === "path") renderPath();
    if (currentPage === "experience") renderExperience();
    if (currentPage === "review") renderReport();
  }
  function formField(name, label, type, value, required = false, min, max) {
    const wrap = el("label", label, type === "textarea" ? "wide" : undefined);
    let input;
    if (
      [
        "status",
        "cadence",
        "rating",
        "goal",
        "project",
        "metric",
        "kind",
      ].includes(type)
    ) {
      input = el("select");
      let options;
      if (type === "status")
        options =
          editorKind === "task"
            ? [
                ["todo", "برای انجام"],
                ["done", "انجام‌شده"],
                ["waiting", "منتظر"],
                ["cancelled", "کنارگذاشته"],
              ]
            : [
                ["active", "فعال"],
                ["done", "انجام‌شده"],
                ["archived", "بایگانی"],
                ["cancelled", "کنارگذاشته"],
              ];
      if (type === "cadence")
        options = [
          ["week", "هفتگی"],
          ["day", "روزانه"],
        ];
      if (type === "rating")
        options = [
          ["", "ثبت نشده"],
          [1, "۱ · خیلی کم"],
          [2, "۲ · کم"],
          [3, "۳ · متوسط"],
          [4, "۴ · خوب"],
          [5, "۵ · خیلی خوب"],
        ];
      if (type === "goal" || type === "project")
        options = [
          ["", "بدون ارتباط"],
          ...entries.filter((e) => e.kind === type).map((e) => [e.id, e.title]),
        ];
      if (type === "metric")
        options = [
          ["focus", "تمرکز"],
          ["energy", "انرژی"],
          ["mood", "حال روز"],
          ["sleepHours", "خواب (ساعت)"],
        ];
      if (type === "kind")
        options = Object.entries(C.titles).filter(
          ([key]) => key !== "habit-log",
        );
      for (const [k, v] of options) input.append(new Option(v, k));
      input.value = value ?? "";
    } else if (type === "textarea") {
      input = el("textarea");
      input.rows = 3;
      input.maxLength = 10000;
      input.value = value || "";
    } else {
      input = el("input");
      input.type = type === "activities" ? "text" : type;
      input.value = type === "checkbox" ? "" : (value ?? "");
      if (type === "checkbox") {
        wrap.className = "inline-check";
        input.checked = !!value;
      }
      if (type === "number") {
        input.min = min ?? 0;
        input.max = max ?? 1e15;
        input.step = name === "odometer" || name === "d.target" ? "1" : "0.01";
        input.inputMode = "decimal";
      }
      if (type === "text" || type === "activities")
        input.maxLength = name === "title" ? 200 : 1000;
    }
    input.name = name;
    input.required = required;
    wrap.append(input);
    return wrap;
  }
  function editorFields(values) {
    const host = $("#editorFields"),
      d = C.details(values);
    host.replaceChildren();
    if (!editing) {
      const kindField = formField("kind", "نوع ثبت", "kind", editorKind, true);
      kindField.querySelector("select").addEventListener("change", (event) => {
        const current = readForm();
        editorKind = event.target.value;
        current.kind = editorKind;
        current.details = {
          ...C.details(current),
          status: editorKind === "task" ? "todo" : "active",
        };
        $("#editorTitle").textContent = C.titles[editorKind];
        editorFields(current);
      });
      host.append(kindField);
    }
    host.append(
      formField("title", "عنوان", "text", values.title || "", true),
      formField(
        "date",
        editorKind === "task" ? "تاریخ انجام / قرار" : "تاریخ",
        "date",
        values.date || C.iso(),
        true,
      ),
      formField("category", "دسته‌بندی", "text", values.category || ""),
    );
    if (["finance", "vehicle"].includes(editorKind))
      host.append(
        formField(
          "amount",
          "مبلغ (تومان)",
          "number",
          values.amount,
          editorKind === "finance",
          editorKind === "finance" ? 0.01 : 0,
          1e15,
        ),
      );
    if (editorKind === "finance") {
      const label = el("label", "نوع تراکنش"),
        select = el("select");
      select.name = "flow";
      select.append(
        new Option("هزینه", "expense"),
        new Option("درآمد", "income"),
      );
      select.value = values.flow || "expense";
      label.append(select);
      host.append(label);
    }
    if (editorKind === "vehicle")
      host.append(
        formField("vehicle", "نام خودرو", "text", values.vehicle || ""),
        formField(
          "odometer",
          "کیلومتر خودرو",
          "number",
          values.odometer,
          false,
          0,
          2147483647,
        ),
      );
    for (const [name, label, type, required, min, max] of fields[editorKind] ||
      []) {
      const value = type === "activities" ? d.activities.join("، ") : d[name];
      const field = formField(
        "d." + name,
        label,
        type,
        value,
        required,
        min,
        max,
      );
      if (
        editing &&
        editorKind === "decision" &&
        [
          "hypothesis",
          "alternatives",
          "expectedOutcome",
          "reviewDate",
        ].includes(name)
      ) {
        field.querySelector("input,textarea,select").disabled = true;
        field.append(el("small", "جزئیات اولیهٔ تصمیم حفظ می‌شود."));
      }
      if (editing && editorKind === "time" && d.startedAt) {
        field.querySelector("input,textarea,select").disabled =
          name === "durationMinutes";
        field.querySelector("input,textarea,select").required = false;
      }
      host.append(field);
    }
    if (editing && editorKind === "decision")
      host.querySelector('[name="date"]').readOnly = true;
    host.append(
      formField("notes", "یادداشت و جزئیات", "textarea", values.notes || ""),
    );
    if (editorKind === "habit")
      host
        .querySelector('[name="d.cadence"]')
        .addEventListener("change", (event) => {
          const target = host.querySelector('[name="d.target"]');
          target.max = event.target.value === "day" ? 1 : 7;
          if (Number(target.value) > Number(target.max))
            target.value = target.max;
        });
  }
  function readForm() {
    const form = $("#editorForm"),
      data = Object.fromEntries(new FormData(form)),
      base = editing ? S.input(editing) : {},
      d = { ...C.details(base) };
    for (const input of form.querySelectorAll('[name^="d."]')) {
      if (input.disabled) continue;
      const name = input.name.slice(2);
      d[name] =
        input.type === "checkbox"
          ? input.checked
          : input.type === "number" ||
              ["mood", "energy", "focus", "sleepQuality"].includes(name)
            ? input.value === ""
              ? null
              : Number(input.value)
            : name === "activities"
              ? input.value
                  .split(/[,،]/)
                  .map((x) => x.trim())
                  .filter(Boolean)
              : input.value || null;
    }
    for (const k of [
      "alternatives",
      "hypothesis",
      "expectedOutcome",
      "actualOutcome",
      "materials",
      "method",
      "observation",
      "conclusion",
    ])
      d[k] = d[k] || "";
    d.attachmentIds = editorFiles.map((f) => f.id);
    if (editorKind === "task") {
      if (d.status === "done") d.completedDate = d.completedDate || C.iso();
      else d.completedDate = null;
    }
    return {
      ...base,
      kind: editorKind,
      title: data.title?.trim() || "",
      date: data.date || C.iso(),
      category: data.category?.trim() || "",
      notes: data.notes?.trim() || "",
      amount:
        ["finance", "vehicle"].includes(editorKind) &&
        data.amount !== "" &&
        data.amount !== undefined
          ? Number(data.amount)
          : null,
      flow: editorKind === "finance" ? data.flow : null,
      vehicle: editorKind === "vehicle" ? data.vehicle || "" : "",
      odometer:
        editorKind === "vehicle" && data.odometer !== ""
          ? Number(data.odometer)
          : null,
      details: d,
    };
  }
  async function openEditor(kind, entry = null, options = {}) {
    if ($("#detailDialog").open) $("#detailDialog").close();
    editing = entry;
    editorKind = kind;
    editorFiles = (
      entry
        ? C.details(entry).attachmentIds
        : options.files?.map((f) => f.id) || []
    ).map(
      (id) => options.files?.find((f) => f.id === id) || { id, name: "پیوست" },
    );
    let values = entry ||
      options.values || {
        kind,
        date: C.iso(),
        details: { ...C.defaults, status: kind === "task" ? "todo" : "active" },
      };
    if (!entry && kind === "experiment")
      values.details = {
        ...C.details(values),
        baselineFrom: C.shift(C.iso(), -7),
        baselineTo: C.shift(C.iso(), -1),
        trialFrom: C.iso(),
        trialTo: C.shift(C.iso(), 6),
      };
    if (!entry && kind === "review")
      values = {
        ...values,
        title:
          "مرور " +
          dateLabel(reportFrom || C.weekStart(C.iso())) +
          " تا " +
          dateLabel(reportTo || C.iso()),
        category: "مرور شخصی",
        details: {
          ...C.details(values),
          baselineFrom: reportFrom || C.weekStart(C.iso()),
          baselineTo: reportTo || C.iso(),
        },
      };
    if (options.review)
      values = {
        ...entry,
        details: { ...C.details(entry), reviewedDate: C.iso() },
      };
    $("#editorTitle").textContent = (entry ? "ویرایش " : "") + C.titles[kind];
    $("#editorError").textContent = "";
    editorFields(values);
    await renderMedia($("#editorAttachments"), editorFiles, true);
    $("#editorDialog").showModal();
  }
  async function renderMedia(host, files, editable = false) {
    host.replaceChildren();
    for (const file of files) {
      const media = await S.fileUrl(file.id),
        item = el("div", undefined, "media-item");
      if (media.local) mediaUrls.push(media.url);
      const isAudio = (media.mime || file.contentType || "").startsWith(
        "audio",
      );
      if (isAudio) {
        const audio = el("audio");
        audio.controls = true;
        audio.preload = "none";
        audio.src = media.url;
        item.append(audio);
      } else {
        const img = el("img");
        img.src = media.url;
        img.alt = media.name || file.name || "پیوست";
        img.loading = "lazy";
        img.addEventListener("error", () => {
          img.hidden = true;
        });
        item.append(img);
      }
      item.append(el("small", media.name || file.name || "پیوست"));
      if (editable)
        item.append(
          button(
            "برداشتن از ثبت",
            () => {
              editorFiles = editorFiles.filter((f) => f.id !== file.id);
              renderMedia(host, editorFiles, true).catch(fail);
            },
            "small-button",
          ),
        );
      host.append(item);
    }
  }
  async function openDetail(entry) {
    const d = C.details(entry);
    $("#detailTitle").textContent = entry.title;
    const host = $("#detailBody");
    host.replaceChildren();
    function row(label, value) {
      if (value === null || value === undefined || value === "") return;
      const n = el("div", undefined, "detail-field");
      n.append(el("small", label), el("p", String(value)));
      host.append(n);
    }
    row("تاریخ", dateLabel(entry.date));
    row("نوع", C.titles[entry.kind]);
    row("دسته‌بندی", entry.category);
    row("مبلغ", entry.amount == null ? "" : nf.format(entry.amount) + " تومان");
    row("خودرو", entry.vehicle);
    row("کیلومتر", entry.odometer == null ? "" : nf.format(entry.odometer));
    row("یادداشت", entry.notes);
    for (const [key, label] of Object.entries(labels)) {
      let value = d[key];
      if (key === "goalId" || key === "projectId") value = linkedTitle(value);
      else if (/Date|From|To/.test(key) && value) value = dateLabel(value);
      else if (typeof value === "number") value = nf.format(value);
      row(label, value);
    }
    if (d.activities.length) row("فعالیت‌ها", d.activities.join("، "));
    if (entry.kind === "experiment") host.append(experimentView(entry));
    const media = el("div", undefined, "media-grid");
    host.append(media);
    await renderMedia(
      media,
      d.attachmentIds.map((id) => ({ id, name: "پیوست" })),
    );
    $("#detailActions").replaceChildren(
      button("ویرایش", () => openEditor(entry.kind, entry), "primary"),
      button(
        "سابقهٔ نسخه‌ها",
        async () => {
          try {
            const history = await S.api("entries/" + entry.id + "/history");
            const detail = el("details", undefined, "disclosure");
            detail.append(el("summary", nf.format(history.length) + " نسخه"));
            for (const e of history) {
              const item = el("div", undefined, "record");
              item.append(
                el("h3", "نسخهٔ " + nf.format(e.revision)),
                el("p", e.notes || e.title),
                el("p", C.details(e).actualOutcome || ""),
              );
              detail.append(item);
            }
            host.append(detail);
            detail.open = true;
          } catch (error) {
            fail(error);
          }
        },
        "quiet",
      ),
    );
    $("#detailDialog").showModal();
  }
  function showSources(ids) {
    $("#detailTitle").textContent = "ثبت‌های مبنا";
    fillList(
      $("#detailBody"),
      entries.filter((e) => ids.includes(e.id)),
      { noDelete: true },
    );
    $("#detailActions").replaceChildren();
    if (!$("#detailDialog").open) $("#detailDialog").showModal();
  }
  async function openSettings() {
    await renderPending();
    $("#settingsDialog").showModal();
  }
  async function renderPending() {
    const jobs = await S.read("pending");
    $("#pendingList").replaceChildren(
      ...(jobs.length
        ? jobs.map((job) => {
            const n = el("div", undefined, "record");
            n.append(
              el("h3", job.entry?.title || "حذف یک ثبت"),
              el("p", job.error || "پس از اتصال ارسال می‌شود."),
            );
            if (job.blocked) {
              if (job.current)
                n.append(
                  el(
                    "p",
                    "نسخهٔ سرور: " +
                      job.current.title +
                      " · " +
                      (job.current.notes || ""),
                  ),
                );
              n.append(
                button(
                  job.current ? "پذیرفتن نسخهٔ سرور" : "کنارگذاشتن این عملیات",
                  async () => {
                    await S.resolve(job.id, "server");
                    await loadLocal();
                    await renderPending();
                    synchronize();
                  },
                  "small-button",
                ),
                button(
                  "اصلاح ثبت من",
                  () => {
                    const e = entries.find((e) => e.id === job.id);
                    if (e) {
                      $("#settingsDialog").close();
                      openEditor(e.kind, e);
                    } else notify("ابتدا نسخهٔ سرور را بررسی کن.");
                  },
                  "small-button",
                ),
              );
              if (job.entry)
                n.append(
                  button(
                    "حفظ به‌عنوان ثبت جدید",
                    async () => {
                      if (!confirm("یک نسخهٔ جدید از اطلاعات خودت ثبت شود؟"))
                        return;
                      await S.resolve(job.id, "server");
                      await S.save({ ...job.entry, clientId: undefined });
                      await loadLocal();
                      await renderPending();
                      synchronize();
                    },
                    "small-button",
                  ),
                );
              if (job.current && job.action !== "create")
                n.append(
                  button(
                    "ارسال دوبارهٔ نسخهٔ من",
                    async () => {
                      if (
                        !confirm("تغییرات خودت روی آخرین نسخهٔ سرور اعمال شود؟")
                      )
                        return;
                      await S.resolve(job.id, "retry");
                      synchronize().then(renderPending);
                    },
                    "small-button",
                  ),
                );
            }
            return n;
          })
        : [el("p", "همهٔ ثبت‌ها همگام هستند.")]),
    );
  }
  function downloadBlob(blob, name) {
    const url = URL.createObjectURL(blob),
      a = el("a");
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async function downloadServer(path, name) {
    if (!online) {
      notify(
        "برای پشتیبان کامل به سرور متصل شو؛ خروجی این گوشی در دسترس است.",
        true,
      );
      return;
    }
    if ((await S.read("pending")).length) {
      notify("ابتدا ثبت‌های در انتظار را همگام کن.", true);
      return;
    }
    try {
      const response = await fetch("/api/personal/" + path, {
        cache: "no-store",
      });
      if (!response.ok) {
        let data;
        try {
          data = await response.json();
        } catch {}
        throw Error(data?.error || "دریافت پشتیبان انجام نشد.");
      }
      downloadBlob(await response.blob(), name);
    } catch (error) {
      fail(error);
    }
  }
  async function stopCapture() {
    if (recorder?.state === "recording") recorder.stop();
    recordStream?.getTracks().forEach((t) => t.stop());
    recordStream = null;
    recognition?.stop();
    recognition = null;
    $("#recordVoice").textContent = "● ضبط صدا";
    $("#dictateButton").textContent = "◉ گفتار به متن";
  }
  function revokeMedia() {
    mediaUrls.forEach((url) => URL.revokeObjectURL(url));
    mediaUrls = [];
  }
  all("#mainNav [data-page]").forEach((b) =>
    b.addEventListener("click", () => showPage(b.dataset.page)),
  );
  all("[data-new]").forEach((b) =>
    b.addEventListener("click", () => openEditor(b.dataset.new).catch(fail)),
  );
  all("[data-close]").forEach((b) =>
    b.addEventListener("click", () => $("#" + b.dataset.close).close()),
  );
  all("dialog").forEach((dialog) =>
    dialog.addEventListener("close", () => {
      if (dialog.id === "captureDialog") stopCapture();
    }),
  );
  $("#syncButton").addEventListener("click", () => synchronize().catch(fail));
  $("#settingsButton").addEventListener("click", () =>
    openSettings().catch(fail),
  );
  $("#quickButton").addEventListener("click", () => {
    $("#captureText").value = "";
    captureFiles = [];
    $("#captureFiles").replaceChildren();
    $("#captureStatus").textContent = "";
    $("#captureDialog").showModal();
  });
  $("#capturePrompt").addEventListener("click", () => $("#quickButton").click());
  $("#profileButton").addEventListener("click", () => openSettings().catch(fail));
  $("#craftShortcut").addEventListener("click", () => {
    experienceKind = "craft";
    all("#experienceTabs button").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.kind === experienceKind)));
    showPage("experience");
  });
  $("#reviewShortcut").addEventListener("click", () => { $("#reportRange").value = "week"; setupReportRange(); showPage("review"); });
  $("#checkinButton").addEventListener("click", () =>
    openEditor(
      "checkin",
      entries.find((e) => e.kind === "checkin" && e.date === C.iso()) || null,
    ).catch(fail),
  );
  $("#weeklyReview").addEventListener("click", () =>
    openEditor("review").catch(fail),
  );
  $("#printReport").addEventListener("click", () => window.print());
  $("#newExperience").addEventListener("click", () =>
    openEditor(experienceKind).catch(fail),
  );
  all("#experienceTabs button").forEach((b) =>
    b.addEventListener("click", () => {
      experienceKind = b.dataset.kind;
      all("#experienceTabs button").forEach((n) =>
        n.setAttribute("aria-pressed", String(n === b)),
      );
      renderExperience();
    }),
  );
  $("#experienceSearch").addEventListener("input", renderExperience);
  for (const id of ["recordSearch", "recordKind", "recordFrom", "recordTo"])
    $("#" + id).addEventListener(
      id === "recordSearch" ? "input" : "change",
      () => {
        visibleCount = 30;
        renderRecords();
      },
    );
  $("#moreRecords").addEventListener("click", () => {
    visibleCount += 30;
    renderRecords();
  });
  $("#showArchived").addEventListener("change", renderPath);
  $("#reportRange").addEventListener("change", () => {
    setupReportRange();
    renderReport();
  });
  $("#applyReport").addEventListener("click", () => {
    reportFrom = $("#reportFrom").value;
    reportTo = $("#reportTo").value;
    if (!reportFrom || !reportTo) {
      notify("بازه را کامل وارد کن.", true);
      return;
    }
    $("#reportRange").value = "custom";
    renderReport();
  });
  $("#chartMetric").addEventListener("change", renderTrend);
  $("#editorForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = $("#saveEntry");
    button.disabled = true;
    $("#editorError").textContent = "";
    try {
      const input = readForm();
      if (
        input.kind === "habit" &&
        ((input.details.cadence === "day" && input.details.target !== 1) ||
          (input.details.cadence === "week" && input.details.target > 7))
      )
        throw Error("عادت روزانه یک روز و عادت هفتگی حداکثر هفت روز است.");
      if (input.kind === "checkin") {
        const existing = entries.find(
          (e) =>
            e.kind === "checkin" &&
            e.date === input.date &&
            e.id !== editing?.id,
        );
        if (existing)
          throw Error("این روز ثبت شده است؛ همان مورد را ویرایش کن.");
      }
      if (
        input.kind === "experiment" &&
        input.details.baselineTo >= input.details.trialFrom
      )
        throw Error("بازهٔ قبل و دورهٔ آزمایش نباید همپوشانی داشته باشند.");
      await persist(input, editing);
      $("#editorDialog").close();
    } catch (error) {
      $("#editorError").textContent = error.message;
    } finally {
      button.disabled = false;
    }
  });
  $("#attachmentInput").addEventListener("change", async (event) => {
    try {
      for (const file of event.target.files) {
        if (editorFiles.length >= 8) throw Error("حداکثر هشت پیوست مجاز است.");
        editorFiles.push(await S.addFile(file));
      }
      await renderMedia($("#editorAttachments"), editorFiles, true);
    } catch (error) {
      $("#editorError").textContent = error.message;
    }
    event.target.value = "";
  });
  $("#capturePhoto").addEventListener("change", async (event) => {
    try {
      for (const file of event.target.files) {
        if (captureFiles.length >= 8) throw Error("حداکثر هشت پیوست مجاز است.");
        captureFiles.push(await S.addFile(file));
      }
      await renderMedia($("#captureFiles"), captureFiles);
    } catch (error) {
      $("#captureStatus").textContent = error.message;
    }
    event.target.value = "";
  });
  $("#parseCapture").addEventListener("click", async () => {
    const text = $("#captureText").value.trim();
    if (!text) {
      $("#captureStatus").textContent =
        "یک توضیح کوتاه بنویس یا گفتار را به متن تبدیل کن.";
      return;
    }
    if (recorder?.state === "recording") {
      $("#captureStatus").textContent = "ابتدا ضبط صدا را پایان بده.";
      return;
    }
    const draft = C.capture(text);
    $("#captureDialog").close();
    await openEditor(draft.entry.kind, null, {
      values: draft.entry,
      files: captureFiles,
    });
    $("#editorError").textContent = draft.warnings.join(" ");
  });
  $("#dictateButton").addEventListener("click", () => {
    const Speech = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Speech) {
      $("#captureStatus").textContent =
        "این مرورگر تبدیل گفتار ندارد؛ از میکروفن صفحه‌کلید یا ضبط صدا استفاده کن.";
      return;
    }
    if (!$("#speechConsent").checked) {
      $("#captureStatus").textContent =
        "برای تبدیل گفتار، گزینهٔ رضایت را انتخاب کن.";
      return;
    }
    if (recognition) {
      recognition.stop();
      return;
    }
    recognition = new Speech();
    recognition.lang = "fa-IR";
    recognition.continuous = true;
    recognition.interimResults = false;
    recognition.onresult = (event) => {
      for (let i = event.resultIndex; i < event.results.length; i++)
        if (event.results[i].isFinal)
          $("#captureText").value +=
            ($("#captureText").value ? " " : "") +
            event.results[i][0].transcript;
    };
    recognition.onerror = (event) => {
      $("#captureStatus").textContent =
        "تبدیل گفتار انجام نشد (" +
        event.error +
        "). می‌توانی متن را وارد یا صدا را نگه داری.";
    };
    recognition.onend = () => {
      recognition = null;
      $("#dictateButton").textContent = "◉ گفتار به متن";
    };
    recognition.start();
    $("#dictateButton").textContent = "■ پایان گفتار";
  });
  $("#recordVoice").addEventListener("click", async () => {
    try {
      if (recorder?.state === "recording") {
        recorder.stop();
        return;
      }
      if (!window.MediaRecorder || !navigator.mediaDevices?.getUserMedia)
        throw Error("ضبط صدا در این مرورگر در دسترس نیست.");
      if (captureFiles.length >= 8) throw Error("حداکثر هشت پیوست مجاز است.");
      recordStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mime = ["audio/webm;codecs=opus", "audio/mp4", "audio/ogg"].find(
        (type) => MediaRecorder.isTypeSupported(type),
      );
      recorder = new MediaRecorder(
        recordStream,
        mime ? { mimeType: mime } : undefined,
      );
      const chunks = [];
      let size = 0;
      recorder.ondataavailable = (event) => {
        if (event.data.size) {
          size += event.data.size;
          chunks.push(event.data);
          if (size > 16 * 1024 * 1024) {
            recorder.stop();
            $("#captureStatus").textContent = "ضبط به سقف اندازه رسید.";
          }
        }
      };
      recorder.onstop = async () => {
        recordStream?.getTracks().forEach((t) => t.stop());
        recordStream = null;
        $("#recordVoice").textContent = "● ضبط صدا";
        $("#parseCapture").disabled = true;
        try {
          const blob = new Blob(chunks, { type: recorder.mimeType });
          if (blob.size > 16 * 1024 * 1024)
            throw Error("صدا بیش از حد بزرگ است.");
          const file = new File(
            [blob],
            "voice-" + C.iso() + (blob.type.includes("mp4") ? ".m4a" : ".webm"),
            { type: blob.type },
          );
          captureFiles.push(await S.addFile(file));
          await renderMedia($("#captureFiles"), captureFiles);
          $("#captureStatus").textContent =
            "صدا نگه داشته شد. یک عنوان کوتاه برای ثبت بنویس.";
        } catch (error) {
          $("#captureStatus").textContent = error.message;
        } finally {
          $("#parseCapture").disabled = false;
        }
      };
      recorder.start(1000);
      $("#parseCapture").disabled = true;
      $("#recordVoice").textContent = "■ پایان ضبط";
      $("#captureStatus").textContent = "در حال ضبط صدا…";
    } catch (error) {
      $("#captureStatus").textContent = error.message;
    }
  });
  $("#downloadBackup").addEventListener("click", () =>
    downloadServer("backup", "personal-backup-" + C.iso() + ".zip"),
  );
  $("#downloadJson").addEventListener("click", () =>
    downloadServer("export", "personal-data-" + C.iso() + ".json"),
  );
  $("#downloadLocal").addEventListener("click", async () => {
    const files = await S.read("files");
    const pending = await S.read("pending");
    downloadBlob(
      new Blob(
        [
          JSON.stringify(
            {
              version: 2,
              exportedAt: new Date().toISOString(),
              entries,
              pending,
              attachments: files.map(({ blob, ...meta }) => meta),
              note: "این خروجی شامل متن ثبت‌ها و وضعیت همگام‌سازی است؛ فایل‌های عکس و صدا در پشتیبان کامل سرور قرار می‌گیرند.",
            },
            null,
            2,
          ),
        ],
        { type: "application/json" },
      ),
      "personal-local-" + C.iso() + ".json",
    );
  });
  $("#importBackup").addEventListener("change", async (event) => {
    const file = event.target.files[0];
    event.target.value = "";
    if (!file) return;
    if (!online) {
      notify("بازیابی کامل نیاز به اتصال سرور دارد.", true);
      return;
    }
    if ((await S.read("pending")).length) {
      notify("ابتدا ثبت‌های در انتظار را همگام کن.", true);
      return;
    }
    if (
      !confirm(
        "ثبت‌های جدید این پشتیبان اضافه شوند؟ اطلاعات موجود بازنویسی نمی‌شود.",
      )
    )
      return;
    try {
      const result = await S.api("import", {
        method: "POST",
        headers: { "Content-Type": "application/zip" },
        body: file,
      });
      await synchronize();
      notify(
        nf.format(result.added) +
          " ثبت اضافه شد؛ " +
          nf.format(result.skipped) +
          " ثبت موجود تغییر نکرد.",
      );
    } catch (error) {
      fail(error);
    }
  });
  $("#loadTrash").addEventListener("click", async () => {
    try {
      const trash = await S.api("trash");
      $("#trashList").replaceChildren(
        ...(trash.length
          ? trash.map((entry) => {
              const row = el("div", undefined, "record");
              row.append(
                el("h3", entry.title),
                button(
                  "بازیابی",
                  async () => {
                    try {
                      await S.api("entries/" + entry.id + "/restore", {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ revision: entry.revision }),
                      });
                      await synchronize();
                      row.remove();
                      notify("ثبت بازیابی شد.");
                    } catch (error) {
                      fail(error);
                    }
                  },
                  "small-button",
                ),
              );
              return row;
            })
          : [el("p", "ثبت حذف‌شده‌ای وجود ندارد.")]),
      );
    } catch (error) {
      fail(error);
    }
  });
  $("#logoutButton").addEventListener("click", async () => {
    const jobs = await S.read("pending");
    if (
      jobs.length &&
      !confirm(
        "ثبت‌های ارسال‌نشده روی این گوشی پاک خواهند شد. ابتدا می‌توانی خروجی بگیری. خروج انجام شود؟",
      )
    )
      return;
    try {
      const response = await fetch("/api/auth/logout", { method: "POST" });
      if (!response.ok) throw Error("خروج از سرور انجام نشد.");
      await S.clear();
      navigator.serviceWorker.controller?.postMessage({
        type: "PERSONAL_CLEAR",
      });
      location.replace("/login");
    } catch (error) {
      fail(error);
    }
  });
  window.addEventListener("online", () => synchronize().catch(fail));
  window.addEventListener("offline", () => {
    online = false;
    syncStatus().catch(fail);
  });
  window.addEventListener("beforeunload", () => {
    recordStream?.getTracks().forEach((t) => t.stop());
    revokeMedia();
  });
  navigator.serviceWorker?.addEventListener("message", (event) => {
    if (event.data?.type === "PERSONAL_LOGOUT") {
      entries = [];
      $("#mainContent").hidden = true;
      location.replace("/login");
    }
  });
  setInterval(tickTimer, 1000);
  let resizeTimer;
  window.addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      if (currentPage === "review" && reportData) renderTrend();
    }, 150);
  });
  $("#todayDate").textContent = new Intl.DateTimeFormat("fa-IR", {
    dateStyle: "full",
  }).format(new Date());
  setupReportRange();
  (async () => {
    try {
      await S.identity();
      online = true;
    } catch (error) {
      if (error.status === 401) {
        location.replace("/login");
        return;
      }
      if (error.status === 403) {
        await S.clear();
        location.replace("/phoenix");
        return;
      }
      try {
        if (!(await S.read("meta", "owner"))) {
          notify("اولین استفاده نیاز به ورود و اتصال سرور دارد.", true);
          $("#mainContent").hidden = true;
          return;
        }
      } catch {
        notify("ذخیرهٔ محلی در این مرورگر در دسترس نیست.", true);
        return;
      }
    }
    try {
      await loadLocal();
      if ("serviceWorker" in navigator) {
        const registration = await navigator.serviceWorker.register("/sw.js");
        await navigator.serviceWorker.ready;
        (
          registration.active || navigator.serviceWorker.controller
        )?.postMessage({ type: "PERSONAL_CACHE_SHELL" });
      }
      await synchronize();
    } catch (error) {
      fail(error);
    }
  })();
})();
