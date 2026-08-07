using System.Security.Cryptography;
using System.Text;

namespace Phoenix.Engine.Exchanges.Bybit;

public static class BybitSignature
{
    public static string CreateHmacSha256(string secret, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(payload);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
