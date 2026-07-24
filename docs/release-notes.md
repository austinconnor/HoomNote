# HoomNote 0.6.0

- Replaced the UI-thread page renderer with a dedicated Win2D game-loop surface.
  Live ink and interaction feedback remain on a lightweight foreground surface,
  so dense-page rendering can no longer block pointer delivery.
- Added shallow immutable page snapshots and nonblocking render invalidation so
  the renderer never traverses a collection while an edit is changing it.
- Made ink capture canonical and independent of viewport zoom. Original pointer
  samples are retained, while live and committed pen strokes share the same
  rounded vector curve.
- Removed the full-viewport live-ink raster and the duplicate warm-page cache,
  reducing GPU/native memory without rasterizing HoomNote-created ink.
- Native-resolution tiles now refine only after navigation settles and at most
  one tile per frame. The last complete page remains visible during pan, pinch,
  and wheel zoom instead of synchronously building every visible tile.
- Added regression coverage for zoom-independent ink sampling and bounded tile
  refinement.
