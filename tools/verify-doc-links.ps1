# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 Wissam HANNA

<#
.SYNOPSIS
  Re-checks every URL in src/Apodofos.Core/Knowledge/doc-catalog.json against docs.oracle.com.
.DESCRIPTION
  NETWORK TOOL - not part of the test suite (the product itself never goes online).
  For each distinct page URL: expects HTTP 200. For entries with an anchor: also checks that the
  fragment id exists in the downloaded page. Prints failures and exits with code 1 if any.
  Local-only references (file:///...) are skipped. No report data is sent anywhere; only the public
  Oracle documentation URLs from the catalog are requested.
.PARAMETER Catalog   Path to the catalog JSON (default: the one in the repo).
.PARAMETER Parallel  Concurrent requests (default 8).
.PARAMETER Stamp     Rewrite verifiedOn with today's date for all entries whose page passed.
#>
param(
    [string]$Catalog = (Join-Path $PSScriptRoot '..\src\Apodofos.Core\Knowledge\doc-catalog.json'),
    [int]$Parallel = 8,
    [switch]$Stamp
)

$ErrorActionPreference = 'Stop'
$json = Get-Content -Raw -Encoding utf8 $Catalog | ConvertFrom-Json
$entries = @($json.entries | Where-Object { $_.url -like 'https://*' })
$pages = $entries | Select-Object -ExpandProperty url -Unique
Write-Host "Checking $($pages.Count) distinct pages for $($entries.Count) entries..."

$results = $pages | ForEach-Object -Parallel {
    $u = $_
    $status = 0; $body = ''
    for ($i = 0; $i -lt 3 -and $status -ne 200; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 60 -Headers @{ 'User-Agent' = 'doc-link-check' }
            $status = [int]$r.StatusCode; $body = $r.Content
        } catch {
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode } else { $status = 0 }
            if ($status -eq 404) { break }
        }
    }
    [pscustomobject]@{ Url = $u; Status = $status; Body = $body }
} -ThrottleLimit $Parallel

$byUrl = @{}; foreach ($r in $results) { $byUrl[$r.Url] = $r }
$fail = @()
foreach ($r in $results) { if ($r.Status -ne 200) { $fail += "HTTP $($r.Status)  $($r.Url)" } }
foreach ($e in $entries) {
    if (-not $e.anchor) { continue }
    $r = $byUrl[$e.url]
    if ($r.Status -eq 200 -and $r.Body -notmatch [regex]::Escape("id=`"$($e.anchor)`"") -and $r.Body -notmatch [regex]::Escape("-$($e.anchor)`"")) {
        $fail += "MISSING ANCHOR  $($e.id)  $($e.url)#$($e.anchor)"
    }
}

if ($Stamp -and $fail.Count -eq 0) {
    $today = (Get-Date).ToString('yyyy-MM-dd')
    foreach ($e in $json.entries) { if ($e.url -like 'https://*') { $e.verifiedOn = $today } }
    $json | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $Catalog
    Write-Host "verifiedOn stamped $today"
}

if ($fail.Count -gt 0) {
    Write-Host "`n$($fail.Count) problem(s):" -ForegroundColor Red
    $fail | Sort-Object | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "All $($pages.Count) pages returned 200 and all anchors were found." -ForegroundColor Green
