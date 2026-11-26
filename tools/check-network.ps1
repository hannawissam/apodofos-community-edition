# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 Wissam HANNA
#Requires -Version 7
<#
.SYNOPSIS
  Release check: do the desktop application and its WebView2 processes open any network connection?

.DESCRIPTION
  Starts the application twice with a WebView2 profile of its own: first a new profile, then again the profile the first
  run left behind (a "used" profile). Some runtime traffic only appears with a used profile, and some of it never appears in
  the runtime's network log, so two things are watched:

    * the TCP table of Windows, for the application, its child processes and every msedgewebview2.exe that uses the profile
      (their UDP sockets are listed too);
    * the Chromium network log of the runtime (requests and host name look-ups).

  The profile is redirected with WEBVIEW2_USER_DATA_FOLDER. The application still writes its recent list and window position
  to its normal settings folder, so run this as a test user, or accept that. The window appears on screen; leave it alone.

  Always run a control as well: a build that is known to connect (the 1.1.0 release) must FAIL this check, otherwise the
  check proves nothing on that computer.

  Exit code: 0 nothing observed, 1 connections or requests observed, 2 the measurement itself did not work.
  Windows only, PowerShell 7, no administrator rights, nothing is downloaded or installed.

.PARAMETER Exe
  The desktop executable to check.
.PARAMETER Report
  A synthetic report to open (never a real one).
.PARAMETER Seconds
  Length of each of the two runs. Use at least 600 for a release: some traffic starts after a minute or more.
.PARAMETER WorkDir
  New or empty folder for the profile and the logs. Default: a new folder under the temporary folder.

.EXAMPLE
  pwsh tools/check-network.ps1 -Exe artifacts\ApodofosReportAnalyzer.exe -Report tests\Apodofos.Tests\TestData\awr-19c-synthetic.html
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Report,
    [ValidateRange(30, 86400)][int]$Seconds = 600,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) ('apodofos-netcheck-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { Write-Host 'This check runs on Windows only.'; exit 2 }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

public sealed class NetRow { public int Pid; public bool Udp; public string Local; public string Remote; public int RemotePort; }

public static class NetTables
{
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int cls, uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int cls, uint reserved);

    static byte[] Table(bool udp, int family)
    {
        int size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr p = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
            try
            {
                uint rc = udp ? GetExtendedUdpTable(p, ref size, false, family, 1, 0) : GetExtendedTcpTable(p, ref size, false, family, 5, 0);
                if (rc == 0 && p != IntPtr.Zero) { var b = new byte[size]; Marshal.Copy(p, b, 0, size); return b; }
                size += 4096;
            }
            finally { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
        }
        throw new InvalidOperationException("The connection table of Windows could not be read.");
    }

    static int Port(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
    static string Address(byte[] b, int o, int length) { var a = new byte[length]; Array.Copy(b, o, a, 0, length); return new IPAddress(a).ToString(); }

    public static List<NetRow> Read()
    {
        var rows = new List<NetRow>();
        foreach (int family in new[] { 2, 23 })
        {
            bool v6 = family == 23;
            int len = v6 ? 16 : 4;
            var t = Table(false, family);
            int step = v6 ? 56 : 24;
            for (int i = 0, n = BitConverter.ToInt32(t, 0); i < n; i++)
            {
                int o = 4 + i * step;
                int local = o + (v6 ? 0 : 4), localPort = o + (v6 ? 20 : 8), remote = o + (v6 ? 24 : 12), remotePort = o + (v6 ? 44 : 16), pid = o + (v6 ? 52 : 20);
                int state = BitConverter.ToInt32(t, o + (v6 ? 48 : 0));
                rows.Add(new NetRow { Pid = BitConverter.ToInt32(t, pid), Local = Address(t, local, len) + ":" + Port(t, localPort),
                    Remote = state == 2 ? "" : Address(t, remote, len), RemotePort = state == 2 ? 0 : Port(t, remotePort) }); // 2 = listening
            }
            var u = Table(true, family);
            step = v6 ? 28 : 12;
            for (int i = 0, n = BitConverter.ToInt32(u, 0); i < n; i++)
            {
                int o = 4 + i * step;
                rows.Add(new NetRow { Udp = true, Pid = BitConverter.ToInt32(u, o + (v6 ? 24 : 8)), Local = Address(u, o, len) + ":" + Port(u, o + (v6 ? 20 : 4)), Remote = "" });
            }
        }
        return rows;
    }
}
'@

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$reportPath = if ($Report) { (Resolve-Path -LiteralPath $Report).Path } else { $null }
$WorkDir = [IO.Path]::GetFullPath($WorkDir)
if ((Test-Path -LiteralPath $WorkDir) -and (Get-ChildItem -LiteralPath $WorkDir -Force | Select-Object -First 1)) {
    Write-Host "The work folder is not empty, so the first profile would not be new: $WorkDir"; exit 2
}
$profileDir = Join-Path $WorkDir 'profile'
New-Item -ItemType Directory -Force $profileDir | Out-Null
$exeName = Split-Path -Leaf $exePath

function Test-LeavesMachine([string]$Address) {
    $Address -and $Address -notin '0.0.0.0', '::', '::1' -and $Address -notlike '127.*' -and $Address -notlike '::ffff:127.*'
}

function Invoke-Run([string]$Name) {
    $netlog = Join-Path $WorkDir "netlog-$Name.json"
    $env:WEBVIEW2_USER_DATA_FOLDER = $profileDir
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--log-net-log=`"$netlog`" --net-log-capture-mode=Default"
    $start = @{ FilePath = $exePath; PassThru = $true }
    if ($reportPath) { $start.ArgumentList = "`"$reportPath`"" }
    $app = Start-Process @start

    $mine = @{ [int]$app.Id = $exeName }   # process id -> program name, for every process of the application
    $seen = @{}                            # every socket of the machine, filtered afterwards so that nothing is missed
    $runtime = $null; $browserOk = $false
    $clock = [Diagnostics.Stopwatch]::StartNew(); $lastScan = -10.0
    while ($clock.Elapsed.TotalSeconds -lt $Seconds -and -not $app.HasExited) {
        foreach ($r in [NetTables]::Read()) {
            $key = "$($r.Pid)|$($r.Udp)|$($r.Local)|$($r.Remote)|$($r.RemotePort)"
            if (-not $seen.ContainsKey($key)) { $seen[$key] = [pscustomobject]@{ Row = $r; At = [int]$clock.Elapsed.TotalSeconds } }
        }
        if ($clock.Elapsed.TotalSeconds - $lastScan -ge 1) {
            $lastScan = $clock.Elapsed.TotalSeconds
            foreach ($p in Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath) {
                $isRuntime = $p.Name -eq 'msedgewebview2.exe' -and $p.CommandLine -and $p.CommandLine.IndexOf($profileDir, [StringComparison]::OrdinalIgnoreCase) -ge 0
                if ($isRuntime -or $mine.ContainsKey([int]$p.ParentProcessId)) { $mine[[int]$p.ProcessId] = $p.Name }
                if ($isRuntime -and $p.CommandLine -notmatch '--type=' -and $p.CommandLine -match '--log-net-log') {
                    $browserOk = $true; $runtime = Split-Path -Leaf (Split-Path -Parent $p.ExecutablePath)
                }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    $early = $app.HasExited
    if (-not $early) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(20000)) { $app.Kill() }   # only the process this script started
    }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($profileDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })) { Start-Sleep -Milliseconds 500 }

    $counted = $exeName, 'msedgewebview2.exe'   # a browser opened by a click on a link is not the application
    $own = @($seen.Values | Where-Object { $mine.ContainsKey($_.Row.Pid) -and $mine[$_.Row.Pid] -in $counted })
    $connections = @($own | Where-Object { -not $_.Row.Udp -and (Test-LeavesMachine $_.Row.Remote) } |
        Sort-Object At | ForEach-Object { "t=$($_.At)s $($mine[$_.Row.Pid]) (pid $($_.Row.Pid)) -> $($_.Row.Remote):$($_.Row.RemotePort)" } | Select-Object -Unique)
    $udp = @($own | Where-Object { $_.Row.Udp } | ForEach-Object { "$($mine[$_.Row.Pid]) $($_.Row.Local)" } | Select-Object -Unique)

    $requests = @(); $lookups = @(); $logRead = $false
    try {
        $log = Get-Content -LiteralPath $netlog -Raw | ConvertFrom-Json -AsHashtable
        $types = @{}; foreach ($k in $log.constants.logEventTypes.Keys) { $types[[int]$log.constants.logEventTypes[$k]] = $k }
        foreach ($e in $log.events) {
            if (-not $e.ContainsKey('params')) { continue }
            $type = $types[[int]$e.type]
            if ($type -like 'URL_REQUEST*' -and $e.params.ContainsKey('url') -and $e.params.url -match '^(https?|wss?)://') { $requests += ($e.params.url -split '\?')[0] }
            if ($type -like 'HOST_RESOLVER*' -and $e.params.ContainsKey('host')) { $lookups += [string]$e.params.host }
        }
        $logRead = $true
    }
    catch { Write-Host "  the network log could not be read: $($_.Exception.Message)" }
    $requests = @($requests | Sort-Object -Unique); $lookups = @($lookups | Sort-Object -Unique)

    Write-Host "== $Name profile: $([int]$clock.Elapsed.TotalSeconds) s, runtime $runtime, processes watched: $($mine.Count)"
    Write-Host "  connections leaving the machine: $($connections.Count)"; $connections | ForEach-Object { Write-Host "    $_" }
    Write-Host "  UDP sockets: $($udp.Count)"; $udp | ForEach-Object { Write-Host "    $_" }
    Write-Host "  network log requests: $($requests.Count)"; $requests | ForEach-Object { Write-Host "    $_" }
    Write-Host "  network log host look-ups: $($lookups.Count)"; $lookups | ForEach-Object { Write-Host "    $_" }
    $valid = $browserOk -and $logRead -and -not $early
    if (-not $valid) { Write-Host "  INVALID RUN (runtime redirected: $browserOk, log read: $logRead, application closed early: $early)" }
    [pscustomobject]@{ Valid = $valid; Found = $connections.Count + $udp.Count + $requests.Count + $lookups.Count }
}

$savedFolder = $env:WEBVIEW2_USER_DATA_FOLDER; $savedArguments = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
try {
    Write-Host "Checking $exePath"
    Write-Host "Work folder $WorkDir"
    $results = @((Invoke-Run 'new'), (Invoke-Run 'used'))
}
finally {
    $env:WEBVIEW2_USER_DATA_FOLDER = $savedFolder; $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $savedArguments
}
if (@($results | Where-Object { -not $_.Valid }).Count) { Write-Host 'RESULT: the measurement did not work.'; exit 2 }
if (@($results | Where-Object { $_.Found -gt 0 }).Count) { Write-Host 'RESULT: FAIL, network activity was observed.'; exit 1 }
Write-Host 'RESULT: PASS, no network activity was observed in these two runs.'
exit 0
