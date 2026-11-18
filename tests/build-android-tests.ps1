$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot
$taskBuild=Join-Path $PSScriptRoot 'android-build'
$taskTools='C:\Users\OMEN\AppData\Local\Android\Sdk\build-tools\36.0.0'
$taskJar='C:\Users\OMEN\AppData\Local\Android\Sdk\platforms\android-36\android.jar'
New-Item -ItemType Directory -Path $taskBuild,(Join-Path $taskBuild 'classes'),(Join-Path $taskBuild 'dex') -Force|Out-Null
javac --release 8 -classpath ($taskJar+';'+(Join-Path $taskRoot 'android\build\classes')) -d (Join-Path $taskBuild 'classes') (Join-Path $PSScriptRoot 'android\UpgradeInstrumentation.java')
if($LASTEXITCODE-ne 0){throw 'Instrumentation compilation failed'}
jar cf (Join-Path $taskBuild 'classes.jar') -C (Join-Path $taskBuild 'classes') .
& (Join-Path $taskTools 'd8.bat') --release --min-api 26 --lib $taskJar --output (Join-Path $taskBuild 'dex') (Join-Path $taskBuild 'classes.jar')
& (Join-Path $taskTools 'aapt2.exe') link -o (Join-Path $taskBuild 'tests-unsigned.apk') -I $taskJar --manifest (Join-Path $PSScriptRoot 'android\AndroidManifest.xml') --min-sdk-version 26 --target-sdk-version 36
jar uf (Join-Path $taskBuild 'tests-unsigned.apk') -C (Join-Path $taskBuild 'dex') classes.dex
& (Join-Path $taskTools 'zipalign.exe') -f -p 4 (Join-Path $taskBuild 'tests-unsigned.apk') (Join-Path $taskBuild 'tests-aligned.apk')
& (Join-Path $taskTools 'apksigner.bat') sign --ks (Join-Path $taskRoot 'android\signing\sentinel-release.p12') --ks-pass ('file:'+(Join-Path $taskRoot 'android\signing\password.txt')) --out (Join-Path $taskBuild 'Sentinel-tests.apk') (Join-Path $taskBuild 'tests-aligned.apk')
if($LASTEXITCODE-ne 0){throw 'Instrumentation signing failed'}
