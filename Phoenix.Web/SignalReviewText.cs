using System.Globalization;

namespace Phoenix.Web;

internal static class SignalReviewText
{
    internal static string Similarity(SignalSimilarityResult result)
    {
        static string Percent(decimal? value) => value.HasValue
            ? Math.Round(value.Value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "٪"
            : "—";

        return Outcome(result.TargetOutcomePercent, result.StopOutcomePercent, result.OutcomeValidationCount) + $"\n\n🟢 <b>شباهت به تارگت‌ها: {Percent(result.TargetPercent)}</b>" +
            $"\n🔴 <b>شباهت به استاپ‌ها: {Percent(result.StopPercent)}</b>" +
            Comparison(result.TargetPercent, result.StopPercent) +
            $"\n\n🧠 نمونه‌های تارگت: {result.TargetSampleCount} | استاپ: {result.StopSampleCount}" +
            $"\n📐 نمونه‌های مقایسه: {result.CalibrationSampleCount}" + Support(result.Evidence);
    }
    internal static string Support(SimilarityEvidence? evidence)
    {
        if (evidence is null) return "";
        var stage = evidence.ReferenceStage == "proposal" ? "زمان پیشنهاد" : "لحظه ورود";
        var text = $"\nمرجع شباهت: شرایط {stage} در گذشته." +
            $"\nنمونه‌های نزدیکِ هم‌تایم‌فریم: {evidence.SameTimeframeNearbyTargets} تارگت / {evidence.SameTimeframeNearbyStops} استاپ.";
        if (evidence.QueryStage != evidence.ReferenceStage)
            text += "\nهنوز سابقهٔ کافی از زمان پیشنهاد نداریم؛ این مقایسه با شرایط ورود است.";
        if (evidence.HistoricalTargetRate is { } rate)
            text += $"\nسهم تارگت در همین نمونه‌های گذشته: {rate:0.0}٪؛ احتمال این سیگنال نیست.";
        else text += "\nنمونه‌های نزدیک برای اعلام نرخ تاریخی کافی نیستند.";
        if (evidence.TechnicalCohort is { } c)
        {
            text += $"\n🔬 نزدیک، هم‌جهت و فاصله‌های معاملاتی مشابه: {c.Targets} تارگت / {c.Stops} استاپ" +
                $"\nپراکندگی: {c.DistinctDays} روز / {c.DistinctSymbols} نماد؛ تعداد مؤثر وزنی: {c.EffectiveSamples:0.#}" +
                $"\n۳۰ روز اخیر این گروه: {c.RecentTargets} تارگت / {c.RecentStops} استاپ.";
            if(c.BaselineTargetRate is { } baseline)
                text += $"\nمرجع کل هم‌جهت و هم‌تایم‌فریم: {c.BaselineTargets} تارگت / {c.BaselineStops} استاپ ({baseline:0.#}٪ تارگت).";
            foreach(var item in c.Conditions)
                text += $"\n{item.Label}: {item.Targets} تارگت / {item.Stops} استاپ.";
            text += "\nزیرگروه‌ها هم‌پوشان‌اند؛ تعداد مؤثر وزنی، تعداد تجربه‌های مستقل نیست؛ سیگنال‌های هم‌زمان می‌توانند وابسته باشند.";
        }
        text += Window(evidence.MainOutcomeWindow) + Window(evidence.ObservationOutcomeWindow);
        if(evidence.Payoff is { } payoff)
            text += $"\n⚖️ سود هدف / زیان استاپ: {payoff.RewardRisk:0.###}R؛ نرخ سربه‌سر نظری: {payoff.GrossBinaryBreakEvenRate:0.#}٪" +
                "\nفقط با دو خروج تارگت/استاپ و بدون کارمزد و لغزش؛ احتمال موفقیت نیست.";
        return text;
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

    internal static string Outcome(decimal? target, decimal? stop, int validationCount) => target.HasValue
        ? $"\n\n🎯 <b>برآورد تارگت پس از ورود: {target:0}٪</b>" +
          $"\n🛑 <b>برآورد استاپ پس از ورود: {stop:0}٪</b>" +
          $"\nاعتبارسنجی زمانی: {validationCount} نتیجه؛ مشروط به ورود و خروج تارگت/استاپ، بدون مهلت ثابت"
        : "\n\nبرآورد نتیجه: داده یا اعتبارسنجی کافی برای مدل وجود ندارد.";
}
