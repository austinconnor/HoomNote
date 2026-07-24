# HoomNote 0.6.1

- Keeps a complete, current page snapshot visible after undo, redo, and other
  structural edits instead of exposing an empty tile-refinement surface.
- Refines native-resolution tiles invisibly and presents the visible set only
  when every required tile is ready, eliminating checkerboard loading during
  panning.
- Adds overscan gutters around tiles so filtered edges overlap cleanly rather
  than exposing a persistent tile grid.
- Adds regression coverage for atomic tile presentation and gutter geometry.
