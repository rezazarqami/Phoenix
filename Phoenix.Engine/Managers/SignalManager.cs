using Phoenix.Core.Entities;
using Phoenix.Engine.Services;
using System.Linq;

namespace Phoenix.Engine.Managers;

public class SignalManager
{
    private readonly List<Signal> _signals = new();

    private readonly CalculationService _calculationService;

    public SignalManager(CalculationService calculationService)
    {
        _calculationService = calculationService;
    }

    public bool AddSignal(Signal signal)
    {
        if (_signals.Any(s => s.Id == signal.Id))
            return false;

        signal.TradePlan = _calculationService.BuildTradePlan(signal);

        _signals.Add(signal);

        return true;
    }
}