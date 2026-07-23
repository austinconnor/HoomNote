# HoomNote Rendering Engine V2

## Objective

The V2 renderer is designed around three non-negotiable requirements:

1. Vector document data is always the source of truth.
2. Navigation must remain inside a 16.7 ms frame budget on low-end hardware.
3. A performance optimization may not visibly reduce ink quality.

It replaces the previous collection of fixed zoom thresholds, page-wide vector command
lists, independent 16 ms timers, and per-interaction mode switches with one predictable
pipeline.

## Architecture

### 1. Immutable vector scene

`NotePage.Objects` remains the authoritative ordered scene. Ink is stored as samples and
fitted vector geometry. Raster resources are disposable representations; they are never
saved into the document and cannot replace authored vector content.

Every committed edit increments the scene revision, updates the spatial index, and either
adds a small overlay or invalidates the retained page snapshot.

### 2. Coalesced input and wet ink

Pointer events consume every Windows intermediate pointer point, not only the last point
delivered to the UI thread. Samples are filtered below a sub-pixel distance to avoid work
that cannot affect the displayed result.

The active stroke uses an incremental viewport-sized mask. Only newly received segments
are appended. The completed stroke is then stabilized once, stored as vector data, and
rendered through the dry-ink path. This follows the wet/dry split described by the Windows
Ink architecture while retaining HoomNote's custom vector model.

### 3. One display-synchronized frame clock

Wheel zoom, touch inertia, and search-highlight animation are advanced by
`CompositionTarget.Rendering`. There are no competing 16 ms dispatcher timers. Multiple
input updates before a display refresh collapse into one invalidation and one draw.

One-shot background-work and autosave timers remain independent because they do not drive
frames.

### 4. Native-resolution navigation snapshot

Normal zooming and panning draw one retained page snapshot. Its resolution is selected
from a strict 24 MiB budget and is capped at 3 source pixels per page DIP.

The renderer accounts for both viewport zoom and monitor DPI before using the snapshot:

`required scale = zoom * display DPI / 96`

The snapshot is used only when it contains at least that many source pixels. It is never
silently enlarged past native display resolution. This removes the old 1.35x quality and
performance cliff.

New strokes remain as vector overlays and are merged in batches, preventing a full busy
page rebuild between handwritten letters.

### 5. Spatially culled detail renderer

When the retained snapshot cannot provide native resolution, the renderer switches to a
single detail path:

- calculate visible page bounds;
- query the page spatial index;
- draw only intersecting objects;
- reuse bounded LRU `CanvasGeometry` paths;
- evict geometry outside the viewport as the budget fills.

The old page-wide vector command-list navigation path is retired. It replayed invisible
content and produced the worst behavior at intermediate zoom levels.

### 6. Explicit memory budgets

- Current page navigation snapshot: 24 MiB maximum.
- One warm page snapshot: 24 MiB maximum.
- Decoded image cache: 24 MiB maximum.
- Ink geometry cache: 2,048 strokes or 180,000 source points.
- Open document cache: one inactive document, capped by source-point count.

All native resources are disposed on invalidation, page removal, device reset, and app
shutdown. No additional renderer runtime is bundled.

### 7. Instrumentation

Frames over 33 ms are recorded asynchronously with zoom, scene size, cache state, active
ink size, overlay count, and interaction state. Disk logging never occurs synchronously
inside `Draw`.

The policy that selects snapshot scale is isolated in `RenderScalePolicy` and covered by
unit tests, including high-DPI cases and memory-budget bounds.

## Research and implementation choices

- [Microsoft Direct2D performance guidance](https://learn.microsoft.com/windows/win32/direct2d/improving-direct2d-performance)
  recommends reusing resources, caching expensive static content, avoiding flushes, using
  axis-aligned clips, and balancing retained geometry against working set. V2 applies those
  recommendations with bounded snapshots and visible-only vector geometry.
- [Win2D CanvasAnimatedControl](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_UI_Xaml_CanvasAnimatedControl.htm)
  demonstrates the value of a single game-loop frame clock. V2 adopts display-synchronized
  scheduling without moving mutable XAML editor state onto a second thread.
- [Windows Ink pen and stylus guidance](https://learn.microsoft.com/windows/uwp/ui-input/pen-and-stylus-interactions)
  separates low-latency wet ink from dry ink and documents custom drying for large,
  transformable ink collections.
- [PointerPoint.GetIntermediatePoints](https://learn.microsoft.com/uwp/api/windows.ui.input.pointerpoint.getintermediatepoints)
  is the source for consuming coalesced hardware samples.
- [Rnote](https://github.com/flxzt/rnote) and
  [Xournal++](https://github.com/xournalpp/xournalpp) validate the retained-vector,
  spatial/cached-page approach used by desktop handwriting applications.
- [Vello](https://github.com/linebender/vello) is useful as a retained-scene reference, but
  its GPU-compute design is not adopted because HoomNote must remain viable on older
  integrated graphics and software fallback.
- [Skia release notes](https://github.com/google/skia/blob/main/RELEASE_NOTES.md) reinforce
  explicit resource-cache limits and tiling for large imagery. Pulling in Skia would add a
  second native rendering stack and substantially increase the binary, so V2 stays on the
  Windows Direct2D/Win2D stack.

## Performance contract

The renderer is considered healthy when:

- wheel and touch navigation submit no more than one draw per display frame;
- a retained navigation frame is a single page image plus small overlays;
- detail frames inspect only visible objects;
- the snapshot is never displayed below native screen resolution;
- pen input never waits for autosave, OCR, thumbnails, or indexing;
- caches remain within their explicit budgets after repeated page and notebook switches.

