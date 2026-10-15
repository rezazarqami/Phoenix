namespace Phoenix.Web;

/// <summary>Sampled-price paper outcomes, never exchange fills or executable orders.</summary>
public static class ObservationOutcomeTracker
{
    public static void TrackFilled(ServerSignal signal, decimal price, DateTime at)
    {
        if (signal.CompletedAtUtc is not null || signal.Status != "Filled" || price <= 0) return;
        signal.LastPrice = price;
        if (DemoOrderWorker.TargetReached(signal, price)) Complete(signal, "Target", at);
        // A gap past the original stop must not be advertised as a protected fill.
        else if (DemoOrderWorker.StopLossReached(signal, price))
        {
            Complete(signal, "StopLoss", at);
            signal.FailureReason = ProfessionalSignalAnalysisService.ExplainStop(signal.TechnicalFeatures);
        }
        else if (signal.ObservationPolicyVersion == 1 && signal.RiskFreePrice is { } activation &&
                 signal.StopLoss2 is { } protection)
        {
            if (signal.RiskFreeReachedAtUtc is null)
            {
                if (signal.Direction == "Long" ? price >= activation : signal.Direction == "Short" && price <= activation)
                    signal.RiskFreeReachedAtUtc = at;
            }
            else if (at > signal.RiskFreeReachedAtUtc &&
                     (signal.Direction == "Long" ? price <= protection : signal.Direction == "Short" && price >= protection))
                Complete(signal, "RiskFree", at);
        }
    }

    public static void Expire(ServerSignal signal, DateTime at)
    {
        if (signal.CompletedAtUtc is not null || signal.Status != "Pending") return;
        Complete(signal, "Expired", at);
        signal.ExpireReason = "Observation initial boundary reached before entry";
    }

    private static void Complete(ServerSignal signal, string outcome, DateTime at)
    {
        signal.Status = outcome == "Expired" ? "Expired" : "Completed";
        signal.Outcome = outcome;
        signal.CompletedAtUtc = at;
        if (outcome == "Target") signal.TargetReachedAtUtc = at;
        else if (outcome == "StopLoss") signal.StopLossReachedAtUtc = at;
        else if (outcome == "RiskFree") signal.RiskFreeClosedAtUtc = at;
        else signal.ExpiredAtUtc = at;
    }
}
