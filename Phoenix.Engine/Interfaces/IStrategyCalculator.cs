using Phoenix.Core.Entities;

namespace Phoenix.Engine.Interfaces;

public interface IStrategyCalculator
{
    TradePlan Calculate(Signal signal);
}