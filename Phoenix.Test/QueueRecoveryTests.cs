using Phoenix.Web;

internal static class QueueRecoveryTests
{
    public static void InactiveRecovery()
    {
        WithStore((directory, path, history, store) =>
        {
            var completed = Signal("Completed"); completed.Outcome = "Target"; completed.CompletedAtUtc = DateTime.UtcNow;
            var removed = Signal("Cancelled");
            store.AddAsync(completed).GetAwaiter().GetResult();
            store.AddAsync(removed).GetAwaiter().GetResult();
            store.RemoveAsync(removed.Id).GetAwaiter().GetResult();
            var before = store.GetHistoryAsync().GetAwaiter().GetResult();
            var damaged = new byte[4096]; File.WriteAllBytes(path, damaged);
            var restored = new ServerOrderStore(path, history).GetAllAsync().GetAwaiter().GetResult();
            if (restored.Count != 1 || restored[0].Id != completed.Id || restored[0].Outcome != "Target")
                throw new Exception("Recovery lost a snapshot or revived a removed record.");
            if (!File.ReadAllBytes(Directory.GetFiles(directory, "queue.json.corrupt-*").Single()).SequenceEqual(damaged))
                throw new Exception("Original damaged bytes were not preserved.");
            var after = store.GetHistoryAsync().GetAwaiter().GetResult();
            if (!before.Select(x => (x.Signal.Id,x.UpdatedAtUtc,x.RemovedAtUtc)).SequenceEqual(after.Select(x => (x.Signal.Id,x.UpdatedAtUtc,x.RemovedAtUtc))))
                throw new Exception("Recovery rewrote history metadata.");
            if (new ServerOrderStore(path, history).GetAllAsync().GetAwaiter().GetResult().Count != 1)
                throw new Exception("Recovered queue did not survive restart.");
        });
    }

    public static void RefusesUnknownOrEmptyHistory()
    {
        foreach (var status in new[] { "unknown", "empty" })
            WithStore((directory, path, history, store) =>
            {
                if (status != "empty") store.AddAsync(Signal(status)).GetAwaiter().GetResult();
                var damaged = new byte[128]; File.WriteAllBytes(path, damaged);
                try { new ServerOrderStore(path,history).GetAllAsync().GetAwaiter().GetResult(); throw new Exception("Unsafe recovery succeeded."); }
                catch (InvalidDataException) { }
                if (!File.ReadAllBytes(path).SequenceEqual(damaged)) throw new Exception("Rejected recovery modified original bytes.");
            });
    }

    public static void QuarantinesActiveHistory()
    {
        foreach (var status in new[] { "Pending", "Submitting", "Submitted", "Filled", "Closing" })
            WithStore((directory,path,history,store) =>
            {
                var original=Signal(status); store.AddAsync(original).GetAwaiter().GetResult();
                File.WriteAllBytes(path,new byte[128]);
                var recoveredStore=new ServerOrderStore(path,history);
                var recovered=recoveredStore.GetAllAsync().GetAwaiter().GetResult().Single();
                if (recovered.Status!="RecoveryRequired" || recovered.RecoveryOriginalStatus!=status || recovered.Outcome is not null)
                    throw new Exception("Recovery must preserve active evidence without inventing an outcome.");
                if (recoveredStore.GetHistoryAsync().GetAwaiter().GetResult().Single().Signal.Status!=status)
                    throw new Exception("Recovery modified archived execution evidence.");
                var next=Signal("Pending"); recoveredStore.AddAsync(next).GetAwaiter().GetResult();
                if (recoveredStore.TryClaimSubmissionAsync(next.Id,100).GetAwaiter().GetResult() ||
                    recoveredStore.TryClaimExclusiveSubmissionAsync(next.Id,100).GetAwaiter().GetResult()!=ExclusiveClaimResult.Unavailable)
                    throw new Exception("New submissions must wait until recovered active orders are reconciled.");
                if (new ServerOrderStore(path,history).GetAllAsync().GetAwaiter().GetResult().Single(x=>x.Id==original.Id).Status!="RecoveryRequired")
                    throw new Exception("Quarantine did not survive restart.");
            });
    }

    private static ServerSignal Signal(string status) => new() { Id=Guid.NewGuid(), Symbol="BTCUSDT", Direction="Long",
        Status=status, CreatedAtUtc=DateTime.UtcNow, Ceiling=120, Floor=80, EntryPrice=100, TakeProfit=110, StopLoss=90 };
    private static void WithStore(Action<string,string,string,ServerOrderStore> run)
    {
        var directory=Path.Combine(Path.GetTempPath(),"phoenix-recovery-"+Guid.NewGuid()); Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"queue.json"); var history=Path.Combine(directory,"history.db");
        try { run(directory,path,history,new ServerOrderStore(path,history)); }
        finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(directory,true); }
    }
}
