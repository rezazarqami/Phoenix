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
    Strategy2Runtime strategy2,
    Strategy2TelegramNotifier strategy2Telegram)
{
    public async Task<IResult> SubmitAsync(SignalRequest request, CancellationToken token)
    {
        var error = request.Validate();
        if (error is not null) return Results.BadRequest(new { error });
        if (!await catalog.ContainsAsync(request.Symbol, token))
            return Results.BadRequest(new { error = "نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد." });

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
            SignalPlanPreviewer.ApplyRequestedQuantity(position, request.Quantity);
            var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
            var queued = ServerSignal.FromPreview(signal, preview, signal.TradePlan.Leverage);
            await store.AddAsync(queued, token);
            await telegram.SignalQueuedAsync(queued, token);

            if (strategy2.Options.Enabled)
            {
                var strategy2Leverage = BybitLeverageRules.Normalize(
                    StrategyCalculator.CalculateLeverage(signal.TradePlan.EntryPrice, signal.TradePlan.TakeProfit, 20m), rules);
                var strategy2Signal = ServerSignal.FromPreview(signal, preview, strategy2Leverage);
                strategy2Signal.PositionSizeUsdt = 0m;
                strategy2Signal.Quantity = 0m;
                strategy2Signal.OrderLinkId = $"s2-{strategy2Signal.Id:N}"[..35];
                await strategy2.Store.AddAsync(strategy2Signal, token);
                await strategy2Telegram.QueuedAsync(strategy2Signal, token);
            }
            return Results.Created($"/api/signals/{queued.Id}", queued);
        }
        catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
}
