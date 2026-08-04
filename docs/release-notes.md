# HoomNote 0.7.38

- Samsung Notes imports made from annotated PDFs now restore the embedded PDF
  pages instead of showing blank white backgrounds.
- Imported Samsung pen and highlighter strokes now preserve their original
  tool, color, width, opacity, pressure behavior, and page placement.
- Samsung highlighters now retain translucent blending in both the live canvas
  and exported PDF/SVG documents.
- Additional Samsung stroke-record layouts are now decoded so annotations are
  not silently omitted from imported notebooks.
- Imported PDF backgrounds now render sharply on adjacent pages as soon as they
  enter the viewport, and remain stable while focus moves between pages instead
  of flashing during the renderer handoff.
- The right-side Settings panel now closes when clicking anywhere outside it.
- The selected page thumbnail remains pinned in the page-rail cache instead of
  falling back to a permanent loading spinner in longer notebooks.
