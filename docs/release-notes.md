# HoomNote 0.5.10

- Replaced the medium-zoom renderer cliff with a bounded native-resolution tile
  working set for dense pages.
- Tiles are scale-bucketed upward to stay sharp, reused across panning frames,
  culled outside the viewport, and kept within a small memory budget.
- Background handwriting indexing now cancels as soon as navigation resumes and
  releases native analyzer stroke data after every batch.
- Expanded slow-frame diagnostics with render mode, visible-object, tile-build,
  tile-count, and tile-memory measurements.
