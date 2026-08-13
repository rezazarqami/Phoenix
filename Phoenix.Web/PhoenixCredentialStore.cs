namespace Phoenix.Web;

public sealed class PhoenixCredentialStore
{
    private const string DefaultEnvironmentFile = "/etc/phoenix/phoenix.env";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task UpdateAsync(string username, string password, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Environment.GetEnvironmentVariable("PHOENIX_ENV_FILE") ?? DefaultEnvironmentFile;
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("مسیر فایل تنظیمات Phoenix معتبر نیست.");
            Directory.CreateDirectory(directory);

            var lines = File.Exists(path)
                ? await File.ReadAllLinesAsync(path, token)
                : Array.Empty<string>();
            var updated = lines
                .Where(line => !line.StartsWith("PHOENIX_AUTH_USERNAME=", StringComparison.Ordinal) &&
                               !line.StartsWith("PHOENIX_AUTH_PASSWORD=", StringComparison.Ordinal))
                .Append($"PHOENIX_AUTH_USERNAME={username}")
                .Append($"PHOENIX_AUTH_PASSWORD={password}")
                .ToArray();

            var temporaryPath = path + ".tmp";
            await File.WriteAllLinesAsync(temporaryPath, updated, token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, path, true);

            Environment.SetEnvironmentVariable("PHOENIX_AUTH_USERNAME", username);
            Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", password);
        }
        finally
        {
            _gate.Release();
        }
    }
}
