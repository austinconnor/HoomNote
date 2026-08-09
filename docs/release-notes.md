# HoomNote 0.7.51

- Writing and erasing no longer redraw the committed page at pointer-down, preventing the temporary grid and page background from flashing.
- Structural edits retain the previous sharp page tiles beneath their correction overlay instead of exposing a blurred fallback.
- Move and erase correction overlays remain visible until every replacement tile in the visible viewport is current.
