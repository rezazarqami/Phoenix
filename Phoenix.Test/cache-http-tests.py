"""Verify authenticated conditional history, private static caching and compressed assets."""
import os, subprocess, tempfile, urllib.request, urllib.error, http.cookiejar, json, time, secrets, socket, gzip
from pathlib import Path
root=Path(__file__).resolve().parents[1]
with socket.socket() as sock:
 sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
base=f'http://127.0.0.1:{port}'
with tempfile.TemporaryDirectory() as tmp:
 password=secrets.token_urlsafe(24)
 env={**os.environ,'ASPNETCORE_URLS':base,'ASPNETCORE_CONTENTROOT':str(root/'Phoenix.Web'),'PHOENIX_AUTH_USERNAME':'owner','PHOENIX_AUTH_PASSWORD':password,'PHOENIX_QUEUE_PATH':tmp+'/signals.json','PHOENIX_USERS_FILE':tmp+'/users.json','PHOENIX_ENV_FILE':tmp+'/phoenix.env','PHOENIX_FIREBASE_SERVICE_ACCOUNT':''}
 client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
 def request(path,data=None,headers=None):
  req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json',**(headers or {})})
  try:
   with client.open(req,timeout=10) as res:return res.status,res.headers,res.read()
  except urllib.error.HTTPError as res:return res.code,res.headers,res.read()
 with open(Path(tmp)/'server.log','w') as log:
  process=subprocess.Popen(['dotnet',str(root/'Phoenix.Web/bin/Release/net10.0/Phoenix.Web.dll')],env=env,stdout=log,stderr=log)
  try:
   for _ in range(100):
    try:
     if request('/login')[0]==200:break
    except Exception:pass
    time.sleep(.1)
   else:raise AssertionError('Server startup failed')
   assert request('/api/analysis/connection')[0]==401
   assert request('/api/auth/login',{'username':'owner','password':password})[0]==200
   status,headers,body=request('/api/analysis/connection');probe=json.loads(body)
   assert status==200 and probe['status']=='ok' and probe['instance'] and probe['startedAtUtc'] and probe['serverTimeUtc']
   assert 'no-store' in headers['Cache-Control'] and headers['X-Phoenix-Request-Id'] and headers['X-Phoenix-Instance']==probe['instance']
   first_trace=headers['X-Phoenix-Request-Id']
   status,headers,body=request('/api/analysis/connection');assert json.loads(body)['instance']==probe['instance'] and headers['X-Phoenix-Request-Id']!=first_trace
   status,headers,body=request('/notifications');assert status==200 and 'no-store' in headers['Cache-Control']
   status,headers,body=request('/notifications.js?v=cache-test',headers={'Accept-Encoding':'gzip'})
   assert status==200 and headers['Content-Encoding']=='gzip' and 'private' in headers['Cache-Control'] and 'max-age=3600' in headers['Cache-Control']
   assert gzip.decompress(body)==(root/'Phoenix.Web/wwwroot/notifications.js').read_bytes()
   status,headers,body=request('/notifications.js');assert 'must-revalidate' in headers['Cache-Control'];asset_tag=headers['ETag']
   assert request('/notifications.js',headers={'If-None-Match':asset_tag})[0]==304
   status,headers,body=request('/api/mobile/history?view=results&source=normal');assert status==200;tag=headers['ETag']
   status,headers,body=request('/api/mobile/history?view=results&source=normal',headers={'If-None-Match':tag});assert status==304 and body==b''
   assert request('/api/auth/logout',{})[0]==200
   assert request('/api/mobile/history?view=results&source=normal',headers={'If-None-Match':tag})[0]==401
   print('PASS HTTP cache: private authenticated assets, gzip, static revalidation, body-free history and session expiry.')
  finally:process.terminate();process.wait(timeout=10)
