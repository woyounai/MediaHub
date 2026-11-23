$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Get-Process MediaHub -ErrorAction SilentlyContinue | Stop-Process -Force
$output = Join-Path $workspace 'artifacts\verified'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
dotnet publish (Join-Path $workspace 'src\MediaHub\MediaHub.csproj') -c Release -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'MediaHub build failed; application was not started.' }
Start-Process -FilePath (Join-Path $output 'MediaHub.exe') -WorkingDirectory $output -ArgumentList '--background' -WindowStyle Hidden
