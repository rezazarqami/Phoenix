"""Descriptive audit of recorded first events; no candle-path reconstruction."""
import json,sys,collections,datetime
from pathlib import Path
from probability_audit import at

HORIZONS={'1':24,'5':120,'15':360,'60':1440,'240':5760}
def classify(r,start,deadline,asof):
 if deadline>asof:return 'Immature'
 update=at(r['updatedAtUtc']);end=at(r['completedAtUtc']) if r.get('completedAtUtc') else None
 if r.get('recoveryOriginalStatus') or r['status']=='RecoveryRequired' or update>asof or update<start or (end and end<start):return 'Unknown'
 events=[]
 for label,key in [('Target','targetReachedAtUtc'),('StopLoss','stopLossReachedAtUtc'),('RiskFree','riskFreeClosedAtUtc'),('Expired','expiredAtUtc')]:
  t=at(r[key]) if r.get(key) else None
  if t and start<=t<=deadline:events.append((t,label))
 if events:
  events.sort();earliest=[x for x in events if x[0]==events[0][0]]
  if len(earliest)!=1:return 'Unknown'
  first,label=earliest[0];entry=at(r['entryTriggeredAtUtc']) if r.get('entryTriggeredAtUtc') else None
  if label in ('Target','StopLoss','RiskFree') and (entry is None or entry<start or entry>first):return 'Unknown'
  return label
 if end and end<=deadline:return 'OtherExit' if r['status'] in ('Cancelled','Rejected','Deactivated') else 'Unknown'
 removed=at(r['removedAtUtc']) if r.get('removedAtUtc') else None
 return 'NoRecordedResolution' if update>=deadline and (removed is None or removed>=deadline) else 'Unknown'

def main():
 rows=json.load(open(sys.argv[1]))['rows'];asof=max(at(x['updatedAtUtc']) for x in rows)
 output={'asOfUtc':asof.isoformat(),'interpretation':'Descriptive recorded-event audit, not a forecast. Broad cohorts; live technical-neighbor filtering is not applied. Observation time is entry.','cohorts':{}}
 for source in ('main','observation'):
  episodes={}
  for r in rows:
   if r['source']!=source or not r.get('entryFeatures') or not r.get('entryTriggeredAtUtc') or r.get('timeframe') not in HORIZONS:continue
   start=at(r['entryTriggeredAtUtc']);key=(r['symbol'].upper(),r['direction'],r['timeframe'],r['entryPrice'],r['takeProfit'],r['stopLoss'],start.replace(second=0,microsecond=0))
   label=classify(r,start,start+datetime.timedelta(minutes=HORIZONS[r['timeframe']]),asof)
   episodes.setdefault(key,[]).append(label)
  counts=collections.Counter(v[0] if len(set(v))==1 else 'Unknown' for v in episodes.values())
  output['cohorts'][source]=dict(counts)
 Path(sys.argv[2]).write_text(json.dumps(output,indent=2));print(json.dumps(output,indent=2))
if __name__=='__main__':main()
