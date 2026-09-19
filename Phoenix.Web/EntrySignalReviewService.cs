using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class EntrySignalReviewService(
    BybitDemoClient bybit,
    ProfessionalSignalAnalysisService professional,
    TelegramNotifier telegram,
    DedicatedTelegramNotifier dedicatedTelegram,
    ILogger<EntrySignalReviewService> logger)
{
    public async Task RefreshAndNotifyAsync(ServerSignal signal, CancellationToken token)
    {
        try
        {
            var interval = string.IsNullOrWhiteSpace(signal.Timeframe) ? "15" : signal.Timeframe!;
            var candles = await bybit.GetKlinesAsync(signal.Symbol, interval, 1000, token);
            if (candles.Count < 52) return;
            var candidate = new SignalCandidate(signal.Symbol, interval, signal.Direction,
                signal.Ceiling, signal.Floor, signal.LastPrice ?? candles[^1].Close, signal.EntryPrice,
                signal.TakeProfit, signal.StopLoss, signal.StopLoss2, signal.RiskFreePrice,
                signal.Leverage ?? 1m, signal.Quantity, 0m,
                candles[0].OpenTime, candles[^1].OpenTime, candles[0].OpenTime, candles[^1].OpenTime,
                candles.Count, "Entry-time review", false, candles[^1].OpenTime);
            var analysis = await professional.AnalyzeAsync(candidate, candles, interval, token);
            signal.TargetSimilarityPercent = analysis.Prediction.TargetPercent;
            signal.StopSimilarityPercent = analysis.Prediction.StopPercent;
            signal.SimilaritySampleCount = analysis.Prediction.SampleCount;
            signal.TechnicalFeatures = analysis.Features;
            signal.AnalysisSummary = string.Join(" | ", analysis.Strengths.Concat(analysis.Risks));
            signal.MarketRegime = analysis.MarketRegime;
            var image = SignalChartRenderer.Render(candles, candidate, false,
                Badge(interval), analysis.Prediction.TargetPercent, analysis.Prediction.StopPercent);
            var caption = $"🎯 <b>قیمت به نقطه ورود رسید</b>\nنماد: {signal.Symbol}\nجهت: {signal.Direction}\n" +
                $"احتمال جدید تارگت: {F(analysis.Prediction.TargetPercent)}٪\n" +
                $"احتمال جدید استاپ: {F(analysis.Prediction.StopPercent)}٪\n" +
                $"رژیم بازار: {analysis.MarketRegime}\n\nاین تصویر و درصدها مربوط به همین لحظهٔ ورود هستند.";
            if (dedicatedTelegram.Owns(signal.RequestedByUsername))
                await dedicatedTelegram.SendEntryReviewAsync(image, caption, signal.Id, token);
            else
                await telegram.SendEntryReviewAsync(image, caption, signal.Id, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Entry-time AI review failed for {Symbol}; order flow continues", signal.Symbol);
            await telegram.EntryReachedAsync(signal, token);
        }
    }

    private static string Badge(string value) => value switch
        { "5" => "5M", "15" => "15M", "60" => "1H", "240" => "4H", "D" => "1D", _ => value };
    private static string F(decimal? value) => value?.ToString("0.0") ?? "—";
}
