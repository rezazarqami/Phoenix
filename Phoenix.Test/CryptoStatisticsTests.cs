using Phoenix.Web;

internal static class CryptoStatisticsTests
{
    public static void Lifetime()
    {
        WithStore((store, _) =>
        {
            var old = DateTime.UtcNow.AddYears(-12);
            Add(store, " btcusdt ", "Target", created: old);
            Add(store, "BTCUSDT", "StopLoss");
            Add(store, "BTCUSDT", "RiskFree");
            Add(store, "BTCUSDT", "ManualClosed");
            Add(store, "BTCUSDT", null, "Filled");
            Add(store, "BTCUSDT", null, "Closing");
            Add(store, "ETHUSDT", "Target");
            Add(store, "ADAUSDT", "StopLoss");
            Add(store, "BTCUSDT", null, "Pending");
            Add(store, "BTCUSDT", null, "Submitted");
            Add(store, "BTCUSDT", "Expired", "Expired");
            Add(store, "BTCUSDT", null, "Cancelled");
            Add(store, "BTCUSDT", null, "Error");
            var legacy = Add(store, "BTCUSDT", null, "Completed");
            legacy.StopLossReachedAtUtc = DateTime.UtcNow;
            store.UpdateAsync(legacy).GetAwaiter().GetResult();
            var report = store.GetCryptoStatisticsAsync().GetAwaiter().GetResult();
            Check(report.Count == 3 && report[0].Symbol == "BTCUSDT");
            Check(report[1].Symbol == "ADAUSDT" && report[2].Symbol == "ETHUSDT");
            var btc = report[0];
            Check(btc.Total == 7 && btc.Open == 2 && btc.Targets == 1 && btc.Stops == 2 &&
                btc.RiskFree == 1 && btc.OtherClosed == 1);
            Check(report.All(x => x.Total == x.Open + x.Targets + x.Stops + x.RiskFree + x.OtherClosed));
        });
    }

    public static void Migration()
    {
        WithStore((store, root) =>
        {
            var trade = Add(store, "BTCUSDT", null, "Filled");
            Check(store.GetCryptoStatisticsAsync().GetAwaiter().GetResult().Single().Open == 1);
            trade.Status = "Completed";
            trade.Outcome = "Target";
            trade.CompletedAtUtc = DateTime.UtcNow;
            store.UpdateAsync(trade).GetAwaiter().GetResult();
            store.UpdateAsync(trade).GetAwaiter().GetResult();
            Check(store.RemoveAsync(trade.Id).GetAwaiter().GetResult());
            var report = store.GetCryptoStatisticsAsync().GetAwaiter().GetResult().Single();
            Check(report.Total == 1 && report.Targets == 1 && report.Open == 0);
            var legacy = new ServerSignal { Id = Guid.NewGuid(), Symbol = "ETHUSDT",
                Status = "Filled", CreatedAtUtc = DateTime.UtcNow };
            File.WriteAllText(Path.Combine(root, "legacy.json"), System.Text.Json.JsonSerializer.Serialize(new[] { legacy },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            var restored = new ServerOrderStore(Path.Combine(root, "legacy.json"), Path.Combine(root, "legacy.db"));
            var migrated = restored.GetCryptoStatisticsAsync().GetAwaiter().GetResult().Single();
            Check(migrated.Symbol == "ETHUSDT" && migrated.Total == 1 && migrated.Open == 1);
            Check(store.GetCryptoStatisticsAsync().GetAwaiter().GetResult().Single().Symbol == "BTCUSDT");
        });
    }

    private static ServerSignal Add(ServerOrderStore store, string symbol, string? outcome,
        string status = "Completed", DateTime? created = null)
    {
        var signal = new ServerSignal { Id = Guid.NewGuid(), Symbol = symbol, Status = status,
            Outcome = outcome, CreatedAtUtc = created ?? DateTime.UtcNow,
            CompletedAtUtc = outcome is null ? null : DateTime.UtcNow };
        store.AddAsync(signal).GetAwaiter().GetResult();
        return signal;
    }

    private static void WithStore(Action<ServerOrderStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-crypto-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new ServerOrderStore(Path.Combine(root, "queue.json"), Path.Combine(root, "history.db")), root); }
        finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Crypto statistics regression failed.");
    }
}
