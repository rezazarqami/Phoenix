using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

public sealed class SignalSubmissionService(
    ServerOrderStore store,
    StrategyCalculator calculator,
    BybitDemoClient bybit,
    BybitInstrumentCatalog catalog,
    TelegramNotifier telegram,
    SignalSimilarityService similarity,
    Strategy2Runtime strategy2,
    Strategy2TelegramNotifier strategy2Telegram)
{
    public async Task<IResult> SubmitAsync(SignalRequest request, CancellationToken token,
        string? requestedByUsername = null)
    {
        var outcome = await QueueAsync(request, token, requestedByUsername: requestedByUsername);
        return outcome.Signal is not null
            ? Results.Created($"/api/signals/{outcome.Signal.Id}", outcome.Signal)
            : Results.BadRequest(new { error = outcome.Error });
    }

    public async Task<SignalSubmissionOutcome> QueueAsync(SignalRequest request, CancellationToken token,
        SignalEvidence? evidence = null, string? requestedByUsername = null, bool notifyTelegram = true)
    {
        var error = request.Validate();
        if (error is not null) return new(null, error);
        if (!await catalog.ContainsAsync(request.Symbol, token))
            return new(null, "نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد.");

        try
        {
            var direction = Enum.Parse<Direction>(request.Direction);
            var signal = new Signal
            {
                Id = Guid.NewGuid(), Symbol = request.Symbol.Trim().ToUpperInvariant(), Direction = direction,
                High = request.Ceiling, Low = request.Floor, PositionSizeUsdt = request.PositionSizeUsdt,
                CreatedAt = DateTime.UtcNow, Status = SignalStatus.WaitingEntry
            };
            signal.TradePlan = calculator.Calculate(signal);
            var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
            signal.TradePlan.Leverage = BybitLeverageRules.Normalize(signal.TradePlan.Leverage, rules);
            var position = new ExecutionManager().PreparePosition(signal)
                ?? throw new InvalidOperationException("ساخت موقعیت برنامه‌ریزی‌شده ناموفق بود.");
            var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
            var queued = ServerSignal.FromPreview(signal, preview, signal.TradePlan.Leverage);
            queued.RequestedByUsername = NormalizeUsername(requestedByUsername);
            queued.Timeframe = evidence?.Timeframe;
            queued.ChartMode = evidence?.ChartMode;
            queued.TechnicalFeatures = evidence?.TechnicalFeatures;
            queued.ProposalEvidence = evidence?.ProposalEvidence;
            queued.AnalysisSummary = evidence?.AnalysisSummary;
            queued.MarketRegime = evidence?.MarketRegime;
            var similarityResult = queued.ProposalEvidence?.Prediction ?? await similarity.CalculateAsync(queued, token);
            queued.TargetSimilarityPercent = similarityResult.TargetPercent;
            queued.StopSimilarityPercent = similarityResult.StopPercent;
            queued.SimilaritySampleCount = similarityResult.SampleCount;
            queued.SimilarityEvidence = similarityResult.Evidence;
            queued.TargetOutcomePercent = similarityResult.TargetOutcomePercent;
            queued.StopOutcomePercent = similarityResult.StopOutcomePercent;
            queued.OutcomeValidationCount = similarityResult.OutcomeValidationCount;
            await store.AddAsync(queued, token, evidence);
            if (notifyTelegram) await telegram.SignalQueuedAsync(queued, token);

            if (strategy2.Options.Enabled)
            {
                var strategy2Leverage = BybitLeverageRules.Normalize(
                    StrategyCalculator.CalculateLeverage(signal.TradePlan.EntryPrice, signal.TradePlan.TakeProfit, 20m), rules);
                var strategy2Signal = ServerSignal.FromPreview(signal, preview, strategy2Leverage);
                strategy2Signal.RequestedByUsername = queued.RequestedByUsername;
                strategy2Signal.PositionSizeUsdt = 0m;
                strategy2Signal.Quantity = 0m;
                strategy2Signal.OrderLinkId = $"s2-{strategy2Signal.Id:N}"[..35];
                strategy2Signal.Timeframe = evidence?.Timeframe;
                strategy2Signal.ChartMode = evidence?.ChartMode;
                await strategy2.Store.AddAsync(strategy2Signal, token);
                if (notifyTelegram) await strategy2Telegram.QueuedAsync(strategy2Signal, token);
            }
            return new(queued, null);
        }
        catch (Exception exception) { return new(null, exception.Message); }
    }

    public async Task<IResult> SubmitManualAsync(ManualSignalRequest input, string? username, CancellationToken token)
    {
        if (input.Validate() is { } error) return Results.BadRequest(new { error });
        await store.ExecutionGate.WaitAsync(token);
        ServerSignal queued;
        try
        {
            // Stable client IDs make an uncertain-response retry safe across restart.
            if (await store.FindSignalAsync(input.RequestId, token) is { } existing)
            {
                if (existing.Origin != ManualSignals.Origin || existing.RequestedByUsername != NormalizeUsername(username))
                    return Results.Conflict(new { error = "شناسه ثبت قبلاً استفاده شده است." });
                return Results.Ok(existing);
            }
            if (!await catalog.ContainsAsync(input.Symbol, token)) return Results.BadRequest(new { error = "نماد معتبر نیست." });
            queued = ManualSignals.Build(input, await bybit.GetInstrumentRulesAsync(input.Symbol, token));
            queued.RequestedByUsername = NormalizeUsername(username);
            await store.AddAsync(queued, token);
        }
        catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        finally { store.ExecutionGate.Release(); }
        // The durable queue is authoritative; Telegram delivery cannot undo registration.
        try { await telegram.SignalQueuedAsync(queued, token); } catch { }
        return Results.Created($"/api/signals/{queued.Id}", queued);
    }

    private static string? NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();
}

public sealed record SignalSubmissionOutcome(ServerSignal? Signal, string? Error);
