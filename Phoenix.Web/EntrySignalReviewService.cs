using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class EntrySignalReviewService(
    BybitDemoClient bybit,
    ProfessionalSignalAnalysisService professional,
    TelegramNotifier telegram,
    DedicatedTelegramNotifier dedicatedTelegram,
    ILogger<EntrySignalReviewService> logger)
{
    public async Task<EntrySignalReview> BuildCurrentAsync(ServerSignal signal, CancellationToken token)
    {
        var interval = string.IsNullOrWhiteSpace(signal.Timeframe) ? "15" : signal.Timeframe!;
        var candles = await bybit.GetKlinesAsync(signal.Symbol, interval, 1000, token);
        if (candles.Count < 52)
            throw new InvalidOperationException("کندل کافی برای تصویر شرایط فعلی وجود ندارد.");
        var anchorStart = candles[Math.Max(0, candles.Count - 220)].OpenTime;
        var candidate = new SignalCandidate(signal.Symbol, interval, signal.Direction,
            signal.Ceiling, signal.Floor, signal.LastPrice ?? candles[^1].Close, signal.EntryPrice,
            signal.TakeProfit, signal.StopLoss, signal.StopLoss2, signal.RiskFreePrice,
            signal.Leverage ?? 1m, signal.Quantity, 0m,
            anchorStart, candles[^1].OpenTime, anchorStart, candles[^1].OpenTime,
            candles.Count, "Current market review", false, candles[^1].OpenTime);
        ProfessionalSignalAnalysis? analysis = null;
        try
        {
            analysis = await professional.AnalyzeAsync(candidate, candles, interval, token);
            signal.TargetSimilarityPercent = analysis.Prediction.TargetPercent;
            signal.StopSimilarityPercent = analysis.Prediction.StopPercent;
            signal.SimilaritySampleCount = analysis.Prediction.SampleCount;
            signal.TechnicalFeatures = analysis.Features;
            signal.AnalysisSummary = string.Join(" | ", analysis.Strengths.Concat(analysis.Risks));
            signal.MarketRegime = analysis.MarketRegime;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Current AI refresh failed for {Symbol}; rendering with stored probabilities", signal.Symbol);
        }
        var image = SignalChartRenderer.Render(candles, candidate, false,
            Badge(interval), signal.TargetSimilarityPercent, signal.StopSimilarityPercent);
        return new(image, analysis is not null);
    }

    public async Task RefreshAndNotifyAsync(ServerSignal signal, CancellationToken token)
    {
        try
        {
            var review = await BuildCurrentAsync(signal, token);
            var caption = $"🎯 <b>قیمت به نقطه ورود رسید</b>\nنماد: {signal.Symbol}\nجهت: {signal.Direction}\n" +
                $"احتمال {(review.AiRefreshed ? "جدید" : "آخرین")} تارگت: {F(signal.TargetSimilarityPercent)}٪\n" +
                $"احتمال {(review.AiRefreshed ? "جدید" : "آخرین")} استاپ: {F(signal.StopSimilarityPercent)}٪\n" +
                $"رژیم بازار: {signal.MarketRegime ?? "در دسترس نیست"}\n\nاین تصویر مربوط به شرایط فعلی بازار در لحظه ورود است.";
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
