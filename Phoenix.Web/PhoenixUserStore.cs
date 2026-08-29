using System.Security.Cryptography;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record PhoenixUser(string Username, string PasswordHash, string Salt, bool ViewerOnly,
    DateTime CreatedAtUtc);

public sealed class PhoenixUserStore
{
    private const string DefaultPath = "/var/lib/phoenix/users.json";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string StorePath =>
        Environment.GetEnvironmentVariable("PHOENIX_USERS_FILE") ?? DefaultPath;

    public async Task<IReadOnlyList<PhoenixUser>> GetAllAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }

    public async Task<PhoenixUser?> AuthenticateAsync(string username, string password, CancellationToken token)
    {
        var users = await GetAllAsync(token);
        var user = users.FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null) return null;
        var actual = Hash(password, Convert.FromBase64String(user.Salt));
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(actual),
            Convert.FromBase64String(user.PasswordHash)) ? user : null;
    }

    public async Task<bool> ExistsAsync(string username, bool viewerOnly, CancellationToken token)
    {
        var users = await GetAllAsync(token);
        return users.Any(item => item.ViewerOnly == viewerOnly &&
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddAsync(string username, string password, bool viewerOnly, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var users = (await ReadAsync(token)).ToList();
            if (users.Any(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("این نام کاربری قبلاً ثبت شده است.");
            var salt = RandomNumberGenerator.GetBytes(16);
            users.Add(new PhoenixUser(username, Hash(password, salt), Convert.ToBase64String(salt), viewerOnly,
                DateTime.UtcNow));
            await WriteAsync(users, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var users = (await ReadAsync(token)).ToList();
            var removed = users.RemoveAll(item =>
                string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await WriteAsync(users, token);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private static string Hash(string password, byte[] salt) => Convert.ToBase64String(
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32));

    private static async Task<IReadOnlyList<PhoenixUser>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(StorePath)) return Array.Empty<PhoenixUser>();
        await using var stream = File.OpenRead(StorePath);
        return await JsonSerializer.DeserializeAsync<List<PhoenixUser>>(stream, cancellationToken: token) ?? [];
    }

    private static async Task WriteAsync(IReadOnlyList<PhoenixUser> users, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(StorePath)
            ?? throw new InvalidOperationException("مسیر ذخیره کاربران معتبر نیست.");
        Directory.CreateDirectory(directory);
        var temporary = StorePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, users, cancellationToken: token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, StorePath, true);
    }
}

public sealed record PhoenixSessionIdentity(string Username, bool ViewerOnly, bool IsAdmin);
