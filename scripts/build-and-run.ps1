$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'prepare-download-tools.ps1')
Get-Process MediaHub -ErrorAction SilentlyContinue | Stop-Process -Force
$output = Join-Path $workspace 'artifacts\verified'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::GetFullPath($output).StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must be inside artifacts.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
dotnet publish (Join-Path $workspace 'src\MediaHub\MediaHub.csproj') -c Release -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'MediaHub build failed; application was not started.' }
Start-Process -FilePath (Join-Path $output 'MediaHub.exe') -WorkingDirectory $output -ArgumentList '--background' -WindowStyle Hidden
$servicePort = if ($env:MEDIAHUB_PORT) { $env:MEDIAHUB_PORT } else { '8765' }
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$servicePort/api/health" -TimeoutSec 1
        if ($health.ok) { Write-Output "MediaHub is ready on port $servicePort"; return }
    } catch { }
    Start-Sleep -Milliseconds 250
}
throw 'MediaHub was published but did not become ready.'
