# HoomNote 0.7.31

- Added a canvas right-click menu for cut, copy, and paste. “Paste here”
  places HoomNote objects, images, or text at the page position that was
  right-clicked.
- Moved the style brush into the main toolbar and replaced its RGB controls
  with a chooser populated from saved pen presets. Brush coverage remains
  adjustable in the same compact flyout.
- Added an independent, persistent eraser-size control for both segment and
  whole-stroke erasers, including a live on-page eraser outline.

- Mouse-wheel input now pans vertically; holding Ctrl while scrolling performs
  smooth cursor-anchored zoom.
- Added a compact corner grip that can be dragged with a mouse or pen to zoom,
  plus a small page number at the bottom of paged notes.
- Made the selected page substantially clearer in the page sidebar.
- Samsung Notes rectangles, ellipses, triangles, diamonds, stars, and line
  objects now import as editable HoomNote vectors with their source bounds,
  color, width, arrow direction, and supported rotation/fill styling.
- Importing a PDF, presentation, or Samsung Note from the home library now
  creates and opens a correctly named notebook automatically.

- Removed the redundant shape icon from the active shape dropdown so longer shape names remain readable and correctly aligned.

- Added a visual startup library with lazy first-page previews. Recently edited
  notebooks appear first, followed by the remaining notebooks alphabetically.
- Added vertical page continuation through ordinary touch or mouse panning and
  removed the hidden right-edge page-switch gesture. The previous and next
  pages now remain visibly staged above and below the current page while
  scrolling instead of popping in at the transition threshold.
- New pages insert immediately after the selected page, and page thumbnails can
  be reordered using press-and-hold drag and drop with undo support. Generated
  Page N labels are renumbered by their actual position after inserts, deletes,
  reorders, undo, and redo.
- Notebook tabs now inherit notebook or folder colors, and full notebook,
  folder, and page titles appear in hover tooltips.
- Large pen-drawn circles and boxes now snap without requiring a terminal hold,
  while handwriting-sized loops remain ink.
- Reduced idle power use by avoiding automatic notebook opening at startup,
  lazily loading home thumbnails, and removing handwriting/OCR indexing from
  the active app. Library search now targets notebook and folder names only.

- Highlighters now use one stable source-over rendering path in the live
  canvas, retained page cache, native tiles, and thumbnails. Committed
  highlighting sits beneath authored ink and text, uses stronger contrast on
  colored/imported backgrounds, and no longer changes blend modes while zooming.
- Horizontal panning is bounded to the actual page width, so a note cannot be
  pushed completely off either side of the viewport.
- Pen, eraser, lasso, selection-transform, and style-brush pointer moves now
  invalidate only the lightweight interaction overlay. Busy committed pages no
  longer redraw for every input sample or switch to a blurry navigation
  snapshot while writing.
- Finished pen strokes remain in the interaction overlay until the retained
  page renderer acknowledges the committed edit, preventing the temporary grid
  from flashing through the stroke on pen-up.
- The style brush now has an independent, persistent 8–120 px brush-size
  control and a visible circular cursor.
- Selection scaling keeps ink and shape line weight visually constant by
  default. An option in Settings can restore proportional stroke-thickness
  scaling.
- Selection rotation remains smooth but magnetically snaps to exact 90-degree
  increments within seven degrees of each quarter turn.
