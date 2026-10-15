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

    private static object ResearchRow(string source, SignalHistoryItem row)
    {
        var s = row.Signal;
        var eligible = s.Id != Guid.Empty && (s.Outcome is "Target" or "StopLoss") &&
            s.EntryTriggeredAtUtc.HasValue && s.CompletedAtUtc > s.EntryTriggeredAtUtc &&
            s.EntryTechnicalFeatures is not null;
        return new
        {
            source, s.ObservationPolicyVersion, s.Id, s.Symbol, s.Direction, s.Timeframe, s.ChartMode,
            s.CreatedAtUtc, s.EntryTriggeredAtUtc, s.FilledAtUtc, s.CompletedAtUtc,
            s.TargetReachedAtUtc, s.StopLossReachedAtUtc, s.RiskFreeReachedAtUtc,
            s.RiskFreeClosedAtUtc, s.ExpiredAtUtc, s.Status, s.RecoveryOriginalStatus, s.Outcome, s.ExpireReason,
            s.EntryPrice, s.TakeProfit, s.StopLoss, s.StopLoss2, s.RiskFreePrice,
            s.Ceiling, s.Floor, s.AverageFillPrice,
            s.TargetSimilarityPercent, s.StopSimilarityPercent, s.SimilaritySampleCount,
            s.TargetOutcomePercent, s.StopOutcomePercent, s.OutcomeValidationCount, s.SimilarityEvidence,
            proposalEvidence = s.ProposalEvidence,
            entryFeatures = s.EntryTechnicalFeatures,
            latestFeatures = s.TechnicalFeatures,
            latestFeaturesAreFrozenProposal = false,
            predictionTimestampKnown = false,
            eligibleForEntryBinaryEvaluation = eligible,
            recordedEntryWindow = Window(row, s.EntryTriggeredAtUtc, s.Timeframe),
            recordedProposalWindow = Window(row, s.ProposalEvidence?.ObservedAtUtc, s.ProposalEvidence?.Timeframe),
            row.UpdatedAtUtc, row.RemovedAtUtc, row.HasImage
        };
    }

    private static object? Window(SignalHistoryItem row, DateTime? observed, string? timeframe) =>
        observed is { } start && OutcomeWindows.Horizon(timeframe) is { } minutes
            ? new { horizonMinutes=minutes, observedAtUtc=start, deadlineUtc=start.AddMinutes(minutes),
                label=OutcomeWindows.Classify(row,start,start.AddMinutes(minutes),DateTime.UtcNow),
                interpretation="Recorded first event; not reconstructed market path or an automatic trading exit." } : null;

    internal static object Page(DateTime from, DateTime to,
        IReadOnlyList<(string Source, IReadOnlyList<SignalHistoryItem> Rows)> sources,
        int offset, int limit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("صفحه خروجی معتبر نیست.");
        var all = sources.SelectMany(x => x.Rows.GroupBy(r => r.Signal.Id).Select(g => (x.Source, Row: g.First()))
            .OrderBy(r => r.Row.Signal.CreatedAtUtc).ThenBy(r => r.Row.Signal.Id)).ToArray();
        if (offset > all.Length) throw new ArgumentException("شماره صفحه از تعداد داده‌ها بیشتر است.");
        var rows = new List<object>();
        long bytes = 0;
        foreach (var item in all.Skip(offset).Take(limit))
        {
            token.ThrowIfCancellationRequested();
            var row = ResearchRow(item.Source, item.Row);
            bytes += JsonSerializer.SerializeToUtf8Bytes(row, Json).Length;
            if (bytes > 8 * 1024 * 1024) throw new ArgumentException("حجم صفحه زیاد است؛ تعداد هر صفحه را کمتر کنید.");
            rows.Add(row);
        }
        return new { schemaVersion = "phoenix-evaluation-page-v1", exportedAtUtc = DateTime.UtcNow,
            createdFromUtcInclusive = from, createdToUtcExclusive = to, total = all.Length,
            offset, count = rows.Count, nextOffset = offset + rows.Count < all.Length ? (int?)(offset + rows.Count) : null,
            completeForRequestedRange = offset == 0 && rows.Count == all.Length,
            notes = new[] { "Pages are sequential live reads, not an atomic snapshot.",
                "proposalEvidence is frozen observed evidence when present. Legacy proposal evidence remains null. latestFeatures and latest percentages are not frozen.",
                "Source populations must be evaluated separately; expiry/RiskFree are not StopLoss labels." }, rows };
    }

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
                    var data = JsonSerializer.SerializeToUtf8Bytes(ResearchRow(source, row), Json);
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
                        "Stored top-level percentages are latest values; proposalEvidence preserves original predictions when present. Older proposal evidence is unknown.",
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
