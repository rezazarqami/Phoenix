using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Phoenix.Web;

internal static class PersistentSessionTests
{
    public static void PersistentRoles() => WithCredentials(() =>
    {
        foreach (var role in new[] { "admin", "viewer", "editor" })
        {
            var username = role == "admin" ? "session-test-admin" : "session-test-user";
            var token = PhoenixSessionAuth.CreateToken(username, role == "viewer", role == "admin");
            var payload = Encoding.UTF8.GetString(Decode(token.Split('.')[0]));
            Check(payload.Split('\n')[2] == "persistent-v1");
            // Persistent tokens do not encode an expiry at 12 hours or any later
            // server time; a fresh request after app/process recreation accepts it.
            foreach (var path in new[] { "/api/auth/me", "/api/analysis/auth/me", "/analysis/coins" })
            {
                var context = Request(token, path);
                Check(PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity));
                Check(identity.Username == username && identity.IsAdmin == (role == "admin") && identity.ViewerOnly == (role == "viewer"));
                Check(AnalysisSessionAuth.TryGetIdentity(context.Request, out var analysis) && analysis == identity);
            }
        }
    });

    public static void LegacyAndTampering() => WithCredentials(() =>
    {
        var legacy = Sign("session-test-admin\nadmin\n" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var context = Request(legacy, "/api/auth/me");
        Check(PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity));
        PhoenixSessionAuth.RenewSessionCookie(context, identity);
        var upgraded = ReadSetCookie(context);
        Check(Encoding.UTF8.GetString(Decode(upgraded.Split('.')[0])).EndsWith("persistent-v1"));
        Check(PhoenixSessionAuth.TryGetIdentity(Request(upgraded).Request, out _));
        var expired = Sign("session-test-admin\nadmin\n" + DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
        Check(!PhoenixSessionAuth.TryGetIdentity(Request(expired).Request, out _));
        Check(!PhoenixSessionAuth.TryGetIdentity(Request(Sign("session-test-admin\nadmin\ninvalid")).Request, out _));
        var persistent = PhoenixSessionAuth.CreateToken("session-test-user", true, false);
        var tampered = Encode(Encoding.UTF8.GetBytes("session-test-user\nadmin\npersistent-v1")) + "." + persistent.Split('.')[1];
        Check(!PhoenixSessionAuth.TryGetIdentity(Request(tampered).Request, out _));
        Check(!PhoenixSessionAuth.TryGetIdentity(Request(Sign("wrong-admin\nadmin\npersistent-v1")).Request, out _));
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", "rotated-session-test-password");
        Check(!PhoenixSessionAuth.TryGetIdentity(Request(upgraded).Request, out _));
    });

    public static void CookieLifecycle() => WithCredentials(() =>
    {
        var token = PhoenixSessionAuth.CreateToken("session-test-admin", false, true);
        foreach (var path in new[] { "/", "/api/signals", "/analysis/coins", "/api/analysis/auth/me" })
        {
            var context = Request(token, path);
            Check(PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity));
            PhoenixSessionAuth.RenewSessionCookie(context, identity);
            var header = context.Response.Headers.SetCookie.ToString();
            Check(header.Contains("max-age=34560000") && header.Contains("httponly") && header.Contains("samesite=strict") && header.Contains("path=/"));
            Check(PhoenixSessionAuth.TryGetIdentity(Request(ReadSetCookie(context)).Request, out _));
        }
        foreach (var path in new[] { "/api/auth/logout", "/api/analysis/auth/logout" })
        {
            var context = Request(token, path);
            Check(PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity));
            PhoenixSessionAuth.RenewSessionCookie(context, identity);
            Check(context.Response.Headers.SetCookie.Count == 0);
            PhoenixSessionAuth.ClearSessionCookies(context.Response);
            var headers = context.Response.Headers.SetCookie;
            Check(headers.Count == 2 && headers.All(x => x!.Contains("expires=Thu, 01 Jan 1970")));
            Check(!PhoenixSessionAuth.TryGetIdentity(new DefaultHttpContext().Request, out _));
        }
    });

    private static DefaultHttpContext Request(string token, string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Cookie = PhoenixSessionAuth.CookieName + "=" + token;
        return context;
    }
    private static string ReadSetCookie(DefaultHttpContext context) =>
        context.Response.Headers.SetCookie[0]!.Split(';')[0][(PhoenixSessionAuth.CookieName.Length + 1)..];
    private static string Sign(string payload) => Encode(Encoding.UTF8.GetBytes(payload)) + "." +
        Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("PHOENIX_AUTH_PASSWORD")!), Encoding.UTF8.GetBytes(payload)));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    }
    private static void WithCredentials(Action test)
    {
        var username = Environment.GetEnvironmentVariable("PHOENIX_AUTH_USERNAME");
        var password = Environment.GetEnvironmentVariable("PHOENIX_AUTH_PASSWORD");
        try {
            Environment.SetEnvironmentVariable("PHOENIX_AUTH_USERNAME", "session-test-admin");
            Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", "session-test-password");
            test();
        }
        finally {
            Environment.SetEnvironmentVariable("PHOENIX_AUTH_USERNAME", username);
            Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", password);
        }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Persistent session regression failed."); }
}
