# HoomNote 0.7.43

- Opening a dense notebook now reads its large SQLite JSON payloads directly as UTF-8 bytes and parses independent pages through a bounded worker set, avoiding a second UTF-16 copy of millions of ink samples while substantially reducing load time and memory pressure.
- Repeated clicks on a notebook that is already loading now join the existing load instead of cancelling and restarting the entire multi-million-point parse.
- Pen and eraser gestures no longer cancel and restart the complete notebook page-preview pass after every interaction.
- High-resolution page tiles now wait until the pen has been idle and can be cancelled inside a long stroke, preventing refinement work from delaying the next stroke.

## Previous release: HoomNote 0.7.42

- Dense imported ink is now reduced to display-resolution geometry before page snapshots and previews are rendered, avoiding multi-million-point replays that blocked tab switches and first pen input without changing the saved source strokes.
- Pen input now cancels page and sidebar rendering immediately, and cancelled preview jobs stop inside long strokes instead of continuing to consume CPU after navigation.
- The full-page snapshot builder no longer starts beneath an active pen stroke; the already-composed preview remains visible until input finishes.
- SQLite operations now run through a serialized worker queue instead of synchronously occupying the WinUI thread while large notebook pages are fetched, saved, or indexed.
- First-page cover thumbnails are regenerated only when the library needs them instead of after every editing pause, preventing dense cover rendering from competing with writing and tab switching.

## Previous release: HoomNote 0.7.41

- Undo and redo now keep the current page sharp while the corrected frame is rebuilt, eliminating the distracting blurry flash between history steps.
- Switching back and forth between two dense notebook tabs now reuses each
  page's composed GPU snapshot instead of replaying thousands of objects and up
  to millions of ink samples after every switch.
- Pen-down now immediately pauses committed-page refinement, preventing tile
  rendering underneath the ink overlay from delaying the first visible stroke.
- In-progress navigation-tile builds now yield when input arrives, and retained
  stroke geometry follows the current viewport instead of remaining filled with
  geometry from a previously viewed location.
- Completed page previews are now retained in a bounded cross-tab cache, avoiding
  repeated background rendering of the same dense pages after every tab switch.
- Slow-frame diagnostics now report tile-build cancellations and input-priority
  state for follow-up performance verification on affected devices.

## Previous release: HoomNote 0.7.40

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
