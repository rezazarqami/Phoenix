using System.Text.Json;
using Phoenix.Web;

internal static class ProposalEvidenceTests
{
    public static void PreservesObservedProposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phoenix-proposal-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var observed = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            var features = new TechnicalFeatureSnapshot(0.5m,1m,0m,0m,0m,1m,1m,0.5m,0.5m,1m,0.5m);
            var candidate = new SignalCandidate("BTCUSDT","5","Long",120,80,101,100,110,90,
                92,105,10,1,0,0,0,0,0,100,"test",false,0);
            var analysis = new ProfessionalSignalAnalysis(features, new SignalSimilarityResult(60,40,10,5,5), "Range", [], [])
                { ObservedAtUtc = observed };
            var evidence = SignalProposalEvidence.Capture(candidate,"5",analysis);
            if (evidence is null || evidence.ObservedAtUtc != observed) throw new Exception("Proposal evidence timestamp lost.");
            if (SignalProposalEvidence.Capture(candidate,"5",analysis with { ObservedAtUtc = null }) is not null)
                throw new Exception("Legacy timestamp must not be invented.");
            var path = Path.Combine(directory,"queue.json"); var history = Path.Combine(directory,"history.db");
            var store = new ServerOrderStore(path,history);
            var signal = new ServerSignal { Id=Guid.NewGuid(), Symbol="BTCUSDT", Direction="Long",
                CreatedAtUtc=observed, Status="Pending", Ceiling=120, Floor=80, EntryPrice=100,
                TakeProfit=110, StopLoss=90, ProposalEvidence=evidence, TechnicalFeatures=features };
            store.AddAsync(signal).GetAwaiter().GetResult();
            var updated = store.GetAllAsync().GetAwaiter().GetResult().Single();
            updated.TechnicalFeatures = features with { Rsi14 = 0.9m };
            updated.EntryTechnicalFeatures = updated.TechnicalFeatures;
            updated.ProposalEvidence = evidence with { ObservedAtUtc = observed.AddDays(1), Features = updated.TechnicalFeatures };
            store.UpdateAsync(updated).GetAwaiter().GetResult();
            var reopened = new ServerOrderStore(path,history).GetAllAsync().GetAwaiter().GetResult().Single();
            if (reopened.ProposalEvidence != evidence || reopened.TechnicalFeatures!.Rsi14 != 0.9m)
                throw new Exception("Current review overwrote the original proposal.");
            var row = store.GetHistoryAsync().GetAwaiter().GetResult().Single();
            var export = JsonSerializer.Serialize(SignalEvaluationExport.Page(observed.AddDays(-1),observed.AddDays(2),[("main",new[]{row})],0,100), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var json = JsonDocument.Parse(export);
            var saved = json.RootElement.GetProperty("rows")[0].GetProperty("proposalEvidence");
            if (saved.GetProperty("observedAtUtc").GetDateTime() != observed ||
                saved.GetProperty("features").GetProperty("rsi14").GetDecimal() != 0.5m)
                throw new Exception("Export lost frozen evidence provenance.");
        }
        finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(directory,true); }
    }
}
