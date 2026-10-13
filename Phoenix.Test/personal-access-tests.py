import os, subprocess, tempfile, urllib.request, urllib.error, http.cookiejar, json, time, secrets, shutil
from pathlib import Path
root=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as tmp:
 password=secrets.token_urlsafe(24)
 env={**os.environ,'ASPNETCORE_URLS':'http://127.0.0.1:5137','ASPNETCORE_CONTENTROOT':str(root/'Phoenix.Web'),'PHOENIX_AUTH_USERNAME':'owner','PHOENIX_AUTH_PASSWORD':password,'PHOENIX_QUEUE_PATH':tmp+'/signals.json','PERSONAL_DATABASE_PATH':tmp+'/personal.db','PHOENIX_USERS_FILE':tmp+'/users.json','PHOENIX_ENV_FILE':tmp+'/phoenix.env'}
 log=open(Path(tmp)/'server.log','w')
 process=subprocess.Popen([shutil.which('dotnet') or os.environ['PERSONAL_TEST_DOTNET'],str(root/'Phoenix.Web/bin/Release/net10.0/Phoenix.Web.dll')],env=env,cwd=root/'Phoenix.Web',stdout=log,stderr=log)
 def client():return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
 def request(c,path,data=None,method=None):
  req=urllib.request.Request('http://127.0.0.1:5137'+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json'},method=method)
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
  owner=client();assert request(owner,'/api/auth/login',{'username':'owner','password':password})[0]==200
  assert 'فضای شخصی من'.encode() in request(owner,'/')[1]
  assert b'PHOENIX' in request(owner,'/phoenix')[1]
  assert request(owner,'/')[3].get('Cache-Control')=='no-store'
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
   for path,method,data in [('/api/personal/entries','GET',None),('/api/personal/export','GET',None),('/api/personal/entries','POST',payload),('/api/personal/entries/'+item['id'],'DELETE',None)]:assert request(c,path,data,method)[0]==403
  assert request(owner,'/api/personal/entries/'+item['id'],method='DELETE')[0]==204
  assert len(json.loads(request(owner,'/api/personal/entries')[1]))==0
  assert request(owner,'/api/auth/logout',method='POST')[0]==200
  assert request(owner,'/api/personal/entries')[0]==401
  print('PASS personal integration: shared login, private root, editor/viewer isolation, validation, export, deletion, logout and no-store HTML.')
 finally:
  process.terminate();process.wait(timeout=10);log.close()
