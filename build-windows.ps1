$ErrorActionPreference='Stop'
dotnet publish (Join-Path $PSScriptRoot 'windows\Sentinel.csproj') -c Release -o (Join-Path $PSScriptRoot 'windows\publish') -p:NoWarn=MSB3277
if($LASTEXITCODE -ne 0){throw 'Windows build failed'}
Get-Item -LiteralPath (Join-Path $PSScriptRoot 'windows\publish\Sentinel.exe') | Select-Object FullName,Length
