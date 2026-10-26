"""Real HTTP tests with disposable credentials and storage; never contacts production."""
import os, subprocess, tempfile, urllib.request, urllib.error, http.cookiejar, json, time, secrets, shutil
from pathlib import Path
root=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as tmp:
 password=secrets.token_urlsafe(24)
 env={**os.environ,'ASPNETCORE_URLS':'http://127.0.0.1:5141','ASPNETCORE_CONTENTROOT':str(root/'Phoenix.Web'),'PHOENIX_AUTH_USERNAME':'rezazarqami','PHOENIX_AUTH_PASSWORD':password,'PHOENIX_QUEUE_PATH':tmp+'/signals.json','PERSONAL_DATABASE_PATH':tmp+'/personal.db','PHOENIX_USERS_FILE':tmp+'/users.json','PHOENIX_ENV_FILE':tmp+'/phoenix.env','PHOENIX_FIREBASE_SERVICE_ACCOUNT':''}
 log=open(Path(tmp)/'server.log','w');process=None
 def client():return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
 def request(c,path,data=None,method=None):
  req=urllib.request.Request('http://127.0.0.1:5141'+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json'},method=method)
  try:
   with c.open(req,timeout=10) as r:return r.status,r.read(),r.url
  except urllib.error.HTTPError as e:return e.code,e.read(),e.url
 def start(exclusive):
  global process
  if process:process.terminate();process.wait(timeout=10)
  process=subprocess.Popen([shutil.which('dotnet') or os.environ['PERSONAL_TEST_DOTNET'],str(root/'Phoenix.Web/bin/Release/net10.0/Phoenix.Web.dll')],env={**env,'OwnerOnlyAccess':str(exclusive).lower()},cwd=root/'Phoenix.Web',stdout=log,stderr=log)
  for _ in range(150):
   if process.poll() is not None:raise AssertionError('Fixture server did not start')
   try:
    if request(client(),'/login')[0]==200:return
   except Exception:pass
   time.sleep(.1)
  raise AssertionError('Fixture server startup timed out')
 try:
  start(False);owner=client();assert request(owner,'/api/auth/login',{'username':'rezazarqami','password':password})[0]==200
  previous=[]
  for name in ['fixture-editor','fixture-viewer']:
   assert request(owner,'/api/users',{'username':name,'password':password,'confirmPassword':password,'viewerOnly':name.endswith('viewer')})[0]==200
   c=client();assert request(c,'/api/auth/login',{'username':name,'password':password})[0]==200;previous.append(c)
  assert len(json.loads(Path(tmp+'/users.json').read_text()))==2
  start(True)
  assert json.loads(Path(tmp+'/users.json').read_text())==[], 'Additional credentials survived'
  assert Path(tmp+'/users.json').stat().st_mode&0o777==0o600
  status=json.loads(Path(tmp+'/users.json.owner-only.json').read_text());assert status['removedOnStartup']==2 and status['additionalUsers']==0 and status['owner']=='rezazarqami'
  assert request(owner,'/api/auth/me')[0]==200,'Existing owner session was revoked'
  assert request(owner,'/api/personal/entries')[0]==404
  assert request(owner,'/api/analysis/auth/me')[0]==200
  assert json.loads(request(owner,'/api/users')[1])==[]
  for c in previous:
   for path in ['/api/auth/me','/api/positions/live','/api/signals','/api/analysis/auth/me','/api/mobile/status']:
    assert request(c,path)[0]==401, 'Stale non-owner session accepted'
   assert request(c,'/phoenix')[2].endswith('/login')
  for name in ['fixture-editor','fixture-viewer']:
   assert request(client(),'/api/auth/login',{'username':name,'password':password})[0]==401
  assert request(owner,'/api/users',{'username':'replacement','password':password,'confirmPassword':password,'viewerOnly':False})[0]==403
  assert request(client(),'/api/auth/login',{'username':'rezazarqami','password':password})[0]==200
  start(True);assert request(owner,'/api/auth/me')[0]==200;assert json.loads(Path(tmp+'/users.json').read_text())==[]
  print('PASS owner-only access: credentials removed, stale editor/viewer sessions denied across Phoenix/analysis/removed routes, owner retained, creation blocked and restart idempotent.')
 finally:
  if process:process.terminate();process.wait(timeout=10)
  log.close()
