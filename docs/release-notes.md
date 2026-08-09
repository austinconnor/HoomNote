# HoomNote 0.7.47

- Writing, drawing shapes, moving selections, and erasing no longer switch the committed page into the lower-resolution pan-and-zoom rendering path, keeping the page sharp throughout editing.
- Completed strokes are folded into the preserved page frame before a structural edit invalidates its cache, preventing recent writing from temporarily disappearing and returning several seconds later.
- Editing still pauses expensive tile and raster refinement, while actual pan, touch, wheel, and zoom navigation retain the optimized snapshot path for responsive lower-end hardware.
- Rectangle, star, line, and circle shortcuts now appear after existing pen and highlighter presets by default and select their shape without replacing the current drawing color or width.
- Every preset tile can now be pressed and held, then dragged to reorder the shared toolbar; neighboring tiles animate into place during the drag and the resulting order persists across launches.
