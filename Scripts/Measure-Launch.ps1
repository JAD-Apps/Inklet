<#
.SYNOPSIS
  Measures Inklet launch milestones (process start -> AppMain -> Activated ->
  FirstCanvasDraw / FirstTextDraw) over N runs and reports median + p95.

.DESCRIPTION
  Runs the app with INKLET_PERF=1 so it writes %TEMP%\inklet-perf.csv (see
  Inklet/Diagnostics/Perf.cs). Two launch modes:
    -ExePath  : direct exe (loose/unpackaged build) - env vars inherit naturally.
    -Aumid    : packaged app (shell:AppsFolder\<PFN>!App). Because packaged apps
                do not inherit this process's environment, INKLET_PERF is set
                user-scope for the duration and restored afterwards.
  Cold-run protocol (manual, documented): reboot or empty the standby list
  (RAMMap64 -Ew) before each run and pass -Runs 5; warm = default 10 back-to-back.

.EXAMPLE
  ./Measure-Launch.ps1 -ExePath ..\Inklet\bin\x64\Release\net8.0-windows10.0.19041.0\Inklet.exe
  ./Measure-Launch.ps1 -Aumid (Get-AppxPackage *Inklet*).PackageFamilyName + '!App'
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$Aumid,
    [int]$Runs = 10,
    [int]$TimeoutSec = 30,
    [string]$Label = 'unlabelled'
)

$ErrorActionPreference = 'Stop'
if (-not $ExePath -and -not $Aumid) { throw 'Pass -ExePath or -Aumid.' }
$csv = Join-Path $env:TEMP 'inklet-perf.csv'

$prevUserVar = $null
if ($Aumid) {
    $prevUserVar = [Environment]::GetEnvironmentVariable('INKLET_PERF', 'User')
    [Environment]::SetEnvironmentVariable('INKLET_PERF', '1', 'User')
} else {
    $env:INKLET_PERF = '1'
}

function Wait-ForMark([string]$name, [datetime]$deadline) {
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $csv) {
            $rows = Import-Csv $csv
            if ($rows | Where-Object name -eq $name) { return $rows }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

$results = @()
try {
    for ($r = 1; $r -le $Runs; $r++) {
        Remove-Item $csv -ErrorAction SilentlyContinue
        if ($ExePath) {
            $proc = Start-Process -FilePath $ExePath -PassThru
        } else {
            Start-Process "explorer.exe" "shell:AppsFolder\$Aumid"
            Start-Sleep -Milliseconds 500
            $proc = Get-Process -Name Inklet -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -Last 1
        }
        $rows = Wait-ForMark 'FirstCanvasDraw' (Get-Date).AddSeconds($TimeoutSec)
        if (-not $rows) { Write-Warning "run ${r}: timed out waiting for FirstCanvasDraw"; Stop-Process -Name Inklet -Force -ErrorAction SilentlyContinue; continue }
        Start-Sleep -Milliseconds 400   # allow FirstTextDraw to land if a doc restores
        $rows = Import-Csv $csv

        $appMain = $rows | Where-Object name -eq 'AppMain' | Select-Object -First 1
        $procStartToMain = $null
        if ($proc -and $appMain) {
            try {
                $mainUtc = [datetime]::new([long]$appMain.utcTicks, 'Utc')
                $procStartToMain = ($mainUtc - $proc.StartTime.ToUniversalTime()).TotalMilliseconds
            } catch {}
        }
        $get = { param($n) ($rows | Where-Object name -eq $n | Select-Object -First 1).msSinceMain -as [double] }
        $results += [pscustomobject]@{
            Run              = $r
            ProcToMainMs     = [math]::Round($procStartToMain, 1)
            ActivatedMs      = & $get 'Activated'
            FirstCanvasDrawMs= & $get 'FirstCanvasDraw'
            FirstTextDrawMs  = & $get 'FirstTextDraw'
        }
        Stop-Process -Name Inklet -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
} finally {
    if ($Aumid) { [Environment]::SetEnvironmentVariable('INKLET_PERF', $prevUserVar, 'User') }
}

if (-not $results) { throw 'No successful runs.' }
$results | Format-Table -AutoSize

function Stat([double[]]$v) {
    $s = $v | Where-Object { $_ -ne $null } | Sort-Object
    if (-not $s) { return 'n/a' }
    $median = $s[[int][math]::Floor(($s.Count - 1) / 2)]
    $p95 = $s[[int][math]::Ceiling(($s.Count - 1) * 0.95)]
    '{0:N1} / {1:N1}' -f $median, $p95
}
Write-Host "`n[$Label] median / p95 (ms):"
Write-Host ("  process->AppMain : " + (Stat $results.ProcToMainMs))
Write-Host ("  AppMain->Activated: " + (Stat $results.ActivatedMs))
Write-Host ("  AppMain->FirstCanvasDraw: " + (Stat $results.FirstCanvasDrawMs))
Write-Host ("  AppMain->FirstTextDraw  : " + (Stat $results.FirstTextDrawMs))
