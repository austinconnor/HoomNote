# HoomNote 0.8.3

- Reworked dense-page rendering around dirty-region updates. Editing, erasing, moving, undoing, and pasting now rebuild only affected navigation tiles and patch the matching area of the retained page image.
- Added scale-aware ink sampling and bounded batching for compatible opaque strokes. A 1.07-million-point HoomNote page reduced its planned object draw calls from 11,026 to 540 while highlighter strokes retain their existing overlap behavior.
- Kept completed tiles and stroke geometry across pan and zoom work when their page, scale, and content remain valid. Background refinement stays incremental so input does not wait for a full-page rebuild.
- Fixed horizontal trackpad scrolling so left and right movement, including inertia, follow the physical gesture instead of moving in the opposite direction.
- Saved the Hold to shape preference and restored it when HoomNote starts.
- Reduced write-side churn on long drawing sessions by allowing larger append journals before compaction.
- Improved notebook compatibility by accepting out-of-order polymorphic metadata and preserving the page overlay-opacity field used by compatible HoomNote data.
