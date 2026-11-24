param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\download-tools'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$Destination = [IO.Path]::GetFullPath($Destination)
if (-not $Destination.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Tool output must be inside repository artifacts.' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$packages = @(
    @{ Name='aria2c.exe'; Url='https://github.com/motrixapp/aria2/releases/download/v1.37.0-motrix.25/aria2c-1.37.0-motrix.25-win32-x64.zip'; Hash='8058bb40b518990f611c27276e6a646991b9a9a44964b04e02e329461af74a36'; BinaryHash='cbdd159c948e1f652a78b8904669d1dc4a140ad5484fd3710f07d7d16919c5bc' },
    @{ Name='ffmpeg.exe'; Url='https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'; Hash='60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba' }
)
foreach ($package in $packages) {
    $archive = Join-Path $Destination ($package.Name + '.zip')
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) {
        Invoke-WebRequest $package.Url -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) { throw "Checksum mismatch: $($package.Name)" }
    $unpacked = Join-Path $Destination ($package.Name + '-package')
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force
    $binary = Get-ChildItem -LiteralPath $unpacked -Filter $package.Name -Recurse | Select-Object -First 1
    if (-not $binary) { throw "Missing binary: $($package.Name)" }
    if ($package.BinaryHash -and (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash -ne $package.BinaryHash) { throw 'Engine binary checksum mismatch.' }
    Copy-Item -LiteralPath $binary.FullName -Destination (Join-Path $Destination $package.Name) -Force
}
Write-Output "Verified portable download tools: $Destination"
