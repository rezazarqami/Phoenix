using System.Globalization;

namespace Phoenix.Web;

internal static class SignalReviewText
{
    internal static string Similarity(SignalSimilarityResult result)
    {
        static string Percent(decimal? value) => value.HasValue
            ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "٪"
            : "—";

        return $"\n\n🟢 <b>شباهت به تارگت‌ها: {Percent(result.TargetPercent)}</b>" +
            $"\n🔴 <b>شباهت به استاپ‌ها: {Percent(result.StopPercent)}</b>" +
            $"\n\n🧠 نتایج آموخته‌شده: {result.SampleCount}" +
            $"\n📐 نمونه‌های مقایسه: {result.CalibrationSampleCount}";
    }
}
