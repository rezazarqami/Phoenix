using Phoenix.Web;

internal static class EntryIntakeSwitchTests
{
    public static void PausedEntries() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "queue.json");
        try
        {
            var store = new ServerOrderStore(path);
            var queued = await AddAsync(store, "Pending");
            var reached = await AddAsync(store, "Pending");
            var buffered = await AddAsync(store, "Pending");
            var filled = await AddAsync(store, "Filled");
            var claimed = await AddAsync(store, "Pending");
            Require(await store.TryClaimSubmissionAsync(claimed.Id, 100m), "Expected an entry claim before pause");
            claimed = (await store.GetAllAsync()).Single(item => item.Id == claimed.Id);
            await store.EntryIntake.SetPausedAsync(true, default);
            Require(new ServerOrderStore(path).EntryIntake.Paused, "Pause must survive a server restart");
            var duringPause = DateTime.UtcNow;
            Require(!await store.TryExpirePausedEntryAsync(queued.Id, 101m, duringPause, store.EntryIntake, default),
                "A pending signal above the long entry must stay in the queue");
            Require(!await store.TryClaimSubmissionAsync(reached.Id, 100m, observedAtUtc: duringPause),
                "A signal reaching entry while paused must not be claimed");
            await store.EntryIntake.SetPausedAsync(false, default);
            Require(!await store.TryClaimSubmissionAsync(buffered.Id, 100m, observedAtUtc: duringPause),
                "A buffered tick from the pause must not enter after resuming");
            var sent = false;
            var dispatch = await store.DispatchEntryAsync(claimed, store.EntryIntake, () =>
            {
                sent = true;
                return Task.FromResult("sent");
            }, default);
            Require(dispatch is null && !sent, "A claim interrupted by a pause must not be dispatched after resume");
            var snapshot = await store.GetAllAsync();
            foreach (var id in new[] { reached.Id, buffered.Id, claimed.Id })
            {
                var expired = snapshot.Single(item => item.Id == id);
                Require(expired.Status == "Expired" && expired.ExpireReason == "OperatorEntryPause" &&
                    expired.ExpiredAtUtc is not null, "Expiration must be saved with its actual reason");
            }
            Require(snapshot.Single(item => item.Id == filled.Id).Status == "Filled", "An open trade must stay open");
            Require(snapshot.Single(item => item.Id == queued.Id).Status == "Pending", "Untouched signals must stay pending");
            Require(await store.TryClaimSubmissionAsync(queued.Id, 100m), "Fresh entries must resume normally");

            var strategyTwo = new ServerOrderStore(Path.Combine(root, "strategy-two.json"));
            var mirrored = await AddAsync(strategyTwo, "Pending");
            await store.EntryIntake.SetPausedAsync(true, default);
            Require(await strategyTwo.TryClaimExclusiveSubmissionAsync(mirrored.Id, 100m, intake: store.EntryIntake)
                == ExclusiveClaimResult.Unavailable, "The same switch must guard strategy two");
            Require((await strategyTwo.GetAllAsync()).Single().ExpireReason == "OperatorEntryPause",
                "Strategy two must persist the same expiration reason");
        }
        finally
        {
            SignalHistoryStore.ClearConnectionPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task<ServerSignal> AddAsync(ServerOrderStore store, string status)
    {
        var signal = new ServerSignal
        {
            Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", EntryPrice = 100m,
            Status = status, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            FilledAtUtc = status == "Filled" ? DateTime.UtcNow.AddSeconds(-10) : null
        };
        await store.AddAsync(signal);
        return signal;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
