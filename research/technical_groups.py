"""Fixed grouped technical ablation on previously inspected history; not a live gate."""
import json,sys,datetime,warnings
from pathlib import Path
import numpy as np
from sklearn.linear_model import LogisticRegression
from probability_audit import at,vector,y,metrics

GROUPS={'levels':list(range(9)), 'structure':[9,10,11,12,13],
        'momentum':[14,15,16,17], 'participation':[18,19],
        'context':[20,21], 'geometry':[22,23,24]}
def features(r):
 f=r['entryFeatures']; sign=1 if r['direction'].lower()=='long' else -1
 def get(k):return float(f[k]) if f.get(k) is not None else np.nan
 entry=float(r['entryPrice']);target=abs(float(r['takeProfit'])-entry);stop=abs(float(r['stopLoss'])-entry)
 valid=entry>0 and stop>0 and target>0 and ((float(r['takeProfit'])-entry)*sign>0) and ((entry-float(r['stopLoss']))*sign>0)
 return np.r_[vector(r)[:9],vector(r)[10:15],(get('rsi14')-.5)*sign,get('trendStrength')*sign,get('priceActionScore'),get('impulseEfficiency'),get('volumeRatio'),get('atrPercent'),get('higherTimeframeAlignment'),get('bitcoinMarketAlignment'),[np.log(target/stop),np.log(stop/entry),np.log(target/entry)] if valid else [np.nan]*3]
def run(rows,gap):
 start=max(40,int(.44*len(rows)));edges=np.linspace(start,len(rows),4,dtype=int);result={};saved={}
 variants={'all':list(range(25))}
 for group,cols in GROUPS.items():
  variants['only_'+group]=cols;variants['without_'+group]=[i for i in range(25) if i not in cols]
 for lo,hi in zip(edges[:-1],edges[1:]):
  test=rows[lo:hi];cut=at(test[0]['entryTriggeredAtUtc'])-datetime.timedelta(hours=gap)
  train=[r for r in rows[:lo] if at(r['completedAtUtc'])<cut]
  a=np.array([features(r) for r in train]);b=np.array([features(r) for r in test]);truth=y(test);prior=y(train).mean()
  # Same timeframe baseline, smoothed using past global outcomes.
  base=np.array([(sum(x['outcome']=='Target' for x in train if x['timeframe']==r['timeframe'])+10*prior)/(sum(x['timeframe']==r['timeframe'] for x in train)+10) for r in test])
  for name,cols in variants.items():
   x=a[:,cols];z=b[:,cols]
   with warnings.catch_warnings():
    warnings.simplefilter('ignore');mean=np.nanmean(x,axis=0);std=np.nanstd(x,axis=0)
   active=(np.isfinite(x).sum(axis=0)>=max(10,len(train)//2))&(std>.0001)
   if not active.any():continue
   def transform(v):return np.nan_to_num(np.clip((v[:,active]-mean[active])/std[active],-4,4),nan=0)
   model=LogisticRegression(C=.1,max_iter=2000).fit(transform(x),y(train))
   p=model.predict_proba(transform(z))[:,1]
   result.setdefault(name,[]).append({'train':len(train),'test':len(test),'brier':float(np.mean((p-truth)**2)),'baselineBrier':float(np.mean((base-truth)**2))})
   saved.setdefault(name,[]).extend((float(pp),float(yy),float(bb),r['entryTriggeredAtUtc'][:10]) for pp,yy,bb,r in zip(p,truth,base,test))
 out={}
 for name,values in saved.items():
  p,t,b=map(np.array,zip(*[v[:3] for v in values]));days=sorted(set(v[3] for v in values));rng=np.random.default_rng(20261015);deltas=[]
  for _ in range(2000):
   ids=[i for day in rng.choice(days,len(days)) for i,v in enumerate(values) if v[3]==day];deltas.append(float(np.mean((p[ids]-t[ids])**2-(b[ids]-t[ids])**2)))
  out[name]={'metrics':metrics(p,t),'baseline':metrics(b,t),'folds':result[name],'delta95':np.quantile(deltas,[.025,.975]).tolist()}
 return out
def main():
 rows=[r for r in json.load(open(sys.argv[1]))['rows'] if r.get('eligibleForEntryBinaryEvaluation') and r['source'] in ('main','observation')]
 rows.sort(key=lambda r:(at(r['entryTriggeredAtUtc']),r['id']));groups={}
 for r in rows:
  key=(r['symbol'].upper(),r['direction'],r['timeframe'],r['entryPrice'],r['takeProfit'],r['stopLoss'],at(r['entryTriggeredAtUtc']).replace(second=0,microsecond=0))
  groups.setdefault(key,[]).append(r)
 rows=[v[0] for v in groups.values() if len({r['outcome'] for r in v})==1]
 output={'exploratoryOnly':True,'groups':GROUPS,'rows':len(rows),'protocol':'Fixed C=.1; three forward folds, outcome purge, train-only scaling; no feature selection from audit; day-cluster bootstrap. Conditional on entered target/stop outcomes; not all proposals.','cohorts':{}}
 for name,items,gap in [('pooled',rows,0),('main_only',[r for r in rows if r['source']=='main'],0),('pooled_gap24',rows,24)]:
  output['cohorts'][name]=run(items,gap)
  print(name)
  for k,v in output['cohorts'][name].items():print(k,round(v['metrics']['brier'],4),round(v['baseline']['brier'],4),[round(z,4) for z in v['delta95']])
 Path(sys.argv[2]).write_text(json.dumps(output,indent=2))
if __name__=='__main__':main()
