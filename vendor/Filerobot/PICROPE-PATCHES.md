# Local export-quality patch

The bundled `filerobot-image-editor.min.js` has a small patch in the local
`getCurrentImgData` export path. Keep it when updating the vendored bundle,
unless the replacement passes `tests/EditorQualityTests.ps1` without it.

Upstream always caches the cloned `FIE_original-image` at its displayed size
before scaling the design layer back to the source dimensions. Saving with
pixel ratio 1 therefore resamples the background twice. PNG output keeps the
reported dimensions but loses fine detail and can gain semi-transparent edges.
Increasing the saving pixel ratio does not eliminate the resampling.

The export clone now uses the source pixel dimensions for its image node and
offset, with an inverse node scale to preserve the existing scene geometry.
It clears the cache when there are no filters; filters still receive a cache,
but at source resolution with pixel ratio 1. The live preview is unchanged.

Run from the repository root on Windows:

```powershell
.\build.ps1
.\tests\EditorQualityTests.ps1
```

The test saves a 1600 × 1000 single-pixel checkerboard through the real WebView2
editor and compares every BGRA byte, including alpha. It also verifies the
filter-cache path with an identity filter.
