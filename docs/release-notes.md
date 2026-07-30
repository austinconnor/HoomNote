# HoomNote 0.7.32

- Added a separate applied stroke-size control to the style brush. Stroke
  thickness can now be adjusted independently from the brush coverage area.
- Prevented the temporary grid from flashing through a finished pen stroke by
  retaining the committed-ink preview before the background render handoff
  begins.
- Reworked the home library to show navigable folders before recently edited
  notebooks. Added a persistent Home button that returns to the root library
  without closing open notebook tabs.
