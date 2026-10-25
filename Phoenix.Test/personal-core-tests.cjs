const assert = require("node:assert/strict"),
  C = require("../Phoenix.Web/wwwroot/personal-core.js");
assert.equal(
  C.capture(
    "روغن ماشین کیلومتر ۸۴۵۰۰ بود و یک میلیون و هشتصد هزار تومان پرداخت کردم",
    "2026-10-14",
  ).entry.amount,
  1800000,
);
assert.equal(
  C.capture(
    "روغن ماشین کیلومتر ۸۴۵۰۰ بود و یک میلیون و هشتصد هزار تومان پرداخت کردم",
    "2026-10-14",
  ).entry.odometer,
  84500,
);
assert.equal(C.amountOf("خرید یک و هشتصد تومان"), null);
assert.equal(C.amountOf("خرید یک میلیون و هشتصد تومان"), null);
assert.equal(C.amountOf("خرید ۲۰۰ تومان و تعمیر ۳۰۰ تومان"), null);
assert.equal(C.amountOf("خرید ۲۵۰٬۰۰۰ تومان"), 250000);
assert.equal(C.amountOf("خرید ۱۰۰۰۰۰ ریال"), 10000);
assert.equal(
  C.capture("باید فردا تعمیرگاه را پیگیری کنم", "2026-10-14").entry.kind,
  "task",
);
assert.equal(
  C.capture("باید فردا تعمیرگاه را پیگیری کنم", "2026-10-14").entry.date,
  "2026-10-15",
);
assert.equal(C.weekStart("2026-10-14"), "2026-10-10");
const entries = [
  {
    id: "1",
    kind: "vehicle",
    date: "2026-10-14",
    amount: 100,
    category: "خودرو",
  },
  {
    id: "2",
    kind: "finance",
    date: "2026-10-14",
    amount: 200,
    flow: "expense",
  },
  {
    id: "3",
    kind: "checkin",
    date: "2026-10-14",
    details: { focus: 4, activities: ["پیاده‌روی"] },
  },
];
const report = C.report(entries, "2026-10-13", "2026-10-14");
assert.equal(report.totals.expenses, 300);
assert.equal(report.chart[0].focus, null);
assert.equal(report.totals.checkinDays, 1);
assert.equal(report.activities[0].enoughData, false);
const experiment = C.experiment(
  {
    details: {
      metric: "focus",
      baselineFrom: "2026-10-12",
      baselineTo: "2026-10-13",
      trialFrom: "2026-10-14",
      trialTo: "2026-10-15",
    },
  },
  entries,
);
assert.equal(experiment.before, null);
assert.equal(experiment.difference, null);
assert.equal(experiment.trialCount, 1);
const progress = C.habitProgress(
  { id: "h", details: { cadence: "week", target: 3 } },
  [
    { kind: "habit-log", date: "2026-10-14", details: { parentId: "h" } },
    { kind: "habit-log", date: "2026-10-14", details: { parentId: "h" } },
  ],
  "2026-10-14",
);
assert.equal(progress.count, 1);
assert.equal(progress.done, false);
assert.equal(C.persianMonthStart("2026-10-14"), "2026-09-23");
console.log(
  "PASS personal domain: Persian capture, ambiguous amounts, dates, unique habit days, missing-data reports and experiments.",
);
