# ADR 0001: Pin SixLabors.ImageSharp to 3.1.x

**Status:** accepted (2026-06-12) · **Phase:** 1 · **Revisit:** Phase 5 (media pipeline)

## Context

ImageSharp 4.0 enforces a commercial license key at **build time** (the build
fails without `$(SixLaborsLicenseKey)`). The 3.1.x line ships under the Six
Labors Split License: free for open source and for companies under the revenue
threshold, with no build-time enforcement.

## Decision

Pin `SixLabors.ImageSharp` to 3.1.12 in `Directory.Packages.props`.

## Consequences

- Phase 5 (webp ladder in media-worker) builds on the 3.1 API.
- Before production launch, either (a) purchase a Six Labors license and move
  to 4.x, or (b) swap to an Apache-licensed alternative (Magick.NET, SkiaSharp)
  behind the media worker's image-processing seam. The processing code is
  isolated to Dcms.MediaWorker, so the swap surface is small.
