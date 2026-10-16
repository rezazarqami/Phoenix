"""Score ONLY frozen proposal probabilities, never retrospective/latest scores.
Usage: python research/frozen_probability_audit.py evaluation.json results.json
Input: a complete evaluation page object with rows, or an array of exported rows.
Source/timeframe/method are reported separately. No model fitting or promotion.
"""
import collections
import datetime as dt
import json
import math
import sys
from pathlib import Path


def timestamp(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')) if value else None


def unique(rows):
    # Exclude conflicting outcomes before selecting binary labels.
    for key in (lambda r: (r['source'], r['id']), episode):
        groups = collections.defaultdict(list)
        for row in rows:
            groups[key(row)].append(row)
        rows = [g[0] for g in groups.values() if len({r.get('outcome') for r in g}) == 1
                and len({json.dumps(r.get('proposalEvidence'), sort_keys=True) for r in g}) == 1]
    return rows


def episode(r):
    p = r.get('proposalEvidence') or {}
    observed = timestamp(p.get('observedAtUtc'))
    if not observed or not p.get('entryPrice'):
        return (r['source'], r['id'])
    return (r['source'], str(p.get('symbol', '')).upper(), p.get('direction'), p.get('timeframe'),
            p.get('entryPrice'), p.get('takeProfit'), p.get('stopLoss'), observed.replace(second=0, microsecond=0))


def metrics(pairs):
    if not pairs:
        return None
    return {'count': len(pairs),
            'brier': sum((p-y)**2 for p, y in pairs)/len(pairs),
            'logLoss': -sum(y*math.log(max(1e-12, p)) + (1-y)*math.log(max(1e-12, 1-p))
                            for p, y in pairs)/len(pairs),
            'meanProbability': sum(p for p, _ in pairs)/len(pairs),
            'targetRate': sum(y for _, y in pairs)/len(pairs)}


def audit(raw):
    rows = unique(raw)
    groups = collections.defaultdict(lambda: {'eligible': 0, 'unknown': 0, 'pairs': [], 'baselinePairs': [], 'paired': []})
    eligible = []
    for r in rows:
        p = r.get('proposalEvidence') or {}
        observed, entered, completed = (timestamp(v) for v in
            (p.get('observedAtUtc'), r.get('entryTriggeredAtUtc'), r.get('completedAtUtc')))
        if r.get('outcome') not in ('Target', 'StopLoss') or not (observed and entered and completed):
            continue
        if not observed <= entered < completed or (p.get('features') or {}).get('similarityContextVersion', 0) < 3:
            continue
        eligible.append((r, p, observed, completed))
    for r, p, observed, completed in eligible:
        prediction = p.get('prediction') or {}
        info = prediction.get('outcomeEstimate') or {}
        method = 'validated' if prediction.get('outcomeValidationCount', 0) > 0 else info.get('version', 'unknown')
        key = (r['source'], p.get('timeframe'), method)
        g = groups[key]
        g['eligible'] += 1
        probability = prediction.get('targetOutcomePercent')
        stop = prediction.get('stopOutcomePercent')
        if probability is None or stop is None or not 0 <= probability <= 100 or abs(probability+stop-100) > .01:
            g['unknown'] += 1
            continue
        if method == 'unknown':
            g['unknown'] += 1
            continue
        pair = (probability/100, int(r['outcome'] == 'Target'))
        g['pairs'].append(pair)
        past = [old for old, evidence, _, done in eligible if old['source'] == r['source'] and
                evidence.get('timeframe') == p.get('timeframe') and done < observed]
        if past:
            prior = (sum(old['outcome'] == 'Target' for old in past)+1)/(len(past)+2)
            g['baselinePairs'].append((prior, pair[1]))
            g['paired'].append(pair)
    reports = []
    for (source, timeframe, method), g in sorted(groups.items(), key=lambda item: str(item[0])):
        bins = []
        for i in range(10):
            values = [(p, y) for p, y in g['pairs'] if min(9, int(p*10)) == i]
            if values:
                bins.append({'lower': i/10, 'upper': (i+1)/10, **metrics(values)})
        reports.append({'source': source, 'timeframe': timeframe, 'method': method,
                        'eligible': g['eligible'], 'unknown': g['unknown'], 'scores': metrics(g['pairs']),
                        'pairedScores': metrics(g['paired']), 'pastOnlyBaseline': metrics(g['baselinePairs']),
                        'reliability': bins})
    return {'schemaVersion': 'phoenix-frozen-audit-v1', 'inputRows': len(raw), 'uniqueRows': len(rows),
            'groups': reports, 'notes': [
                'Conditional on entry and eventual Target/StopLoss; not a profitability claim.',
                'No latest percentages, reconstructed predictions, expiry or RiskFree relabelling.',
                'Coverage denominators include eligible binary outcomes, not unresolved proposals.',
                'Compare pairedScores and pastOnlyBaseline on identical covered rows only.',
                'Small samples and correlated trades limit precision. No automatic promotion.']}


if __name__ == '__main__':
    payload = json.loads(Path(sys.argv[1]).read_text())
    if isinstance(payload, dict):
        if payload.get('nextOffset') is not None or payload.get('completeForRequestedRange') is False:
            raise SystemExit('Supply all pages as a merged rows array; partial exports cannot be audited as complete.')
        payload = payload['rows']
    Path(sys.argv[2]).write_text(json.dumps(audit(payload), indent=2, ensure_ascii=False))
