# HoomNote 0.7.50

- Writing, erasing, and dragging no longer trigger full source-vector page replays on the shared graphics device while the pointer is down.
- Zoomed pages progressively retain every completed high-resolution tile without redrawing the entire visible scene after each tile.
- Switching away from a dense notebook now releases its inactive document, undo, spatial-index, and stroke-geometry caches before editing another notebook.
- Smart-shape recognition and pointer classification perform less work in the per-sample writing path.
- Diagnostics now report slow live interaction frames and expensive selection move-cache preparation separately from committed-page rendering.
