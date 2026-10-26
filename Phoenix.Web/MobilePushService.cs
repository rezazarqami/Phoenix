using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phoenix.Web;

// Device registrations are private server state. No service-account key is ever
// returned to a browser/APK. Real signal delivery uses the durable per-device outbox.
public sealed partial class MobilePushService
{
    private readonly string connectionString;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1), oauthGate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessUntil;
    private bool initialized;
    public bool Configured => File.Exists(Environment.GetEnvironmentVariable("PHOENIX_FIREBASE_SERVICE_ACCOUNT") ?? "");
    public MobilePushService(IHttpClientFactory factory)
    {
        http = factory.CreateClient("MobilePush");
        var queue = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH") ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        // Keep the existing notification database location for installed devices.
        var legacyStore = Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(queue))!, "personal.db");
        var path = Path.GetFullPath(legacyStore) + ".mobile.db";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(connectionString);
        await db.OpenAsync(ct);
        if (!initialized)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS mobile_devices(owner TEXT NOT NULL, device_id TEXT NOT NULL, token TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY(owner,device_id));";
            await cmd.ExecuteNonQueryAsync(ct);
            await InitializeOutboxAsync(db, ct);
            initialized = true;
        }
        return db;
    }
    public async Task RegisterAsync(string owner, MobileRegistration input, CancellationToken ct)
    {
        if (input.DeviceId == Guid.Empty || input.Token is null || input.Token.Length is < 20 or > 4096 || !Regex.IsMatch(input.Token, @"\A[A-Za-z0-9:_\-.]+\z"))
            throw new ArgumentException("شناسهٔ دستگاه یا توکن اعلان معتبر نیست.");
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM mobile_devices WHERE token=$t AND (owner<>$o OR device_id<>$d); INSERT INTO mobile_devices(owner,device_id,token,updated,enabled_since,enrollment) VALUES($o,$d,$t,$u,$u,$enrollment) ON CONFLICT(owner,device_id) DO UPDATE SET token=$t,updated=$u,enabled_since=CASE WHEN mobile_devices.enrollment<>excluded.enrollment THEN excluded.enabled_since ELSE mobile_devices.enabled_since END,enrollment=excluded.enrollment; DELETE FROM mobile_devices WHERE owner=$o AND device_id NOT IN(SELECT device_id FROM mobile_devices WHERE owner=$o ORDER BY updated DESC LIMIT 20);";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$d", input.DeviceId.ToString());
            cmd.Parameters.AddWithValue("$enrollment", input.EnrollmentId?.ToString() ?? "");
            cmd.Parameters.AddWithValue("$t", input.Token); cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string owner, Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM mobile_devices WHERE owner=$o AND device_id=$d";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$d", id.ToString()); await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }
    }
    public Task<MobilePushResult> SendAsync(string owner, string kind, string destination, CancellationToken ct) =>
        SendCoreAsync(owner, kind, Guid.NewGuid().ToString(), "Test", null, ct);
    private async Task<MobilePushResult> SendCoreAsync(string owner, string kind, string eventId, string eventKind, Guid? deviceId, CancellationToken ct)
    {
        // Deliberately generic notification content: private records are fetched
        // only after the user opens and unlocks the app.
        if (kind is not ("signal" or "test")) throw new ArgumentException("نوع اعلان معتبر نیست.");
        var destination = "/phoenix";
        if (!Configured) return new(0, 0, false);
        var tokens = new List<(Guid Id, string Token)>();
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT device_id,token FROM mobile_devices WHERE owner=$o AND ($d IS NULL OR device_id=$d)"; cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$d", deviceId?.ToString() ?? (object)DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) tokens.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }
        finally { gate.Release(); }
        if (tokens.Count == 0) return new(0, 0, true);
        var account = ReadAccount();
        if (!Regex.IsMatch(account.Project, @"\A[a-z0-9-]{4,100}\z")) throw new InvalidOperationException("Firebase project configuration is invalid.");
        var auth = await AuthorizeAsync(account, ct);
        int sent = 0, failed = 0;
        foreach (var item in tokens)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{account.Project}/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
            request.Content = JsonContent.Create(new { message = new { token = item.Token, data = new { kind, destination, eventId, eventKind }, android = new { priority = "high", ttl = "300s" } } });
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) { sent++; continue; }
                failed++;
                // Remove only the explicit FCM UNREGISTERED condition, not
                // transient network/configuration errors or a generic 404.
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array && details.EnumerateArray().Any(d => d.TryGetProperty("errorCode", out var code) && code.GetString() == "UNREGISTERED")) await RemoveAsync(owner, item.Id, ct);
            }
            catch (HttpRequestException) { failed++; }
            catch (JsonException) { /* Already counted as a failure. */ }
        }
        return new(sent, failed, true);
    }
    private (string Project, string Email, string Key) ReadAccount()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("PHOENIX_FIREBASE_SERVICE_ACCOUNT")!));
        var root = doc.RootElement;
        return (root.GetProperty("project_id").GetString()!, root.GetProperty("client_email").GetString()!, root.GetProperty("private_key").GetString()!);
    }
    private async Task<string> AuthorizeAsync((string Project, string Email, string Key) account, CancellationToken ct)
    {
        await oauthGate.WaitAsync(ct);
        try
        {
            if (accessToken is not null && accessUntil > DateTimeOffset.UtcNow.AddMinutes(2)) return accessToken;
            static string Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var payload = Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = account.Email, scope = "https://www.googleapis.com/auth/firebase.messaging", aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 }));
            var unsigned = header + "." + payload;
            using var rsa = RSA.Create(); rsa.ImportFromPem(account.Key);
            var jwt = unsigned + "." + Url(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = jwt }), ct);
            response.EnsureSuccessStatusCode();
            using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            accessToken = token.RootElement.GetProperty("access_token").GetString()!;
            accessUntil = DateTimeOffset.UtcNow.AddSeconds(token.RootElement.GetProperty("expires_in").GetInt32());
            return accessToken;
        }
        finally { oauthGate.Release(); }
    }
}
public sealed record MobileRegistration(Guid DeviceId, string Token, Guid? EnrollmentId = null);
public sealed record MobilePushResult(int Sent, int Failed, bool Configured);
public static class MobileEndpoints
{
    public static void MapMobileEndpoints(this WebApplication app)
    {
        foreach (var route in new[] { "/api/mobile", "/api/personal/mobile" })
        {
        var group = app.MapGroup(route).AddEndpointFilter(async (context, next) => {
            if (!PhoenixSessionAuth.TryGetIdentity(context.HttpContext.Request, out var identity) || !identity.IsAdmin) return Results.StatusCode(403);
            try { return await next(context); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        static string Owner(HttpRequest request) { PhoenixSessionAuth.TryGetIdentity(request, out var user); return user.Username; }
        group.MapGet("/status", (MobilePushService service) => Results.Ok(new { pushConfigured = service.Configured, signalEvents = true, clientConfig = MobileFirebaseConfig.FromEnvironment() }));
        group.MapPost("/devices", async (MobileRegistration input, HttpRequest request, MobilePushService service, CancellationToken ct) => { await service.RegisterAsync(Owner(request), input, ct); return Results.Ok(new { registered = true }); });
        group.MapDelete("/devices/{id:guid}", async (Guid id, HttpRequest request, MobilePushService service, CancellationToken ct) => { await service.RemoveAsync(Owner(request), id, ct); return Results.NoContent(); });
        group.MapPost("/test", async (HttpRequest request, MobilePushService service, CancellationToken ct) => {
            try { return Results.Ok(await service.SendAsync(Owner(request), "test", "/phoenix", ct)); }
            catch (Exception e) when (e is HttpRequestException or JsonException or CryptographicException or InvalidOperationException or IOException or KeyNotFoundException) { return Results.Json(new { error = "ارسال اعلان انجام نشد؛ تنظیم Push سرور نیاز به بررسی دارد." }, statusCode: 503); }
        }).RequireRateLimiting("account-login");
        }
    }
}
