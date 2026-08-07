using Phoenix.Core.Entities;
using Phoenix.Engine.Interfaces;

namespace Phoenix.Engine.Services;

public class CalculationService
{
    private readonly IStrategyCalculator _calculator;

    public CalculationService(IStrategyCalculator calculator)
    {
        _calculator = calculator;
    }

    public TradePlan BuildTradePlan(Signal signal)
    {
        return _calculator.Calculate(signal);
    }
}