$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"')) -Wait
    exit
}
Get-Process MediaHub -ErrorAction SilentlyContinue | Stop-Process -Force
$output = Join-Path $workspace 'artifacts\verified'
dotnet publish (Join-Path $workspace 'src\MediaHub\MediaHub.csproj') -c Release -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'MediaHub build failed; application was not started.' }
Start-Process -FilePath (Join-Path $output 'MediaHub.exe') -WorkingDirectory $output -ArgumentList '--background' -WindowStyle Hidden
