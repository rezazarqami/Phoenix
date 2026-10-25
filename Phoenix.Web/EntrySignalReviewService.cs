using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class EntrySignalReviewService(
    BybitDemoClient bybit,
    TelegramNotifier telegram,
    DedicatedTelegramNotifier dedicatedTelegram,
    ILogger<EntrySignalReviewService> logger)
{
    public async Task<EntrySignalReview> BuildCurrentAsync(ServerSignal signal, CancellationToken token)
    {
        ApplyInitialEvidence(signal);
        var interval = string.IsNullOrWhiteSpace(signal.Timeframe) ? "15" : signal.Timeframe!;
        var chartInterval = SignalBatchService.ReviewChartInterval(interval);
        var chartCandles = await bybit.GetKlinesAsync(signal.Symbol, chartInterval, 1000, token);
        if (chartCandles.Count < 52)
            throw new InvalidOperationException("کندل کافی برای تصویر شرایط فعلی وجود ندارد.");
        var anchorStart = chartCandles[Math.Max(0, chartCandles.Count - 220)].OpenTime;
        var candidate = new SignalCandidate(signal.Symbol, interval, signal.Direction,
            signal.Ceiling, signal.Floor, signal.LastPrice ?? chartCandles[^1].Close, signal.EntryPrice,
            signal.TakeProfit, signal.StopLoss, signal.StopLoss2, signal.RiskFreePrice,
            signal.Leverage ?? 1m, signal.Quantity, 0m,
            anchorStart, chartCandles[^1].OpenTime, anchorStart, chartCandles[^1].OpenTime,
            chartCandles.Count, "Current market review", false, chartCandles[^1].OpenTime);
        var image = SignalChartRenderer.Render(chartCandles, candidate, false,
            Badge(chartInterval), signal.TargetSimilarityPercent, signal.StopSimilarityPercent,
            targetOutcome: signal.TargetOutcomePercent, stopOutcome: signal.StopOutcomePercent);
        return new(image, false);
    }

    // Restore the exact proposal fingerprint and result; never relabel it as
    // entry-time evidence or compare it again against a newer archive.
    internal static void ApplyInitialEvidence(ServerSignal signal)
    {
        if (signal.ProposalEvidence is not { } proposal) return;
        var initial = proposal.Prediction;
        signal.TechnicalFeatures = proposal.Features;
        signal.TargetSimilarityPercent = initial.TargetPercent;
        signal.StopSimilarityPercent = initial.StopPercent;
        signal.SimilaritySampleCount = initial.SampleCount;
        signal.SimilarityEvidence = initial.Evidence;
        signal.TargetOutcomePercent = null;
        signal.StopOutcomePercent = null;
        signal.OutcomeValidationCount = 0;
    }

    public async Task RefreshAndNotifyAsync(ServerSignal signal, CancellationToken token)
    {
        try
        {
            var review = await BuildCurrentAsync(signal, token);
            var caption = SignalReviewText.Outcome(signal.TargetOutcomePercent, signal.StopOutcomePercent, signal.OutcomeValidationCount) + $"\n\n🎯 <b>قیمت به نقطه ورود رسید</b>\nنماد: {signal.Symbol}\nجهت: {signal.Direction}\n" +
                SignalReviewText.Support(signal.SimilarityEvidence);
            var sent = dedicatedTelegram.Owns(signal.RequestedByUsername)
                ? await dedicatedTelegram.SendEntryReviewAsync(review.Image, caption, signal.Id, token)
                : await telegram.SendEntryReviewAsync(review.Image, caption, signal.Id, token);
            if (!sent) throw new InvalidOperationException("ارسال عکس لحظه ورود به تلگرام ناموفق بود.");
            signal.EntryReviewSentAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Entry-time chart failed for {Symbol}; order flow continues", signal.Symbol);
            await telegram.EntryReachedAsync(signal, token);
        }
    }

    private static string Badge(string value) => value switch
        { "5" => "5M", "15" => "15M", "60" => "1H", "240" => "4H", "D" => "1D", _ => value };
    private static string F(decimal? value) => value?.ToString("0.0") ?? "—";
}

public sealed record EntrySignalReview(byte[] Image, bool AiRefreshed);
