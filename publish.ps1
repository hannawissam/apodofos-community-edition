# Publishes the desktop app and the CLI as self-contained single files into artifacts/ (git-ignored).
# Note: the UI is hosted by the Microsoft Edge WebView2 *Evergreen* runtime, which ships with
# Windows 11 and Microsoft Edge. The application never downloads it; install it manually on
# machines that lack it (e.g. a clean Windows 10).
# License files (LICENSE, NOTICE, THIRD-PARTY-NOTICES.md) and the .NET runtime's own third-party
# notices are copied next to each published executable.
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'artifacts'
foreach ($d in 'desktop', 'cli') {
    $p = Join-Path $out $d
    if (Test-Path $p) { Remove-Item -Recurse -Force $p }   # no stale files from earlier publishes
}
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish src/QuietLoad.Desktop -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $out 'desktop')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish src/QuietLoad.Cli -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $out 'cli')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem (Join-Path $out 'desktop') -Filter *.xml -File | Remove-Item -Force   # WebView2 API doc files, not needed at runtime

# --- legal files next to each executable ---
$legal = 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md' | ForEach-Object { Join-Path $PSScriptRoot $_ } | Where-Object { Test-Path $_ }

function Find-RuntimeNotice([string]$publishDir) {
    $hit = Get-ChildItem $publishDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(THIRD[-_ ]?PARTY[-_ ]?NOTICES?|ThirdPartyNotices)\.(txt|md)$' } | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    $pkgRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
    $pack = Join-Path $pkgRoot "microsoft.netcore.app.runtime.$Runtime"
    if (Test-Path $pack) {
        $hit = Get-ChildItem $pack -Recurse -File -Filter 'THIRD-PARTY-NOTICES.TXT' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $root = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:ProgramFiles 'dotnet' }
    $hit = Join-Path $root 'ThirdPartyNotices.txt'
    if (Test-Path $hit) { return $hit }
    return $null
}

foreach ($d in 'desktop', 'cli') {
    $dir = Join-Path $out $d
    $rt = Find-RuntimeNotice $dir   # look before copying our own THIRD-PARTY-NOTICES.md, which the pattern would match
    $legal | ForEach-Object { Copy-Item $_ $dir -Force }
    if ($rt) { Copy-Item $rt (Join-Path $dir 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt') -Force }
    else { Write-Warning ".NET runtime third-party notices not found for $d" }
}

Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName.Substring($out.Length + 1) }
Write-Host "Done: $out"

