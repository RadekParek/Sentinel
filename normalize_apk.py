from pathlib import Path
import sys, zipfile
target = Path(sys.argv[1])
temporary = target.with_suffix('.normalized.apk')
with zipfile.ZipFile(target) as source, zipfile.ZipFile(temporary, 'w') as output:
    for info in source.infolist():
        data = source.read(info)
        info.filename = info.filename.replace('\\', '/')
        output.writestr(info, data)
temporary.replace(target)
