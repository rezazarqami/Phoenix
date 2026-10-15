using Phoenix.Web;

internal static class ObservationOutcomeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Transitions()
    {
        var at = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        foreach (var sign in new[] { 1m, -1m })
        {
            ServerSignal New() => new() { Id = Guid.NewGuid(), Direction = sign == 1 ? "Long" : "Short",
                Status = "Filled", EntryPrice = 100, TakeProfit = 100 + sign * 20,
                StopLoss = 100 - sign * 10, RiskFreePrice = 100 + sign * 15,
                StopLoss2 = 100 + sign * 10, ObservationPolicyVersion = 1, EntryTriggeredAtUtc = at };
            var s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 11, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 9, at.AddSeconds(2));
            Check(s.Outcome is null && s.RiskFreeReachedAtUtc is null, "Protection must first activate");
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(3));
            Check(s.RiskFreeReachedAtUtc == at.AddSeconds(3) && s.Outcome is null, "Activation is not closure");
            s = System.Text.Json.JsonSerializer.Deserialize<ServerSignal>(System.Text.Json.JsonSerializer.Serialize(s))!;
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 10, at.AddSeconds(4));
            Check(s.Outcome == "RiskFree" && s.RiskFreeClosedAtUtc == at.AddSeconds(4) && s.StopLossReachedAtUtc is null,
                "Restart preserves protection and risk-free must not become a stop");
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 20, at.AddSeconds(5));
            Check(s.Outcome == "RiskFree", "Terminal outcomes cannot be overwritten");
            s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 20, at.AddSeconds(2));
            Check(s.Outcome == "Target", "Target wins when price continues to target");
            s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 - sign * 12, at.AddSeconds(2));
            Check(s.Outcome == "StopLoss", "A gap cannot imply a successful protected exchange fill");
            s = New(); s.ObservationPolicyVersion = null;
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 10, at.AddSeconds(2));
            Check(s.Outcome is null && s.RiskFreeReachedAtUtc is null, "Legacy history must not silently acquire a new policy");
            s = New(); s.Status = "Pending"; s.EntryTriggeredAtUtc = null;
            ObservationOutcomeTracker.Expire(s, at);
            Check(s.Outcome == "Expired" && s.ExpiredAtUtc == at && s.Status == "Expired" && s.StopLossReachedAtUtc is null,
                "Unentered expiry is archived explicitly, not deleted or labeled stop");
        }
    }
}
