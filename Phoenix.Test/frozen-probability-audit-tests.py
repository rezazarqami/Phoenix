import importlib.util
from pathlib import Path
spec = importlib.util.spec_from_file_location('audit', Path(__file__).parents[1]/'research/frozen_probability_audit.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
def row(i, outcome, prob, source='main'):
    return dict(id=str(i), source=source, outcome=outcome, targetOutcomePercent=99,
        entryTriggeredAtUtc=f'2026-01-{i:02d}T01:00:00Z', completedAtUtc=f'2026-01-{i:02d}T02:00:00Z',
        proposalEvidence=dict(observedAtUtc=f'2026-01-{i:02d}T00:00:00Z', timeframe='15',
            symbol='TEST', direction='Long', entryPrice=100, takeProfit=110, stopLoss=90,
            features=dict(similarityContextVersion=3), prediction=dict(targetOutcomePercent=prob,
                stopOutcomePercent=None if prob is None else 100-prob, outcomeValidationCount=0,
                outcomeEstimate=dict(version='technical-neighbours-v1'))))
rows = [row(1, 'Target', 60), row(2, 'StopLoss', 40), row(3, 'Target', None), row(4, 'RiskFree', 90)]
a = m.audit(rows)['groups'][0]
assert a['eligible'] == 3 and a['unknown'] == 1
assert abs(a['scores']['brier']-.16) < 1e-10
assert a['pairedScores']['count'] == a['pastOnlyBaseline']['count'] == 1
assert abs(a['pastOnlyBaseline']['brier']-(2/3)**2) < 1e-10
assert m.audit(rows+rows) == {**m.audit(rows), 'inputRows': 8}
conflict = row(1, 'StopLoss', 60)
assert m.audit(rows+[conflict])['uniqueRows'] == 3
assert len(m.audit(rows+[row(1, 'StopLoss', 40, 'observation')])['groups']) == 2
assert sum(b['count'] for b in a['reliability']) == 2
print('PASS frozen probabilities, missingness, paired baseline, deduplication and source separation')
