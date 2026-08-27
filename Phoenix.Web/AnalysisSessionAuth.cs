using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Phoenix.Web;

public static class AnalysisSessionAuth
{
    public const string CookieName = "phoenix_analysis_session";

    public static bool CredentialsConfigured(out string username, out string password)
    {
        username = Environment.GetEnvironmentVariable("PHOENIX_ANALYSIS_USERNAME") ?? string.Empty;
        password = Environment.GetEnvironmentVariable("PHOENIX_ANALYSIS_PASSWORD") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    public static bool CredentialsMatch(string suppliedUsername, string suppliedPassword)
    {
        if (!CredentialsConfigured(out var username, out var password)) return false;
        return FixedTimeEquals(suppliedUsername, username) && FixedTimeEquals(suppliedPassword, password);
    }

    public static string CreateToken(string username, bool viewerOnly, bool isAdmin)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var role = isAdmin ? "admin" : viewerOnly ? "viewer" : "editor";
        var payload = $"analysis\n{username}\n{role}\n{expires.ToString(CultureInfo.InvariantCulture)}";
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
            var values = payload.Split('\n', 4);
            if (values.Length != 4 || values[0] != "analysis" || values[2] is not ("admin" or "viewer" or "editor") ||
                !long.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry) return false;
            var isAdmin = values[2] == "admin";
            var viewerOnly = values[2] == "viewer";
            if (isAdmin && !FixedTimeEquals(values[1], username)) return false;
            identity = new PhoenixSessionIdentity(values[1], viewerOnly, isAdmin);
            return true;
        }
        catch { return false; }
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
