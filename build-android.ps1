param([string]$SdkRoot = "$env:LOCALAPPDATA\Android\Sdk")
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$androidRoot = Join-Path $projectRoot 'android'
$buildRoot = Join-Path $androidRoot 'build'
$toolsRoot = Join-Path $SdkRoot 'build-tools\36.0.0'
$platformJar = Join-Path $SdkRoot 'platforms\android-36\android.jar'
$taskJavaHome = Split-Path (Split-Path (Get-Command java).Source)
New-Item -ItemType Directory -Force -Path $buildRoot,(Join-Path $buildRoot 'classes'),(Join-Path $buildRoot 'dex'),(Join-Path $buildRoot 'assets\web'),(Join-Path $androidRoot 'signing') | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'web\*') -Destination (Join-Path $buildRoot 'assets\web') -Force -Recurse
$sources = Get-ChildItem -LiteralPath (Join-Path $androidRoot 'src') -Recurse -Filter '*.java' | ForEach-Object FullName
& (Join-Path $taskJavaHome 'bin\javac.exe') --release 8 -Xlint:-options -classpath $platformJar -d (Join-Path $buildRoot 'classes') $sources
if ($LASTEXITCODE -ne 0) { throw 'Java compilation failed' }
& (Join-Path $taskJavaHome 'bin\jar.exe') cf (Join-Path $buildRoot 'classes.jar') -C (Join-Path $buildRoot 'classes') .
& (Join-Path $toolsRoot 'd8.bat') --release --min-api 26 --lib $platformJar --output (Join-Path $buildRoot 'dex') (Join-Path $buildRoot 'classes.jar')
if ($LASTEXITCODE -ne 0) { throw 'DEX compilation failed' }
& (Join-Path $toolsRoot 'aapt2.exe') compile --dir (Join-Path $androidRoot 'res') -o (Join-Path $buildRoot 'compiled-res.zip')
if ($LASTEXITCODE -ne 0) { throw 'Resource compilation failed' }
& (Join-Path $toolsRoot 'aapt2.exe') link --auto-add-overlay -o (Join-Path $buildRoot 'Sentinel-unsigned.apk') -I $platformJar --manifest (Join-Path $androidRoot 'AndroidManifest.xml') -R (Join-Path $buildRoot 'compiled-res.zip') -A (Join-Path $buildRoot 'assets') --min-sdk-version 26 --target-sdk-version 36
if ($LASTEXITCODE -ne 0) { throw 'APK resource linking failed' }
python (Join-Path $projectRoot 'normalize_apk.py') (Join-Path $buildRoot 'Sentinel-unsigned.apk')
if ($LASTEXITCODE -ne 0) { throw 'APK asset path normalization failed' }
& (Join-Path $taskJavaHome 'bin\jar.exe') uf (Join-Path $buildRoot 'Sentinel-unsigned.apk') -C (Join-Path $buildRoot 'dex') classes.dex
& (Join-Path $toolsRoot 'zipalign.exe') -f -p 4 (Join-Path $buildRoot 'Sentinel-unsigned.apk') (Join-Path $buildRoot 'Sentinel-aligned.apk')
if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed' }
$keyFile = Join-Path $androidRoot 'signing\sentinel-release.p12'
$passwordFile = Join-Path $androidRoot 'signing\password.txt'
if (!(Test-Path -LiteralPath $keyFile)) {
    $password = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [System.IO.File]::WriteAllText($passwordFile,$password)
    & (Join-Path $taskJavaHome 'bin\keytool.exe') -genkeypair -keystore $keyFile -storetype PKCS12 -storepass:file $passwordFile -keypass:file $passwordFile -alias sentinel -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Sentinel Local Build,OU=Personal Apps,O=Sentinel,C=SK' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'Signing key creation failed' }
}
& (Join-Path $toolsRoot 'apksigner.bat') sign --ks $keyFile --ks-pass "file:$passwordFile" --out (Join-Path $buildRoot 'Sentinel.apk') (Join-Path $buildRoot 'Sentinel-aligned.apk')
if ($LASTEXITCODE -ne 0) { throw 'APK signing failed' }
& (Join-Path $toolsRoot 'apksigner.bat') verify --verbose (Join-Path $buildRoot 'Sentinel.apk')
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed' }
Get-Item -LiteralPath (Join-Path $buildRoot 'Sentinel.apk') | Select-Object FullName,Length
