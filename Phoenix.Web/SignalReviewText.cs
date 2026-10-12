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
            $"\n📐 نمونه‌های مقایسه: {result.CalibrationSampleCount}";
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
          $"\nاعتبارسنجی زمانی: {validationCount} نتیجه؛ برآورد آماری، نه احتمال قطعی"
        : "\n\nبرآورد نتیجه: داده یا اعتبارسنجی کافی برای مدل وجود ندارد.";
}
