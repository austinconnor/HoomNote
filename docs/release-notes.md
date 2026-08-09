# HoomNote 0.7.48

- Moving ink, shapes, images, and multi-object selections now records the clean source region and selected content once, then reuses those GPU commands throughout the drag instead of rebuilding dense geometry on every pointer frame.
- Move completion now uses the final pointer position and commits the last valid preview if pointer capture is lost, preventing fast moves from jumping backward, being discarded, or briefly showing the original object after release.
- The committed-page correction remains visible until the renderer confirms the matching edit version, preventing stale originals from flashing during the page-cache replacement.
