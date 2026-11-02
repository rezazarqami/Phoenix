namespace Phoenix.Web;

public sealed partial class ServerOrderStore
{
    // Caller holds ExecutionGate and has just confirmed that the Demo account is flat.
    internal async Task<Strategy2RecoveryResult> ResolveFlatStrategy2RecoveryAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            var recovered = signals.Where(s => s.Status == "RecoveryRequired").ToArray();
            if (recovered.Length == 0) return new(0, 0, false);
            var now = DateTime.UtcNow;
            var pending = signals.Where(s => s.Status == "Pending" && s.CompletedAtUtc is null).ToArray();
            // Persist archive changes first; keep the executable queue paused if archival fails.
            var resolved = new List<ServerSignal>();
            foreach (var original in recovered.Concat(pending))
            {
                var s = Clone(original);
                var neverSent = s.RecoveryOriginalStatus == "Pending" && s.SubmittedAtUtc is null &&
                    s.EntryTriggeredAtUtc is null && s.BybitOrderId is null && s.FilledAtUtc is null;
                s.Status = original.Status == "Pending" || neverSent ? "Cancelled" : "Completed";
                s.Outcome = s.Status == "Cancelled" ? "Cancelled" : "RecoveryResolved";
                s.CompletedAtUtc ??= now;
                s.RecoveryResolvedAtUtc = now;
                s.Error = s.Status == "Cancelled"
                    ? "صف قدیمی پس از تطبیق حساب دمو بایگانی شد؛ سفارش دوباره ارسال نشد."
                    : "حساب دمو بدون پوزیشن و سفارش باز تأیید شد؛ نتیجهٔ معاملهٔ قدیمی نامشخص است.";
                await _history.UpsertAsync(s, "Strategy2RecoveryResolved", token);
                resolved.Add(s);
            }
            // Save a detached list so a failed queue write cannot unpause in-memory execution.
            var ids = resolved.ToDictionary(s => s.Id);
            var next = signals.Select(s => ids.GetValueOrDefault(s.Id, s)).ToList();
            await SaveUnsafeAsync(next, token);
            _signals = next;
            return new(recovered.Length, pending.Length, false);
        }
        finally { _gate.Release(); }
    }
}
