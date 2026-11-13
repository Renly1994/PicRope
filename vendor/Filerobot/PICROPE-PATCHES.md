# Local export-quality patch

The bundled `filerobot-image-editor.min.js` has a small patch in the local
`getCurrentImgData` export path. Keep it when updating the vendored bundle,
unless the replacement passes `tests/EditorQualityTests.ps1` without it.

The baseline is Filerobot v4.9.1 (pristine Git blob
`ebffb1513a8da3d5f139ad9ec8309e9d0c3208b3`). The patched bundle's SHA-256 is
`30d921bc6d2845aa8939c511e4beea3e71f968dcbb21a11e5dd0d2e60069cec7`.
These fingerprints describe this vendored
revision, not a requirement that future upgrades retain the same file hash.

Upstream always caches the cloned `FIE_original-image` at its displayed size
before scaling the design layer back to the source dimensions. Saving with
pixel ratio 1 therefore resamples the background twice. PNG output keeps the
reported dimensions but loses fine detail and can gain semi-transparent edges.
Increasing the saving pixel ratio does not eliminate the resampling.

The export clone now uses the source pixel dimensions for its image node and
offset, with an inverse node scale to preserve the existing scene geometry.
It clears the cache when there are no filters; filters still receive a cache,
but at source resolution with pixel ratio 1. The live preview is unchanged.

## Readable reference for the vendored change

The changed expression is inside the local export function, after cloning the
stage and finding `FIE_original-image`, before scaling the cloned design layer.
In this bundle, `x` is that image node, `i` is the original image, and `o` is
`shownImageDimensions`. Its equivalent unminified code is:

```javascript
const sourceScaleX = i.width / o.width;
const sourceScaleY = i.height / o.height;
x.setAttrs({
  width: x.width() * sourceScaleX,
  height: x.height() * sourceScaleY,
  offsetX: x.offsetX() * sourceScaleX,
  offsetY: x.offsetY() * sourceScaleY,
  scaleX: x.scaleX() / sourceScaleX,
  scaleY: x.scaleY() / sourceScaleY,
});
if (x.filters() && x.filters().length) {
  x.cache({ pixelRatio: 1 });
} else {
  x.clearCache();
}
```

The original expression at this point was just `x.cache()`. Only the disposable
export clone is modified. No runtime dependency, preview cache, or persistent
source-image copy is added. A filtered export needs a source-resolution bitmap
cache (about `4 * width * height` bytes per RGBA buffer, before library/browser
overhead); unfiltered exports do not create that node cache.

Minified identifiers can change on upgrade. Re-find the export function by its
behavior and `FIE_original-image`, rather than applying the expression to a new
bundle blindly. Keep the readable reference and pixel/geometry tests together.

Run from the repository root on Windows:

```powershell
.\build.ps1
.\tests\EditorQualityTests.ps1
```

The test saves a 1600 × 1000 single-pixel checkerboard through the real WebView2
editor and compares every BGRA byte, including alpha. It also verifies the
filter-cache path with an identity filter.

## Optional resource probe

After building, run `tests/EditorResourceProbe.ps1 -ExportReview` for a bounded
3840 × 2160 probe: five ordinary exports and one identity-filter export, followed
by closing the editor and sampling after seven seconds. The report is written
under `build/performance/editor-review.json`. Process memory includes the probe
and its WebView2 descendants; elapsed time also includes memory sampling.

The generated source fixture is still present at the baseline and closing
samples. Browser/native caches and garbage-collection timing affect the totals;
these snapshots are observations, not a leak test or a guaranteed memory bound.
Compare identical fixtures, runtime versions, and export counts before drawing
performance conclusions. The probe and test fixtures are not shipped in the
application package.

## Single-render local export

The local exporter previously called `b.toCanvas(h)` and `b.toDataURL(h)`.
Both Konva Node methods call `_toKonvaCanvas(h)`, rendering the cloned stage
twice. It now renders once and shares that Konva canvas wrapper between the
returned HTML canvas and the base64 encoder. The equivalent readable code is:

```javascript
const exportCanvas = b._toKonvaCanvas(h);
m = exportCanvas._canvas;
v = exportCanvas.toDataURL(h.mimeType || null, h.quality || null);
```

The minified implementation declares the previously unused
`picropeExportCanvas` in the export function's existing `var` declaration.
Only that declaration and the paired render expression change. The earlier
source-resolution image/cache patch remains in place.

Use the Konva wrapper's encoder, which retries native canvas encoding without
arguments on failure, then reports an error and returns an empty string if
the retry also fails. The MIME/quality defaults, including zero quality becoming
`null` at the encoder, match Konva Node's `toDataURL`. The exporter constructs
these options itself and does not supply a callback. Output metadata, resize
behavior, design state, and cleanup keep their existing paths.

Run from the repository root (Node.js, no added dependencies):

```powershell
node --test tests/EditorExportTests.js
node --check tests/EditorExportTests.js
node --check vendor/Filerobot/filerobot-image-editor.min.js
```

The regression extracts the actual local exporter, Konva Node export methods,
and Konva canvas encoder from this bundle into Node VMs. Only stage drawing and
native canvas encoding are stubbed at those boundaries. It guards unique
extraction markers and verifies one stage render, the shared canvas, PNG/JPEG/
JPG/WebP options, resize metadata, cleanup, and both encoding-failure paths.
Before the optimization, five output-contract cases passed and three render
checks failed with two renders instead of one; all eight pass after it.

This uses internal Konva `_toKonvaCanvas` and `_canvas` APIs: inspect them again
when upgrading the bundle. These VM checks do not measure browser pixels,
elapsed time, or memory; the WebView2 quality tests and optional resource probe
above remain separate checks.

## Optional PicRope canvas-only export

PicRope can explicitly skip Base64 generation while keeping the same exported
canvas, metadata, design state, render, and cleanup paths:

```javascript
const result = editor.getCurrentImgData({
  extension: 'png',
  picropeCanvasOnly: true,
}, 1);
// result.imageData.imageCanvas is the normal HTML canvas.
// result.imageData.imageBase64 is null; the wrapper encoder is never called.
```

This is a PicRope-only option, enabled only by the boolean `true`. Omitted,
false, or other values retain the default exports, including the Konva encoder's
retry/error behavior and all eight existing output contracts. Preview behavior
is unchanged.

The exact inner options variable in this bundle is `g`, initialized by
`g=uk({size:a},g=0<arguments.length&&void 0!==arguments[0]?arguments[0]:{})`.
It is later reused for the extension. Capture the flag before that reassignment;
the readable equivalent of the additional change is:

```javascript
const picropeCanvasOnly = g.picropeCanvasOnly === true;
// Existing geometry, resize, and MIME/quality setup stays in place.
const exportCanvas = b._toKonvaCanvas(h);
m = exportCanvas._canvas;
v = picropeCanvasOnly
  ? null
  : exportCanvas.toDataURL(h.mimeType || null, h.quality || null);
```

The frontend stores `options.exportToken` in `startEditor`. Submission applies
pending tool/text work, awaits fonts, requests this canvas-only export, and
captures `submitted` before awaiting `imageCanvas.toBlob(callback, 'image/png')`.
It rejects null, empty, non-PNG, or larger-than-64-MiB results. Exactly 64 MiB is
allowed. It posts the Blob to `https://screenline-editor.invalid/export` with:

```text
Content-Type: image/png
X-PicRope-Token: <exportToken>
X-PicRope-Action: save | saveAs | copy
X-PicRope-Close: true | false
```

`finish` maps to `save` with close `true`; other actions use close `false`.
Native fetch supplies `Origin`. HTTP 202 only means accepted: the editor stays
busy until the host invokes the existing `window.completeAction` after the
actual operation. Export, Blob, HTTP, or transport failure first sends
`{action:'exportFailed'}` so the host can settle save-on-close failure, then
invokes the existing failure completion. There is no automatic Base64 retry.

Run the Node-only regressions and syntax checks from the repository root:

```powershell
node --test tests/EditorExportTests.js tests/EditorSubmissionTests.js tests/EditorInitializationTests.js
node --check vendor/Filerobot/filerobot-image-editor.min.js
node --check src/editor/editor.js
node --check tests/EditorExportTests.js
node --check tests/EditorSubmissionTests.js
```

TDD RED: the new canvas-only regression reached native encoding twice, while
the eight existing contracts still passed; the submission VM tests failed on
the missing canvas-only option, Blob/HTTP protocol, and host failure notification.
GREEN: all 38 Node tests pass, including startup, default export contracts,
submission actions/headers, delayed operation and encoding timing, dirty/busy
state, validation boundaries, and failure notification before completion.

These are frontend contract checks. The VM uses the real editor
script with drawing, DOM, Blob encoding, and transport boundaries stubbed; its
size-boundary Blobs override the reported size without allocating 64 MiB.
It does not by itself prove host transport/provenance validation, save-on-close
task completion, or pixel/security behavior. Run `tests/EditorTransferTests.ps1`,
`tests/EditorUploadTests.ps1` and `tests/EditorQualityTests.ps1` for the separate
host/real-WebView2 checks. See `docs/export-memory.md` for measured results,
protocol restrictions and remaining lifecycle/coverage limitations.
