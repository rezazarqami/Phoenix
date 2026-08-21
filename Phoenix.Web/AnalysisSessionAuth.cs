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

    public static string CreateToken(string username, string password)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var payload = $"analysis\n{username}\n{expires.ToString(CultureInfo.InvariantCulture)}";
        return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Sign(payload, password);
    }

    public static bool IsValid(HttpRequest request)
    {
        if (!CredentialsConfigured(out var username, out var password) ||
            !request.Cookies.TryGetValue(CookieName, out var token)) return false;
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;
        try
        {
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            if (!FixedTimeEquals(parts[1], Sign(payload, password))) return false;
            var values = payload.Split('\n', 3);
            return values.Length == 3 && values[0] == "analysis" && FixedTimeEquals(values[1], username) &&
                   long.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) &&
                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
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
