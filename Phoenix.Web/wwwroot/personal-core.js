/* Pure domain helpers shared by the UI, offline reports and regression tests. */
(function (scope) {
  "use strict";
  const defaults = {
    status: "active",
    important: false,
    cadence: "week",
    target: 3,
    activities: [],
    attachmentIds: [],
    alternatives: "",
    hypothesis: "",
    expectedOutcome: "",
    actualOutcome: "",
    materials: "",
    method: "",
    observation: "",
    conclusion: "",
    metric: "focus",
  };
  const titles = {
    finance: "حساب‌وکتاب",
    vehicle: "سابقهٔ خودرو",
    journal: "یادداشت",
    task: "کار روزانه",
    checkin: "وضعیت روز",
    goal: "هدف",
    project: "پروژه",
    habit: "عادت",
    "habit-log": "انجام عادت",
    decision: "تصمیم",
    craft: "تجربهٔ ساخت ساز",
    time: "زمان فعالیت",
    experiment: "آزمایش تغییر",
    review: "مرور شخصی",
  };
  function normalize(text) {
    return String(text || "")
      .replace(/[۰-۹]/g, (c) => String("۰۱۲۳۴۵۶۷۸۹".indexOf(c)))
      .replace(/[٠-٩]/g, (c) => String("٠١٢٣٤٥٦٧٨٩".indexOf(c)))
      .replace(/ي/g, "ی")
      .replace(/ك/g, "ک")
      .replace(/٬|,/g, "")
      .replace(/\u200c/g, " ");
  }
  function iso(date = new Date()) {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
  }
  function shift(date, days) {
    const d = new Date(date + "T12:00:00");
    d.setDate(d.getDate() + days);
    return iso(d);
  }
  function weekStart(date) {
    return shift(date, -((new Date(date + "T12:00:00").getDay() + 1) % 7));
  }
  function days(from, to) {
    return (
      Math.round(
        (new Date(to + "T12:00:00Z") - new Date(from + "T12:00:00Z")) /
          86400000,
      ) + 1
    );
  }
  function persianMonthStart(date) {
    const f = new Intl.DateTimeFormat("fa-IR", {
      month: "numeric",
      year: "numeric",
    });
    const month = f.format(new Date(date + "T12:00:00"));
    let start = date;
    while (f.format(new Date(shift(start, -1) + "T12:00:00")) === month)
      start = shift(start, -1);
    return start;
  }
  function details(record) {
    return {
      ...defaults,
      ...record?.details,
      activities: record?.details?.activities || [],
      attachmentIds: record?.details?.attachmentIds || [],
    };
  }
  const words = {
    صفر: 0,
    یک: 1,
    یه: 1,
    دو: 2,
    سه: 3,
    چهار: 4,
    پنج: 5,
    شش: 6,
    هفت: 7,
    هشت: 8,
    نه: 9,
    ده: 10,
    یازده: 11,
    دوازده: 12,
    سیزده: 13,
    چهارده: 14,
    پانزده: 15,
    شانزده: 16,
    هفده: 17,
    هجده: 18,
    نوزده: 19,
    بیست: 20,
    سی: 30,
    چهل: 40,
    پنجاه: 50,
    شصت: 60,
    هفتاد: 70,
    هشتاد: 80,
    نود: 90,
    صد: 100,
    دویست: 200,
    سیصد: 300,
    چهارصد: 400,
    پانصد: 500,
    ششصد: 600,
    هفتصد: 700,
    هشتصد: 800,
    نهصد: 900,
  };
  function amountOf(text) {
    const tokens = normalize(text).match(/[\d]+(?:\.[\d]+)?|[آ-ی]+/g) || [];
    if (tokens.filter((t) => ["تومان", "تومن", "ریال"].includes(t)).length > 1)
      return null;
    const moneyIndex = tokens.findIndex((t) =>
      ["تومان", "تومن", "ریال"].includes(t),
    );
    let stop = moneyIndex >= 0 ? moneyIndex : tokens.length;
    let start = stop;
    const isNumber = (t) =>
      /^\d+(\.\d+)?$/.test(t) ||
      Object.hasOwn(words, t) ||
      ["هزار", "میلیون", "میلیارد", "و"].includes(t);
    while (start > 0 && isNumber(tokens[start - 1])) start--;
    let phrase = tokens.slice(start, stop);
    if (moneyIndex < 0) {
      const mul = tokens.findIndex((t) =>
        ["هزار", "میلیون", "میلیارد"].includes(t),
      );
      if (mul < 0) return null;
      start = mul;
      while (start > 0 && isNumber(tokens[start - 1])) start--;
      stop = mul + 1;
      while (stop < tokens.length && isNumber(tokens[stop])) stop++;
      phrase = tokens.slice(start, stop);
    }
    if (!phrase.length) return null;
    if (
      !phrase.some((t) => ["هزار", "میلیون", "میلیارد"].includes(t)) &&
      phrase.filter((t) => t !== "و").length > 1
    )
      return null;
    const bigIndex = Math.max(
      phrase.lastIndexOf("میلیون"),
      phrase.lastIndexOf("میلیارد"),
    );
    if (
      bigIndex >= 0 &&
      phrase.slice(bigIndex + 1).some((t) => t !== "و") &&
      !phrase.slice(bigIndex + 1).includes("هزار")
    )
      return null;
    let total = 0,
      current = 0;
    for (const t of phrase) {
      if (t === "و") continue;
      if (["هزار", "میلیون", "میلیارد"].includes(t)) {
        total +=
          (current || 1) *
          { هزار: 1000, میلیون: 1000000, میلیارد: 1000000000 }[t];
        current = 0;
      } else current += /^\d/.test(t) ? Number(t) : words[t];
    }
    const amount =
      (total + current) /
      (moneyIndex >= 0 && tokens[moneyIndex] === "ریال" ? 10 : 1);
    return Number.isFinite(amount) && amount > 0 && amount <= 1e15
      ? amount
      : null;
  }
  function capture(text, today = iso()) {
    const n = normalize(text),
      kind = /باید|یادم بنداز|پیگیری|قرار/.test(n)
        ? "task"
        : /روغن|تعمیرگاه|ماشین|خودرو|فیلتر|لاستیک/.test(n)
          ? "vehicle"
          : /آزمایش.*ساز|کوک|ساخت ساز/.test(n)
            ? "craft"
            : /خرید|هزینه|پرداخت|فروش|درآمد|دریافت|تومان|تومن|ریال/.test(n)
              ? "finance"
              : "journal";
    const amount = amountOf(n),
      km = n.match(/(?:کیلومتر(?:ش)?\s*(?:بود|روی)?\s*)(\d+)|(\d+)\s*کیلومتر/);
    const entry = {
      kind,
      date: shift(today, /فردا/.test(n) ? 1 : /دیروز/.test(n) ? -1 : 0),
      title: text.trim().slice(0, 200),
      category:
        kind === "vehicle" ? "سرویس" : /خوراک|میوه|غذا/.test(n) ? "خوراک" : "",
      notes: text.trim(),
      amount: ["finance", "vehicle"].includes(kind) ? amount : null,
      flow:
        kind === "finance"
          ? /فروش|درآمد|دریافت/.test(n)
            ? "income"
            : "expense"
          : null,
      vehicle: "",
      odometer: km ? Number(km[1] || km[2]) : null,
      details: { ...defaults, status: kind === "task" ? "todo" : "active" },
    };
    return {
      entry,
      warnings:
        entry.kind === "finance" && !amount
          ? ["مبلغ مشخص نیست؛ پیش از ثبت واردش کن."]
          : entry.kind === "vehicle" && !amount
            ? ["مبلغ را فقط اگر مشخص است وارد کن."]
            : ["نوع ثبت، مبلغ و تاریخ پیشنهادی را بررسی کن."],
    };
  }
  function report(entries, from, to) {
    const selected = entries.filter((e) => e.date >= from && e.date <= to),
      length = days(from, to),
      previousFrom = shift(from, -length),
      previousTo = shift(from, -1),
      prev = entries.filter(
        (e) => e.date >= previousFrom && e.date <= previousTo,
      );
    const sum = (list, fn) => list.reduce((s, e) => s + fn(e), 0),
      expenses = (list) =>
        sum(list, (e) =>
          e.kind === "vehicle" || (e.kind === "finance" && e.flow === "expense")
            ? e.amount || 0
            : 0,
        ),
      income = (list) =>
        sum(list, (e) =>
          e.kind === "finance" && e.flow === "income" ? e.amount || 0 : 0,
        ),
      minutes = (list) =>
        sum(list, (e) =>
          e.kind === "time" ? details(e).durationMinutes || 0 : 0,
        );
    const checkins = selected.filter((e) => e.kind === "checkin");
    const totals = {
      income: income(selected),
      expenses: expenses(selected),
      vehicleExpenses: sum(selected, (e) =>
        e.kind === "vehicle" ? e.amount || 0 : 0,
      ),
      minutes: minutes(selected),
      completedTasks: entries.filter(
        (e) =>
          e.kind === "task" &&
          details(e).status === "done" &&
          (details(e).completedDate || e.date) >= from &&
          (details(e).completedDate || e.date) <= to,
      ).length,
      journalCount: selected.filter((e) => e.kind === "journal").length,
      checkinDays: new Set(checkins.map((e) => e.date)).size,
      calendarDays: length,
      records: selected.length,
    };
    const chart = Array.from({ length }, (_, i) => {
      const date = shift(from, i),
        day = selected.filter((e) => e.date === date),
        c = day.find((e) => e.kind === "checkin");
      return {
        date,
        expense: expenses(day),
        income: income(day),
        minutes: minutes(day),
        energy: c ? (details(c).energy ?? null) : null,
        focus: c ? (details(c).focus ?? null) : null,
        mood: c ? (details(c).mood ?? null) : null,
        sleepHours: c ? (details(c).sleepHours ?? null) : null,
      };
    });
    const cats = new Map();
    selected
      .filter(
        (e) =>
          e.kind === "vehicle" ||
          (e.kind === "finance" && e.flow === "expense"),
      )
      .forEach((e) => {
        const name =
            e.category || (e.kind === "vehicle" ? "خودرو" : "بدون دسته"),
          row = cats.get(name) || { name, amount: 0, count: 0, recordIds: [] };
        row.amount += e.amount || 0;
        row.count++;
        row.recordIds.push(e.id);
        cats.set(name, row);
      });
    const projects = entries
      .filter((e) => ["goal", "project"].includes(e.kind))
      .map((parent) => {
        const children = new Set(
            entries
              .filter(
                (e) => e.kind === "project" && details(e).goalId === parent.id,
              )
              .map((e) => e.id),
          ),
          linked = selected.filter(
            (e) =>
              details(e).goalId === parent.id ||
              details(e).projectId === parent.id ||
              children.has(details(e).projectId),
          );
        return {
          id: parent.id,
          title: parent.title,
          kind: parent.kind,
          status: details(parent).status,
          minutes: minutes(linked),
          completedTasks: linked.filter(
            (e) => e.kind === "task" && details(e).status === "done",
          ).length,
          recordCount: linked.length,
          recordIds: linked.map((e) => e.id),
        };
      });
    const avg = (list) =>
      list.length
        ? Math.round((sum(list, (e) => details(e).focus) / list.length) * 100) /
          100
        : null;
    const activities = [
      ...new Set(checkins.flatMap((e) => details(e).activities)),
    ].map((activity) => {
      const withRecords = checkins.filter(
          (e) =>
            details(e).activities.includes(activity) &&
            details(e).focus != null,
        ),
        without = checkins.filter(
          (e) =>
            !details(e).activities.includes(activity) &&
            details(e).focus != null,
        );
      return {
        activity,
        withCount: withRecords.length,
        withoutCount: without.length,
        withFocus: avg(withRecords),
        withoutFocus: avg(without),
        enoughData: withRecords.length >= 5 && without.length >= 5,
      };
    });
    return {
      from,
      to,
      previousFrom,
      previousTo,
      totals,
      previous: {
        income: income(prev),
        expenses: expenses(prev),
        minutes: minutes(prev),
        checkinDays: prev.filter((e) => e.kind === "checkin").length,
      },
      chart,
      categories: [...cats.values()].sort((a, b) => b.amount - a.amount),
      projects,
      activities,
      reviewsDue: entries.filter(
        (e) =>
          e.kind === "decision" &&
          details(e).reviewDate &&
          details(e).reviewDate <= to &&
          !details(e).reviewedDate,
      ),
      overdueTasks: entries.filter(
        (e) =>
          e.kind === "task" &&
          e.date <= to &&
          ["todo", "waiting", "active"].includes(details(e).status),
      ),
      craft: selected.filter((e) => e.kind === "craft"),
      recordIds: selected.map((e) => e.id),
    };
  }
  function experiment(entry, entries) {
    const d = details(entry),
      select = (from, to) =>
        entries.filter(
          (e) =>
            e.kind === "checkin" &&
            e.date >= from &&
            e.date <= to &&
            details(e)[d.metric] != null,
        ),
      baseline = select(d.baselineFrom, d.baselineTo),
      trial = select(d.trialFrom, d.trialTo),
      avg = (list) =>
        list.length
          ? Math.round(
              (list.reduce((s, e) => s + details(e)[d.metric], 0) /
                list.length) *
                100,
            ) / 100
          : null,
      before = avg(baseline),
      after = avg(trial);
    return {
      before,
      after,
      difference:
        before != null && after != null
          ? Math.round((after - before) * 100) / 100
          : null,
      baselineCount: baseline.length,
      trialCount: trial.length,
      baselineDays: days(d.baselineFrom, d.baselineTo),
      trialDays: days(d.trialFrom, d.trialTo),
      enoughData: baseline.length >= 5 && trial.length >= 5,
      recordIds: [...baseline, ...trial].map((e) => e.id),
    };
  }
  function habitProgress(habit, entries, today = iso()) {
    const d = details(habit),
      from = d.cadence === "day" ? today : weekStart(today),
      to = d.cadence === "day" ? today : shift(from, 6),
      count = new Set(
        entries
          .filter(
            (e) =>
              e.kind === "habit-log" &&
              details(e).parentId === habit.id &&
              e.date >= from &&
              e.date <= to,
          )
          .map((e) => e.date),
      ).size;
    return { from, to, count, target: d.target, done: count >= d.target };
  }
  const api = {
    defaults,
    titles,
    normalize,
    iso,
    shift,
    weekStart,
    days,
    persianMonthStart,
    details,
    capture,
    amountOf,
    report,
    experiment,
    habitProgress,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else scope.PersonalCore = api;
})(typeof window !== "undefined" ? window : globalThis);
