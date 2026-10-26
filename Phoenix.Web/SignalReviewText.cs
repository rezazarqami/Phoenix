namespace Phoenix.Web;

internal static class SignalReviewText
{
    internal static string Similarity(SignalSimilarityResult result)
    {
        return Outcome(result.TargetOutcomePercent, result.StopOutcomePercent, result.OutcomeValidationCount)
            + Support(result.Evidence);
    }
    internal static string Support(SimilarityEvidence? e)
    {
        if (e is null) return "\nنمونه‌های مشابه: اطلاعات فنی کافی نیست.";
        if (e.ComparableTargets + e.ComparableStops + e.ComparableRiskFree == 0)
            return "\nنمونه‌های مشابه: سابقهٔ فنی قابل‌مقایسه در این مرحله وجود ندارد.";
        var stageNote = e.QueryStage != e.ReferenceStage ? " (پیشنهاد با سابقهٔ ورود؛ سابقهٔ پیشنهاد موجود نیست)" : "";
        var sameDenominator=e.ComparisonVersion>=4?$" (از {e.SameTimeframeComparable} نمونه)":"";
        var otherDenominator=e.ComparisonVersion>=4?$" (از {e.OtherTimeframeComparable} نمونه)":"";
        return $"\nنزدیکِ هم‌تایم‌فریم{stageNote}{sameDenominator}: {e.SameTimeframeNearbyTargets} تارگت / {e.SameTimeframeNearbyStops} استاپ / {e.SameTimeframeNearbyRiskFree} ریسک‌فری" +
            $"\nنزدیکِ سایر تایم‌فریم‌ها{otherDenominator}: {e.OtherTimeframeNearbyTargets} تارگت / {e.OtherTimeframeNearbyStops} استاپ / {e.OtherTimeframeNearbyRiskFree} ریسک‌فری";
    }
    internal static string Window(OutcomeWindowEvidence? w)
    {
        if(w is null)return "";
        var source=w.Source=="main"?"اصلی":"فقط تأیید";
        var stage=w.Stage=="proposal"?"پیشنهاد":"ورود";
        if(w.MatureCount==0 && w.Immature==0)return $"\n⏱ پنجرهٔ {w.HorizonMinutes} دقیقه‌ای از {stage} — {source}: سابقهٔ قابل‌مقایسه نداریم.";
        var text=$"\n⏱ نتیجهٔ ثبت‌شده تا {w.HorizonMinutes} دقیقه از {stage} — {source}:" +
            $"\nتارگت {w.Targets} / استاپ {w.Stops} / ریسک‌فری {w.RiskFree} / انقضا {w.Expired} / خروج دیگر {w.OtherExit}" +
            $"\nبدون نتیجهٔ ثبت‌شده {w.NoRecordedResolution} / نامعلوم {w.Unknown}؛ هنوز نرسیده به مهلت {w.Immature}.";
        if(w.RecordedTargetShare is { } tp)
            text+=$"\nسهم ثبت‌شده از کل {w.MatureCount} نمونه: تارگت {tp:0.#}٪ / استاپ {w.RecordedStopShare:0.#}٪.";
        else text+=w.LegacyObservationCount>0 ? $"\n{w.LegacyObservationCount} نمونه با رصد قدیمیِ ناقص ریسک‌فری؛ سهم درصدی نمایش داده نمی‌شود." : "\nنمونه برای نمایش سهم‌ها کافی نیست.";
        return text+(w.Source=="observation" ? "\nرصد بدون معامله: سابقهٔ قدیمی ریسک‌فری را کامل ثبت نمی‌کرد؛ نتیجهٔ رصد، تضمین اجرای سفارش نیست." : "")+"\nاین آمار گذشته است؛ پایان مهلت به معنی بستن معامله نیست.";
    }
    internal static string Comparison(decimal? target, decimal? stop)
    {
        if (!target.HasValue || !stop.HasValue) return "\nمقایسه شباهت: شواهد قابل‌مقایسه کافی نیست.";
        var gap = Math.Abs(target.Value - stop.Value);
        var side = target > stop ? "تارگت" : "استاپ";
        return gap < 5m
            ? $"\nفاصله شباهت: {gap:0.0} واحد درصد؛ شباهت دو گروه نزدیک است." +
              "\nدرصد شباهت، احتمال نتیجه معامله نیست."
            : $"\nفاصله شباهت: {gap:0.0} واحد درصد به سمت {side}." +
              "\nدرصد شباهت، احتمال نتیجه معامله نیست.";
    }

    // Kept as a no-op for archived callers; probability is retired.
    internal static string Outcome(decimal? target, decimal? stop, int validationCount) => "";
}
