namespace Phoenix.Web;

internal static class OrderedConcurrentFetch
{
    internal static async Task<T[]> RunAsync<T>(IReadOnlyList<string> keys,
        Func<string, Task<T>> fetch, int concurrency, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(concurrency);
        var tasks = keys.Select(async key =>
        {
            await slots.WaitAsync(token);
            try { token.ThrowIfCancellationRequested(); return await fetch(key); }
            finally { slots.Release(); }
        }).ToArray();
        // WhenAll drains outstanding work before disposing the semaphore, including
        // on error/cancellation. Returned results follow input, not completion order.
        return await Task.WhenAll(tasks);
    }
}
