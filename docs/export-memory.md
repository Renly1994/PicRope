# Binary editor export and capture persistence (2026-10-31)

This follow-up targets the measured PNG/Base64 transfer cost, not preview quality
or the size of source-resolution filter buffers. The earlier investigation in
`editor-performance.md` remains a historical report of a different fixture and
export-only workload; its numbers are not directly comparable with this one.

## Changes and boundaries

- The vendored exporter accepts the explicit boolean `picropeCanvasOnly: true`.
  It returns the normal source-resolution canvas, metadata and design state,
  with `imageBase64: null`, without running the string encoder. Default callers
  retain the existing encoding options, retry behavior and single-render path.
- PicRope awaits pending tool/text/font work, exports once and encodes the canvas
  with `toBlob(..., 'image/png')`. It posts the Blob to the existing WebView2
  resource interceptor instead of serializing a Base64 string through JSON.
  This removes those application-level string conversions, **not all copies**.
- The intercepted URL is exactly `https://screenline-editor.invalid/export`.
  CSP allows connections only to that endpoint. The host requires POST,
  `image/png`, the editor's initiating Origin, its current index page, a
  per-window random token, a known action, and a valid close flag. There are no
  CORS headers, network fallback, or JavaScript-supplied destination filenames.
  Other external resources remain blocked by the interceptor.
- The host rejects simultaneous uploads. Both sides enforce a 64 MiB encoded
  payload limit; the host checks actual bytes read, including non-seekable
  streams, the full PNG signature, and decoded image validity before accepting.
  This is not a bound on decoded pixel memory, nor a sandbox against hostile
  code already executing inside the trusted editor page.
- HTTP 202 means accepted, not saved. Save/copy completion still comes from the
  host's existing acknowledgement. Encoding/transfer failures notify the host
  to settle pending save-on-close. Concurrent close callers share the same
  pending result. Closure and dispatch failure release pending waiters.
- Only the captured request body stream is read on a background worker; WebView2
  objects stay on the UI thread. The request deferral completes before queued
  file/dialog work. The bundled SDK stays at 1.0.4258.31. See the SDK XML for
  `CoreWebView2WebResourceRequest.Content`, `GetDeferral`, and Microsoft's
  [threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model).
- The legacy Base64 host message handler remains for compatibility, but the
  application does not automatically retry failed Blob exports through it.
  File encoding, atomic replacement, copy ownership tagging, preview resolution
  and saved dimensions keep their existing behavior. No runtime dependency,
  SDK upgrade, production forced GC, or installed-app change was added.

## Why transfer was selected

The diagnostic `-PhaseReview` probe uses a deterministic 2048 x 1152 opaque
seeded RGB-noise image (seed 20261031). Its pre-change run observed:

| Boundary | Elapsed ms | Process-tree WS MiB | Private MiB |
| --- | ---: | ---: | ---: |
| Opened | n/a | 596.0 | 447.7 |
| Render + PNG/Base64 export | 0.7 + 68.3 | 621.7 | 540.1 |
| Transfer + JSON parse | 292 | 691.5 | 608.5 |
| Host decode | 143 | 714.9 | 630.6 |
| File save | 200 | 724.0 | 635.4 |

The export contained 10,823,886 string characters representing 8,117,897 PNG
bytes. **The diagnostic transfers via ExecuteScriptAsync's JSON return, not
the production WebMessageReceived event.** Its 292 ms is not a measurement of
that event's cost. Phase outputs remain rooted during sampling, and the
boundaries are snapshots, not allocation peaks. This supports investigating
string transfer; actual-save A/B measurements below test the changed path.

## Actual-save comparison

Same Windows x64 host and WebView2 runtime **148.0.3967.54**, same seeded fixture
and probe source. Before is PR commit **2940e9e**, which already includes the
image-quality and single-render fixes, **not pristine upstream**. The baseline
host source and editor assets were extracted from that commit into a separate
staging directory; the after executable uses the changed host and assets.
Other probe sources and dependencies were held constant. Runs were sequential,
in before/after/before/after order, without forced GC.

The timer runs in JavaScript from actual `window.submit('save')` to the host's
`completeAction` acknowledgement. The probe verifies the saved pixel
fingerprint for every run. Saved PNG size was 8,069,891 bytes. Memory is the sum
of the probe and its WebView2 descendants, in MiB (`bytes / 1048576`); working
set and private bytes are different metrics. Every in-flight sample below
tracked seven processes.

Final-code verification:

| Version/run | Save ms | Open WS | Sampled max save WS | Sampled max private | Closed WS |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before 1 | 627.2 | 595.5 | 692.9 | 613.5 | 198.9 |
| After 1 | 506.0 | 647.9 | 710.6 | 613.4 | 207.3 |
| Before 2 | 624.3 | 589.3 | 684.7 | 617.4 | 198.9 |
| After 2 | 486.7 | 584.1 | 647.1 | 580.8 | 160.0 |

Earlier paired exploratory runs, before the final lifecycle exception/waiter
hardening (same normal-save path and sampling method):

| Version/run | Save ms | Open WS | Sampled max save WS | Sampled max private | Closed WS |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before 1 | 649.5 | 602.4 | 700.4 | 620.6 | 198.6 |
| After 1 | 480.7 | 598.6 | 659.4 | 575.2 | 159.9 |
| Before 2 | 688.0 | 599.3 | 695.5 | 616.6 | 198.8 |
| After 2 | 515.0 | 599.3 | 664.5 | 583.4 | 159.8 |

A standalone final-code run, outside the staged A/B order, observed 613.2 ms,
open WS 598.2 MiB, sampled max WS 708.3 MiB, max private 613.8 MiB and closed
WS 207.0 MiB. It is retained here rather than selecting only favorable runs.

**Interpretation:** actual saving was faster in both final matched pairs for
this fixture. Memory did **not** consistently improve: the first final after
run had a higher sampled working-set maximum than before. Do not advertise a
fixed memory saving, a universal percentage speedup, or that peak memory is
solved. Encoding, decoding, atomic save and source-resolution filter buffers
still have costs. Two matched pairs are not a latency distribution.

Sampling targets the process tree discovered at ready, with approximately
10 ms waits plus query/script overhead. New processes and transient peaks can
be missed. Closed samples are seven seconds after closure and contain only the
probe process; the source fixture remains rooted. They are not tray-idle memory
or proof of leak freedom. Browser/GPU caches, GC and host load vary; packaging
also ran briefly during the final sequence, so these are local observations,
not an isolated benchmark. Raw local JSON/logs are under ignored
`build/export-memory-20261031/`; tests, reports and caches are not packaged.

## Clipboard persistence race

The previous pipeline accepted a receipt before awaiting the clipboard file
write. A failed/stale write could leave a receipt that suppressed the matching
folder event, even though no clipboard capture was published. Receipt acceptance
now happens only after the write and final sequence/collection/disposal checks.
Failed, invalidated and duplicate writes remove their abandoned files.

This deliberately favors correctness over an early I/O shortcut: a repeated
clipboard image may now be written and then deleted. It does not change the
fixed two-second clipboard window or five-second cross-source heuristic. The
heuristic still cannot infer every intentional same-image copy; see README.

## Verification and reproduction

Final application build succeeded. Focused checks: **193 passed** in total;
package whitelist separately passed with 20 files.

| Suite | Passed |
| --- | ---: |
| Core | 50 |
| Controllable-write capture persistence | 5 |
| Initialization / bundled exporter / submission Node VMs | 7 / 12 / 19 |
| Bounded binary PNG reader | 8 |
| Real WebView2 upload/security/lifecycle | 18 |
| Real WebView2 pixel/export | 15 |
| Synthetic clipboard format recognition | 48 |
| Image-file validation / saving | 11 |

Capture persistence, canvas-only submission and concurrent-close regressions
were observed failing before their fixes and passing afterwards. Pixel tests
include grids, crop, 90/180-degree rotation, flips, partial/full alpha and an
identity filter cache. PNG source dimensions, alpha and visible RGB remain
unchanged in those cases. Node syntax checks and `git diff --check` passed.

```powershell
.\build.ps1
node --test tests/EditorInitializationTests.js tests/EditorExportTests.js tests/EditorSubmissionTests.js
node --check src/editor/editor.js
node --check vendor/Filerobot/filerobot-image-editor.min.js
.\tests\EditorTransferTests.ps1
.\tests\EditorUploadTests.ps1
.\tests\EditorQualityTests.ps1
.\tests\ClipboardImageTests.ps1
.\tests\EditorResourceProbe.ps1 -PhaseReview
.\tests\EditorResourceProbe.ps1 -SaveReview
.\package.ps1
```

The build runs core and capture persistence checks. The existing
`build.ps1 -EditorTests` target also includes the file tests and interactive
desktop coverage; it was **not** run here because those desktop tests can touch
the actual clipboard. The 11 file checks were compiled/run separately using the
same Framework references. Node is optional development tooling, not a runtime
dependency. For A/B, stage separate executable/asset directories from the two
revisions with the same probe source and dependencies; preserve each JSON before
the next run overwrites it.

Remaining gaps: no user-clipboard manipulation, modal save-picker interaction,
closure while a request-body read/deferral is outstanding, arbitrary-angle
rotation, modifying-filter pixel equivalence, non-unit export ratios, or
non-PNG browser pixel-equivalence validation. Token, method, type, action,
close-flag, malformed-PNG, busy-state and closure-during-save checks are not a
complete adversarial security test. The installed application and its data
were left untouched.
