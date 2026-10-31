# Editor review validation (2026-10-30)

Historical report: the subsequent binary-transfer optimization and actual-save
measurements are documented in [export-memory.md](export-memory.md). The fixtures
and workloads differ; do not compare their memory totals directly.

## Changes and correctness gates

- Initialization checks loaded state, positive displayed dimensions and an attached
  design layer instead of exporting image pixels just to detect readiness. Its
  design-state baseline is established before the ready notification.
- Export renders the cloned Konva stage once, returning that canvas and encoding
  it through the existing Konva wrapper. This preserves the encoder's fallback,
  dimensions, MIME/quality options, metadata and cleanup paths. It does not halve
  PNG encoding work: the previous implementation already encoded only once.
- Source-resolution background rendering remains enabled. Preview resolution and
  saved image dimensions have not been reduced, and no runtime dependency or
  production forced-GC call was added.
- Capture matching now consumes pending folder receipts even when a clipboard
  event is suppressed, without extending the fixed two-second clipboard window.
  Five-second cross-source matching remains heuristic; it cannot infer every
  intentional same-image copy. README documents this remaining tradeoff.

Fresh verification for this revision:

| Check | Result |
| --- | --- |
| Application build and core tests | 50 passed, 0 failed |
| Clipboard format recognition (synthetic data, not the user clipboard) | 48 passed |
| Image-file validation and saving | 11 passed |
| Initialization protocol (Node VM) | 7 passed |
| Actual bundled exporter/encoder contract (Node VM) | 8 passed |
| Real WebView2 pixel/export contract | 15 passed, 0 failed |

The real editor cases cover an exact one-pixel grid, offset crop, 90/180-degree
rotation, horizontal/vertical flips, partial/full transparency, and the
source-resolution identity-filter cache path. Expected pixels come from the
fixture's independent coordinate mapping. Fully transparent hidden RGB is not a
visible-pixel requirement. Three additional cases verify a single stage render
and equality between the returned canvas and decoded PNG. Before the single-render
change, these three failed with two renders; all 15 pass afterwards.

Node extraction tests also check JPEG/JPG/WebP option forwarding, resize metadata,
and both encoder failure paths. They do not establish browser pixel equivalence
for these non-PNG formats, non-unit export ratios, arbitrary-angle rotation, or
actual modifying filters. The initialization VM does not model all React timing;
the real WebView2 runs independently exercise editor startup and saving.

## Bounded resource comparison

Two sequential runs per version on the same Windows x64 host and installed
WebView2 runtime. The fixture is a synthetic solid-color 3840 x 2160 image.
Each run opens a fresh editor, performs five ordinary exports and one
identity-filter export, collects browser garbage for an additional observation,
closes the editor, and samples after seven seconds.

**Before means PR revision `6570282`, which already includes the source-resolution
quality fix, not pristine upstream.** Before/after used the same newly compiled
probe executable and dependencies in separate asset directories; only
`editor.js` and the vendored bundle differed. The original comparison bundle's
SHA-256 is `b3fb2a385e8d5aaae7ef5daad9c1c785df6d403f9ec28ea7098bec2c2e63a9f2`;
the optimized bundle's is
`9fae46b32fc74660da67acd649199d571a30141b9f8da8a0d940d007bd7109c7`.

Memory units below are MiB (`bytes / 1048576`). Each value sums the probe and its
WebView2 descendants. Working set and private bytes are different metrics and
must not be interpreted as interchangeable.

| Version/run | Open ms | Open WS | Open private | Sampled max export WS | Sampled max export private | Closed WS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Before 1 | 1313 | 833.9 | 915.7 | 1020.3 | 2003.1 | 114.8 |
| Before 2 | 1112 | 818.5 | 916.0 | 988.2 | 1413.2 | 114.9 |
| After 1 | 1073 | 606.1 | 458.1 | 1008.4 | 1193.5 | 114.8 |
| After 2 | 973 | 602.7 | 456.6 | 1007.4 | 1191.0 | 114.8 |

Per-export milliseconds, excluding the WMI memory sampling that follows each
export (last entry is the identity-filter export):

```text
Before 1: 60, 57, 54, 54, 54, 128
Before 2: 62, 54, 54, 50, 89, 183
After  1: 113, 55, 55, 50, 50, 130
After  2: 114, 58, 53, 53, 53, 174
```

Opening used less sampled memory in both runs. The first explicit export was
slower after removing the eager initialization export; later export timings were
not consistently better. Export working-set maxima also did not consistently
decrease. Do not claim a general save-time speedup, a fixed memory bound, or a
percentage improvement from this small synthetic comparison.

All four closing samples contained one process; private bytes were 102.4-102.5
MiB. This observes browser subprocess exit, not proof of leak freedom. Sampled
maxima are not true allocation peaks; GC, GPU/browser caches and host load vary.
The synthetic source fixture remains alive in the probe's baseline/closing
samples. Full-resolution filters still need source-size buffers (one 4K RGBA
buffer is about 31.6 MiB), and the quality fix is not resource-free relative to
the original blurred export path.

## Reproduce

```powershell
.\build.ps1
node --test tests/EditorInitializationTests.js tests/EditorExportTests.js
node --check src/editor/editor.js
node --check vendor/Filerobot/filerobot-image-editor.min.js
.\tests\EditorQualityTests.ps1
.\tests\ClipboardImageTests.ps1
.\tests\EditorResourceProbe.ps1 -ExportReview
```

The probe checks every export's returned dimensions and writes
`build/performance/editor-review.json`, including opening time, individual export
times, the sampling-inclusive total and memory snapshots. For comparisons, retain
the same probe executable/dependencies, use separate directories for each
version's editor assets, and run them sequentially. Tests and reports are not
part of the application package. Node.js is required only for the optional
development regressions, not at application runtime. See
`vendor/Filerobot/PICROPE-PATCHES.md` for the readable patch and upgrade checklist.
