using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Phoenix.Web;

public static class PhoenixSessionAuth
{
    public const string CookieName = "phoenix_session";
    private const string PersistentSession = "persistent-v1";
    // Browsers limit persistent cookie lifetimes. Renew this cookie on every
    // authenticated visit; the signed server token has no timed expiration.
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(400);

    public static bool CredentialsConfigured(out string username, out string password)
    {
        username = Environment.GetEnvironmentVariable("PHOENIX_AUTH_USERNAME") ?? string.Empty;
        password = Environment.GetEnvironmentVariable("PHOENIX_AUTH_PASSWORD") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    public static bool CredentialsMatch(string suppliedUsername, string suppliedPassword)
    {
        if (!CredentialsConfigured(out var username, out var password)) return false;
        return FixedTimeEquals(suppliedUsername, username) && FixedTimeEquals(suppliedPassword, password);
    }

    public static string CreateToken(string username, bool viewerOnly, bool isAdmin)
    {
        var role = isAdmin ? "admin" : viewerOnly ? "viewer" : "editor";
        var payload = $"{username}\n{role}\n{PersistentSession}";
        CredentialsConfigured(out _, out var password);
        return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Sign(payload, password);
    }

    public static bool TryGetIdentity(HttpRequest request, out PhoenixSessionIdentity identity)
    {
        identity = new PhoenixSessionIdentity(string.Empty, true, false);
        if (!CredentialsConfigured(out var username, out var password) ||
            !request.Cookies.TryGetValue(CookieName, out var token)) return false;
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;
        try
        {
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            if (!FixedTimeEquals(parts[1], Sign(payload, password))) return false;
            var values = payload.Split('\n', 3);
            if (values.Length != 3 || values[1] is not ("admin" or "viewer" or "editor")) return false;
            // Keep unexpired old sessions valid so they can be upgraded without
            // another login. Expired or malformed legacy tokens stay invalid.
            if (values[2] != PersistentSession &&
                (!long.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) ||
                 DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry)) return false;
            var isAdmin = values[1] == "admin";
            var viewerOnly = values[1] == "viewer";
            if (isAdmin && !FixedTimeEquals(values[0], username)) return false;
            identity = new PhoenixSessionIdentity(values[0], viewerOnly, isAdmin);
            return true;
        }
        catch { return false; }
    }

    public static void WriteSessionCookie(HttpResponse response, string token) =>
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false,
            MaxAge = CookieLifetime, Path = "/"
        });

    public static void RenewSessionCookie(HttpContext context, PhoenixSessionIdentity identity)
    {
        var path = context.Request.Path;
        if (path == "/api/auth/logout" || path == "/api/analysis/auth/logout") return;
        WriteSessionCookie(context.Response, CreateToken(identity.Username, identity.ViewerOnly, identity.IsAdmin));
    }

    public static void ClearSessionCookies(HttpResponse response)
    {
        response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        response.Cookies.Delete(AnalysisSessionAuth.CookieName, new CookieOptions { Path = "/" });
    }

    private static string Sign(string payload, string password) =>
        Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(payload)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    }
    private static bool FixedTimeEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
