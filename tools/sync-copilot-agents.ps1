# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 Wissam HANNA

<#
.SYNOPSIS
  Mirrors the Claude Code agents (.claude/agents/*.md) to GitHub Copilot custom agents (.github/agents/*.agent.md).

.DESCRIPTION
  The Claude files stay the source of truth (backward compatibility); the Copilot files are generated and must not be
  edited by hand. Frontmatter is converted (tools: Read/Glob/Grep -> read/search, Edit/Write -> edit,
  Bash/PowerShell -> execute); the body is copied unchanged. agents/README.md is copied as is.
  Files in .github/agents that no longer have a source are removed. Re-run after any change to .claude/agents.

.EXAMPLE
  pwsh tools/sync-copilot-agents.ps1                                     # this repository
  pwsh tools/sync-copilot-agents.ps1 -Root <folder>                      # another folder that has .claude/agents
  pwsh tools/sync-copilot-agents.ps1 -Check                              # exit 1 if the generated files are stale
#>
[CmdletBinding()]
param(
    [string]$Root = '.',
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $Root '.claude/agents'
$dst = Join-Path $Root '.github/agents'
if (-not (Test-Path $src)) { throw "No agents folder: $src" }

$toolMap = @{ read = 'read'; glob = 'search'; grep = 'search'; edit = 'edit'; write = 'edit'; bash = 'execute'; powershell = 'execute' }
$utf8 = New-Object Text.UTF8Encoding($false)
$expected = @{}

foreach ($f in Get-ChildItem $src -Filter *.md -File) {
    $text = [IO.File]::ReadAllText($f.FullName)
    if ($f.Name -eq 'README.md') { $expected['README.md'] = $text; continue }
    $m = [regex]::Match($text, '^---\r?\n(.*?)\r?\n---\r?\n', 'Singleline')
    if (-not $m.Success) { throw "$($f.Name): no frontmatter" }
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $name = [regex]::Match($m.Groups[1].Value, '(?m)^name:\s*(.+?)\s*$').Groups[1].Value
    $desc = [regex]::Match($m.Groups[1].Value, '(?m)^description:\s*(.+?)\s*$').Groups[1].Value
    $tools = [regex]::Match($m.Groups[1].Value, '(?m)^tools:\s*(.+?)\s*$').Groups[1].Value -split '\s*,\s*' |
        ForEach-Object { $toolMap[$_.ToLowerInvariant()] } | Where-Object { $_ } | Select-Object -Unique
    if (-not $name -or -not $desc) { throw "$($f.Name): name or description missing" }
    $body = $text.Substring($m.Length)
    $out = @(
        '---'
        "name: $name"
        "description: '$($desc.Replace("'", "''"))'"
        "tools: [$(($tools | ForEach-Object { "'$_'" }) -join ', ')]"
        '---'
        "<!-- Generated from .claude/agents/$($f.Name) by tools/sync-copilot-agents.ps1. Edit the source, then re-run. -->"
    ) -join $nl
    $expected[$f.BaseName + '.agent.md'] = $out + $nl + $body
}

$stale = @()
foreach ($k in $expected.Keys) {
    $p = Join-Path $dst $k
    if (-not (Test-Path $p) -or [IO.File]::ReadAllText($p) -ne $expected[$k]) { $stale += $k }
}
$orphans = if (Test-Path $dst) { Get-ChildItem $dst -File | Where-Object { -not $expected.ContainsKey($_.Name) } }

if ($Check) {
    if ($stale -or $orphans) { "Stale: $($stale -join ', ') Orphans: $($orphans.Name -join ', ')"; exit 1 }
    "Up to date ($($expected.Count) files)."; exit 0
}
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($k in $stale) { [IO.File]::WriteAllText((Join-Path $dst $k), $expected[$k], $utf8) }
foreach ($o in $orphans) { Remove-Item $o.FullName -Force }
"Wrote $($stale.Count) file(s), removed $(@($orphans).Count), $($expected.Count) total in $dst"
