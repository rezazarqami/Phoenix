using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

public sealed class SignalPlanPreviewer(StrategyCalculator calculator, BybitDemoClient bybit,
    BybitInstrumentCatalog catalog)
{
    public async Task<SignalPlanPreview> PreviewAsync(SignalRequest request, CancellationToken token)
    {
        var error = request.Validate();
        if (error is not null) throw new ArgumentException(error);
        if (!await catalog.ContainsAsync(request.Symbol, token))
            throw new ArgumentException("نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد.");
        var signal = new Signal
        {
            Id = Guid.NewGuid(), Symbol = request.Symbol.Trim().ToUpperInvariant(),
            Direction = Enum.Parse<Direction>(request.Direction), High = request.Ceiling,
            Low = request.Floor, PositionSizeUsdt = request.PositionSizeUsdt,
            CreatedAt = DateTime.UtcNow, Status = SignalStatus.WaitingEntry
        };
        signal.TradePlan = calculator.Calculate(signal);
        var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
        signal.TradePlan.Leverage = BybitLeverageRules.Normalize(signal.TradePlan.Leverage, rules);
        var position = new ExecutionManager().PreparePosition(signal)
            ?? throw new InvalidOperationException("پیش‌نمایش موقعیت ساخته نشد.");
        var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
        return new SignalPlanPreview(preview.Price, preview.TakeProfit, preview.StopLoss,
            signal.TradePlan.StopLoss2, signal.TradePlan.RiskFreePrice,
            signal.TradePlan.Leverage, preview.Quantity);
    }
}

public sealed record SignalPlanPreview(decimal EntryPrice, decimal TakeProfit, decimal StopLoss,
    decimal? StopLoss2, decimal? RiskFreePrice, decimal Leverage, decimal Quantity);
