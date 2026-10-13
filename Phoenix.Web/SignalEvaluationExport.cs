using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Phoenix.Web;

/// <summary>Explicit research schema: never serialize account or exchange credentials.</summary>
internal static class SignalEvaluationExport
{
    internal const int MaxRowsPerSource = 2000;
    private const int MaxBytes = 64 * 1024 * 1024;
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static byte[] Build(DateTime from, DateTime to,
        IReadOnlyList<(string Source, IReadOnlyList<SignalHistoryItem> Rows)> sources,
        CancellationToken token = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(3650))
            throw new ArgumentException("بازه باید معتبر و حداکثر ده سال باشد.");
        if (sources.Any(x => x.Rows.Count > MaxRowsPerSource))
            throw new ArgumentException("بیش از ۲۰۰۰ سیگنال در یکی از بخش‌ها وجود دارد؛ بازه تاریخ را کوتاه‌تر کنید. هیچ داده‌ای به‌صورت ناقص صادر نشد.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            long bytes = 0;
            var counts = new List<object>();
            foreach (var (source, rows) in sources)
            {
                if (source is not ("main" or "observation" or "strategy2"))
                    throw new ArgumentException("Unknown dataset source.");
                var unique = rows.GroupBy(x => x.Signal.Id).Select(x => x.First())
                    .OrderBy(x => x.Signal.CreatedAtUtc).ThenBy(x => x.Signal.Id).ToArray();
                using var stream = zip.CreateEntry(source + ".jsonl", CompressionLevel.Fastest).Open();
                foreach (var row in unique)
                {
                    token.ThrowIfCancellationRequested();
                    var s = row.Signal;
                    var eligible = s.Id != Guid.Empty && s.Outcome is "Target" or "StopLoss" &&
                        s.EntryTriggeredAtUtc.HasValue && s.CompletedAtUtc > s.EntryTriggeredAtUtc &&
                        s.EntryTechnicalFeatures is not null;
                    var data = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        source, s.Id, s.Symbol, s.Direction, s.Timeframe, s.ChartMode,
                        s.CreatedAtUtc, s.EntryTriggeredAtUtc, s.FilledAtUtc, s.CompletedAtUtc,
                        s.TargetReachedAtUtc, s.StopLossReachedAtUtc, s.RiskFreeReachedAtUtc,
                        s.RiskFreeClosedAtUtc, s.ExpiredAtUtc, s.Status, s.Outcome, s.ExpireReason,
                        s.EntryPrice, s.TakeProfit, s.StopLoss, s.StopLoss2, s.RiskFreePrice,
                        s.Ceiling, s.Floor, s.AverageFillPrice,
                        s.TargetSimilarityPercent, s.StopSimilarityPercent, s.SimilaritySampleCount,
                        s.TargetOutcomePercent, s.StopOutcomePercent, s.OutcomeValidationCount,
                        entryFeatures = s.EntryTechnicalFeatures,
                        latestFeatures = s.TechnicalFeatures,
                        latestFeaturesAreFrozenProposal = false,
                        predictionTimestampKnown = false,
                        eligibleForEntryBinaryEvaluation = eligible,
                        row.UpdatedAtUtc, row.RemovedAtUtc, row.HasImage
                    }, Json);
                    bytes += data.Length + 1;
                    if (bytes > MaxBytes) throw new ArgumentException("حجم داده زیاد است؛ بازه تاریخ را کوتاه‌تر کنید.");
                    stream.Write(data); stream.WriteByte((byte)'\n');
                }
                counts.Add(new { source, count = unique.Length,
                    outcomes = unique.GroupBy(x => x.Signal.Outcome ?? "Unresolved")
                        .ToDictionary(x => x.Key, x => x.Count()),
                    withEntryFeatures = unique.Count(x => x.Signal.EntryTechnicalFeatures is not null) });
            }
            using (var manifest = zip.CreateEntry("manifest.json").Open())
                JsonSerializer.Serialize(manifest, new
                {
                    schemaVersion = "phoenix-evaluation-v1", exportedAtUtc = DateTime.UtcNow,
                    createdFromUtcInclusive = from, createdToUtcExclusive = to,
                    counts, completeForRequestedRange = true,
                    notes = new[] {
                        "Range filters signal creation, not completion. Sources are read sequentially, not an atomic database snapshot.",
                        "Entry features and latest features are distinct. Latest features may have been refreshed after proposal.",
                        "Stored percentages are latest values, not immutable out-of-sample predictions; their timestamp is unknown.",
                        "Target/StopLoss labels are conditional on entry and binary resolution. Do not label expiry or RiskFree as stop.",
                        "Strategy2 is a separate population; never silently pool it with main or observation.",
                        "No images, credentials, usernames, balances, order IDs or free-text logs are included." }
                }, Json);
            using var readme = new StreamWriter(zip.CreateEntry("README.txt").Open(), new UTF8Encoding(false));
            readme.Write("خروجی ارزیابی فونیکس\nهر خط فایل‌های jsonl یک سیگنال است. توضیح فیلدها و محدودیت‌ها در manifest.json آمده است.\nmain: استراتژی اصلی؛ observation: فقط تأیید؛ strategy2: استراتژی دوم.\nاین فایل را برای بررسی همین پروژه ارسال کنید. نتیجه‌های واقعی حفظ شده‌اند؛ ویژگی‌های ناموجود ساخته نمی‌شوند.\n");
        }
        return output.ToArray();
    }
}
