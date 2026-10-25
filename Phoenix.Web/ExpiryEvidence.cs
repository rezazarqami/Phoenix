namespace Phoenix.Web;

// Samples the ticker while the pending signal waits for the target boundary.
// Keep the activation, the closest observed price to entry, and the final tick.
public static class ExpiryEvidence
{
    public static void Record(ServerSignal signal, decimal price, DateTime atUtc, bool final = false)
    {
        var trail = signal.ExpiryPriceTrail ??= [];
        var nearest = trail.Count == 0 ? decimal.MaxValue : trail.Min(x => Math.Abs(x.Price - signal.EntryPrice));
        if (!final && trail.Count > 0 && atUtc - trail[^1].AtUtc < TimeSpan.FromSeconds(5) &&
            Math.Abs(price - signal.EntryPrice) >= nearest) return;
        if (trail.Count > 0 && trail[^1].AtUtc == atUtc) trail[^1] = new(atUtc, price);
        else trail.Add(new(atUtc, price));
        if (trail.Count <= 240) return;
        var closest = trail.MinBy(x => Math.Abs(x.Price - signal.EntryPrice));
        var reduced = trail.Where((point, index) => index == 0 || index == trail.Count - 1 ||
            index % 2 == 0 || ReferenceEquals(point, closest)).ToList();
        signal.ExpiryPriceTrail = reduced;
    }

    public static bool ObservedEntryTouch(ServerSignal signal) => signal.Direction == "Long"
        ? signal.ExpiryPriceTrail?.Any(x => x.Price <= signal.EntryPrice) == true
        : signal.ExpiryPriceTrail?.Any(x => x.Price >= signal.EntryPrice) == true;
}
