using System.Text.Json;

namespace Phoenix.Web;

public sealed record EntryPauseRequest(bool? Paused);

public sealed class EntryIntakeSwitch
{
    public sealed record PauseWindow(DateTime StartedAt, DateTime? EndedAt);

    private readonly string path;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private volatile PauseWindow[] windows;

    public EntryIntakeSwitch(string path)
    {
        this.path = path;
        windows = File.Exists(path)
            ? JsonSerializer.Deserialize<PauseWindow[]>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid entry switch state.")
            : [];
    }

    public bool Paused => windows.LastOrDefault() is { EndedAt: null };

    internal bool BlocksObservation(DateTime observedAt)
    {
        var snapshot = windows;
        return snapshot.LastOrDefault() is { EndedAt: null } ||
            snapshot.Any(window => observedAt >= window.StartedAt && observedAt < window.EndedAt);
    }

    internal bool BlocksClaim(DateTime? claimedAt)
    {
        var latest = windows.LastOrDefault();
        return latest is not null && (latest.EndedAt is null || claimedAt is null || claimedAt.Value <= latest.StartedAt);
    }

    public async Task SetPausedAsync(bool paused, CancellationToken token)
    {
        await dispatchGate.WaitAsync(token);
        try
        {
            if (Paused == paused) return;
            var now = DateTime.UtcNow;
            var updated = paused
                ? windows.Append(new PauseWindow(now, null)).ToArray()
                : windows.Select(window => window.EndedAt is null ? window with { EndedAt = now } : window).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated), token);
                File.Move(temporary, path, overwrite: true);
                windows = updated;
            }
            finally { File.Delete(temporary); }
        }
        finally { dispatchGate.Release(); }
    }

    internal async Task<T?> DispatchAsync<T>(DateTime? claimedAt, Func<Task<T>> submit,
        Func<Task> expire, CancellationToken token) where T : class
    {
        Task<T> dispatched;
        await dispatchGate.WaitAsync(token);
        try
        {
            if (BlocksClaim(claimedAt))
            {
                await expire();
                return null;
            }
            dispatched = submit();
        }
        finally { dispatchGate.Release(); }
        return await dispatched;
    }
}
