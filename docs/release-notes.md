# HoomNote 0.7.40

- Switching between open notebook tabs is substantially faster, especially for
  dense handwritten notebooks and lower-end devices: the two most recently used
  notebooks now remain memory-resident even when they exceed the normal cache
  budget, and up to four notebooks can stay cached.
- Tab activation now refreshes the page sidebar in one virtualized update instead
  of issuing a separate UI notification for every page.
- Background page-preview rendering now yields to the active page so it cannot
  compete with the first visible frame during a tab switch.
- Tab-switch diagnostics now report save, document-fetch, and UI-binding timings
  separately to make future device-specific performance reports actionable.

## Previous release: HoomNote 0.7.39

- Smart Shapes now recognize smaller and rougher rectangles, ovals, and stars,
  then show the closest snapped shape while the pen is still held in place.
- Highlighters are stronger and more marker-like on both light and dark pages,
  while repeated passes on dark pages stay readable instead of quickly
  saturating into an opaque block.
- Dragged strokes, shapes, text, and images can now cross page boundaries while
  preserving their visible position, selection, save state, and undo history.
- Copy and cut operations now paste at the cursor or pen's last canvas position
  on the destination page instead of reusing the source page coordinates.
- Temporary-grid visibility is now remembered independently for every open
  notebook tab, so toggling the grid does not change the other tabs.
- The active page thumbnail now repairs and restarts a missed render after
  navigation instead of remaining on a distracting loading spinner.
- Image and shape lock controls now stay visible on white backgrounds and move
  continuously with the selected object while it is being dragged.
