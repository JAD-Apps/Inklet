# Inklet performance measurements

Protocol: `Scripts/Measure-Launch.ps1` and `Scripts/Measure-Typing.ps1` against an
unpackaged x64 Release build (`-p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true`),
warm machine, INKLET_PERF=1 (see `Inklet/Diagnostics/Perf.cs`). Corpus from
`Scripts/New-TestCorpus.ps1`. All figures ms unless noted.

## Baseline — 1.0.9 engine (2026-08-27, machine: JohnD dev box, x64)

### Launch (empty session, 5 warm runs, median / p95)

| Milestone | median | p95 |
|---|---|---|
| process start -> AppMain | 63.8 | 855.6 (first run cold-ish) |
| AppMain -> Activated | 378.7 | 5,088.8 |
| AppMain -> FirstCanvasDraw | 482.6 | 5,648.6 |

Session-restore launch: not measurable unpackaged (ApplicationData guard disables
persistence); capture from the packaged build when Phase 6 lands.

### Typing latency (input -> draw-complete, SendInput at ~30 cps)

| Document | pairs | p50 | p95 | p99 | working set |
|---|---|---|---|---|---|
| empty | 196 | 0.7 | 20.5 | 41.3 | ~150 MB |
| corpus-100mb.log | 82 | 87.8 | 154.9 | 158.1 | 871 MB |

The 100 MB row is the per-keystroke O(N) materialisation
(`EditorBuffer.InvalidateLineIndex` -> `PieceTable.GetText()`) plus the full
`LineIndex` rescan; working set ~8.7x file size matches the ~4-copy UTF-16
memory model. These are the numbers the overhaul must beat: target p99 < 8 ms
at any file size, working set flat (~100 MB ceiling) regardless of document.

### Notes

- 1.0.9 cannot open the 1 GB corpus (2 GB `File.ReadAllBytesAsync` ceiling is
  above it, but ~8x expansion exhausts memory in practice) - no baseline row.
- Found & fixed during baseline work: command-line/file-association open of any
  file > 10 MB silently failed (Large File `ContentDialog.ShowAsync` raced a
  null `XamlRoot`; the fire-and-forget `InitialLoadAsync` swallowed the
  `ArgumentException`).

## v2 engine (streamed piece-tree Document, 2026-08-27, same machine/protocol)

### Typing latency (input -> draw-complete)

| Document | p50 | p95 | p99 | working set | private bytes |
|---|---|---|---|---|---|
| corpus-100mb.log | 0.9 | 14.8 | 83.2 | 328 MB | - |
| corpus-1gb.log | 0.8 | 15.1 | 76.8 | 1,374 MB* | 309 MB |

*Working set includes reclaimable OS page cache for the mapped file (the
background index touches every page once); private commit is the real
footprint and stays ~flat in file size.

- 1 GB file: FirstTextDraw 560 ms after process start - the document itself
  adds nothing measurable to launch (open = mmap + one 1 MB segment scan).
- vs baseline: 100 MB typing p50 improved ~98x (87.8 -> 0.9 ms); the 1 GB
  row had no baseline (the old engine needed ~8x the file in RAM and O(N)
  per keystroke).
- Launch (empty session) unchanged from baseline (~480 ms to first draw);
  the startup phase targets that next.
- p95/p99 outliers (~15/80 ms) correlate with background-index absorption
  and GC; candidates for the startup/polish phase.

Smoke (automated): open 100 MB -> Ctrl+End -> Ctrl+Home -> 3x PageDown ->
arrows -> shift-select -> Delete -> Ctrl+Z -> type -> Ctrl+S: app healthy,
save byte-identical except the 9-byte edit.

## Extreme-size validation (10 GB / megaline / CJK / mixed-EOL, 2026-08-27)

10 GB log (~113 M lines), real app, Release x64:

| Scenario | result |
|---|---|
| Typing DURING background index | p50 0.8 / p95 42 / p99 79 ms (after the LOH + batched-absorb + indexer-priority fixes; was p99 1,422 ms) |
| Typing after indexing completes | p50 0.7 / p95 15 / p99 41 ms - identical to a small file |
| Ctrl+End across 113 M lines | 61 ms |
| Go To line 50,000,000 + edit there | works; edit landed correctly |
| Save (one edit at far end + one mid-file) | 5.4 s wall (disk-bound sequential), file delta EXACTLY +17 bytes, byte-identical elsewhere |
| Private bytes at 10 GB | 155 MB (working set grows with reclaimable page cache only) |

Pathological/encoding corpus:

| File | typing p50/p95/p99 (ms) | notes |
|---|---|---|
| corpus-megaline.txt (256 MB, ONE line) | 10.4 / 16.4 / 77 | each keystroke rebuilds the 64 K-cap layout; documented cost, no stall/crash (old wrap would hang) |
| corpus-cjk.txt (64 MB CJK/mixed-width) | 1.4 / 15.7 / 32 | |
| corpus-mixed-eol.txt (CRLF/LF/CR alternating) | - | open -> edit -> save: +7 bytes exactly, mixed endings byte-preserved |

Root causes fixed for the during-index jank: tier-2 long[] break lists and
128 KB decode chunks were LOH-sized (Gen2 pauses on the UI thread); every
segment absorb decoded a fresh chunk at the moving frontier; absorbs ran one
tree op per segment; the indexer thread competed at normal priority.
