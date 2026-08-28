<#
.SYNOPSIS
  Measures per-keystroke input->draw latency by driving real keyboard input into
  a running Inklet instance and reading the paired KeystrokeIn/KeystrokeDrawn
  rows from %TEMP%\inklet-perf.csv (see Inklet/Diagnostics/Perf.cs).

.DESCRIPTION
  Launches the exe with INKLET_PERF=1 (optionally opening -File), waits for the
  first draw, focuses the window, sends -Count characters at -Cps via SendKeys,
  then computes p50/p95/p99 of (KeystrokeDrawn - KeystrokeIn) per id.
  Close is forced; the document is never saved.

.EXAMPLE
  ./Measure-Typing.ps1 -ExePath ...\Inklet.exe -File D:\perf-corpus\corpus-1gb.log -Label 1gb-baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$File,
    [int]$Count = 500,
    [int]$Cps = 30,
    [int]$WarmupSec = 3,
    [string]$Label = 'unlabelled'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$csv = Join-Path $env:TEMP 'inklet-perf.csv'
Remove-Item $csv -ErrorAction SilentlyContinue
$env:INKLET_PERF = '1'

$proc = if ($File) { Start-Process -FilePath $ExePath -ArgumentList "`"$File`"" -PassThru }
        else       { Start-Process -FilePath $ExePath -PassThru }

# Wait for first draw so we know the editor exists and has focus-able content.
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $csv) -and (Import-Csv $csv | Where-Object name -eq 'FirstCanvasDraw')) { break }
    Start-Sleep -Milliseconds 200
}
Start-Sleep -Seconds $WarmupSec   # let session restore / initial load settle

# Focus the window and type. SetForegroundWindow from a background process is
# blocked by the foreground lock; the documented workaround is to pulse the Alt
# key (opens the lock) before calling it.
Add-Type -Namespace Native -Name Win32 -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.IntPtr dwExtraInfo);
"@
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw 'Main window never appeared.' }
for ($try = 0; $try -lt 10; $try++) {
    [Native.Win32]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)          # Alt down
    [void][Native.Win32]::SetForegroundWindow($hwnd)
    [Native.Win32]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)          # Alt up
    Start-Sleep -Milliseconds 200
    if ([Native.Win32]::GetForegroundWindow() -eq $hwnd) { break }
}
if ([Native.Win32]::GetForegroundWindow() -ne $hwnd) { Write-Warning 'Could not obtain foreground; keystrokes may be lost.' }
Start-Sleep -Milliseconds 300

# Large files trigger a confirmation ContentDialog (default button = Open).
# Accept it, then wait for the real first text draw before measuring.
if ($File -and (Get-Item $File).Length -gt 10MB) {
    # Retry Enter until the load begins - the dialog's appearance time varies.
    $deadline = (Get-Date).AddSeconds(300)
    $loaded = $false
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $csv) -and (Import-Csv $csv | Where-Object name -eq 'FirstTextDraw')) { $loaded = $true; break }
        [void][Native.Win32]::SetForegroundWindow($hwnd)
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 2000
    }
    if (-not (Import-Csv $csv | Where-Object name -eq 'FirstTextDraw')) { throw 'File never finished loading.' }
    Start-Sleep -Seconds $WarmupSec
    # Ground truth: the loaded document must show in the title and the working set.
    $proc.Refresh()
    $fileName = Split-Path $File -Leaf
    Write-Host ("  window title: '{0}'  workingSet: {1:N0} MB" -f $proc.MainWindowTitle, ($proc.WorkingSet64 / 1MB))
    if ($proc.MainWindowTitle -notlike "*$fileName*") { throw "Window title does not contain '$fileName' - file not loaded; aborting measurement." }
}

$delayMs = [int](1000 / $Cps)
for ($i = 0; $i -lt $Count; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('x')
    Start-Sleep -Milliseconds $delayMs
}
Start-Sleep -Milliseconds 500
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

# Pair up ids.
$rows = Import-Csv $csv
$in    = @{}; $drawn = @{}
foreach ($row in $rows) {
    if ($row.name -eq 'KeystrokeIn')    { $in[$row.id]    = [double]$row.msSinceMain }
    if ($row.name -eq 'KeystrokeDrawn') { $drawn[$row.id] = [double]$row.msSinceMain }
}
$lat = foreach ($k in $in.Keys) { if ($drawn.ContainsKey($k)) { $drawn[$k] - $in[$k] } }
$lat = $lat | Sort-Object
if (-not $lat) { throw 'No keystroke pairs captured - did the editor have focus?' }

function Pct([double[]]$s, [double]$p) { $s[[int][math]::Min($s.Count - 1, [math]::Ceiling(($s.Count - 1) * $p))] }
Write-Host ("[{0}] {1} pairs  p50={2:N1}ms  p95={3:N1}ms  p99={4:N1}ms  max={5:N1}ms" -f `
    $Label, $lat.Count, (Pct $lat 0.5), (Pct $lat 0.95), (Pct $lat 0.99), $lat[-1])
