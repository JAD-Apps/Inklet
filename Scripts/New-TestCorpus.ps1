<#
.SYNOPSIS
  Generates the performance-test corpus used by the measurement scripts and the
  Perf/HugeFiles test categories.

.DESCRIPTION
  Creates deterministic text files of various sizes and shapes under -OutDir:
    corpus-1mb.log      1 MB   log-style lines (~80 chars, CRLF)
    corpus-100mb.log    100 MB same
    corpus-1gb.log      1 GB   same                (skipped unless -Large)
    corpus-10gb.log     10 GB  same                (skipped unless -Huge)
    corpus-megaline.txt 256 MB single line, no line breaks (skipped unless -Large)
    corpus-cjk.txt      64 MB  UTF-8 CJK + mixed-width lines
    corpus-mixed-eol.txt 4 MB  deliberately mixed CRLF/LF/CR endings
  Files are only rewritten when missing or the wrong size (idempotent).

.EXAMPLE
  ./New-TestCorpus.ps1 -OutDir D:\perf-corpus -Large
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $env:TEMP 'inklet-corpus'),
    [switch]$Large,   # additionally generate the 1 GB + megaline files
    [switch]$Huge     # additionally generate the 10 GB file
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Write-RepeatedBlock {
    param([string]$Path, [byte[]]$Block, [long]$TargetBytes)
    if ((Test-Path $Path) -and (Get-Item $Path).Length -eq ([long][math]::Floor($TargetBytes / $Block.Length) * $Block.Length)) {
        Write-Host "  exists: $Path"; return
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $fs = [System.IO.File]::Create($Path)
    try {
        $reps = [long][math]::Floor($TargetBytes / $Block.Length)
        for ($i = 0; $i -lt $reps; $i++) { $fs.Write($Block, 0, $Block.Length) }
    } finally { $fs.Dispose() }
    Write-Host ("  wrote {0}  ({1:N0} bytes, {2:N1}s)" -f $Path, (Get-Item $Path).Length, $sw.Elapsed.TotalSeconds)
}

# Log-style block: 1024 lines of ~80 chars each, CRLF, deterministic content.
$sb = [System.Text.StringBuilder]::new()
for ($n = 0; $n -lt 1024; $n++) {
    [void]$sb.AppendFormat("2026-08-27T00:00:{0:d2}.{1:d3}Z INFO  worker-{2:d3} request handled path=/api/v1/items/{3:d6} status=200`r`n",
        ($n % 60), ($n % 1000), ($n % 8), $n)
}
$logBlock = [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())

Write-Host "Corpus -> $OutDir"
Write-RepeatedBlock (Join-Path $OutDir 'corpus-1mb.log')   $logBlock 1MB
Write-RepeatedBlock (Join-Path $OutDir 'corpus-100mb.log') $logBlock 100MB
if ($Large) { Write-RepeatedBlock (Join-Path $OutDir 'corpus-1gb.log') $logBlock 1GB }
if ($Huge)  { Write-RepeatedBlock (Join-Path $OutDir 'corpus-10gb.log') $logBlock 10GB }

if ($Large) {
    # Megaline: one 256 MB line, no breaks anywhere (minified-JSON shape).
    $mlBlock = [System.Text.Encoding]::ASCII.GetBytes('{"id":123456,"name":"item","tags":["a","b","c"],"value":3.14159},' * 1024)
    Write-RepeatedBlock (Join-Path $OutDir 'corpus-megaline.txt') $mlBlock 256MB
}

# CJK / mixed-width, UTF-8 (no BOM).
$cjkLine = "第{0:d5}行 こんにちは世界 안녕하세요 你好世界 ｱｲｳｴｵ ABC 123 — テスト`r`n"
$sb = [System.Text.StringBuilder]::new()
for ($n = 0; $n -lt 512; $n++) { [void]$sb.AppendFormat($cjkLine, $n) }
$cjkBlock = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
Write-RepeatedBlock (Join-Path $OutDir 'corpus-cjk.txt') $cjkBlock 64MB

# Mixed EOL: alternating CRLF / LF / CR endings; round-trip byte-identity fixture.
$sb = [System.Text.StringBuilder]::new()
for ($n = 0; $n -lt 300; $n++) {
    [void]$sb.Append("crlf line $n").Append("`r`n")
    [void]$sb.Append("lf line $n").Append("`n")
    [void]$sb.Append("cr line $n").Append("`r")
}
$mixBlock = [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())
Write-RepeatedBlock (Join-Path $OutDir 'corpus-mixed-eol.txt') $mixBlock 4MB

Write-Host 'Done.'
