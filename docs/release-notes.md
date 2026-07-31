# HoomNote 0.7.34

- Restored authored highlighter layering across the live canvas, retained page
  cache, navigation tiles, and thumbnails. Highlighting no longer moves beneath
  handwriting after a notebook is saved or reopened.
- Highlighter opacity is now preserved exactly on light, dark, colored, and
  imported backgrounds instead of being silently raised or lowered by the
  renderer. New highlighters use the restored 60% default opacity.
