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
