from pathlib import Path
import subprocess,json,hashlib,zipfile,os
r=Path(__file__).resolve().parent.parent
exe=r/'windows/publish/Sentinel.exe'
exe_hash=hashlib.sha256(exe.read_bytes()).hexdigest()
for mode,name in [('--connect-local','final-connections.json'),('--self-test','final-native-tests.json'),('--test-upgrade','final-upgrade-tests.json'),('--test-meter','final-meter-tests.json'),('--verify-webview','final-exe-integration.json')]:
 report=r/'tests'/name;subprocess.run([str(exe),mode,str(report)],check=True,timeout=60);data=json.loads(report.read_text(encoding='utf-8-sig'))
 if mode=='--connect-local':print(json.dumps({'connections':[{'provider':c['providerId'],'adapter':c['adapter'],'hasKey':c['hasKey']}for c in data['connections']],'importResult':data['importResult']}));continue
 if not data.get('passed'):raise AssertionError(data)
 data['artifactSha256']=exe_hash;report.write_text(json.dumps(data,indent=2),encoding='utf-8')
 print(name,'passed')
apk=r/'android/build/Sentinel.apk'
adb=r'C:\Users\OMEN\AppData\Local\Android\Sdk\platform-tools\adb.exe'
subprocess.run([adb,'-s','emulator-5554','install','-r',str(apk)],check=True,capture_output=True,timeout=60)
result=subprocess.run([adb,'-s','emulator-5554','shell','am','instrument','-w','com.sentinel.tests/com.sentinel.app.UpgradeInstrumentation'],check=True,capture_output=True,text=True,timeout=45)
line=next(l for l in result.stdout.splitlines()if l.startswith('INSTRUMENTATION_RESULT: report='));data=json.loads(line.split('report=',1)[1]);assert data.get('passed'),data
data['artifactSha256']=hashlib.sha256(apk.read_bytes()).hexdigest();(r/'tests/android-results.json').write_text(json.dumps(data,indent=2),encoding='utf-8');print('Android native and WebView checks passed')
with zipfile.ZipFile(apk)as z:
 for file in (r/'web').rglob('*'):
  if file.is_file():assert z.read('assets/web/'+file.relative_to(r/'web').as_posix())==file.read_bytes(),file.name
print('APK bundled assets match current source')
keys=[v.encode()for k,v in os.environ.items()if k in {'COARENA_API_KEY','AGENTROUTER_API_KEY'}and v]
assert all(key not in exe.read_bytes() and key not in apk.read_bytes()for key in keys)
print('Configured secrets absent from binaries')
