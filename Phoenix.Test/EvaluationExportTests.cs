using System.IO.Compression;
using System.Text.Json;
using Phoenix.Web;

internal static class EvaluationExportTests
{
    public static void SchemaAndLimits()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var f = new TechnicalFeatureSnapshot(1,1,1,1,1,1,1,1,1,1,1);
        var s = new ServerSignal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long",
            CreatedAtUtc = from, EntryTriggeredAtUtc = from.AddHours(1), CompletedAtUtc = from.AddHours(2),
            Outcome = "Target", EntryTechnicalFeatures = f, TechnicalFeatures = f with { Rsi14 = 2 },
            RequestedByUsername = "PRIVATE_USER", BybitOrderId = "PRIVATE_ORDER", Error = "PRIVATE_ERROR" };
        var row = new SignalHistoryItem(s, from.AddHours(3), null, false);
        var bytes = SignalEvaluationExport.Build(from, from.AddDays(1), [("main", new[] { row, row }), ("observation", Array.Empty<SignalHistoryItem>())]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("main.jsonl")!.Open());
        var text = reader.ReadToEnd();
        if (text.Contains("PRIVATE_") || text.Trim().Split('\n').Length != 1) throw new Exception("Private data leaked or duplicate rows exported.");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (!root.GetProperty("eligibleForEntryBinaryEvaluation").GetBoolean() ||
            root.GetProperty("latestFeaturesAreFrozenProposal").GetBoolean() ||
            root.GetProperty("entryFeatures").GetProperty("rsi14").GetDecimal() != 1 ||
            root.GetProperty("latestFeatures").GetProperty("rsi14").GetDecimal() != 2)
            throw new Exception("Feature provenance or binary eligibility lost.");
        if (zip.GetEntry("manifest.json") is null || zip.GetEntry("observation.jsonl") is null)
            throw new Exception("Missing manifest or empty population.");
        try {
            SignalEvaluationExport.Build(from, from.AddDays(1), [("main", Enumerable.Repeat(row,2001).ToArray())]);
            throw new Exception("Oversized export silently accepted.");
        } catch (ArgumentException) { }
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try {
            SignalEvaluationExport.Build(from, from.AddDays(1), [("main", new[]{row})], cts.Token);
            throw new Exception("Cancellation ignored.");
        } catch (OperationCanceledException) { }
    }
}
