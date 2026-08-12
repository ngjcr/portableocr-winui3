param(
    [Parameter(Mandatory=$true)]
    [string]$OriginalPortableOcrFolder
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'PortableOCR.WinUI3'
$source = Join-Path $OriginalPortableOcrFolder 'resources\app'

if (-not (Test-Path (Join-Path $source 'engines'))) {
    throw "Could not find resources\app\engines under: $OriginalPortableOcrFolder"
}

$runtime = Join-Path $project 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -Recurse -Force (Join-Path $source 'engines') $runtime
if (Test-Path (Join-Path $source 'licenses')) {
    Copy-Item -Recurse -Force (Join-Path $source 'licenses') $runtime
}

$icon = Join-Path $OriginalPortableOcrFolder 'resources\app\assets\icon.ico'
if (-not (Test-Path $icon)) {
    $icon = Join-Path $OriginalPortableOcrFolder 'resources\icon.ico'
}
if (Test-Path $icon) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets') | Out-Null
    Copy-Item -Force $icon (Join-Path $project 'Assets\PortableOCR.ico')
}

Write-Host "PortableOCR runtime imported into $runtime"
