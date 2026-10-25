using System.Text.Json;

namespace Phoenix.Web;

public sealed record TelegramAccessUser(long UserId, string DisplayName, string? Username, bool Enabled,
    DateTime CreatedAtUtc);

public sealed class TelegramAccessStore
{
    private const string DefaultPath = "/var/lib/phoenix/telegram-access.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static string StorePath => Environment.GetEnvironmentVariable("PHOENIX_TELEGRAM_ACCESS_FILE") ?? DefaultPath;

    public async Task<IReadOnlyList<TelegramAccessUser>> GetAllAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(long userId, string displayName, string? username, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var users = (await ReadAsync(token)).ToList();
            if (users.Any(user => user.UserId == userId))
                throw new InvalidOperationException("این شناسه تلگرام قبلاً ثبت شده است.");
            var normalizedUsername = username?.Trim().TrimStart('@');
            users.Add(new TelegramAccessUser(userId, displayName.Trim(),
                string.IsNullOrWhiteSpace(normalizedUsername) ? null : normalizedUsername, true, DateTime.UtcNow));
            await WriteAsync(users, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetEnabledAsync(long userId, bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var users = (await ReadAsync(token)).ToList();
            var index = users.FindIndex(user => user.UserId == userId);
            if (index < 0) return false;
            users[index] = users[index] with { Enabled = enabled };
            await WriteAsync(users, token);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(long userId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var users = (await ReadAsync(token)).ToList();
            var removed = users.RemoveAll(user => user.UserId == userId) > 0;
            if (removed) await WriteAsync(users, token);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private static async Task<IReadOnlyList<TelegramAccessUser>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(StorePath)) return Array.Empty<TelegramAccessUser>();
        await using var stream = File.OpenRead(StorePath);
        return await JsonSerializer.DeserializeAsync<List<TelegramAccessUser>>(stream, cancellationToken: token) ?? [];
    }

    private static async Task WriteAsync(IReadOnlyList<TelegramAccessUser> users, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(StorePath) ?? throw new InvalidOperationException("مسیر ذخیره دسترسی تلگرام معتبر نیست.");
        Directory.CreateDirectory(directory);
        var temporary = StorePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, users, cancellationToken: token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, StorePath, true);
    }
}
