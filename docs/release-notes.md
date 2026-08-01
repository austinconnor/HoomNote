# HoomNote 0.7.35

- Page scrolling is now smooth and continuous across page boundaries. Either
  visible page can be edited during a transition, wheel and touch scrolling use
  inertia, and notebook pages are preloaded into a high-resolution preview cache
  to prevent navigation buffering and blurry scrolling artifacts.
- Shapes can now be locked and unlocked from the selection controls. Locked
  shapes cannot be erased or transformed.
- Lasso selection now selects an entire ink stroke when any portion of that
  stroke crosses or falls inside the lasso.
- The page indicator now includes the notebook total, such as `4 of 56`.
- Notebook cover thumbnails are rendered at a sharper card resolution and now
  include incremental first-page edits instead of remaining stale.
- The home library now shows folders followed by one alphabetically ordered
  `All notebooks` section. The recently edited section was removed, and a back
  arrow provides navigation out of folders.
- Samsung Notes imports now resolve every placed image through its stored media
  bind ID, preventing image contents from being assigned to the wrong image box.
- Pen-drawn shape recognition now uses stricter rectangle and ellipse fitting,
  rejects irregular closed scribbles instead of forcing them into ovals, and
  adds reliable five-point star snapping.
- Highlighters now use page-aware marker blending on both light and dark paper,
  preserving underlying page content and avoiding cumulative alpha coverage.
- Added one-tap pen size presets for `0.7`, `1.5`, and `2` below the size slider.
