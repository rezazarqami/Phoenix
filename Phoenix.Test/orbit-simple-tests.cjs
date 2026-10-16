const assert = require("node:assert/strict");
const O = require("../Phoenix.Web/wwwroot/orbit-simple.js");
const C = require("../Phoenix.Web/wwwroot/personal-core.js");
assert.deepEqual(O.month("2026-10-17"), {
  start: "2026-09-23",
  end: "2026-10-22",
  offset: 4,
});
assert.deepEqual(O.month("2025-03-20"), {
  start: "2025-02-19",
  end: "2025-03-20",
  offset: 4,
});
for (const [text, toman] of [
  ["برداشت: ۷۰۰٬۰۰۰ ریال\nمانده: ۹۰۰۰۰۰۰ ریال", 70000],
  ["بانک نمونه\nخرید 70,000 تومان", 70000],
  ["برداشت از حساب ۱۲۳۴\nمبلغ: ۷۰۰۰۰۰ ریال\nمانده ۹۰۰۰۰۰ ریال", 70000],
  ["بانک نمونه\nبرداشت مبلغ ٧٠٠٠٠٠ ریال", 70000],
])
  assert.equal(O.parseSms(text)?.toman, toman);
for (const text of [
  "رمز خرید ۷۰۰۰۰۰ ریال",
  "واریز ۷۰۰۰۰۰ ریال",
  "برداشت از کارت ۱۲۳۴، مانده ۷۰۰۰۰۰ ریال",
  "برداشت ۷۰۰۰۰۰",
  "خرید ۵۰۰ ریال و خرید ۶۰۰ ریال",
])
  assert.equal(O.parseSms(text), null, text);
const draft = {
  kind: "finance",
  amount: 70000,
  flow: "expense",
  date: "2026-10-17",
  details: { sourceId: "sms:1", status: "waiting" },
};
assert(O.isDraft(draft));
assert.equal(C.report([draft], draft.date, draft.date).totals.expenses, 0);
assert.equal(
  C.report(
    [{ ...draft, details: { ...draft.details, status: "active" } }],
    draft.date,
    draft.date,
  ).totals.expenses,
  70000,
);
console.log(
  "PASS Orbit: Persian month boundaries, leap day, debit parsing, units, ambiguity and draft totals.",
);
