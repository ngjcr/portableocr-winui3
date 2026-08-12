$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
Write-Host 'Restoring PortableOCR WinUI 3...'
dotnet restore
Write-Host 'Publishing portable x64 build...'
dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishSingleFile=false
Write-Host 'Done.'
