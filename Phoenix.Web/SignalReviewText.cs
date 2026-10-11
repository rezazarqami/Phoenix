using System.Globalization;

namespace Phoenix.Web;

internal static class SignalReviewText
{
    internal static string Similarity(SignalSimilarityResult result)
    {
        static string Percent(decimal? value) => value.HasValue
            ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "٪"
            : "—";

        return Outcome(result.TargetOutcomePercent, result.StopOutcomePercent, result.OutcomeValidationCount) + $"\n\n🟢 <b>شباهت به تارگت‌ها: {Percent(result.TargetPercent)}</b>" +
            $"\n🔴 <b>شباهت به استاپ‌ها: {Percent(result.StopPercent)}</b>" +
            $"\n\n🧠 نتایج آموخته‌شده: {result.SampleCount}" +
            $"\n📐 نمونه‌های مقایسه: {result.CalibrationSampleCount}";
    }
    internal static string Outcome(decimal? target, decimal? stop, int validationCount) => target.HasValue
        ? $"\n\n🎯 <b>برآورد تارگت پس از ورود: {target:0}٪</b>" +
          $"\n🛑 <b>برآورد استاپ پس از ورود: {stop:0}٪</b>" +
          $"\nاعتبارسنجی زمانی: {validationCount} نتیجه؛ برآورد آماری، نه احتمال قطعی"
        : "\n\nبرآورد نتیجه: داده یا اعتبارسنجی کافی برای مدل وجود ندارد.";
}
