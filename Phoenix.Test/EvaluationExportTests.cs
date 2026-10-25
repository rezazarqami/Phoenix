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
        var pageRows = Enumerable.Range(0, 3).Select(i => new SignalHistoryItem(new ServerSignal {
            Id = Guid.NewGuid(), CreatedAtUtc = from.AddMinutes(i), Symbol = "BTCUSDT",
            RequestedByUsername = "PRIVATE_USER", Error = "PRIVATE_ERROR" }, from, null, false)).ToArray();
        var page = JsonSerializer.Serialize(SignalEvaluationExport.Page(from, from.AddDays(1), [("main", pageRows)], 0, 2));
        using var pageJson = JsonDocument.Parse(page);
        if (page.Contains("PRIVATE_") || pageJson.RootElement.GetProperty("total").GetInt32() != 3 ||
            pageJson.RootElement.GetProperty("rows").GetArrayLength() != 2 ||
            pageJson.RootElement.GetProperty("nextOffset").GetInt32() != 2 ||
            pageJson.RootElement.GetProperty("completeForRequestedRange").GetBoolean())
            throw new Exception("Pagination or privacy metadata invalid.");
        var last = JsonSerializer.Serialize(SignalEvaluationExport.Page(from, from.AddDays(1), [("main", pageRows)], 2, 2));
        using var lastJson = JsonDocument.Parse(last);
        if (lastJson.RootElement.GetProperty("nextOffset").ValueKind != JsonValueKind.Null ||
            lastJson.RootElement.GetProperty("rows").GetArrayLength() != 1)
            throw new Exception("Last page must have no continuation.");
        try { SignalEvaluationExport.Page(from, from.AddDays(1), [("main", pageRows)], -1, 2); throw new Exception("Negative offset accepted"); }
        catch (ArgumentException) { }
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try {
            SignalEvaluationExport.Build(from, from.AddDays(1), [("main", new[]{row})], cts.Token);
            throw new Exception("Cancellation ignored.");
        } catch (OperationCanceledException) { }
    }
}
