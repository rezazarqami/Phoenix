using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phoenix.Web;

public record RiskSample(string Key, DateTimeOffset At, double Value);
public record RiskMetric(string Key, string Label, string Unit, double? Value, double? Baseline, int? Score, int Samples, DateTimeOffset? At, string State);
public record RiskEvent(string Id, string Title, string Source, DateTimeOffset At, string Url, string Importance);
public record RiskSource(string Name, DateTimeOffset? UpdatedAt, string State);
public record RiskAlert(string Id, DateTimeOffset At, string Kind, string Severity, string Title, string Detail);
public record RiskSnapshot(DateTimeOffset At, int? Score, string State, RiskMetric[] Metrics, RiskEvent[] Events, RiskSource[] Sources, RiskAlert[] Alerts, int StoredSamples, bool ObservationOnly = true);

// Deterministic, observational scoring. Scores describe unusual conditions, not probabilities.
public static class MarketRiskMath
{
    public static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
    public static int? Score(double value, IReadOnlyCollection<double> history, bool inverse = false, double floor = .000001)
    {
        if (history.Count < 60 || !double.IsFinite(value) || value < 0) return null;
        var median = Median(history);
        if (median <= 0 && inverse) return null;
        var scale = Math.Max(floor, Math.Max(median * .15, 1.4826 * Median(history.Select(v => Math.Abs(v - median)))));
        var deviation = Math.Max(0, inverse ? median - value : value - median) / scale;
        return (int)Math.Round(Math.Clamp(deviation * 20, 0, 100));
    }
    public static (int? Score, string State) Overall(IEnumerable<RiskMetric> metrics)
    {
        var a = metrics.ToArray();
        var scores = a.Where(m => m.State == "live" && m.Score.HasValue).Select(m => m.Score!.Value).OrderDescending().ToArray();
        if (scores.Length == 0) return (null, a.Any(m => m.State == "missing" || m.State == "stale") ? "unavailable" : "calibrating");
        // One extreme cannot disappear in an average. A partial snapshot never claims normality.
        var score = Math.Max(scores[0], (int)Math.Round(scores.Take(3).Average()));
        var state = scores[0] >= 90 || scores.Count(s => s >= 70) >= 2 ? "danger" : score >= 60 ? "warning" : a.Any(m => m.State != "live") ? "partial" : "normal";
        return (score, state);
    }
}

public static class MarketRiskCalendar
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static DateTimeOffset Eastern(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    public static bool Relevant(string title) => Regex.IsMatch(title, "Consumer Price|Employment Situation|Producer Price|Gross Domestic|Personal Income|PCE|FOMC|Monetary Policy|Economic Outlook|Chairman|Chair Powell|Chair Warsh", RegexOptions.IgnoreCase);
    public static RiskEvent[] ParseIcs(string content, string source, string url)
    {
        var result = new List<RiskEvent>();
        var unfolded = Regex.Replace(content.Replace("\r\n", "\n"), "\n[ \\t]", "");
        foreach (var block in unfolded.Split("BEGIN:VEVENT").Skip(1))
        {
            var fields = block.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(':')).Select(l => (Head:l[..l.IndexOf(':')], Value:l[(l.IndexOf(':') + 1)..])).ToArray();
            string Get(string key) => fields.FirstOrDefault(f => f.Head.Split(';')[0] == key).Value ?? "";
            var title = Get("SUMMARY").Replace("\\,", ",").Replace("\\;", ";").Replace("\\n", " ");
            if (!Relevant(title)) continue;
            var start = fields.FirstOrDefault(f => f.Head.Split(';')[0] == "DTSTART");
            if (start.Value is null || start.Head.Contains("VALUE=DATE;") || start.Head.EndsWith("VALUE=DATE")) continue; // No invented times.
            DateTimeOffset at;
            if (start.Value.EndsWith('Z') && DateTimeOffset.TryParseExact(start.Value, "yyyyMMdd'T'HHmmss'Z'", Inv, DateTimeStyles.AssumeUniversal, out var utc)) at = utc;
            else if ((start.Head.Contains("America/New_York") || content.Contains("X-WR-TIMEZONE:America/New_York")) && DateTime.TryParseExact(start.Value,"yyyyMMdd'T'HHmmss",Inv,DateTimeStyles.None,out var local)) at = Eastern(local);
            else continue;
            result.Add(new(source + ":" + (Get("UID") is { Length: > 0 } uid ? uid : title + at.ToString("O")), title, source, at, url, "high"));
        }
        return result.ToArray();
    }
    public static RiskEvent[] ParseFed(string content)
    {
        using var doc = JsonDocument.Parse(content.TrimStart('\uFEFF'));
        var result = new List<RiskEvent>();
        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            string S(string key) => e.TryGetProperty(key, out var p) ? p.GetString() ?? "" : "";
            var title = Regex.Replace(System.Net.WebUtility.HtmlDecode(S("title") + " · " + S("description")), "<[^>]*>", " ");
            if (!Relevant(title)) continue;
            // Ranged dates and missing release times remain unasserted; only exact instants generate alerts.
            var time = S("time").Replace("a.m.", "AM").Replace("p.m.", "PM");
            if (!DateTime.TryParseExact(S("month") + "-" + S("days").PadLeft(2, '0') + " " + time, new[]{"yyyy-MM-dd h:mm tt","yyyy-MM-dd h tt"}, Inv, DateTimeStyles.None, out var local)) continue;
            var at = Eastern(local);
            result.Add(new("FED:" + at.ToString("O") + ":" + S("title"), title, "FED", at, "https://www.federalreserve.gov/newsevents/calendar.htm", "high"));
        }
        return result.ToArray();
    }
}
