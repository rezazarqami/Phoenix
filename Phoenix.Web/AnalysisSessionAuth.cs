namespace Phoenix.Web;

// Compatibility facade: analysis has no independent credentials or session anymore.
public static class AnalysisSessionAuth
{
    public const string CookieName = "phoenix_analysis_session"; // only used to clear old cookies
    public static bool CredentialsConfigured(out string username, out string password) =>
        PhoenixSessionAuth.CredentialsConfigured(out username, out password);
    public static bool TryGetIdentity(HttpRequest request, out PhoenixSessionIdentity identity) =>
        PhoenixSessionAuth.TryGetIdentity(request, out identity);
}
