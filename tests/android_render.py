from pathlib import Path
import subprocess,time,xml.etree.ElementTree as ET,json
r=Path(__file__).parent
adb=r'C:\Users\OMEN\AppData\Local\Android\Sdk\platform-tools\adb.exe'
base=[adb,'-s','emulator-5554']
subprocess.run(base+['shell','am','start','-W','-n','com.sentinel.app/.MainActivity'],capture_output=True,check=True)
time.sleep(2)
(r/'final-android.png').write_bytes(subprocess.run(base+['exec-out','screencap','-p'],capture_output=True,check=True).stdout)
subprocess.run(base+['shell','uiautomator','dump','/sdcard/sentinel-window.xml'],capture_output=True,check=True)
subprocess.run(base+['pull','/sdcard/sentinel-window.xml',str(r/'android-window.xml')],capture_output=True,check=True)
nodes=ET.parse(r/'android-window.xml').getroot().iter('node')
print(json.dumps([{'text':n.get('text'),'description':n.get('content-desc'),'bounds':n.get('bounds')}for n in nodes if n.get('text') or n.get('content-desc')]))
