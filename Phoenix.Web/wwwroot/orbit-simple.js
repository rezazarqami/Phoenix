(function (scope) {
  "use strict";
  const normalize = (text) =>
    String(text)
      .replace(/[۰-۹]/g, (c) => String("۰۱۲۳۴۵۶۷۸۹".indexOf(c)))
      .replace(/[٠-٩]/g, (c) => String("٠١٢٣٤٥٦٧٨٩".indexOf(c)))
      .replace(/ي/g, "ی")
      .replace(/ك/g, "ک");
  const day = (date) => new Date(date + "T12:00:00");
  const shift = (iso, n) => {
    const d = day(iso);
    d.setDate(d.getDate() + n);
    return (
      d.getFullYear() +
      "-" +
      String(d.getMonth() + 1).padStart(2, "0") +
      "-" +
      String(d.getDate()).padStart(2, "0")
    );
  };
  function month(anchor) {
    const fmt = new Intl.DateTimeFormat("en-US-u-ca-persian", {
        year: "numeric",
        month: "numeric",
      }),
      key = fmt.format(day(anchor));
    let start = anchor,
      end = anchor;
    while (fmt.format(day(shift(start, -1))) === key) start = shift(start, -1);
    while (fmt.format(day(shift(end, 1))) === key) end = shift(end, 1);
    return { start, end, offset: (day(start).getDay() + 1) % 7 };
  }
  function parseSms(body) {
    const text = normalize(body).replace(/\u200c/g, " ");
    if (
      !/(برداشت|خرید|بدهکار|پرداخت)/.test(text) ||
      /(رمز|یکبار مصرف|واریز|بستانکار)/.test(text)
    )
      return null;
    if (!/(بانک|حساب|کارت|مانده|موجودی)/.test(text)) return null;
    // Never interpret the balance, account/card number or an amount without a unit.
    const matches = [
      ...text.matchAll(
        /(?:برداشت|خرید|بدهکار|پرداخت|انتقال وجه)(?:\s*(?:مبلغ|به مبلغ|از حساب|موفق|:|\*|\n)){0,5}\s*([0-9][0-9,٬\s]*[0-9]|[0-9])\s*(ریال|تومان)/g,
      ),
    ];
    if (matches.length === 0)
      matches.push(
        ...text.matchAll(
          /مبلغ(?:\s+تراکنش)?\s*:?\s*([0-9][0-9,٬]*[0-9]|[0-9])\s*(ریال|تومان)/g,
        ),
      );
    if (matches.length !== 1) return null;
    const amount = Number(matches[0][1].replace(/[,٬\s]/g, "")),
      toman = matches[0][2] === "ریال" ? amount / 10 : amount;
    if (!Number.isFinite(toman) || toman <= 0 || toman > 1e15) return null;
    return { toman };
  }
  const api = {
    month,
    parseSms,
    isDraft: (e) =>
      e.kind === "finance" &&
      e.details?.sourceId &&
      e.details.status === "waiting",
  };
  if (typeof module !== "undefined") module.exports = api;
  else scope.OrbitSimple = api;
})(globalThis);
