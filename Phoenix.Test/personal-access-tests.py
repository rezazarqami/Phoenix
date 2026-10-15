import os, subprocess, tempfile, urllib.request, urllib.error, http.cookiejar, json, time, secrets, shutil
from pathlib import Path
root=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as tmp:
 password=secrets.token_urlsafe(24)
 env={**os.environ,'ASPNETCORE_URLS':'http://127.0.0.1:5137','ASPNETCORE_CONTENTROOT':str(root/'Phoenix.Web'),'PHOENIX_AUTH_USERNAME':'owner','PHOENIX_AUTH_PASSWORD':password,'PHOENIX_QUEUE_PATH':tmp+'/signals.json','PERSONAL_DATABASE_PATH':tmp+'/personal.db','PHOENIX_USERS_FILE':tmp+'/users.json','PHOENIX_ENV_FILE':tmp+'/phoenix.env','PHOENIX_FIREBASE_SERVICE_ACCOUNT':''}
 log=open(Path(tmp)/'server.log','w')
 process=subprocess.Popen([shutil.which('dotnet') or os.environ['PERSONAL_TEST_DOTNET'],str(root/'Phoenix.Web/bin/Release/net10.0/Phoenix.Web.dll')],env=env,cwd=root/'Phoenix.Web',stdout=log,stderr=log)
 def client():return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
 def request(c,path,data=None,method=None):
  req=urllib.request.Request('http://127.0.0.1:5137'+path,data=None if data is None else data if isinstance(data,bytes) else json.dumps(data).encode(),headers={'Content-Type':'application/json'},method=method)
  try:
   with c.open(req,timeout=10) as res:return res.status,res.read(),res.url,res.headers
  except urllib.error.HTTPError as e:return e.code,e.read(),e.url,e.headers
 try:
  anon=client()
  for _ in range(100):
   try:
    if request(anon,'/login')[0]==200:break
   except Exception:pass
   time.sleep(.1)
  assert request(anon,'/api/personal/entries')[0]==401
  assert request(anon,'/api/personal/mobile/status')[0]==401
  assert request(anon,'/fonts/Vazirmatn-variable.woff2')[0]==200
  assert request(anon,'/')[2].endswith('/login')
  owner=client();assert request(owner,'/api/auth/login',{'username':'owner','password':password})[0]==200
  assert b'data-personal-workspace="2"' in request(owner,'/')[1]
  assert b'PHOENIX' in request(owner,'/phoenix')[1]
  assert b'href="/phoenix"' in request(owner,'/')[1]
  assert b'href="/"' in request(owner,'/phoenix')[1]
  assert json.loads(request(owner,'/api/analysis/auth/me')[1])['isAdmin'] is True
  assert json.loads(request(owner,'/api/auth/me')[1])['username']=='owner'
  assert request(owner,'/')[3].get('Cache-Control')=='no-store'
  assert json.loads(request(owner,'/api/personal/mobile/status')[1])['pushConfigured'] is False
  import uuid,sqlite3
  device_id=str(uuid.uuid4())
  registration={'deviceId':device_id,'token':'fixture:token_for_mobile_tests_123456789'}
  assert request(owner,'/api/personal/mobile/devices',registration)[0]==200
  assert request(owner,'/api/personal/mobile/devices',registration)[0]==200
  with sqlite3.connect(tmp+'/personal.db.mobile.db') as db:assert db.execute('SELECT count(*) FROM mobile_devices').fetchone()[0]==1
  assert request(owner,'/api/personal/mobile/devices',{**registration,'token':'bad'})[0]==400
  assert request(owner,'/api/personal/mobile/devices/'+device_id,method='DELETE')[0]==204
  payload={'kind':'vehicle','date':'2026-10-14','title':'تعویض روغن','amount':2500000,'notes':'تعمیرگاه','vehicle':'خودروی من','odometer':90000}
  status,body,*_=request(owner,'/api/personal/entries',payload);assert status==200,body
  item=json.loads(body);assert len(json.loads(request(owner,'/api/personal/entries')[1]))==1
  assert request(owner,'/api/personal/entries',{**payload,'amount':-1})[0]==400
  assert request(owner,'/api/personal/entries',{**payload,'kind':'finance','flow':'expense','amount':None})[0]==400
  assert request(owner,'/api/personal/export')[0]==200
  for role in ('editor','viewer'):
   assert request(owner,'/api/users',{'username':role,'password':password,'confirmPassword':password,'viewerOnly':role=='viewer'})[0]==200
   c=client();assert request(c,'/api/auth/login',{'username':role,'password':password})[0]==200
   assert request(c,'/')[2].endswith('/phoenix')
   assert request(c,'/index.html')[2].endswith('/phoenix')
   for path,method,data in [('/api/personal/mobile/status','GET',None),('/api/personal/mobile/devices','POST',registration),('/api/personal/mobile/devices/'+device_id,'DELETE',None),('/api/personal/mobile/test','POST',{}),('/api/personal/entries','GET',None),('/api/personal/export','GET',None),('/api/personal/entries','POST',payload),('/api/personal/entries/'+item['id'],'DELETE',None)]:assert request(c,path,data,method)[0]==403
  assert request(owner,'/api/personal/entries/'+item['id'],method='DELETE')[0]==204
  assert len(json.loads(request(owner,'/api/personal/entries')[1]))==0
  import uuid, io, zipfile
  parent_id=str(uuid.uuid4())
  goal={'kind':'goal','date':'2026-10-14','title':'هدف تست','details':{'status':'active','expectedOutcome':'معیار رسیدن'},'clientId':parent_id}
  code,body,*_=request(owner,'/api/personal/entries',goal);assert code==200,body
  assert json.loads(request(owner,'/api/personal/entries',goal)[1])['id']==parent_id
  records=json.loads(request(owner,'/api/personal/entries')[1]);assert len(records)==1
  edit={'entry':{**goal,'notes':'نسخه دوم'},'revision':1}
  code,body,*_=request(owner,'/api/personal/entries/'+parent_id,edit,'PUT');assert code==200,body
  assert json.loads(body)['revision']==2
  stale={'entry':{**goal,'notes':'نسخه قدیمی'},'revision':1}
  assert request(owner,'/api/personal/entries/'+parent_id,stale,'PUT')[0]==409
  assert len(json.loads(request(owner,'/api/personal/entries/'+parent_id+'/history')[1]))==2
  checkin={'kind':'checkin','date':'2026-10-14','title':'وضعیت','details':{'focus':4,'energy':3,'activities':['پیاده روی']}}
  assert request(owner,'/api/personal/entries',checkin)[0]==200
  assert request(owner,'/api/personal/entries',checkin)[0]==409
  report=json.loads(request(owner,'/api/personal/reports?from=2026-10-13&to=2026-10-14')[1])
  assert report['totals']['checkinDays']==1 and report['chart'][0]['focus'] is None
  backup=request(owner,'/api/personal/backup')[1]
  with zipfile.ZipFile(io.BytesIO(backup)) as archive:
   manifest=json.loads(archive.read('personal.json'));assert manifest['version']==2 and len(manifest['entries'])==2
  # Import is additive and does not overwrite the latest record.
  raw=urllib.request.Request('http://127.0.0.1:5137/api/personal/import',data=backup,headers={'Content-Type':'application/zip'},method='POST')
  with owner.open(raw) as response: result=json.loads(response.read());assert result['added']==0 and result['skipped']==2
  assert request(owner,'/api/personal/entries/'+parent_id+'?revision=1',method='DELETE')[0]==409
  assert request(owner,'/api/personal/entries/'+parent_id+'?revision=2',method='DELETE')[0]==204
  trash=json.loads(request(owner,'/api/personal/trash')[1]);assert trash[0]['revision']==3
  assert request(owner,'/api/personal/entries/'+parent_id+'/restore',{'revision':3})[0]==200
  for role in ('editor','viewer'):
   c=client();request(c,'/api/auth/login',{'username':role,'password':password})
   for path,method,data in [('/api/personal/trash','GET',None),('/api/personal/backup','GET',None),('/api/personal/reports?from=2026-10-13&to=2026-10-14','GET',None),('/api/personal/entries/'+parent_id+'/history','GET',None),('/api/personal/entries/'+parent_id,'PUT',edit),('/api/personal/import','POST',{}),('/api/personal/attachments/'+parent_id,'POST',{})]:assert request(c,path,data,method)[0]==403
  assert request(owner,'/api/auth/logout',method='POST')[0]==200
  assert request(owner,'/api/personal/entries')[0]==401
  assert request(owner,'/api/auth/login',{'username':'owner','password':password})[0]==200
  assert request(owner,'/api/analysis/auth/logout',method='POST')[0]==200
  assert request(owner,'/api/personal/entries')[0]==401
  for _ in range(12):
   code,*_=request(anon,'/api/auth/login',{'username':'owner','password':'invalid'})
   if code==429:break
  assert code==429,'Login rate limit missing'
  print('PASS personal integration: shared login, private root, editor/viewer isolation, validation, idempotent sync, update conflicts, reports, media backup, additive restore, deletion, logout and no-store HTML.')
 finally:
  process.terminate();process.wait(timeout=10);log.close()
