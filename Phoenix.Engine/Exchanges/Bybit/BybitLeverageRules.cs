namespace Phoenix.Engine.Exchanges.Bybit;

public static class BybitLeverageRules
{
    public static decimal Normalize(decimal calculated, BybitInstrumentRules rules)
    {
        if (calculated <= 0m || rules.LeverageStep <= 0m || rules.MaximumLeverage <= 0m)
            throw new ArgumentOutOfRangeException(nameof(calculated));

        var capped = Math.Min(calculated, rules.MaximumLeverage);
        var stepped = Math.Floor(capped / rules.LeverageStep) * rules.LeverageStep;
        return Math.Max(rules.MinimumLeverage, stepped);
    }
}
