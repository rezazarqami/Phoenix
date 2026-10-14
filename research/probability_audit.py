"""Offline, chronological Phoenix probability audit. Never publishes private records."""
import json,sys,math,collections,datetime,warnings
from pathlib import Path
import numpy as np
from sklearn.metrics import brier_score_loss,log_loss,roc_auc_score

def at(value):
 return datetime.datetime.fromisoformat(value.replace('Z','+00:00'))
def vector(r):
 f=r['entryFeatures'];sign=1 if r['direction'].lower()=='long' else -1
 def d(key,mul=1):
  v=f.get(key);return np.nan if v is None else float(v)*mul
 vals=[float(f.get('ichimokuEntryPosition') if f.get('ichimokuEntryPosition') is not None else f['ichimokuPosition'])*sign,d('ichimokuCloudBias',sign),d('supportDistanceAtr' if sign>0 else 'resistanceDistanceAtr'),d('resistanceDistanceAtr' if sign>0 else 'supportDistanceAtr'),d('entryLevelStrength'),d('stopLevelStrength'),d('targetLevelStrength'),d('ichimokuStopPosition',sign),d('ichimokuTargetPosition',sign),math.log(f['atrPercent']) if f['atrPercent']>0 else np.nan]
 vals += [d(k) if f.get('similarityContextVersion',0)>=1 else np.nan for k in ['marketRegimeScore','structureScore','liquiditySweepScore']]+[d('structureBreakScore'),d('structureRetestScore')]
 levels={(str(z['interval']),z['window']):z for z in f.get('multiScaleLevels') or []}
 for interval in ['1','5','15','60','240','D','W','M']:
  for window in [80,240,720]:
   level=levels.get((interval,window))
   vals += [float(level[k])*(sign if k.startswith('cloud') else 1) for k in ['entryStrength','stopStrength','targetStrength','fibonacciEntry','fibonacciStop','fibonacciTarget','cloudEntry','cloudStop','cloudTarget','cloudBias']] if level else [np.nan]*10
 return np.array(vals)
def y(rows):return np.array([r['outcome']=='Target' for r in rows],float)
def sigmoid(z):return 1/(1+np.exp(-np.clip(z,-30,30)))
def preprocess(rows):
 raw=np.array([vector(r) for r in rows]);n=len(rows);means=np.zeros(raw.shape[1]);scales=means.copy()
 for j in range(raw.shape[1]):
  values=raw[:,j][np.isfinite(raw[:,j])]
  if len(values)<max(10,n//2):continue
  means[j]=values.mean();scales[j]=values.std()
  if scales[j]<.0001:scales[j]=0
 return means,scales
class Linear:
 def __init__(self,rows,stable=False):
  self.rows=rows;self.means,self.scales=preprocess(rows);x=self.standard(np.array([vector(r) for r in rows]));self.w=np.zeros(x.shape[1]+1);prior=y(rows).mean();self.w[-1]=math.log(prior/(1-prior));active=self.scales>0
  step=min(.08,1/(.25*np.mean(1+np.sum(x*x,axis=1))+.08)) if stable else .08
  for _ in range(350):
   err=sigmoid(x@self.w[:-1]+self.w[-1])-y(rows)
   grad=np.r_[x.T@err/len(rows)+.08*self.w[:-1],err.mean()]
   self.w-=step*grad
 def standard(self,raw):
  result=np.zeros(raw.shape);active=self.scales>0
  result[:,active]=np.nan_to_num(np.clip((raw[:,active]-self.means[active])/self.scales[active],-4,4),nan=0)
  return result
 def score(self,r):
  raw=vector(r);active=self.scales>0
  if active.sum()==0 or (active & np.isfinite(raw)).sum()<max(1,math.ceil(active.sum()*.7)):return None
  return float(self.standard(raw[None])[0]@self.w[:-1]+self.w[-1])
class Neighbors:
 """Fixed exploratory settings: same timeframe, <=15 neighbors, RMS<=1.5, ESS>=5."""
 def __init__(self,rows):self.rows=rows;self.means,self.scales=preprocess(rows);self.raw=np.array([vector(r) for r in rows]);self.prior=y(rows).mean()
 def score(self,r):
  raw=vector(r);active=self.scales>0;need=max(1,math.ceil(active.sum()*.7));neighbors=[]
  if active.sum()==0:return None
  for i,old in enumerate(self.rows):
   if old.get('timeframe')!=r.get('timeframe'):continue
   shared=active&np.isfinite(raw)&np.isfinite(self.raw[i])
   if shared.sum()<need:continue
   distance=np.sqrt(np.mean(np.clip((raw[shared]-self.raw[i,shared])/self.scales[shared],-6,6)**2))
   if distance<=1.5:neighbors.append((distance,old['outcome']=='Target'))
  neighbors=sorted(neighbors,key=lambda x:x[0])[:15]
  if len(neighbors)<5:return None
  w=np.exp(-.5*np.array([a*a for a,b in neighbors]));ess=w.sum()**2/(w@w)
  if ess<5:return None
  local=np.dot(w,[b for a,b in neighbors])/w.sum()
  p=(ess*local+5*self.prior)/(ess+5)
  return math.log(p/(1-p))
def calibrate(scores,targets):
 scores=np.clip(scores,-20,20);pos=targets.sum();soft=np.where(targets==1,(pos+1)/(pos+2),1/(len(targets)-pos+2));a,b=1.,0.;step=1/(.25*np.mean(scores*scores+1)+.02)
 for _ in range(600):
  err=sigmoid(a*scores+b)-soft
  a=np.clip(a-step*(np.mean(err*scores)+.01*(a-1)),0,10)
  b=np.clip(b-step*(err.mean()+.01*b),-10,10)
 return float(a),float(b)
def metrics(p,labels):
 return dict(n=len(p),brier=float(brier_score_loss(labels,p)),logLoss=float(log_loss(labels,p,labels=[0,1])),auc=float(roc_auc_score(labels,p)) if len(set(labels))==2 else None,meanProbability=float(np.mean(p)),observedTargetRate=float(np.mean(labels)))
def bins(p,labels):
 result=[]
 for lower in range(0,100,10):
  mask=(p>=lower/100)&(p<(lower+10)/100 if lower<90 else p<=1)
  if mask.any():result.append(dict(lower=lower,upper=lower+10,n=int(mask.sum()),meanProbability=float(p[mask].mean()),actualTargetRate=float(labels[mask].mean())))
 return result


# Fixed candidates, no hyperparameter search. This is an exploratory re-analysis
# of previously inspected history, NOT a new untouched holdout or deployment gate.
def compact_vector(r):
 raw=vector(r);levels=raw[15:].reshape(24,10)
 with warnings.catch_warnings():
  warnings.simplefilter('ignore');avg=np.nanmean(levels,axis=0)
 return np.r_[raw[:15],avg]

class SmallLinear:
 def __init__(self,rows,compact=False):
  from sklearn.linear_model import LogisticRegression
  self.vector=compact_vector if compact else lambda r:vector(r)[:15]
  x=np.array([self.vector(r) for r in rows]);self.active=np.isfinite(x).sum(axis=0)>=max(10,len(rows)//2)
  with warnings.catch_warnings():
   warnings.simplefilter('ignore');self.mean=np.nanmean(x,axis=0);self.scale=np.nanstd(x,axis=0)
  self.active &= self.scale>.0001
  self.model=LogisticRegression(C=.1,max_iter=2000).fit(self.transform(x),y(rows))
 def transform(self,x):return np.nan_to_num(np.clip((x[:,self.active]-self.mean[self.active])/self.scale[self.active],-4,4),nan=0)
 def score(self,r):return float(self.model.decision_function(self.transform(self.vector(r)[None]))[0])

class FamilyNeighbors:
 def __init__(self,rows):
  self.rows=rows;self.mean,self.scale=preprocess(rows);self.raw=np.array([vector(r) for r in rows]);self.prior=y(rows).mean()
 def score(self,r):
  a=vector(r);active=self.scale>0;found=[]
  # Equal family contribution: primary, multi-scale levels, Fibonacci, cloud.
  groups=[np.arange(15),np.array([15+10*i+j for i in range(24) for j in range(3)]),np.array([15+10*i+j for i in range(24) for j in range(3,6)]),np.array([15+10*i+j for i in range(24) for j in range(6,10)])]
  for i,old in enumerate(self.rows):
   if old.get('timeframe')!=r.get('timeframe'):continue
   shared=active&np.isfinite(a)&np.isfinite(self.raw[i]);dist=[]
   for group in groups:
    available=group[active[group]];used=group[shared[group]]
    if len(available) and len(used)>=math.ceil(.7*len(available)):
     dist.append(np.mean(np.clip((a[used]-self.raw[i,used])/self.scale[used],-6,6)**2))
   if len(dist)<3:continue
   d=float(np.sqrt(np.mean(dist)))
   if d<=1.5:found.append((d,old['outcome']=='Target'))
  found=sorted(found)[:15]
  if len(found)<5:return None
  w=np.exp(-.5*np.array([d*d for d,_ in found]));ess=w.sum()**2/(w@w)
  if ess<5:return None
  p=(ess*np.dot(w,[v for _,v in found])/w.sum()+5*self.prior)/(ess+5)
  return math.log(p/(1-p))

class TimeframePrior:
 def __init__(self,rows):self.rows=rows;self.prior=y(rows).mean()
 def score(self,r):
  same=[x for x in self.rows if x.get('timeframe')==r.get('timeframe')];p=(sum(y(same))+10*self.prior)/(len(same)+10)
  return math.log(p/(1-p))

def main():
 data=json.load(open(sys.argv[1]));rows=[r for r in data['rows'] if r.get('eligibleForEntryBinaryEvaluation') and r['source'] in ('main','observation')]
 rows.sort(key=lambda r:(at(r['entryTriggeredAtUtc']),r['id']))
 seen=set();unique=[]
 for r in rows:
  key=(r['symbol'],r['direction'],r.get('timeframe'),r['entryPrice'],r['takeProfit'],r['stopLoss'],at(r['entryTriggeredAtUtc']).replace(second=0,microsecond=0))
  if key not in seen:unique.append(r);seen.add(key)
 result={'eligible':len(rows),'unique':len(unique),'exploratoryOnly':True,'selectionWarning':'Previously inspected data. No winning candidate is activated from this comparison.','protocol':'Three expanding chronological folds; labels must resolve before fold start; preprocessing fitted on training only; fixed candidates; no random folds. Same covered cases for each baseline comparison. Repeat with 24h label gap and main-only cohort.','cohorts':{}}
 for name,cohort,gap in [('pooled',unique,0),('main_only',[r for r in unique if r['source']=='main'],0),('pooled_24h_gap',unique,24)]:
  start=max(40,int(len(cohort)*.44));edges=np.linspace(start,len(cohort),4,dtype=int);folds=[];predictions=collections.defaultdict(list)
  for lo,hi in zip(edges[:-1],edges[1:]):
   test=cohort[lo:hi];cut=at(test[0]['entryTriggeredAtUtc'])-datetime.timedelta(hours=gap);train=[r for r in cohort[:lo] if at(r['completedAtUtc'])<cut]
   if len(train)<20 or len(set(y(train)))<2:continue
   prior=y(train).mean();fold={'train':len(train),'test':len(test),'purged':int(lo)-len(train),'from':cut.isoformat(),'methods':{}}
   models={'deployed_logistic':Linear(train),'primary_ridge':SmallLinear(train),'compact_ridge':SmallLinear(train,True),'neighbors_original':Neighbors(train),'neighbors_family':FamilyNeighbors(train),'timeframe_shrinkage':TimeframePrior(train)}
   for label,model in models.items():
    scores=[model.score(r) for r in test];mask=np.array([v is not None for v in scores]);p=sigmoid(np.array([v for v in scores if v is not None]));truth=y(test)[mask]
    if len(p):
     base=np.repeat(prior,len(p));fold['methods'][label]={'coverage':float(mask.mean()),'scores':metrics(p,truth),'baseline':metrics(base,truth)}
     for r,pp,yy in zip(np.array(test,dtype=object)[mask],p,truth):predictions[label].append((float(pp),float(yy),float(prior),r['entryTriggeredAtUtc'][:10]))
   folds.append(fold)
  aggregate={}
  for label,values in predictions.items():
   p,truth,base=map(np.array,zip(*[v[:3] for v in values]));days=sorted(set(v[3] for v in values));byday={day:[i for i,v in enumerate(values) if v[3]==day] for day in days};rng=np.random.default_rng(20261014);deltas=[]
   for _ in range(2000):
    indices=[i for day in rng.choice(days,len(days)) for i in byday[day]];deltas.append(float(np.mean((p[indices]-truth[indices])**2-(base[indices]-truth[indices])**2)))
   aggregate[label]={'scores':metrics(p,truth),'baseline':metrics(base,truth),'coverage':len(values)/sum(f['test'] for f in folds),'dayClusterBootstrapBrierDelta95':np.quantile(deltas,[.025,.975]).tolist(),'days':len(days)}
  result['cohorts'][name]={'rows':len(cohort),'folds':folds,'aggregate':aggregate}
 Path(sys.argv[2]).write_text(json.dumps(result,indent=2))
 for cohort,v in result['cohorts'].items():
  print(cohort,v['rows'])
  for model,m in v['aggregate'].items():print(model,'n',m['scores']['n'],'brier',round(m['scores']['brier'],4),'base',round(m['baseline']['brier'],4),'coverage',round(m['coverage'],2),'delta95',[round(x,4) for x in m['dayClusterBootstrapBrierDelta95']])

if __name__=='__main__':main()
