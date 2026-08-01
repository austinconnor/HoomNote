# HoomNote 0.7.36

- Shapes now show the same floating lock control as images, making the lock
  state visible and directly accessible on the canvas.
- Undo and redo now rebind transformed selections to the restored document
  objects, preventing stale selection state from leaving strokes extremely
  thick or text incorrectly scaled.
- Handwriting committed on one visible page no longer disappears when focus
  immediately moves across the page break. Delayed previews are tracked per
  page, and undo or redo refreshes every page affected by the command.
- Continuous scrolling now requests high-resolution previews up to five pages
  in both directions so upcoming pages are ready before they enter the viewport.
- Page navigation retains bounded vector geometry and a larger two-notebook hot
  cache, reducing repeated rendering and deserialization in dense notebooks.
- Navigation refinement uses smaller work units to reduce long frames, newly
  created notes open from memory instead of being reloaded from the database,
  and redundant ink-point scans were removed from document loading.
- Added timing diagnostics for library refreshes and note creation to make
  remaining slow interaction paths directly measurable in the app log.
- The page sidebar now maintains exactly one visual selection and keeps its
  selection styling clear of the scrollbar.
- Samsung Notes image fallback matching now uses each media file's stable
  numeric index instead of arbitrary ZIP entry order, preventing image content
  from being placed into the wrong correctly sized box in image-heavy notes.
